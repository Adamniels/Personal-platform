//! The front page's cards: one type per feature the core renders, for that
//! feature's own display data, and the choice between them. There is no
//! shared display shape, so putting a feature on the front page means a type
//! here and its piece of templates/front.html. A feature the core cannot show
//! gets the placeholder rather than vanishing. See "What a feature exposes
//! for the front page" in docs/core-architecture.md.

use crate::display::Fetched;
use crate::relative;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Deserializer};
use serde_json::Value;
use time::OffsetDateTime;
use time::format_description::well_known::Rfc3339;

/// One feature's place on the front page.
#[derive(Debug, PartialEq, Eq)]
pub enum Card {
    /// Canary's data, for the feature at `/canary`.
    Canary(CanaryCard),
    /// Review's data, for the feature at `/review`.
    Review(ReviewCard),
    /// A feature whose data could not be shown: it could not be reached,
    /// its data did not fit its type, or the core has no card for it. The
    /// page says so with the registry's name for it; the log says why.
    Unavailable { name: String },
}

/// The prefix whose display data reads into `CanaryCard`.
const CANARY_PREFIX: &str = "/canary";

/// The prefix whose display data reads into `ReviewCard`.
const REVIEW_PREFIX: &str = "/review";

impl Card {
    /// The card for what one feature gave. The feature is told apart by its
    /// registry prefix, which is unique, rather than its display name. A
    /// failed fetch was logged by `fetch_all`; the two failures found here
    /// are logged here, once each, with the feature's name.
    pub fn from_fetched(fetched: Fetched) -> Card {
        let Fetched { feature, data } = fetched;
        let Ok(data) = data else {
            return Card::Unavailable { name: feature.name };
        };
        let card = match feature.prefix.as_str() {
            CANARY_PREFIX => read(data).map(Card::Canary),
            REVIEW_PREFIX => read(data).map(Card::Review),
            _ => {
                eprintln!("the core has no card for {}", feature.name);
                return Card::Unavailable { name: feature.name };
            }
        };
        card.unwrap_or_else(|err| {
            eprintln!(
                "display data from {} does not fit its card: {err}",
                feature.name
            );
            Card::Unavailable { name: feature.name }
        })
    }
}

// A feature's data as its card type. Only a JSON object reads: serde would
// also fill a struct from an array by position, which is not a shape any
// feature sends.
fn read<T: DeserializeOwned>(data: Value) -> Result<T, String> {
    if !data.is_object() {
        return Err("not a JSON object".into());
    }
    serde_json::from_value(data).map_err(|err| err.to_string())
}

/// What review sends, pinned by `contracts/display/review.json`. Like
/// canary's, any other shape fails to deserialise, an unknown field included,
/// in a deck as well, and `next_due_at` must be there, null when no card is
/// scheduled. Review sends counts and its decks; the order they are shown in
/// is decided here.
#[derive(Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReviewCard {
    /// Cards due at or before now.
    pub due_now: u32,
    /// Cards due before review's day ends, those due now included.
    pub due_today: u32,
    /// The earliest due after now, sent as RFC 3339, or null.
    #[serde(deserialize_with = "rfc3339_or_null")]
    pub next_due_at: Option<OffsetDateTime>,
    /// The decks with cards due now, at most twenty.
    pub decks: Vec<ReviewDeck>,
}

/// A deck with cards due now: its name and how many.
#[derive(Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReviewDeck {
    pub name: String,
    pub due_now: u32,
}

/// How many decks the card lists.
const REVIEW_DECKS_SHOWN: usize = 3;

impl ReviewCard {
    /// How many more come due before the day ends, when any do. The two
    /// counts are read a moment apart, so today never counts as less.
    pub fn later_today(&self) -> Option<u32> {
        self.due_today
            .checked_sub(self.due_now)
            .filter(|later| *later > 0)
    }

    /// The three decks with the most due now, ties by name without letter
    /// case, then as written, whatever order review sent them in.
    pub fn top_decks(&self) -> Vec<&ReviewDeck> {
        let mut decks: Vec<&ReviewDeck> = self.decks.iter().collect();
        decks.sort_by(|a, b| {
            b.due_now
                .cmp(&a.due_now)
                .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
                .then_with(|| a.name.cmp(&b.name))
        });
        decks.truncate(REVIEW_DECKS_SHOWN);
        decks
    }

    /// When the next card comes due, from `now`, such as "in 3 hours", or
    /// None when no card is scheduled.
    pub fn next_due_in(&self, now: OffsetDateTime) -> Option<String> {
        self.next_due_at.map(|at| relative::ahead(at - now))
    }
}

/// What canary sends, pinned by `contracts/display/canary.json`. Anything
/// else fails to deserialise, an unknown field included, so a change to
/// canary's data shows as a placeholder and fails the fixture test rather
/// than rendering half a card.
#[derive(Debug, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CanaryCard {
    pub account_id: String,
    /// When the token canary verified expires, sent as Unix seconds.
    #[serde(deserialize_with = "unix_seconds")]
    pub token_expires_at: OffsetDateTime,
    pub key_id: String,
    pub python_version: String,
}

impl CanaryCard {
    /// The token's expiry as `HH:MM`, in UTC.
    pub fn valid_until(&self) -> String {
        let at = self.token_expires_at;
        format!("{:02}:{:02}", at.hour(), at.minute())
    }

    /// The key id's first eight characters, enough to tell keys apart.
    pub fn key_id_prefix(&self) -> &str {
        match self.key_id.char_indices().nth(8) {
            Some((end, _)) => &self.key_id[..end],
            None => &self.key_id,
        }
    }
}

fn rfc3339_or_null<'de, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<OffsetDateTime>, D::Error> {
    let text = Option::<String>::deserialize(deserializer)?;
    text.map(|text| OffsetDateTime::parse(&text, &Rfc3339).map_err(serde::de::Error::custom))
        .transpose()
}

// Unix seconds as a time in UTC, refusing seconds outside the range `time`
// represents, so the card never has to.
fn unix_seconds<'de, D: Deserializer<'de>>(deserializer: D) -> Result<OffsetDateTime, D::Error> {
    let seconds = i64::deserialize(deserializer)?;
    OffsetDateTime::from_unix_timestamp(seconds).map_err(serde::de::Error::custom)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::display::Unavailable;
    use crate::registry::Feature;
    use serde_json::{Value, json};
    use std::path::Path;

    // A committed display fixture, through the manifest directory, so the
    // test does not depend on where cargo test runs from.
    fn committed(name: &str) -> Value {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../contracts/display")
            .join(name);
        serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap()
    }

    fn fixture() -> Value {
        committed("canary.json")
    }

    fn review_fixture() -> Value {
        committed("review.json")
    }

    fn review(due_now: u32, due_today: u32, decks: &[(&str, u32)]) -> ReviewCard {
        ReviewCard {
            due_now,
            due_today,
            next_due_at: None,
            decks: decks
                .iter()
                .map(|(name, due_now)| ReviewDeck {
                    name: (*name).into(),
                    due_now: *due_now,
                })
                .collect(),
        }
    }

    fn fetched(name: &str, prefix: &str, data: Result<Value, Unavailable>) -> Fetched {
        Fetched {
            feature: Feature {
                name: name.into(),
                prefix: prefix.into(),
                upstream: "http://127.0.0.1:8081".into(),
                display: Some(format!("{prefix}/api/display")),
                icon: None,
            },
            data,
        }
    }

    fn unavailable(name: &str) -> Card {
        Card::Unavailable { name: name.into() }
    }

    #[test]
    fn the_committed_canary_fixture_reads_into_the_card() {
        let card: CanaryCard = serde_json::from_value(fixture()).unwrap();

        assert_eq!(
            card,
            CanaryCard {
                account_id: "01900000-0000-7000-8000-000000000001".into(),
                token_expires_at: OffsetDateTime::from_unix_timestamp(1_800_001_800).unwrap(),
                key_id: "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k".into(),
                python_version: "3.14.0".into(),
            }
        );
    }

    #[test]
    fn the_expiry_is_hours_and_minutes_in_utc() {
        let card: CanaryCard = serde_json::from_value(fixture()).unwrap();
        // 1 800 001 800 seconds is 2027-01-15 08:30:00 UTC.
        assert_eq!(card.valid_until(), "08:30");

        let mut data = fixture();
        data["token_expires_at"] = json!(0);
        let card: CanaryCard = serde_json::from_value(data).unwrap();
        assert_eq!(card.valid_until(), "00:00");
    }

    #[test]
    fn the_key_id_is_cut_to_eight_characters() {
        let mut data = fixture();
        let card: CanaryCard = serde_json::from_value(data.clone()).unwrap();
        assert_eq!(card.key_id_prefix(), "kPrK_qmx");

        data["key_id"] = json!("short");
        let card: CanaryCard = serde_json::from_value(data.clone()).unwrap();
        assert_eq!(card.key_id_prefix(), "short");

        // Characters, not bytes, so a multibyte character is never split.
        data["key_id"] = json!("ééééééééé");
        let card: CanaryCard = serde_json::from_value(data).unwrap();
        assert_eq!(card.key_id_prefix(), "éééééééé");
    }

    #[test]
    fn canary_data_of_any_other_shape_does_not_read() {
        let mut missing = fixture();
        missing.as_object_mut().unwrap().remove("python_version");
        let mut extra = fixture();
        extra["colour"] = json!("red");
        let mut wrong_type = fixture();
        wrong_type["token_expires_at"] = json!("1800001800");
        let mut out_of_range = fixture();
        out_of_range["token_expires_at"] = json!(i64::MAX);

        for (case, data) in [
            ("a field missing", missing),
            ("an unknown field", extra),
            ("a field of the wrong type", wrong_type),
            ("seconds out of range", out_of_range),
        ] {
            assert!(
                serde_json::from_value::<CanaryCard>(data).is_err(),
                "{case} read"
            );
        }
    }

    #[test]
    fn canarys_data_is_canarys_card() {
        let card = Card::from_fetched(fetched("Canary", "/canary", Ok(fixture())));
        assert_eq!(
            card,
            Card::Canary(serde_json::from_value(fixture()).unwrap())
        );
    }

    #[test]
    fn a_feature_that_could_not_be_reached_is_unavailable() {
        let card = Card::from_fetched(fetched("Canary", "/canary", Err(Unavailable::TimedOut)));
        assert_eq!(card, unavailable("Canary"));
    }

    #[test]
    fn data_that_does_not_fit_is_unavailable() {
        let card = Card::from_fetched(fetched("Canary", "/canary", Ok(json!({ "active": 4 }))));
        assert_eq!(card, unavailable("Canary"));
    }

    // serde reads a struct from an array by position, so canary's values in
    // order would otherwise make a card.
    #[test]
    fn canarys_values_as_a_list_are_unavailable() {
        let values = json!([
            "01900000-0000-7000-8000-000000000001",
            1_800_001_800,
            "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k",
            "3.14.0"
        ]);
        let card = Card::from_fetched(fetched("Canary", "/canary", Ok(values)));
        assert_eq!(card, unavailable("Canary"));
    }

    // The prefix decides, not the name or the data: canary's data from a
    // feature the core has no card for is still a placeholder.
    #[test]
    fn a_feature_the_core_has_no_card_for_is_unavailable() {
        let card = Card::from_fetched(fetched("Desk", "/desk", Ok(fixture())));
        assert_eq!(card, unavailable("Desk"));

        let card = Card::from_fetched(fetched("Canary", "/canary-two", Ok(fixture())));
        assert_eq!(card, unavailable("Canary"));
    }

    #[test]
    fn the_committed_review_fixture_reads_into_the_card() {
        let card: ReviewCard = serde_json::from_value(review_fixture()).unwrap();

        // 1 794 641 400 seconds is 2026-11-14 07:30:00 UTC.
        assert_eq!(
            card,
            ReviewCard {
                due_now: 12,
                due_today: 18,
                next_due_at: Some(OffsetDateTime::from_unix_timestamp(1_794_641_400).unwrap()),
                decks: vec![
                    ReviewDeck {
                        name: "Real Time Systems".into(),
                        due_now: 8
                    },
                    ReviewDeck {
                        name: "Concurrent Algorithms".into(),
                        due_now: 4
                    },
                ],
            }
        );
    }

    #[test]
    fn no_next_due_reads_as_none() {
        let mut data = review_fixture();
        data["next_due_at"] = Value::Null;
        let card: ReviewCard = serde_json::from_value(data).unwrap();
        assert_eq!(card.next_due_at, None);

        // Any offset is read as the instant it names.
        let mut data = review_fixture();
        data["next_due_at"] = json!("2026-11-14T08:30:00+01:00");
        let card: ReviewCard = serde_json::from_value(data).unwrap();
        assert_eq!(
            card.next_due_at,
            Some(OffsetDateTime::from_unix_timestamp(1_794_641_400).unwrap())
        );
    }

    #[test]
    fn review_data_of_any_other_shape_does_not_read() {
        let mut missing = review_fixture();
        missing.as_object_mut().unwrap().remove("due_today");
        // Null is an answer, absent is not.
        let mut no_next = review_fixture();
        no_next.as_object_mut().unwrap().remove("next_due_at");
        let mut extra = review_fixture();
        extra["label"] = json!("12 cards due");
        let mut extra_in_deck = review_fixture();
        extra_in_deck["decks"][0]["colour"] = json!("red");
        let mut negative = review_fixture();
        negative["due_now"] = json!(-1);
        let mut string_count = review_fixture();
        string_count["due_now"] = json!("12");
        let mut not_a_time = review_fixture();
        not_a_time["next_due_at"] = json!("tomorrow");
        let mut seconds = review_fixture();
        seconds["next_due_at"] = json!(1_794_641_400);
        let mut decks_not_a_list = review_fixture();
        decks_not_a_list["decks"] = json!({ "Real Time Systems": 8 });

        for (case, data) in [
            ("a field missing", missing),
            ("next_due_at missing", no_next),
            ("an unknown field", extra),
            ("an unknown field in a deck", extra_in_deck),
            ("a negative count", negative),
            ("a count as a string", string_count),
            ("a time that is not RFC 3339", not_a_time),
            ("a time as seconds", seconds),
            ("decks that are not a list", decks_not_a_list),
        ] {
            assert!(
                serde_json::from_value::<ReviewCard>(data).is_err(),
                "{case} read"
            );
        }
    }

    #[test]
    fn reviews_data_is_reviews_card() {
        let card = Card::from_fetched(fetched("Review", "/review", Ok(review_fixture())));
        assert_eq!(
            card,
            Card::Review(serde_json::from_value(review_fixture()).unwrap())
        );

        // Canary's data is not review's.
        let card = Card::from_fetched(fetched("Review", "/review", Ok(fixture())));
        assert_eq!(card, unavailable("Review"));
    }

    #[test]
    fn the_top_three_decks_have_the_most_due_ties_by_name() {
        let card = review(
            10,
            10,
            &[
                ("Rust", 2),
                ("databases", 3),
                ("Compilers", 3),
                ("Algorithms", 1),
                ("Networks", 1),
            ],
        );
        let names: Vec<&str> = card
            .top_decks()
            .iter()
            .map(|deck| deck.name.as_str())
            .collect();
        // The core decides the order, whatever order review sent: by count,
        // then by name without letter case.
        assert_eq!(names, ["Compilers", "databases", "Rust"]);

        assert_eq!(review(1, 1, &[("Only", 1)]).top_decks().len(), 1);
        assert!(review(0, 0, &[]).top_decks().is_empty());
    }

    #[test]
    fn more_later_today_is_what_is_due_today_beyond_now() {
        assert_eq!(review(3, 5, &[]).later_today(), Some(2));
        assert_eq!(review(3, 3, &[]).later_today(), None);
        // Counted a moment apart, today can trail now; that is not negative.
        assert_eq!(review(3, 2, &[]).later_today(), None);
    }

    #[test]
    fn the_next_due_is_relative_to_now() {
        let now = OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap();
        let next = |seconds: i64| {
            let mut card = review(0, 0, &[]);
            card.next_due_at = Some(now + time::Duration::seconds(seconds));
            card.next_due_in(now).unwrap()
        };

        assert_eq!(next(-30), "in under a minute");
        assert_eq!(next(0), "in under a minute");
        assert_eq!(next(59), "in under a minute");
        assert_eq!(next(60), "in 1 minute");
        assert_eq!(next(89), "in 1 minute");
        assert_eq!(next(90), "in 2 minutes");
        assert_eq!(next(59 * 60), "in 59 minutes");
        // Rounded to the nearest, so 59 minutes 40 seconds is an hour.
        assert_eq!(next(59 * 60 + 40), "in 1 hour");
        assert_eq!(next(3 * 3600 - 60), "in 3 hours");
        assert_eq!(next(23 * 3600), "in 23 hours");
        assert_eq!(next(23 * 3600 + 40 * 60), "in 1 day");
        assert_eq!(next(3 * 86_400 + 3600), "in 3 days");

        assert_eq!(review(0, 0, &[]).next_due_in(now), None);
    }
}

//! The front page's cards: one type per feature the core renders, for that
//! feature's own display data, and the choice between them. There is no
//! shared display shape, so putting a feature on the front page means a type
//! here and its piece of templates/front.html. A feature the core cannot show
//! gets the placeholder rather than vanishing. See "What a feature exposes
//! for the front page" in docs/core-architecture.md.

use crate::display::Fetched;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Deserializer};
use serde_json::Value;
use time::OffsetDateTime;

/// One feature's place on the front page.
#[derive(Debug, PartialEq, Eq)]
pub enum Card {
    /// Canary's data, for the feature at `/canary`.
    Canary(CanaryCard),
    /// A feature whose data could not be shown: it could not be reached,
    /// its data did not fit its type, or the core has no card for it. The
    /// page says so with the registry's name for it; the log says why.
    Unavailable { name: String },
}

/// The prefix whose display data reads into `CanaryCard`.
const CANARY_PREFIX: &str = "/canary";

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
        match feature.prefix.as_str() {
            CANARY_PREFIX => match read(data) {
                Ok(card) => Card::Canary(card),
                Err(err) => {
                    eprintln!(
                        "display data from {} does not fit its card: {err}",
                        feature.name
                    );
                    Card::Unavailable { name: feature.name }
                }
            },
            _ => {
                eprintln!("the core has no card for {}", feature.name);
                Card::Unavailable { name: feature.name }
            }
        }
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

    fn fixture() -> Value {
        // Through the manifest directory, so the test does not depend on
        // where cargo test runs from.
        let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../contracts/display/canary.json");
        serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap()
    }

    fn fetched(name: &str, prefix: &str, data: Result<Value, Unavailable>) -> Fetched {
        Fetched {
            feature: Feature {
                name: name.into(),
                prefix: prefix.into(),
                upstream: "http://127.0.0.1:8081".into(),
                display: Some(format!("{prefix}/api/display")),
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
}

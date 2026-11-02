//! Invite codes: generated from a terminal command, stored only as their
//! hash, consumed by exactly one registration. See "Registration is open but
//! invite coded" in docs/core-architecture.md.

use crate::secret_token;
use sqlx::PgExecutor;
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

/// How long a code lasts when `--expires-in` is not given.
pub const DEFAULT_EXPIRY: Duration = Duration::days(7);

/// Stores a new code created at `now` and expiring at `expires_at`, and
/// returns the code, which exists nowhere else afterwards.
pub async fn create(
    executor: impl PgExecutor<'_>,
    now: OffsetDateTime,
    expires_at: OffsetDateTime,
) -> Result<String, sqlx::Error> {
    let (code, code_hash) = secret_token::generate();
    sqlx::query!(
        "INSERT INTO invite_codes (code_hash, created_at, expires_at) VALUES ($1, $2, $3)",
        &code_hash[..],
        now,
        expires_at,
    )
    .execute(executor)
    .await?;
    Ok(code)
}

/// Marks the code with `code_hash` used by `account_id` at `now`, if it is
/// unused and has not expired, and says whether it did. One conditional
/// update, never a read then a write, so two registrations racing on one code
/// cannot both succeed: whichever runs second finds `used_at` set and changes
/// nothing. `expires_at` is the first instant a code no longer works.
///
/// `used_by` is checked at commit, so registration can name an account it
/// inserts later in the same transaction.
pub async fn consume(
    executor: impl PgExecutor<'_>,
    code_hash: &secret_token::TokenHash,
    account_id: Uuid,
    now: OffsetDateTime,
) -> Result<bool, sqlx::Error> {
    let consumed = sqlx::query_scalar!(
        "UPDATE invite_codes SET used_at = $3, used_by = $2
         WHERE code_hash = $1 AND used_at IS NULL AND expires_at > $3
         RETURNING code_hash",
        &code_hash[..],
        account_id,
        now,
    )
    .fetch_optional(executor)
    .await?;
    Ok(consumed.is_some())
}

/// A duration as `--expires-in` takes it: a whole number above zero and one
/// unit, `s`, `m`, `h` or `d`, such as `7d` or `30s`.
pub fn parse_duration(text: &str) -> Result<Duration, String> {
    let refused = || format!("'{text}' is not a duration like 7d, 12h, 30m or 90s");

    let unit_at = text.len().saturating_sub(1);
    if !text.is_char_boundary(unit_at) {
        return Err(refused());
    }
    let (number, unit) = text.split_at(unit_at);
    if number.is_empty() || !number.bytes().all(|b| b.is_ascii_digit()) {
        return Err(refused());
    }
    let count: i64 = number.parse().map_err(|_| refused())?;
    if count == 0 {
        return Err(refused());
    }

    let seconds_each = match unit {
        "s" => 1,
        "m" => 60,
        "h" => 60 * 60,
        "d" => 24 * 60 * 60,
        _ => return Err(refused()),
    };
    let seconds = count.checked_mul(seconds_each).ok_or_else(refused)?;
    Ok(Duration::seconds(seconds))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_parse_in_each_unit() {
        assert_eq!(parse_duration("30s"), Ok(Duration::seconds(30)));
        assert_eq!(parse_duration("15m"), Ok(Duration::minutes(15)));
        assert_eq!(parse_duration("2h"), Ok(Duration::hours(2)));
        assert_eq!(parse_duration("7d"), Ok(Duration::days(7)));
    }

    #[test]
    fn anything_else_is_refused() {
        for text in [
            "",
            "7",
            "d",
            "0d",
            "-1d",
            "+1d",
            "1.5h",
            "7days",
            "7 d",
            "1w",
            "7é",
            "99999999999999999999d",
        ] {
            assert!(parse_duration(text).is_err(), "accepted {text:?}");
        }
    }
}

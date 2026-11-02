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

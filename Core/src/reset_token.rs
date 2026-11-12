//! Password reset tokens: made from a terminal command for an account, stored
//! only as their hash, usable once, for an hour. The invite code machinery
//! again, see "Password reset, and changing a password" in
//! docs/core-architecture.md.

use crate::{accounts, secret_token};
use sqlx::{PgExecutor, PgPool};
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

/// How long a token works: an hour rather than a day, since it stands in for
/// the password.
pub const LIFETIME: Duration = Duration::hours(1);

/// Stores a new token for `account_id`, made at `now` and expiring
/// `LIFETIME` later, and returns the token, which exists nowhere else
/// afterwards. Earlier unused tokens for the account keep working until
/// their own hour runs out.
pub async fn create(
    executor: impl PgExecutor<'_>,
    account_id: Uuid,
    now: OffsetDateTime,
) -> Result<String, sqlx::Error> {
    let (token, token_hash) = secret_token::generate();
    sqlx::query!(
        "INSERT INTO password_reset_tokens (token_hash, account_id, created_at, expires_at)
         VALUES ($1, $2, $3, $4)",
        &token_hash[..],
        account_id,
        now,
        now + LIFETIME,
    )
    .execute(executor)
    .await?;
    Ok(token)
}

/// Marks the token with `token_hash` used at `now`, if it is unused and has
/// not expired, and returns the account it is for. One conditional update,
/// never a read then a write, so two resets racing on one token cannot both
/// succeed: whichever runs second finds `used_at` set and changes nothing.
/// `expires_at` is the first instant a token no longer works.
pub async fn consume(
    executor: impl PgExecutor<'_>,
    token_hash: &secret_token::TokenHash,
    now: OffsetDateTime,
) -> Result<Option<Uuid>, sqlx::Error> {
    sqlx::query_scalar!(
        "UPDATE password_reset_tokens SET used_at = $2
         WHERE token_hash = $1 AND used_at IS NULL AND expires_at > $2
         RETURNING account_id",
        &token_hash[..],
        now,
    )
    .fetch_optional(executor)
    .await
}

/// What the reset command does: a token for the account registered with
/// `email`, trimmed and lowercased first, or `None` when there is no such
/// account. The account's status is not looked at, so a disabled account can
/// be given a token, and resetting never changes it.
pub async fn create_for_email(
    pool: &PgPool,
    email: &str,
    now: OffsetDateTime,
) -> Result<Option<String>, sqlx::Error> {
    let email = email.trim().to_lowercase();
    let Some(account) = accounts::find_by_email(pool, &email).await? else {
        return Ok(None);
    };
    create(pool, account.id, now).await.map(Some)
}

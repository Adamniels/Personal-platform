//! Sessions: the refresh side of the token model. A login starts a family,
//! and each refresh will add a row to it, never moving its thirty day cap. See
//! "The refresh token is opaque rather than a JWT" and "The refresh token
//! lasts fourteen days idle, and the session thirty days in total" in
//! docs/core-architecture.md.

use crate::secret_token;
use sqlx::PgExecutor;
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

/// How long a refresh token lasts unused.
pub const REFRESH_TOKEN_LIFETIME: Duration = Duration::days(14);

/// How long a session lasts in total, however often it is refreshed.
pub const FAMILY_LIFETIME: Duration = Duration::days(30);

/// A session just started: the refresh token to hand out once, its family,
/// and when the token expires, which the refresh cookie's lifetime follows.
pub struct Started {
    pub refresh_token: String,
    pub family_id: Uuid,
    pub expires_at: OffsetDateTime,
}

/// Starts a new session family for `account_id` at `now`, storing its first
/// refresh token hashed.
pub async fn start(
    executor: impl PgExecutor<'_>,
    account_id: Uuid,
    now: OffsetDateTime,
) -> Result<Started, sqlx::Error> {
    let (refresh_token, token_hash) = secret_token::generate();
    let family_id = Uuid::now_v7();
    let family_expires_at = now + FAMILY_LIFETIME;
    let expires_at = token_expiry(now, family_expires_at);

    sqlx::query!(
        "INSERT INTO sessions
             (id, account_id, family_id, token_hash, issued_at, expires_at, family_expires_at)
         VALUES ($1, $2, $3, $4, $5, $6, $7)",
        Uuid::now_v7(),
        account_id,
        family_id,
        &token_hash[..],
        now,
        expires_at,
        family_expires_at,
    )
    .execute(executor)
    .await?;

    Ok(Started {
        refresh_token,
        family_id,
        expires_at,
    })
}

/// A stored refresh token, found by its hash, with what a refresh decides on:
/// its own state, its family's cap, and whether its account is still active.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    pub id: Uuid,
    pub account_id: Uuid,
    pub family_id: Uuid,
    /// The first instant the token no longer works.
    pub expires_at: OffsetDateTime,
    /// The first instant no token of the family works.
    pub family_expires_at: OffsetDateTime,
    pub used_at: Option<OffsetDateTime>,
    pub revoked_at: Option<OffsetDateTime>,
    pub account_active: bool,
}

/// When a token issued at `now` expires: fourteen days out, or at its family's
/// cap if that comes first.
pub fn token_expiry(now: OffsetDateTime, family_expires_at: OffsetDateTime) -> OffsetDateTime {
    (now + REFRESH_TOKEN_LIFETIME).min(family_expires_at)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_token_never_outlives_its_family() {
        let now = OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap();
        let cap = now + Duration::days(3);
        assert_eq!(
            token_expiry(now, now + FAMILY_LIFETIME),
            now + REFRESH_TOKEN_LIFETIME
        );
        assert_eq!(token_expiry(now, cap), cap);
    }
}

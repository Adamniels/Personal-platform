//! Sessions: the refresh side of the token model. A login starts a family,
//! and each refresh adds a row to it, never moving its thirty day cap. See
//! "The refresh token is opaque rather than a JWT" and "The refresh token
//! lasts fourteen days idle, and the session thirty days in total" in
//! docs/core-architecture.md.

use crate::secret_token;
use sqlx::{PgExecutor, PgPool};
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

/// How long a refresh token lasts unused.
pub const REFRESH_TOKEN_LIFETIME: Duration = Duration::days(14);

/// How long a session lasts in total, however often it is refreshed.
pub const FAMILY_LIFETIME: Duration = Duration::days(30);

/// A refresh token just issued, by a login starting a family or a rotation
/// within one: the token to hand out once, its family, and when it expires,
/// which the refresh cookie's lifetime follows.
pub struct Issued {
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
) -> Result<Issued, sqlx::Error> {
    insert(
        executor,
        account_id,
        Uuid::now_v7(),
        now,
        now + FAMILY_LIFETIME,
    )
    .await
}

/// The stored token whose hash is `token_hash`, with its account's status, or
/// `None` when nothing matches. Found by hash only, so the presented token is
/// never compared in a way that could leak it through timing.
pub async fn find(
    executor: impl PgExecutor<'_>,
    token_hash: &secret_token::TokenHash,
) -> Result<Option<Row>, sqlx::Error> {
    sqlx::query_as!(
        Row,
        r#"SELECT s.id, s.account_id, s.family_id, s.expires_at, s.family_expires_at,
                  s.used_at, s.revoked_at, a.status = 'active' AS "account_active!"
           FROM sessions s
           JOIN accounts a ON a.id = s.account_id
           WHERE s.token_hash = $1"#,
        &token_hash[..],
    )
    .fetch_optional(executor)
    .await
}

/// Locks the family of the token `id` until the end of the transaction
/// `executor` belongs to; outside one it is released at once and protects
/// nothing. Rotating and revoking both take it first, so neither runs while
/// the other is part way through: a revoke that waited on a rotation then
/// sees the row it stored, and a rotation that waited on a revoke finds its
/// token revoked. Without it, a refresh committing during a logout leaves its
/// new row live, since under READ COMMITTED the revoke's snapshot predates
/// the insert. A transaction scoped advisory lock keyed by the family id,
/// since there is no row per family to lock; two families whose keys
/// collide only wait for each other. Nothing to lock when nothing matches.
pub async fn lock_family(executor: impl PgExecutor<'_>, id: Uuid) -> Result<(), sqlx::Error> {
    sqlx::query!(
        r#"SELECT pg_advisory_xact_lock(hashtextextended(family_id::text, 0))::text AS "locked"
           FROM sessions WHERE id = $1"#,
        id,
    )
    .fetch_optional(executor)
    .await?;
    Ok(())
}

/// What marking a token used returns: what its successor copies.
pub struct Marked {
    pub account_id: Uuid,
    pub family_id: Uuid,
    pub family_expires_at: OffsetDateTime,
}

/// Marks the token `id` used at `now`, if it is neither used nor revoked, and
/// returns what its successor copies; `None` when it changed nothing. One
/// conditional update, never a read then a write, so of two refreshes racing
/// on one token only one gets a row back and the family cannot fork.
pub async fn mark_used(
    executor: impl PgExecutor<'_>,
    id: Uuid,
    now: OffsetDateTime,
) -> Result<Option<Marked>, sqlx::Error> {
    sqlx::query_as!(
        Marked,
        "UPDATE sessions SET used_at = $2
         WHERE id = $1 AND used_at IS NULL AND revoked_at IS NULL
         RETURNING account_id, family_id, family_expires_at",
        id,
        now,
    )
    .fetch_optional(executor)
    .await
}

/// Rotates the token `id` at `now`: marks it used and stores the next token of
/// its family, for the same account, expiring fourteen days out or at the
/// family's unchanged cap. One transaction, holding the family's lock, so a
/// token is never used without a successor and a revoke never misses the
/// successor. `None` when the mark changed nothing, because another request
/// rotated it first or it was revoked; nothing is stored then. Nothing is
/// ever deleted, the used row is what will make reuse detectable.
pub async fn rotate(
    pool: &PgPool,
    id: Uuid,
    now: OffsetDateTime,
) -> Result<Option<Issued>, sqlx::Error> {
    let mut transaction = pool.begin().await?;
    lock_family(&mut *transaction, id).await?;
    let Some(marked) = mark_used(&mut *transaction, id, now).await? else {
        return Ok(None);
    };
    let issued = insert(
        &mut *transaction,
        marked.account_id,
        marked.family_id,
        now,
        marked.family_expires_at,
    )
    .await?;
    transaction.commit().await?;
    Ok(Some(issued))
}

/// Revokes every token of family `family_id` at `now`, and returns how many
/// rows it marked. A row already revoked keeps the time it was first revoked,
/// and nothing is deleted, so a used row still makes reuse detectable. Takes
/// an executor, so a flow that revokes as one step of several can call it
/// inside its transaction. See "Nothing about a session is deleted in normal
/// operation" in docs/core-architecture.md.
pub async fn revoke_family(
    executor: impl PgExecutor<'_>,
    family_id: Uuid,
    now: OffsetDateTime,
) -> Result<u64, sqlx::Error> {
    let result = sqlx::query!(
        "UPDATE sessions SET revoked_at = $2 WHERE family_id = $1 AND revoked_at IS NULL",
        family_id,
        now,
    )
    .execute(executor)
    .await?;
    Ok(result.rows_affected())
}

// Stores a new token of family `family_id`, issued at `now`, hashed.
async fn insert(
    executor: impl PgExecutor<'_>,
    account_id: Uuid,
    family_id: Uuid,
    now: OffsetDateTime,
    family_expires_at: OffsetDateTime,
) -> Result<Issued, sqlx::Error> {
    let (refresh_token, token_hash) = secret_token::generate();
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

    Ok(Issued {
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

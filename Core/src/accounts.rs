//! Accounts, see "The core's data" and "The user id" in
//! docs/core-architecture.md.

use sqlx::PgExecutor;
use time::OffsetDateTime;
use uuid::Uuid;

/// Stores a new active account, and says whether it did: `false` when the
/// email is taken. `email` is already lowercased; the table refuses it
/// otherwise.
pub async fn create(
    executor: impl PgExecutor<'_>,
    id: Uuid,
    email: &str,
    password_hash: &str,
    now: OffsetDateTime,
) -> Result<bool, sqlx::Error> {
    let inserted = sqlx::query!(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES ($1, $2, $3, 'active', $4)",
        id,
        email,
        password_hash,
        now,
    )
    .execute(executor)
    .await;

    match inserted {
        Ok(_) => Ok(true),
        Err(err)
            if err
                .as_database_error()
                .and_then(|db_err| db_err.constraint())
                == Some("accounts_email_key") =>
        {
            Ok(false)
        }
        Err(err) => Err(err),
    }
}

/// What login needs to know about an account.
pub struct Account {
    pub id: Uuid,
    pub password_hash: String,
    pub status: String,
}

impl Account {
    pub fn is_active(&self) -> bool {
        self.status == "active"
    }
}

/// The account registered with `email`, which is already lowercased.
pub async fn find_by_email(
    executor: impl PgExecutor<'_>,
    email: &str,
) -> Result<Option<Account>, sqlx::Error> {
    sqlx::query_as!(
        Account,
        "SELECT id, password_hash, status FROM accounts WHERE email = $1",
        email,
    )
    .fetch_optional(executor)
    .await
}

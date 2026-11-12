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

/// The email of the account with `id`, or `None` when there is no such
/// account. What the nav shows as who is logged in.
pub async fn email_of(
    executor: impl PgExecutor<'_>,
    id: Uuid,
) -> Result<Option<String>, sqlx::Error> {
    sqlx::query_scalar!("SELECT email FROM accounts WHERE id = $1", id)
        .fetch_optional(executor)
        .await
}

/// Sets the password hash of the account with `id`. A plain update, which
/// leaves the account's status alone: a reset works for a disabled account
/// and does not enable it.
pub async fn set_password(
    executor: impl PgExecutor<'_>,
    id: Uuid,
    password_hash: &str,
) -> Result<(), sqlx::Error> {
    sqlx::query!(
        "UPDATE accounts SET password_hash = $2 WHERE id = $1",
        id,
        password_hash,
    )
    .execute(executor)
    .await?;
    Ok(())
}

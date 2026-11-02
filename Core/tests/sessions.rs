//! What the sessions table guarantees by itself, as the app role, see "The
//! core's data" and "The refresh token lasts fourteen days idle, and the
//! session thirty days in total" in docs/core-architecture.md.

mod common;

use sqlx::PgPool;
use uuid::Uuid;

async fn account(pool: &PgPool) -> Uuid {
    let id = Uuid::now_v7();
    sqlx::query(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES ($1, $2, '$argon2id$placeholder', 'active', now())",
    )
    .bind(id)
    .bind(format!("{id}@example.com"))
    .execute(pool)
    .await
    .unwrap();
    id
}

// A session issued now, its token expiring `expires` from now and its family
// `family_expires` from now, both as Postgres intervals.
async fn insert_session(
    pool: &PgPool,
    account_id: Uuid,
    expires: &str,
    family_expires: &str,
) -> Result<(), sqlx::Error> {
    sqlx::query(
        "INSERT INTO sessions
             (id, account_id, family_id, token_hash, issued_at, expires_at, family_expires_at)
         VALUES ($1, $2, $3, sha256($4::bytea), now(), now() + $5::interval, now() + $6::interval)",
    )
    .bind(Uuid::now_v7())
    .bind(account_id)
    .bind(Uuid::now_v7())
    .bind(Uuid::now_v7().as_bytes().to_vec())
    .bind(expires)
    .bind(family_expires)
    .execute(pool)
    .await
    .map(|_| ())
}

// Rotation never moves the cap, so a token outliving its family would be a
// session longer than thirty days that nothing reading the row would notice.
#[tokio::test]
async fn a_token_cannot_expire_after_its_family() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool).await;

    insert_session(&db.pool, account_id, "30 days", "30 days")
        .await
        .expect("a token expiring at its family's cap is refused");

    let err = insert_session(&db.pool, account_id, "30 days 1 second", "30 days")
        .await
        .expect_err("stored a token expiring after its family");
    assert_eq!(
        err.as_database_error()
            .and_then(|db_err| db_err.code())
            .as_deref(),
        Some("23514"),
        "expected the check, got: {err}"
    );

    db.close().await;
}

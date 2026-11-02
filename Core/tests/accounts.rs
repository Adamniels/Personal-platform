//! What the accounts and invite_codes tables guarantee by themselves, as the
//! app role, see "The core's data" in docs/core-architecture.md.

mod common;

use sqlx::PgPool;

async fn insert_account(pool: &PgPool, email: &str) -> Result<(), sqlx::Error> {
    sqlx::query(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES (gen_random_uuid(), $1, '$argon2id$placeholder', 'active', now())",
    )
    .bind(email)
    .execute(pool)
    .await
    .map(|_| ())
}

fn code_of(err: &sqlx::Error) -> Option<String> {
    err.as_database_error()
        .and_then(|db_err| db_err.code())
        .map(|code| code.into_owned())
}

// The core lowercases, and the database refuses anything that was not, so a
// path that forgot cannot store a second spelling of the same address.
#[tokio::test]
async fn two_spellings_of_one_email_cannot_both_be_stored() {
    let db = common::TestDb::new().await;

    insert_account(&db.pool, "walk@example.com").await.unwrap();

    let mixed = insert_account(&db.pool, "Walk@Example.com")
        .await
        .expect_err("stored an email that was not lowercased");
    assert_eq!(
        code_of(&mixed).as_deref(),
        Some("23514"),
        "expected the check, got: {mixed}"
    );

    let again = insert_account(&db.pool, "walk@example.com")
        .await
        .expect_err("stored the same email twice");
    assert_eq!(
        code_of(&again).as_deref(),
        Some("23505"),
        "expected unique, got: {again}"
    );

    db.close().await;
}

#[tokio::test]
async fn the_app_role_can_read_and_write_invite_codes() {
    let db = common::TestDb::new().await;

    sqlx::query(
        "INSERT INTO invite_codes (code_hash, created_at, expires_at)
         VALUES (sha256('code'), now(), now() + interval '7 days')",
    )
    .execute(&db.pool)
    .await
    .unwrap();

    let updated = sqlx::query("UPDATE invite_codes SET expires_at = now() + interval '1 day'")
        .execute(&db.pool)
        .await
        .unwrap();
    assert_eq!(updated.rows_affected(), 1);

    let stored: i64 = sqlx::query_scalar("SELECT count(*) FROM invite_codes")
        .fetch_one(&db.pool)
        .await
        .unwrap();
    assert_eq!(stored, 1);

    db.close().await;
}

//! Registration in one transaction: the code is consumed and the account
//! created together, or neither happens. See "Registration is open but invite
//! coded" in docs/core-architecture.md.

mod common;

use platform_core::registration::{self, Outcome};
use platform_core::{invite, secret_token};
use sqlx::PgPool;
use time::OffsetDateTime;
use uuid::Uuid;

const PASSWORD: &str = "a password of sixteen";

fn now() -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap()
}

async fn fresh_code(pool: &PgPool) -> String {
    invite::create(pool, now(), now() + time::Duration::days(7))
        .await
        .unwrap()
}

async fn submit(pool: &PgPool, code: &str, email: &str) -> Outcome {
    let input = registration::validate(code, email, PASSWORD).unwrap();
    registration::register(pool, input, now()).await.unwrap()
}

async fn code_use(pool: &PgPool, code: &str) -> (Option<OffsetDateTime>, Option<Uuid>) {
    sqlx::query_as("SELECT used_at, used_by FROM invite_codes WHERE code_hash = $1")
        .bind(&secret_token::hash(code).unwrap()[..])
        .fetch_one(pool)
        .await
        .unwrap()
}

async fn accounts(pool: &PgPool) -> i64 {
    sqlx::query_scalar("SELECT count(*) FROM accounts")
        .fetch_one(pool)
        .await
        .unwrap()
}

#[tokio::test]
async fn a_registration_consumes_the_code_and_creates_the_account() {
    let db = common::TestDb::new().await;
    let code = fresh_code(&db.pool).await;

    assert_eq!(
        submit(&db.pool, &code, " Walk@Example.com ").await,
        Outcome::Registered
    );

    let (id, email, password_hash, status, created_at): (
        Uuid,
        String,
        String,
        String,
        OffsetDateTime,
    ) = sqlx::query_as("SELECT id, email, password_hash, status, created_at FROM accounts")
        .fetch_one(&db.pool)
        .await
        .unwrap();
    assert_eq!(id.get_version_num(), 7);
    assert_eq!(email, "walk@example.com");
    assert!(password_hash.starts_with("$argon2id$"));
    assert!(!password_hash.contains(PASSWORD));
    assert_eq!((status.as_str(), created_at), ("active", now()));
    assert_eq!(code_use(&db.pool, &code).await, (Some(now()), Some(id)));

    db.close().await;
}

// The code is consumed first, so a taken email is only learned by someone
// holding a valid code, and the rollback hands that code back unused.
#[tokio::test]
async fn a_taken_email_rolls_back_and_leaves_the_code_unconsumed() {
    let db = common::TestDb::new().await;
    let first = fresh_code(&db.pool).await;
    assert_eq!(
        submit(&db.pool, &first, "walk@example.com").await,
        Outcome::Registered
    );

    let second = fresh_code(&db.pool).await;
    assert_eq!(
        submit(&db.pool, &second, "WALK@example.com").await,
        Outcome::EmailTaken
    );

    assert_eq!(code_use(&db.pool, &second).await, (None, None));
    assert_eq!(accounts(&db.pool).await, 1);

    db.close().await;
}

#[tokio::test]
async fn a_bad_code_creates_no_account_and_reveals_no_email() {
    let db = common::TestDb::new().await;
    let code = fresh_code(&db.pool).await;
    assert_eq!(
        submit(&db.pool, &code, "walk@example.com").await,
        Outcome::Registered
    );

    assert_eq!(
        submit(&db.pool, &code, "other@example.com").await,
        Outcome::InvalidCode
    );
    let (unknown, _) = secret_token::generate();
    assert_eq!(
        submit(&db.pool, &unknown, "other@example.com").await,
        Outcome::InvalidCode
    );
    assert_eq!(
        submit(&db.pool, "not a code", "other@example.com").await,
        Outcome::InvalidCode
    );

    // A taken email behind a bad code is still only "not valid", so the form
    // never tells a stranger which emails have accounts.
    assert_eq!(
        submit(&db.pool, "not a code", "walk@example.com").await,
        Outcome::InvalidCode
    );
    assert_eq!(
        submit(&db.pool, &unknown, "walk@example.com").await,
        Outcome::InvalidCode
    );

    assert_eq!(accounts(&db.pool).await, 1);

    db.close().await;
}

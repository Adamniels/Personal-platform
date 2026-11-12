//! Password reset tokens: made by the reset command for an account, stored
//! only as their hash, usable once, for an hour. Consuming is one conditional
//! update, tested deterministically by running it twice. See "Password reset,
//! and changing a password" and "Marking a token used is one conditional
//! update" in docs/core-architecture.md.

mod common;

use platform_core::{reset_token, secret_token};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

// Whole seconds, since timestamptz keeps microseconds and the comparisons
// below are exact.
fn at(seconds: i64) -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
}

async fn account_with_email(pool: &PgPool, email: &str) -> Uuid {
    let id = Uuid::now_v7();
    sqlx::query(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES ($1, $2, '$argon2id$placeholder', 'active', now())",
    )
    .bind(id)
    .bind(email)
    .execute(pool)
    .await
    .unwrap();
    id
}

async fn account(pool: &PgPool) -> Uuid {
    account_with_email(pool, &format!("{}@example.com", Uuid::now_v7())).await
}

async fn row_count(pool: &PgPool) -> i64 {
    sqlx::query_scalar("SELECT count(*) FROM password_reset_tokens")
        .fetch_one(pool)
        .await
        .unwrap()
}

async fn used_at(pool: &PgPool, hash: &secret_token::TokenHash) -> Option<OffsetDateTime> {
    sqlx::query_scalar("SELECT used_at FROM password_reset_tokens WHERE token_hash = $1")
        .bind(&hash[..])
        .fetch_one(pool)
        .await
        .unwrap()
}

#[tokio::test]
async fn a_reset_token_is_stored_only_as_its_hash_for_its_account() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;

    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();

    let (token_hash, account_id, created_at, expires_at, used_at): (
        Vec<u8>,
        Uuid,
        OffsetDateTime,
        OffsetDateTime,
        Option<OffsetDateTime>,
    ) = sqlx::query_as(
        "SELECT token_hash, account_id, created_at, expires_at, used_at
         FROM password_reset_tokens",
    )
    .fetch_one(&db.pool)
    .await
    .unwrap();

    assert_eq!(row_count(&db.pool).await, 1);
    assert_eq!(
        Some(token_hash.as_slice()),
        secret_token::hash(&token).as_ref().map(|h| &h[..])
    );
    assert_eq!(account_id, id);
    assert_eq!(created_at, at(0));
    assert_eq!(expires_at, at(0) + Duration::hours(1));
    assert_eq!(reset_token::LIFETIME, Duration::hours(1));
    assert_eq!(used_at, None);

    // The whole row as text, so the token cannot hide in any column.
    let row: String = sqlx::query_scalar("SELECT t::text FROM password_reset_tokens t")
        .fetch_one(&db.pool)
        .await
        .unwrap();
    assert!(!row.contains(&token), "the token itself was stored: {row}");
    assert_ne!(token_hash, token.as_bytes());

    db.close().await;
}

#[tokio::test]
async fn consuming_a_token_twice_changes_nothing_the_second_time() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;
    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();
    let hash = secret_token::hash(&token).unwrap();

    assert_eq!(
        reset_token::consume(&db.pool, &hash, at(10)).await.unwrap(),
        Some(id)
    );
    assert_eq!(
        reset_token::consume(&db.pool, &hash, at(20)).await.unwrap(),
        None
    );
    assert_eq!(used_at(&db.pool, &hash).await, Some(at(10)));

    db.close().await;
}

// expires_at is the first instant a token no longer works, as for invite
// codes and a JWT's exp.
#[tokio::test]
async fn a_token_expires_exactly_at_its_hour() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;
    let hour = reset_token::LIFETIME.whole_seconds();

    for (now, works) in [
        (at(hour - 1), true),
        (at(hour), false),
        (at(hour + 1), false),
    ] {
        let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();
        let hash = secret_token::hash(&token).unwrap();
        assert_eq!(
            reset_token::consume(&db.pool, &hash, now).await.unwrap(),
            works.then_some(id),
            "a token made at 0 and expiring at {hour}, consumed at {}",
            (now - at(0)).whole_seconds()
        );
        if !works {
            assert_eq!(used_at(&db.pool, &hash).await, None);
        }
    }

    db.close().await;
}

#[tokio::test]
async fn an_unknown_or_malformed_token_consumes_nothing() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;
    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();
    let real = secret_token::hash(&token).unwrap();

    // Malformed: what does not decode to 32 bytes has no hash to look up.
    for input in ["", "not a token", &token[..42], &format!("{token}A")] {
        assert_eq!(secret_token::hash(input), None, "accepted {input:?}");
    }

    // Unknown: a well formed token that was never stored.
    let (_, unknown) = secret_token::generate();
    assert_eq!(
        reset_token::consume(&db.pool, &unknown, at(10))
            .await
            .unwrap(),
        None
    );

    assert_eq!(used_at(&db.pool, &real).await, None);
    assert_eq!(row_count(&db.pool).await, 1);

    db.close().await;
}

#[tokio::test]
async fn a_reset_for_an_unknown_email_fails_and_stores_nothing() {
    let db = common::TestDb::new().await;
    account_with_email(&db.pool, "someone@example.com").await;

    let token = reset_token::create_for_email(&db.pool, "nobody@example.com", at(0))
        .await
        .unwrap();

    assert_eq!(token, None);
    assert_eq!(row_count(&db.pool).await, 0);

    db.close().await;
}

#[tokio::test]
async fn the_email_is_trimmed_and_lowercased_before_the_lookup() {
    let db = common::TestDb::new().await;
    let id = account_with_email(&db.pool, "someone@example.com").await;

    let token = reset_token::create_for_email(&db.pool, "  SomeOne@Example.COM\n", at(0))
        .await
        .unwrap()
        .expect("no token for the account's email in another spelling");

    let hash = secret_token::hash(&token).unwrap();
    assert_eq!(
        reset_token::consume(&db.pool, &hash, at(10)).await.unwrap(),
        Some(id)
    );

    db.close().await;
}

#[tokio::test]
async fn the_table_refuses_a_token_expiring_before_it_was_created() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;

    // Before, and at the same instant, since a token that expires as it is
    // made could never work either.
    for expires_at in [at(-1), at(0)] {
        let (_, hash) = secret_token::generate();
        let err = sqlx::query(
            "INSERT INTO password_reset_tokens (token_hash, account_id, created_at, expires_at)
             VALUES ($1, $2, $3, $4)",
        )
        .bind(&hash[..])
        .bind(id)
        .bind(at(0))
        .bind(expires_at)
        .execute(&db.pool)
        .await
        .expect_err("stored a token expiring before it was created");
        assert_eq!(
            err.as_database_error().and_then(|e| e.constraint()),
            Some("password_reset_tokens_check"),
            "refused for another reason: {err}"
        );
    }
    assert_eq!(row_count(&db.pool).await, 0);

    db.close().await;
}

// Creating a token leaves the account's earlier unused ones working, per the
// slice: invalidating them is out of scope.
#[tokio::test]
async fn a_new_token_leaves_earlier_ones_valid() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;
    let first = reset_token::create(&db.pool, id, at(0)).await.unwrap();
    reset_token::create(&db.pool, id, at(5)).await.unwrap();

    let hash = secret_token::hash(&first).unwrap();
    assert_eq!(
        reset_token::consume(&db.pool, &hash, at(10)).await.unwrap(),
        Some(id)
    );

    db.close().await;
}

// Resetting never looks at, or changes, an account's status.
#[tokio::test]
async fn a_disabled_account_can_be_given_a_token() {
    let db = common::TestDb::new().await;
    let id = account_with_email(&db.pool, "disabled@example.com").await;
    sqlx::query("UPDATE accounts SET status = 'disabled' WHERE id = $1")
        .bind(id)
        .execute(&db.pool)
        .await
        .unwrap();

    let token = reset_token::create_for_email(&db.pool, "disabled@example.com", at(0))
        .await
        .unwrap();
    assert!(token.is_some());

    let status: String = sqlx::query_scalar("SELECT status FROM accounts WHERE id = $1")
        .bind(id)
        .fetch_one(&db.pool)
        .await
        .unwrap();
    assert_eq!(status, "disabled");

    db.close().await;
}

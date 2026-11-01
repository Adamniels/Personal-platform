//! Consuming an invite code is one conditional update, so a code makes at most
//! one account however registrations interleave. Tested deterministically, by
//! running the same update twice, rather than by racing two requests. See
//! "Marking a token used is one conditional update" in
//! docs/core-architecture.md.

mod common;

use platform_core::{invite, secret_token};
use sqlx::PgPool;
use time::OffsetDateTime;
use uuid::Uuid;

fn at(seconds: i64) -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
}

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

// A code created at 0 and expiring at 100, returned as the hash that
// registration would look it up by.
async fn code(pool: &PgPool) -> secret_token::TokenHash {
    let code = invite::create(pool, at(0), at(100)).await.unwrap();
    secret_token::hash(&code).unwrap()
}

#[tokio::test]
async fn a_code_is_consumed_once_and_the_second_attempt_changes_nothing() {
    let db = common::TestDb::new().await;
    let (first, second) = (account(&db.pool).await, account(&db.pool).await);
    let hash = code(&db.pool).await;

    assert!(
        invite::consume(&db.pool, &hash, first, at(10))
            .await
            .unwrap()
    );
    assert!(
        !invite::consume(&db.pool, &hash, second, at(20))
            .await
            .unwrap()
    );

    let (used_at, used_by): (Option<OffsetDateTime>, Option<Uuid>) =
        sqlx::query_as("SELECT used_at, used_by FROM invite_codes WHERE code_hash = $1")
            .bind(&hash[..])
            .fetch_one(&db.pool)
            .await
            .unwrap();
    assert_eq!((used_at, used_by), (Some(at(10)), Some(first)));

    db.close().await;
}

// expires_at is the first instant a code no longer works, as with a JWT's exp.
#[tokio::test]
async fn a_code_works_until_the_instant_it_expires() {
    let db = common::TestDb::new().await;
    // Only the first case consumes, so one account is enough for used_by,
    // which is unique.
    let id = account(&db.pool).await;

    for (now, consumed) in [(at(99), true), (at(100), false), (at(101), false)] {
        let hash = code(&db.pool).await;
        assert_eq!(
            invite::consume(&db.pool, &hash, id, now).await.unwrap(),
            consumed,
            "a code expiring at 100, consumed at {}",
            (now - at(0)).whole_seconds()
        );
    }

    db.close().await;
}

#[tokio::test]
async fn an_unknown_code_is_not_consumed() {
    let db = common::TestDb::new().await;
    let id = account(&db.pool).await;
    code(&db.pool).await;

    let (_, unknown) = secret_token::generate();
    assert!(
        !invite::consume(&db.pool, &unknown, id, at(10))
            .await
            .unwrap()
    );

    db.close().await;
}

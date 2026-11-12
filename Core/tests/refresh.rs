//! Refresh: the SQL that rotates a session, on a real Postgres as the app
//! role. See "Marking a token used is one conditional update", "Nothing about
//! a session is deleted in normal operation" and "The refresh token lasts
//! fourteen days idle, and the session thirty days in total" in
//! docs/core-architecture.md.

mod common;

use platform_core::{secret_token, sessions};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

fn at(seconds: i64) -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
}

fn day(days: i64) -> OffsetDateTime {
    at(0) + Duration::days(days)
}

async fn account(pool: &PgPool, status: &str) -> Uuid {
    let id = Uuid::now_v7();
    sqlx::query(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES ($1, $2, '$argon2id$placeholder', $3, now())",
    )
    .bind(id)
    .bind(format!("{id}@example.com"))
    .bind(status)
    .execute(pool)
    .await
    .unwrap();
    id
}

// The stored row behind a token handed out.
async fn row_of(pool: &PgPool, refresh_token: &str) -> sessions::Row {
    let hash = secret_token::hash(refresh_token).unwrap();
    sessions::find(pool, &hash)
        .await
        .unwrap()
        .expect("no row for the token")
}

async fn used_at(pool: &PgPool, id: Uuid) -> Option<OffsetDateTime> {
    sqlx::query_scalar("SELECT used_at FROM sessions WHERE id = $1")
        .bind(id)
        .fetch_one(pool)
        .await
        .unwrap()
}

async fn count(pool: &PgPool) -> i64 {
    sqlx::query_scalar("SELECT count(*) FROM sessions")
        .fetch_one(pool)
        .await
        .unwrap()
}

#[tokio::test]
async fn a_token_is_found_by_its_hash_with_its_accounts_status() {
    let db = common::TestDb::new().await;
    let active = account(&db.pool, "active").await;
    let disabled = account(&db.pool, "disabled").await;
    let live = sessions::start(&db.pool, active, at(0)).await.unwrap();
    let gone = sessions::start(&db.pool, disabled, at(0)).await.unwrap();

    let row = row_of(&db.pool, &live.refresh_token).await;
    assert_eq!(row.account_id, active);
    assert_eq!(row.family_id, live.family_id);
    assert_eq!(row.expires_at, live.expires_at);
    assert_eq!(row.family_expires_at, day(30));
    assert_eq!((row.used_at, row.revoked_at), (None, None));
    assert!(row.account_active);
    assert!(!row_of(&db.pool, &gone.refresh_token).await.account_active);

    let (_, unknown) = secret_token::generate();
    assert_eq!(sessions::find(&db.pool, &unknown).await.unwrap(), None);

    db.close().await;
}

// Deterministic rather than a race: the second run of the same update is what
// the losing one of two concurrent refreshes would be.
#[tokio::test]
async fn marking_a_token_used_twice_changes_nothing_the_second_time() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, at(0)).await.unwrap();
    let id = row_of(&db.pool, &started.refresh_token).await.id;

    let first = sessions::mark_used(&db.pool, id, at(10)).await.unwrap();
    let first = first.expect("the first mark changed nothing");
    assert_eq!(first.account_id, account_id);
    assert_eq!(first.family_id, started.family_id);
    assert_eq!(first.family_expires_at, day(30));

    let second = sessions::mark_used(&db.pool, id, at(20)).await.unwrap();
    assert!(second.is_none(), "the second mark changed the row");
    assert_eq!(used_at(&db.pool, id).await, Some(at(10)));

    db.close().await;
}

#[tokio::test]
async fn a_revoked_token_is_not_marked_used() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, at(0)).await.unwrap();
    let id = row_of(&db.pool, &started.refresh_token).await.id;
    sqlx::query("UPDATE sessions SET revoked_at = $2 WHERE id = $1")
        .bind(id)
        .bind(at(5))
        .execute(&db.pool)
        .await
        .unwrap();

    assert!(
        sessions::mark_used(&db.pool, id, at(10))
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(used_at(&db.pool, id).await, None);

    db.close().await;
}

#[tokio::test]
async fn a_rotation_leaves_the_old_row_used_and_a_new_live_row_in_the_family() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, day(0)).await.unwrap();
    let first = row_of(&db.pool, &started.refresh_token).await;

    // A day in, fourteen days out is before the cap.
    let second_issued = sessions::rotate(&db.pool, first.id, day(1))
        .await
        .unwrap()
        .expect("a live token did not rotate");
    assert_eq!(used_at(&db.pool, first.id).await, Some(day(1)));
    let second = row_of(&db.pool, &second_issued.refresh_token).await;
    assert_ne!(second.id, first.id);
    assert_eq!(second.account_id, account_id);
    assert_eq!(second.family_id, started.family_id);
    assert_eq!(second_issued.family_id, started.family_id);
    assert_eq!(second.expires_at, day(15));
    assert_eq!(second_issued.expires_at, day(15));
    assert_eq!(second.family_expires_at, day(30));
    assert_eq!((second.used_at, second.revoked_at), (None, None));
    let issued_at: OffsetDateTime =
        sqlx::query_scalar("SELECT issued_at FROM sessions WHERE id = $1")
            .bind(second.id)
            .fetch_one(&db.pool)
            .await
            .unwrap();
    assert_eq!(issued_at, day(1));

    // Twenty days in, fourteen days out is past the cap, so the cap wins and
    // stays where the login put it.
    let third_issued = sessions::rotate(&db.pool, second.id, day(20))
        .await
        .unwrap()
        .expect("a live token did not rotate");
    assert_eq!(used_at(&db.pool, second.id).await, Some(day(20)));
    let third = row_of(&db.pool, &third_issued.refresh_token).await;
    assert_eq!(third.family_id, started.family_id);
    assert_eq!(third.expires_at, day(30));
    assert_eq!(third.family_expires_at, day(30));

    // A used token rotates nothing, and the failed attempt leaves no row.
    assert!(
        sessions::rotate(&db.pool, first.id, day(21))
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(used_at(&db.pool, first.id).await, Some(day(1)));

    // Every row is still there: one per token the family has had.
    assert_eq!(count(&db.pool).await, 3);

    db.close().await;
}

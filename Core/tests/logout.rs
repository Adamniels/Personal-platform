//! Logout: the SQL that revokes a session family, on a real Postgres as the
//! app role. See "The session lives in two cookies" and "Nothing about a
//! session is deleted in normal operation" in docs/core-architecture.md.

mod common;

use platform_core::{secret_token, sessions};
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

// The stored row behind a token handed out.
async fn row_of(pool: &PgPool, refresh_token: &str) -> sessions::Row {
    let hash = secret_token::hash(refresh_token).unwrap();
    sessions::find(pool, &hash)
        .await
        .unwrap()
        .expect("no row for the token")
}

// The revoked time of every row of `family_id`, oldest row first.
async fn revoked_in(pool: &PgPool, family_id: Uuid) -> Vec<Option<OffsetDateTime>> {
    sqlx::query_scalar("SELECT revoked_at FROM sessions WHERE family_id = $1 ORDER BY id")
        .bind(family_id)
        .fetch_all(pool)
        .await
        .unwrap()
}

// Every column of every row, to compare before and after a request that must
// change nothing.
async fn snapshot(pool: &PgPool) -> Vec<String> {
    sqlx::query_scalar("SELECT s::text FROM sessions s ORDER BY id")
        .fetch_all(pool)
        .await
        .unwrap()
}

// A family of two rows, the first used, the second live, started at `now`.
async fn rotated_family(pool: &PgPool, account_id: Uuid, now: OffsetDateTime) -> (String, String) {
    let started = sessions::start(pool, account_id, now).await.unwrap();
    let first = row_of(pool, &started.refresh_token).await;
    let next = sessions::rotate(pool, first.id, now)
        .await
        .unwrap()
        .expect("a live token did not rotate");
    (started.refresh_token, next.refresh_token)
}

#[tokio::test]
async fn revoking_a_family_marks_every_row_and_keeps_the_first_time() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool).await;
    let (first, _) = rotated_family(&db.pool, account_id, at(0)).await;
    let family_id = row_of(&db.pool, &first).await.family_id;
    let other = sessions::start(&db.pool, account_id, at(0)).await.unwrap();

    let revoked = sessions::revoke_family(&db.pool, family_id, at(100))
        .await
        .unwrap();

    assert_eq!(revoked, 2, "rows revoked");
    assert_eq!(
        revoked_in(&db.pool, family_id).await,
        vec![Some(at(100)), Some(at(100))]
    );
    assert_eq!(
        revoked_in(&db.pool, other.family_id).await,
        vec![None],
        "another family of the same account was revoked"
    );

    // Revoking again changes nothing: each row keeps the time it was first
    // revoked.
    let again = sessions::revoke_family(&db.pool, family_id, at(200))
        .await
        .unwrap();
    assert_eq!(again, 0, "rows revoked the second time");
    assert_eq!(
        revoked_in(&db.pool, family_id).await,
        vec![Some(at(100)), Some(at(100))]
    );

    // Nothing is deleted.
    assert_eq!(snapshot(&db.pool).await.len(), 3);

    db.close().await;
}

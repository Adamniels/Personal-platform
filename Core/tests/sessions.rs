//! What the sessions table guarantees by itself, as the app role, see "The
//! core's data" and "The refresh token lasts fourteen days idle, and the
//! session thirty days in total" in docs/core-architecture.md.

mod common;

use platform_core::{secret_token, sessions};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
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

fn now() -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap()
}

// A login starts a family: one row, its token stored only as a hash,
// expiring fourteen days out within a thirty day family.
#[tokio::test]
async fn a_login_stores_a_new_family_with_both_expiries() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool).await;

    let started = sessions::start(&db.pool, account_id, now()).await.unwrap();

    type Row = (
        Uuid,
        Uuid,
        Vec<u8>,
        OffsetDateTime,
        OffsetDateTime,
        OffsetDateTime,
        Option<OffsetDateTime>,
        Option<OffsetDateTime>,
    );
    let (
        stored_account,
        family_id,
        token_hash,
        issued_at,
        expires_at,
        family_expires_at,
        used_at,
        revoked_at,
    ): Row = sqlx::query_as(
        "SELECT account_id, family_id, token_hash, issued_at, expires_at, family_expires_at,
                    used_at, revoked_at
             FROM sessions",
    )
    .fetch_one(&db.pool)
    .await
    .unwrap();

    assert_eq!(stored_account, account_id);
    assert_eq!(family_id, started.family_id);
    assert_eq!(
        token_hash,
        secret_token::hash(&started.refresh_token).unwrap()
    );
    assert_eq!(issued_at, now());
    assert_eq!(expires_at, now() + Duration::days(14));
    assert_eq!(started.expires_at, expires_at);
    assert_eq!(family_expires_at, now() + Duration::days(30));
    assert_eq!((used_at, revoked_at), (None, None));

    db.close().await;
}

// The revoked time of every row of `family_id`, oldest row first.
async fn revoked_in(pool: &PgPool, family_id: Uuid) -> Vec<Option<OffsetDateTime>> {
    sqlx::query_scalar("SELECT revoked_at FROM sessions WHERE family_id = $1 ORDER BY id")
        .bind(family_id)
        .fetch_all(pool)
        .await
        .unwrap()
}

// A family of two rows, the first used and the second live.
async fn rotated_family(pool: &PgPool, account_id: Uuid) -> Uuid {
    let started = sessions::start(pool, account_id, now()).await.unwrap();
    let hash = secret_token::hash(&started.refresh_token).unwrap();
    let first = sessions::find(pool, &hash).await.unwrap().unwrap();
    sessions::rotate(pool, first.id, now())
        .await
        .unwrap()
        .expect("a live token did not rotate");
    started.family_id
}

// What a reset revokes, and what changing a password while logged in will:
// every family of the account, or every one but the family it is sitting in.
// A row already revoked keeps the time it was first revoked, as revoke_family
// does, and another account's sessions are never touched.
#[tokio::test]
async fn revoking_an_account_spares_only_the_named_family() {
    let db = common::TestDb::new().await;
    let account_id = account(&db.pool).await;
    let other_account = account(&db.pool).await;
    let rotated = rotated_family(&db.pool, account_id).await;
    let kept = sessions::start(&db.pool, account_id, now())
        .await
        .unwrap()
        .family_id;
    let third = sessions::start(&db.pool, account_id, now())
        .await
        .unwrap()
        .family_id;
    let earlier = sessions::start(&db.pool, account_id, now())
        .await
        .unwrap()
        .family_id;
    sessions::revoke_family(&db.pool, earlier, now() + Duration::seconds(5))
        .await
        .unwrap();
    let others = sessions::start(&db.pool, other_account, now())
        .await
        .unwrap()
        .family_id;
    let first = now() + Duration::seconds(100);
    let second = now() + Duration::seconds(200);

    let mut connection = db.pool.acquire().await.unwrap();
    let revoked = sessions::revoke_account(&mut connection, account_id, Some(kept), first)
        .await
        .unwrap();

    assert_eq!(revoked, 3, "rows revoked sparing {kept}");
    assert_eq!(revoked_in(&db.pool, rotated).await, vec![Some(first); 2]);
    assert_eq!(revoked_in(&db.pool, third).await, vec![Some(first)]);
    assert_eq!(
        revoked_in(&db.pool, kept).await,
        vec![None],
        "the named family was revoked"
    );
    assert_eq!(
        revoked_in(&db.pool, earlier).await,
        vec![Some(now() + Duration::seconds(5))],
        "an already revoked row lost its first time"
    );

    let revoked = sessions::revoke_account(&mut connection, account_id, None, second)
        .await
        .unwrap();

    assert_eq!(revoked, 1, "rows revoked with no exception");
    assert_eq!(revoked_in(&db.pool, kept).await, vec![Some(second)]);
    assert_eq!(revoked_in(&db.pool, rotated).await, vec![Some(first); 2]);
    assert_eq!(revoked_in(&db.pool, third).await, vec![Some(first)]);
    assert_eq!(
        revoked_in(&db.pool, earlier).await,
        vec![Some(now() + Duration::seconds(5))]
    );
    assert_eq!(
        revoked_in(&db.pool, others).await,
        vec![None],
        "another account's session was revoked"
    );

    drop(connection);
    db.close().await;
}

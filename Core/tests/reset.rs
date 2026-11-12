//! Resetting a password with a reset token, on a real Postgres as the app
//! role: the token, the password and the revocation as one transaction, and
//! a refresh that races it. See "Password reset, and changing a password" and
//! "Rotating and revoking take the family's lock first" in
//! docs/core-architecture.md.

mod common;

use platform_core::login::{self, Outcome as LogIn};
use platform_core::registry::Registry;
use platform_core::reset::{self, Outcome};
use platform_core::signing_key::SigningKey;
use platform_core::{
    AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, accounts, password, reset_token, secret_token,
    sessions,
};
use sqlx::{Connection, PgPool};
use time::OffsetDateTime;
use uuid::Uuid;

const OLD: &str = "the old password, long enough";
const NEW: &str = "the new password, long enough";

fn at(seconds: i64) -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
}

async fn state(pool: PgPool) -> AppState {
    AppState::new(
        pool,
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap()
}

// An account with OLD as its password, and its email.
async fn account(pool: &PgPool) -> (Uuid, String) {
    let id = Uuid::now_v7();
    let email = format!("{id}@example.com");
    let hash = password::hash(OLD).await.unwrap();
    assert!(
        accounts::create(pool, id, &email, &hash, at(0))
            .await
            .unwrap()
    );
    (id, email)
}

// The revoked time of every row of `family_id`, oldest row first.
async fn revoked_in(pool: &PgPool, family_id: Uuid) -> Vec<Option<OffsetDateTime>> {
    sqlx::query_scalar("SELECT revoked_at FROM sessions WHERE family_id = $1 ORDER BY id")
        .bind(family_id)
        .fetch_all(pool)
        .await
        .unwrap()
}

async fn row_of(pool: &PgPool, refresh_token: &str) -> sessions::Row {
    let hash = secret_token::hash(refresh_token).unwrap();
    sessions::find(pool, &hash)
        .await
        .unwrap()
        .expect("no row for the token")
}

// A family of two rows, the first used, the second live; returns the live
// token.
async fn rotated_family(pool: &PgPool, account_id: Uuid) -> String {
    let started = sessions::start(pool, account_id, at(0)).await.unwrap();
    let first = row_of(pool, &started.refresh_token).await;
    sessions::rotate(pool, first.id, at(0))
        .await
        .unwrap()
        .expect("a live token did not rotate")
        .refresh_token
}

async fn logs_in(state: &AppState, email: &str, password: &str) -> bool {
    match login::log_in(state, email, password, OffsetDateTime::now_utc())
        .await
        .unwrap()
    {
        LogIn::LoggedIn { .. } => true,
        LogIn::Refused => false,
    }
}

#[tokio::test]
async fn a_reset_sets_the_password_and_revokes_every_family() {
    let db = common::TestDb::new().await;
    let state = state(db.pool.clone()).await;
    let (id, email) = account(&db.pool).await;
    let (other, _) = account(&db.pool).await;
    let rotated = row_of(&db.pool, &rotated_family(&db.pool, id).await)
        .await
        .family_id;
    let single = sessions::start(&db.pool, id, at(0))
        .await
        .unwrap()
        .family_id;
    let others = sessions::start(&db.pool, other, at(0))
        .await
        .unwrap()
        .family_id;
    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();

    let outcome = reset::reset(&db.pool, &token, NEW, at(10)).await.unwrap();

    assert_eq!(outcome, Outcome::Reset);
    assert_eq!(revoked_in(&db.pool, rotated).await, vec![Some(at(10)); 2]);
    assert_eq!(revoked_in(&db.pool, single).await, vec![Some(at(10))]);
    assert_eq!(
        revoked_in(&db.pool, others).await,
        vec![None],
        "another account's session was revoked"
    );
    assert!(
        !logs_in(&state, &email, OLD).await,
        "the old password works"
    );
    assert!(logs_in(&state, &email, NEW).await, "the new password fails");

    db.close().await;
}

// The token is the trust, not the account's state: a disabled account can be
// given a new password, and getting one does not enable it.
#[tokio::test]
async fn a_reset_works_for_a_disabled_account_and_leaves_it_disabled() {
    let db = common::TestDb::new().await;
    let (id, _) = account(&db.pool).await;
    let mut admin = db.admin().await;
    sqlx::query("UPDATE accounts SET status = 'disabled' WHERE id = $1")
        .bind(id)
        .execute(&mut admin)
        .await
        .unwrap();
    admin.close().await.unwrap();
    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();

    let outcome = reset::reset(&db.pool, &token, NEW, at(10)).await.unwrap();

    assert_eq!(outcome, Outcome::Reset);
    let (status, hash): (String, String) =
        sqlx::query_as("SELECT status, password_hash FROM accounts WHERE id = $1")
            .bind(id)
            .fetch_one(&db.pool)
            .await
            .unwrap();
    assert_eq!(status, "disabled");
    assert!(password::verify(NEW, &hash).await.unwrap());

    db.close().await;
}

// A refresh that commits while a reset of its account runs. Under READ
// COMMITTED a revoke whose snapshot predates the refresh's new row leaves it
// live, so a session survives the reset that was meant to end it. Built as
// a_refresh_committing_during_a_logout_is_revoked_too in logout.rs.
#[tokio::test]
async fn a_refresh_committing_during_a_reset_is_revoked_too() {
    let db = common::TestDb::new().await;
    let (id, _) = account(&db.pool).await;
    let live = rotated_family(&db.pool, id).await;
    let live_row = row_of(&db.pool, &live).await;
    let token = reset_token::create(&db.pool, id, at(0)).await.unwrap();

    // A rotation of the live token part way through, as sessions::rotate
    // does it: the token marked used and its successor stored, uncommitted.
    let mut rotation = db.pool.begin().await.unwrap();
    sessions::lock_family(&mut *rotation, live_row.id)
        .await
        .unwrap();
    let marked = sessions::mark_used(&mut *rotation, live_row.id, at(10))
        .await
        .unwrap()
        .expect("the live token did not mark");
    let (_, next_hash) = secret_token::generate();
    sqlx::query(
        "INSERT INTO sessions
             (id, account_id, family_id, token_hash, issued_at, expires_at, family_expires_at)
         VALUES ($1, $2, $3, $4, $5, $6, $7)",
    )
    .bind(Uuid::now_v7())
    .bind(marked.account_id)
    .bind(marked.family_id)
    .bind(&next_hash[..])
    .bind(at(10))
    .bind(sessions::token_expiry(at(10), marked.family_expires_at))
    .bind(marked.family_expires_at)
    .execute(&mut *rotation)
    .await
    .unwrap();

    let pool = db.pool.clone();
    let resetting = tokio::spawn(async move { reset::reset(&pool, &token, NEW, at(20)).await });
    common::until_one_waits_on_a_lock(&db, "the reset did not wait for the rotation").await;
    rotation.commit().await.unwrap();
    assert_eq!(resetting.await.unwrap().unwrap(), Outcome::Reset);

    let revoked = revoked_in(&db.pool, live_row.family_id).await;
    assert_eq!(revoked.len(), 3);
    assert!(
        revoked.iter().all(Option::is_some),
        "the row the refresh stored during the reset was left live: {revoked:?}"
    );

    db.close().await;
}

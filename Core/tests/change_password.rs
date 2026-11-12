//! Changing a password while logged in, on a real Postgres as the app role:
//! the password and the revocation of every other family as one
//! transaction, keeping the family the session belongs to. See "Password
//! reset, and changing a password" in docs/core-architecture.md.

mod common;

use platform_core::change_password::{self, Outcome};
use platform_core::login::{self, Outcome as LogIn};
use platform_core::registry::Registry;
use platform_core::session::Session;
use platform_core::signing_key::SigningKey;
use platform_core::{
    AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, accounts, password, secret_token, sessions,
};
use sqlx::PgPool;
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

// A family of two rows, the first used, the second live; returns its id.
async fn rotated_family(pool: &PgPool, account_id: Uuid) -> Uuid {
    let started = sessions::start(pool, account_id, at(0)).await.unwrap();
    let hash = secret_token::hash(&started.refresh_token).unwrap();
    let first = sessions::find(pool, &hash).await.unwrap().unwrap();
    sessions::rotate(pool, first.id, at(0))
        .await
        .unwrap()
        .expect("a live token did not rotate");
    started.family_id
}

// The revoked time of every row of `family_id`, oldest row first.
async fn revoked_in(pool: &PgPool, family_id: Uuid) -> Vec<Option<OffsetDateTime>> {
    sqlx::query_scalar("SELECT revoked_at FROM sessions WHERE family_id = $1 ORDER BY id")
        .bind(family_id)
        .fetch_all(pool)
        .await
        .unwrap()
}

// The session a request carrying an access token of `family_id` would have.
// The token itself plays no part in a change.
fn session_of(account_id: Uuid, family_id: Uuid) -> Session {
    Session {
        account_id,
        family_id,
        access_token: String::new(),
    }
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
async fn changing_the_password_revokes_other_families_and_keeps_this_one() {
    let db = common::TestDb::new().await;
    let state = state(db.pool.clone()).await;
    let (id, email) = account(&db.pool).await;
    let (other, _) = account(&db.pool).await;
    // Both the current family and one of the others have rotated, so "every
    // row" means more than one.
    let current = rotated_family(&db.pool, id).await;
    let rotated = rotated_family(&db.pool, id).await;
    let single = sessions::start(&db.pool, id, at(0))
        .await
        .unwrap()
        .family_id;
    let others = sessions::start(&db.pool, other, at(0))
        .await
        .unwrap()
        .family_id;

    let outcome = change_password::change(&state, &session_of(id, current), OLD, NEW, at(10))
        .await
        .unwrap();

    assert_eq!(outcome, Outcome::Changed);
    assert_eq!(revoked_in(&db.pool, rotated).await, vec![Some(at(10)); 2]);
    assert_eq!(revoked_in(&db.pool, single).await, vec![Some(at(10))]);
    assert_eq!(
        revoked_in(&db.pool, current).await,
        vec![None; 2],
        "the session the change was made in was revoked"
    );
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

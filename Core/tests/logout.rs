//! Logout: the SQL that revokes a session family, on a real Postgres as the
//! app role, and the endpoint over HTTP, through the real router on a real
//! socket, raw HTTP/1.1 as in refresh.rs. See "The session lives in two
//! cookies" and "Nothing about a session is deleted in normal operation" in
//! docs/core-architecture.md.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::refresh::{self, Outcome};
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, secret_token, sessions};
use sqlx::PgPool;
use std::collections::BTreeSet;
use time::OffsetDateTime;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";

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

// Over HTTP. The handler reads the clock, so the sessions here start at the
// real time.

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

async fn serve(pool: PgPool) -> (std::net::SocketAddr, AppState) {
    let state = state(pool).await;
    let app = platform_core::app(state.clone(), PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    (addr, state)
}

struct Response {
    status: u16,
    headers: Vec<(String, String)>,
    body: String,
}

impl Response {
    fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(n, _)| n == name)
            .map(|(_, v)| v.as_str())
    }

    fn all(&self, name: &str) -> Vec<&str> {
        self.headers
            .iter()
            .filter(|(n, _)| n == name)
            .map(|(_, v)| v.as_str())
            .collect()
    }
}

async fn send(addr: std::net::SocketAddr, request: String) -> Response {
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut raw = String::new();
    stream.read_to_string(&mut raw).await.unwrap();

    let (head, body) = raw.split_once("\r\n\r\n").unwrap();
    let mut lines = head.split("\r\n");
    let status = lines
        .next()
        .unwrap()
        .split(' ')
        .nth(1)
        .unwrap()
        .parse()
        .unwrap();
    let headers = lines
        .map(|line| {
            let (name, value) = line.split_once(": ").unwrap();
            (name.to_lowercase(), value.to_owned())
        })
        .collect();
    Response {
        status,
        headers,
        body: body.to_owned(),
    }
}

// The refresh cookie as a request header, when there is one.
fn cookie_header(refresh_token: Option<&str>) -> String {
    refresh_token
        .map(|token| format!("Cookie: __Secure-refresh={token}\r\n"))
        .unwrap_or_default()
}

// POSTs the logout form, which has no fields, with the refresh cookie if
// given and `origin` as the Origin header if given.
async fn post(
    addr: std::net::SocketAddr,
    refresh_token: Option<&str>,
    origin: Option<&str>,
) -> Response {
    let origin = origin
        .map(|origin| format!("Origin: {origin}\r\n"))
        .unwrap_or_default();
    send(
        addr,
        format!(
            "POST /auth/logout HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
             {origin}{}Content-Type: application/x-www-form-urlencoded\r\n\
             Content-Length: 0\r\n\r\n",
            cookie_header(refresh_token),
        ),
    )
    .await
}

// A Set-Cookie header as its name, value and the set of its attributes.
fn cookie(header: &str) -> (String, String, BTreeSet<String>) {
    let mut parts = header.split("; ");
    let (name, value) = parts.next().unwrap().split_once('=').unwrap();
    (
        name.to_owned(),
        value.to_owned(),
        parts.map(str::to_owned).collect(),
    )
}

// The Set-Cookie header naming `name`, in whatever order they were sent.
fn named<'a>(response: &'a Response, name: &str) -> &'a str {
    response
        .all("set-cookie")
        .into_iter()
        .find(|header| header.starts_with(&format!("{name}=")))
        .unwrap_or_else(|| panic!("no {name} cookie in {:?}", response.headers))
}

fn attributes(list: &[&str]) -> BTreeSet<String> {
    list.iter().map(|a| a.to_string()).collect()
}

// A 303 to /login clearing both cookies, each on the path it was set on: a
// clear on the wrong one leaves the cookie alive while looking like it worked.
fn assert_cleared_to_login(response: &Response, case: &str) {
    assert_eq!(response.status, 303, "{case}: {}", response.body);
    assert_eq!(response.header("location"), Some("/login"), "{case}");
    assert_eq!(response.all("set-cookie").len(), 2, "{case}");
    for (name, path) in [("__Host-access", "/"), ("__Secure-refresh", "/auth")] {
        let (_, value, attrs) = cookie(named(response, name));
        assert_eq!(value, "", "{case}: {name}");
        assert_eq!(
            attrs,
            attributes(&[
                "HttpOnly",
                "SameSite=Lax",
                "Secure",
                &format!("Path={path}"),
                "Max-Age=0"
            ]),
            "{case}: {name}"
        );
    }
}

#[tokio::test]
async fn logout_revokes_the_family_and_clears_both_cookies() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool).await;
    let now = OffsetDateTime::now_utc();
    let (_, newest) = rotated_family(&db.pool, account_id, now).await;
    let family_id = row_of(&db.pool, &newest).await.family_id;
    let other = sessions::start(&db.pool, account_id, now).await.unwrap();

    let response = post(addr, Some(&newest), Some(PLATFORM)).await;

    assert_cleared_to_login(&response, "live token");
    let revoked = revoked_in(&db.pool, family_id).await;
    assert_eq!(revoked.len(), 2);
    assert!(
        revoked.iter().all(Option::is_some),
        "a row of the family was left unrevoked: {revoked:?}"
    );
    assert_eq!(
        revoked_in(&db.pool, other.family_id).await,
        vec![None],
        "another session of the account was logged out"
    );

    db.close().await;
}

// The family is found by the presented token whatever state its row is in,
// so a browser holding a token another tab already rotated still logs out.
#[tokio::test]
async fn logout_with_a_used_refresh_token_still_revokes_its_family() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool).await;
    let (used, newest) = rotated_family(&db.pool, account_id, OffsetDateTime::now_utc()).await;
    assert!(row_of(&db.pool, &used).await.used_at.is_some());

    let response = post(addr, Some(&used), Some(PLATFORM)).await;

    assert_cleared_to_login(&response, "used token");
    assert!(
        row_of(&db.pool, &newest).await.revoked_at.is_some(),
        "the family's live token was left unrevoked"
    );
    assert!(row_of(&db.pool, &used).await.revoked_at.is_some());

    db.close().await;
}

#[tokio::test]
async fn logout_without_a_known_refresh_token_only_clears_and_redirects() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool).await;
    sessions::start(&db.pool, account_id, OffsetDateTime::now_utc())
        .await
        .unwrap();
    let before = snapshot(&db.pool).await;
    let (unknown, _) = secret_token::generate();

    for (case, token) in [
        ("no cookie", None),
        ("empty cookie", Some("")),
        ("not a token", Some("not-a-token")),
        ("unknown token", Some(unknown.as_str())),
    ] {
        let response = post(addr, token, Some(PLATFORM)).await;
        assert_cleared_to_login(&response, case);
    }

    assert_eq!(snapshot(&db.pool).await, before, "a logout changed a row");

    db.close().await;
}

#[tokio::test]
async fn after_logout_the_refresh_token_goes_to_login() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool).await;
    let (_, newest) = rotated_family(&db.pool, account_id, OffsetDateTime::now_utc()).await;

    let response = post(addr, Some(&newest), Some(PLATFORM)).await;
    assert_eq!(response.status, 303, "{}", response.body);

    let outcome = refresh::refresh(&state, Some(&newest), OffsetDateTime::now_utc())
        .await
        .unwrap();
    assert!(
        matches!(outcome, Outcome::LogIn),
        "the newest token of a logged out family still refreshed"
    );
    assert_eq!(
        row_of(&db.pool, &newest).await.used_at,
        None,
        "the refused refresh used the token up"
    );

    db.close().await;
}

#[tokio::test]
async fn a_logout_post_needs_the_platforms_origin() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool).await;
    let started = sessions::start(&db.pool, account_id, OffsetDateTime::now_utc())
        .await
        .unwrap();
    let before = snapshot(&db.pool).await;

    for origin in [
        None,
        Some("https://evil.test"),
        Some("https://platform.test.evil.test"),
    ] {
        let response = post(addr, Some(&started.refresh_token), origin).await;
        assert_eq!(response.status, 403, "{origin:?}");
        assert!(response.all("set-cookie").is_empty(), "{origin:?}");
    }

    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused post changed a row"
    );
    assert_eq!(
        row_of(&db.pool, &started.refresh_token).await.revoked_at,
        None
    );

    db.close().await;
}

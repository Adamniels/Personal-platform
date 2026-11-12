//! The change password page over HTTP, through the real router on a real
//! socket and a test database: what a browser gets back. Raw HTTP/1.1, as in
//! reset_form.rs. See "Password reset, and changing a password" in
//! docs/core-architecture.md.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, accounts, password, sessions};
use sqlx::{Connection, PgPool};
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";
const PATH: &str = "/settings/password";
// Form encoding needs nothing escaped in these, so they are searched for as
// they are.
const OLD: &str = "the-old-password-long-enough";
const NEW: &str = "the-new-password-long-enough";
const WRONG: &str = "not-the-password-at-all";
const TOO_SHORT: &str = "short-password";
const INCORRECT: &str = "Current password is incorrect.";
const INVALID_PASSWORD: &str = "Choose a password of 15 to 1024 characters.";
const CHANGED: &str = "Password changed. Your other sessions were signed out.";

async fn serve(pool: PgPool) -> (std::net::SocketAddr, AppState) {
    let state = AppState::new(
        pool,
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap();
    let app = platform_core::app(state.clone(), PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    (addr, state)
}

// An account with OLD as its password and two sessions, both made now, and
// an access token for the first: a person logged in from two places.
async fn logged_in(pool: &PgPool, state: &AppState) -> (Uuid, String) {
    let id = Uuid::now_v7();
    let hash = password::hash(OLD).await.unwrap();
    let now = OffsetDateTime::now_utc();
    assert!(
        accounts::create(pool, id, &format!("{id}@example.com"), &hash, now)
            .await
            .unwrap()
    );
    let here = sessions::start(pool, id, now).await.unwrap();
    sessions::start(pool, id, now).await.unwrap();
    let token = state.issuer.issue(id, here.family_id, Uuid::now_v7(), now);
    (id, token)
}

// Every column of every row a change could touch, to compare before and
// after a request that must change nothing.
async fn snapshot(pool: &PgPool) -> Vec<String> {
    sqlx::query_scalar(
        "SELECT a::text FROM accounts a
         UNION ALL SELECT s::text FROM sessions s
         ORDER BY 1",
    )
    .fetch_all(pool)
    .await
    .unwrap()
}

async fn live_families(pool: &PgPool, id: Uuid) -> i64 {
    sqlx::query_scalar(
        "SELECT count(DISTINCT family_id) FROM sessions
         WHERE account_id = $1 AND revoked_at IS NULL",
    )
    .bind(id)
    .fetch_one(pool)
    .await
    .unwrap()
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

fn cookie(token: Option<&str>) -> String {
    token
        .map(|token| format!("Cookie: __Host-access={token}\r\n"))
        .unwrap_or_default()
}

async fn get(addr: std::net::SocketAddr, target: &str, token: Option<&str>) -> Response {
    let cookie = cookie(token);
    send(
        addr,
        format!(
            "GET {target} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n{cookie}\r\n"
        ),
    )
    .await
}

// Posts the form as a browser would, with the access cookie set to `token`
// and `origin` as the Origin header, each if given.
async fn post_from(
    addr: std::net::SocketAddr,
    token: Option<&str>,
    current: &str,
    new: &str,
    origin: Option<&str>,
) -> Response {
    let body = format!("current_password={current}&new_password={new}");
    let cookie = cookie(token);
    let origin = origin
        .map(|origin| format!("Origin: {origin}\r\n"))
        .unwrap_or_default();
    send(
        addr,
        format!(
            "POST {PATH} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
             {cookie}{origin}Content-Type: application/x-www-form-urlencoded\r\n\
             Content-Length: {}\r\n\r\n{body}",
            body.len()
        ),
    )
    .await
}

async fn post(addr: std::net::SocketAddr, token: &str, current: &str, new: &str) -> Response {
    post_from(addr, Some(token), current, new, Some(PLATFORM)).await
}

#[tokio::test]
async fn a_change_answers_with_the_confirmation_and_keeps_this_session() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (id, token) = logged_in(&db.pool, &state).await;

    let response = post(addr, &token, OLD, NEW).await;

    assert_eq!(response.status, 303, "{}", response.body);
    assert_eq!(
        response.header("location"),
        Some("/settings/password?changed=1")
    );
    assert_eq!(
        response.header("set-cookie"),
        None,
        "a change touched the cookies: {:?}",
        response.headers
    );
    assert_eq!(live_families(&db.pool, id).await, 1);

    // The page it lands on says so, with the same access token, which still
    // works; without the parameter it says nothing.
    let page = get(addr, "/settings/password?changed=1", Some(&token)).await;
    assert_eq!(page.status, 200, "{}", page.body);
    assert!(page.body.contains(CHANGED), "{}", page.body);
    let page = get(addr, PATH, Some(&token)).await;
    assert_eq!(page.status, 200, "{}", page.body);
    assert!(!page.body.contains(CHANGED), "{}", page.body);

    db.close().await;
}

#[tokio::test]
async fn a_wrong_current_password_changes_nothing() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (_, token) = logged_in(&db.pool, &state).await;
    let before = snapshot(&db.pool).await;

    let response = post(addr, &token, WRONG, NEW).await;

    assert_eq!(response.status, 422, "{}", response.body);
    assert!(response.body.contains(INCORRECT), "{}", response.body);
    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused change changed a row"
    );

    db.close().await;
}

#[tokio::test]
async fn a_new_password_outside_the_rule_changes_nothing() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (_, token) = logged_in(&db.pool, &state).await;
    let before = snapshot(&db.pool).await;

    let too_long = "x".repeat(1025);
    for new in ["", TOO_SHORT, too_long.as_str()] {
        let response = post(addr, &token, OLD, new).await;
        assert_eq!(response.status, 422, "{}", response.body);
        assert!(
            response.body.contains(INVALID_PASSWORD),
            "{}",
            response.body
        );
    }
    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused change changed a row"
    );

    db.close().await;
}

// A disabled account still holding a valid access token, until it expires,
// gets the answer a wrong password gets, word for word, whether or not it
// knows its password.
#[tokio::test]
async fn a_disabled_account_cannot_change_its_password() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (id, token) = logged_in(&db.pool, &state).await;
    let wrong = post(addr, &token, WRONG, NEW).await;
    let mut admin = db.admin().await;
    sqlx::query("UPDATE accounts SET status = 'disabled' WHERE id = $1")
        .bind(id)
        .execute(&mut admin)
        .await
        .unwrap();
    admin.close().await.unwrap();
    let before = snapshot(&db.pool).await;

    for current in [OLD, WRONG] {
        let response = post(addr, &token, current, NEW).await;
        assert_eq!(response.status, 422, "{current}: {}", response.body);
        assert_eq!(response.body, wrong.body, "{current}: told apart");
    }
    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused change changed a row"
    );

    db.close().await;
}

#[tokio::test]
async fn the_page_needs_a_session() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (id, _) = logged_in(&db.pool, &state).await;
    let expired = state.issuer.issue(
        id,
        Uuid::now_v7(),
        Uuid::now_v7(),
        OffsetDateTime::now_utc() - Duration::hours(1),
    );
    let before = snapshot(&db.pool).await;
    let back = Some("/auth/refresh?return_to=%2Fsettings%2Fpassword");

    for (case, token) in [("no cookie", None), ("an expired token", Some(&*expired))] {
        let page = get(addr, PATH, token).await;
        assert_eq!(page.status, 303, "GET, {case}");
        assert_eq!(page.header("location"), back, "GET, {case}");

        let response = post_from(addr, token, OLD, NEW, Some(PLATFORM)).await;
        assert_eq!(response.status, 303, "POST, {case}");
        assert_eq!(response.header("location"), back, "POST, {case}");
    }
    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a post without a session changed a row"
    );

    db.close().await;
}

#[tokio::test]
async fn a_change_post_needs_the_platforms_origin() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (_, token) = logged_in(&db.pool, &state).await;
    let before = snapshot(&db.pool).await;

    for origin in [
        None,
        Some("https://evil.test"),
        Some("https://platform.test.evil.test"),
    ] {
        let response = post_from(addr, Some(&token), OLD, NEW, origin).await;
        assert_eq!(response.status, 403, "{origin:?}");
    }
    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused post changed a row"
    );

    db.close().await;
}

#[tokio::test]
async fn the_form_never_echoes_a_password() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let (_, token) = logged_in(&db.pool, &state).await;

    let page = get(addr, PATH, Some(&token)).await;
    assert_eq!(page.status, 200, "{}", page.body);
    let body = &page.body;
    assert!(body.contains("<platform-nav></platform-nav>"), "{body}");
    assert!(
        body.contains(r#"<form method="post" action="/settings/password">"#),
        "{body}"
    );
    assert!(
        body.contains(
            r#"<input name="current_password" type="password" required autocomplete="current-password">"#
        ),
        "{body}"
    );
    assert!(
        body.contains(
            r#"<input name="new_password" type="password" required minlength="15" maxlength="1024" autocomplete="new-password">"#
        ),
        "{body}"
    );
    assert!(body.contains("15 characters or more"), "{body}");
    assert!(
        body.contains(r#"<button type="submit">Change password</button>"#),
        "{body}"
    );
    // Two fields and no third to repeat the new password.
    assert_eq!(body.matches("<input").count(), 2, "{body}");

    for (case, current, new, error, field) in [
        ("wrong current", WRONG, NEW, INCORRECT, "current_password"),
        (
            "short new",
            OLD,
            TOO_SHORT,
            INVALID_PASSWORD,
            "new_password",
        ),
    ] {
        let response = post(addr, &token, current, new).await;
        assert_eq!(response.status, 422, "{case}: {}", response.body);
        assert!(response.body.contains(error), "{case}: {}", response.body);
        common::forms::assert_refused_on(&response.body, field, error);
        assert!(
            !response.body.contains(current),
            "{case}: the current password came back"
        );
        assert!(
            !response.body.contains(new),
            "{case}: the new password came back"
        );
        assert!(
            !response.body.contains("value="),
            "{case}: a field was filled in: {}",
            response.body
        );
    }

    db.close().await;
}

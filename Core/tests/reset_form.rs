//! The reset page over HTTP, through the real router on a real socket and a
//! test database: what a browser gets back. Raw HTTP/1.1, as in logout.rs.
//! See "Password reset, and changing a password" in
//! docs/core-architecture.md.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{
    AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, accounts, password, reset_token, secret_token,
    sessions,
};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";
// Form encoding needs nothing escaped in these, so they are searched for as
// they are.
const OLD: &str = "the-old-password-long-enough";
const NEW: &str = "the-new-password-long-enough";
const TOO_SHORT: &str = "short-password";
const INVALID_TOKEN: &str = "This reset token is not valid.";
const INVALID_PASSWORD: &str = "Choose a password of 15 to 1024 characters.";

async fn serve(pool: PgPool) -> std::net::SocketAddr {
    let state = AppState::new(
        pool,
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap();
    let app = platform_core::app(state, PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    addr
}

// An account with OLD as its password and a session, both made now.
async fn account(pool: &PgPool) -> Uuid {
    let id = Uuid::now_v7();
    let hash = password::hash(OLD).await.unwrap();
    let now = OffsetDateTime::now_utc();
    assert!(
        accounts::create(pool, id, &format!("{id}@example.com"), &hash, now)
            .await
            .unwrap()
    );
    sessions::start(pool, id, now).await.unwrap();
    id
}

async fn fresh_token(pool: &PgPool, id: Uuid) -> String {
    reset_token::create(pool, id, OffsetDateTime::now_utc())
        .await
        .unwrap()
}

// Every column of every row the reset could touch, to compare before and
// after a request that must change nothing.
async fn snapshot(pool: &PgPool) -> Vec<String> {
    sqlx::query_scalar(
        "SELECT a::text FROM accounts a
         UNION ALL SELECT s::text FROM sessions s
         UNION ALL SELECT t::text FROM password_reset_tokens t
         ORDER BY 1",
    )
    .fetch_all(pool)
    .await
    .unwrap()
}

async fn token_used(pool: &PgPool, token: &str) -> bool {
    let hash = secret_token::hash(token).unwrap();
    sqlx::query_scalar::<_, Option<OffsetDateTime>>(
        "SELECT used_at FROM password_reset_tokens WHERE token_hash = $1",
    )
    .bind(&hash[..])
    .fetch_one(pool)
    .await
    .unwrap()
    .is_some()
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

// Posts the form as a browser would, with `origin` as the Origin header if
// given.
async fn post_from(
    addr: std::net::SocketAddr,
    token: &str,
    password: &str,
    origin: Option<&str>,
) -> Response {
    let body = format!("token={token}&password={password}");
    let origin = origin
        .map(|origin| format!("Origin: {origin}\r\n"))
        .unwrap_or_default();
    send(
        addr,
        format!(
            "POST /reset HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
             {origin}Content-Type: application/x-www-form-urlencoded\r\n\
             Content-Length: {}\r\n\r\n{body}",
            body.len()
        ),
    )
    .await
}

async fn post(addr: std::net::SocketAddr, token: &str, password: &str) -> Response {
    post_from(addr, token, password, Some(PLATFORM)).await
}

#[tokio::test]
async fn a_bad_used_or_expired_token_all_get_the_same_answer_and_change_nothing() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let id = account(&db.pool).await;
    let used = fresh_token(&db.pool, id).await;
    reset_token::consume(
        &db.pool,
        &secret_token::hash(&used).unwrap(),
        OffsetDateTime::now_utc(),
    )
    .await
    .unwrap()
    .expect("a fresh token did not consume");
    let expired = reset_token::create(
        &db.pool,
        id,
        OffsetDateTime::now_utc() - reset_token::LIFETIME - Duration::seconds(1),
    )
    .await
    .unwrap();
    let (unknown, _) = secret_token::generate();
    let before = snapshot(&db.pool).await;

    let mut bodies = Vec::new();
    for (case, token) in [
        ("unknown", unknown.as_str()),
        ("malformed", "not-a-token"),
        ("used", used.as_str()),
        ("expired", expired.as_str()),
    ] {
        let response = post(addr, token, NEW).await;
        assert_eq!(response.status, 422, "{case}: {}", response.body);
        assert!(response.body.contains(INVALID_TOKEN), "{case}");
        // The form keeps what was pasted, so the bodies differ only there.
        bodies.push((case, response.body.replace(token, "<token>")));
    }
    for (case, body) in &bodies[1..] {
        assert_eq!(body, &bodies[0].1, "{case} told apart from unknown");
    }

    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused reset changed a row"
    );

    db.close().await;
}

#[tokio::test]
async fn a_password_outside_the_rule_is_refused_before_the_token_is_consumed() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let id = account(&db.pool).await;
    let token = fresh_token(&db.pool, id).await;
    let before = snapshot(&db.pool).await;

    let too_long = "x".repeat(1025);
    for password in [TOO_SHORT, too_long.as_str()] {
        let response = post(addr, &token, password).await;
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
        "a refused reset changed a row"
    );
    assert!(!token_used(&db.pool, &token).await);

    let response = post(addr, &token, NEW).await;
    assert_eq!(response.status, 303, "{}", response.body);
    assert!(token_used(&db.pool, &token).await);

    db.close().await;
}

#[tokio::test]
async fn a_reset_does_not_log_you_in() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let id = account(&db.pool).await;
    let token = fresh_token(&db.pool, id).await;

    let response = post(addr, &token, NEW).await;

    assert_eq!(response.status, 303, "{}", response.body);
    assert_eq!(response.header("location"), Some("/login"));
    assert_eq!(
        response.header("set-cookie"),
        None,
        "a reset set a cookie: {:?}",
        response.headers
    );
    let live: i64 = sqlx::query_scalar(
        "SELECT count(*) FROM sessions WHERE account_id = $1 AND revoked_at IS NULL",
    )
    .bind(id)
    .fetch_one(&db.pool)
    .await
    .unwrap();
    assert_eq!(live, 0, "a reset left or started a live session");

    db.close().await;
}

#[tokio::test]
async fn a_reset_post_needs_the_platforms_origin() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let id = account(&db.pool).await;
    let token = fresh_token(&db.pool, id).await;
    let before = snapshot(&db.pool).await;

    for origin in [
        None,
        Some("https://evil.test"),
        Some("https://platform.test.evil.test"),
    ] {
        let response = post_from(addr, &token, NEW, origin).await;
        assert_eq!(response.status, 403, "{origin:?}");
    }

    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused post changed a row"
    );
    assert!(!token_used(&db.pool, &token).await);

    db.close().await;
}

#[tokio::test]
async fn the_reset_form_keeps_the_token_and_never_echoes_the_password() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let id = account(&db.pool).await;
    let token = fresh_token(&db.pool, id).await;

    let page = send(
        addr,
        "GET /reset HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\r\n".to_owned(),
    )
    .await;
    assert_eq!(page.status, 200, "{}", page.body);
    let body = &page.body;
    assert!(
        body.contains(r#"<form method="post" action="/reset">"#),
        "{body}"
    );
    assert!(
        body.contains(
            r#"<input name="token" value="" required autocomplete="off" spellcheck="false">"#
        ),
        "{body}"
    );
    assert!(
        body.contains(
            r#"<input name="password" type="password" required minlength="15" maxlength="1024" autocomplete="new-password">"#
        ),
        "{body}"
    );
    assert!(body.contains("15 characters or more"), "{body}");
    assert!(!body.contains("<platform-nav"), "the reset page has a nav");

    // Pasted with the whitespace a terminal copy brings along, and shown
    // back trimmed.
    let pasted = format!("%20{token}%0A");
    let (unknown, _) = secret_token::generate();
    for (case, sent, kept, password, error) in [
        (
            "short password",
            pasted.as_str(),
            token.as_str(),
            TOO_SHORT,
            INVALID_PASSWORD,
        ),
        (
            "unknown token",
            unknown.as_str(),
            unknown.as_str(),
            NEW,
            INVALID_TOKEN,
        ),
    ] {
        let response = post(addr, sent, password).await;
        assert_eq!(response.status, 422, "{case}: {}", response.body);
        assert!(response.body.contains(error), "{case}: {}", response.body);
        assert!(
            response.body.contains(&format!(r#"value="{kept}""#)),
            "{case}: the token was not kept: {}",
            response.body
        );
        assert!(
            !response.body.contains(password),
            "{case}: the password came back"
        );
    }

    db.close().await;
}

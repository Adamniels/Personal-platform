//! `GET /api/nav`, what the shared nav bar renders: the registered features
//! and who is logged in, for a request with a valid access token, and 401
//! with nothing else for any other, since the script fetching it must never
//! be redirected. The script that renders the bar is in design/, served by
//! the proxy, not by the core. See "Navigation" in
//! High-level/docs/platform-architecture.md. Raw HTTP/1.1, as in
//! core_pages.rs.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, accounts};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";

const CANARY: &str = r#"
[[feature]]
name = "Canary"
prefix = "/canary"
upstream = "http://127.0.0.1:8081"
"#;

async fn serve(pool: PgPool, registry: &str) -> (std::net::SocketAddr, AppState) {
    let state = AppState::new(
        pool,
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::parse(registry).unwrap(),
    )
    .await
    .unwrap();
    let app = platform_core::app(state.clone(), PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    (addr, state)
}

struct Answer {
    status: u16,
    headers: Vec<(String, String)>,
    body: String,
}

impl Answer {
    fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(_, value)| value.as_str())
    }
}

// GETs `target` with the access cookie set to `token`, if any.
async fn get(addr: std::net::SocketAddr, target: &str, token: Option<&str>) -> Answer {
    let cookie = token
        .map(|token| format!("Cookie: __Host-access={token}\r\n"))
        .unwrap_or_default();
    let request = format!(
        "GET {target} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n{cookie}\r\n"
    );
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
        .filter_map(|line| line.split_once(": "))
        .map(|(name, value)| (name.to_owned(), value.to_owned()))
        .collect();
    Answer {
        status,
        headers,
        body: body.to_owned(),
    }
}

// An active account with `email`, and a valid access token for it.
async fn account(pool: &PgPool, state: &AppState, email: &str) -> String {
    let id = Uuid::now_v7();
    let now = OffsetDateTime::now_utc();
    assert!(
        accounts::create(pool, id, email, "not a real hash", now)
            .await
            .unwrap()
    );
    state.issuer.issue(id, Uuid::now_v7(), Uuid::now_v7(), now)
}

#[tokio::test]
async fn api_nav_lists_the_features_and_the_user() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone(), CANARY).await;
    let token = account(&db.pool, &state, "nav@example.com").await;

    let answer = get(addr, "/api/nav", Some(&token)).await;

    assert_eq!(answer.status, 200, "{}", answer.body);
    assert_eq!(answer.header("content-type"), Some("application/json"));
    // Whose it is depends on the cookie, so no cache may keep it.
    assert_eq!(answer.header("cache-control"), Some("no-store"));
    let json: serde_json::Value = serde_json::from_str(&answer.body).unwrap();
    assert_eq!(
        json,
        serde_json::json!({
            "features": [{"name": "Canary", "path": "/canary"}],
            "user": {"email": "nav@example.com"},
        })
    );

    db.close().await;
}

// The order is the registry file's, which is the order the nav shows.
#[tokio::test]
async fn api_nav_keeps_the_registry_order() {
    let db = common::TestDb::new().await;
    let registry = format!(
        "{}\n{CANARY}",
        r#"
        [[feature]]
        name = "Desk"
        prefix = "/desk"
        upstream = "http://127.0.0.1:9000"
        "#
    );
    let (addr, state) = serve(db.pool.clone(), &registry).await;
    let token = account(&db.pool, &state, "order@example.com").await;

    let answer = get(addr, "/api/nav", Some(&token)).await;

    let json: serde_json::Value = serde_json::from_str(&answer.body).unwrap();
    assert_eq!(
        json["features"],
        serde_json::json!([
            {"name": "Desk", "path": "/desk"},
            {"name": "Canary", "path": "/canary"},
        ])
    );

    db.close().await;
}

// The icon is passed as the registry names it, and left out, not null, for
// a feature that names none, so the nav draws its fallback.
#[tokio::test]
async fn the_nav_gives_each_feature_its_icon() {
    let db = common::TestDb::new().await;
    let registry = format!(
        "{}\n{CANARY}",
        r#"
        [[feature]]
        name = "Review"
        prefix = "/review"
        upstream = "http://127.0.0.1:8083"
        icon = "layers"
        "#
    );
    let (addr, state) = serve(db.pool.clone(), &registry).await;
    let token = account(&db.pool, &state, "icons@example.com").await;

    let answer = get(addr, "/api/nav", Some(&token)).await;

    assert_eq!(answer.status, 200, "{}", answer.body);
    let json: serde_json::Value = serde_json::from_str(&answer.body).unwrap();
    assert_eq!(
        json["features"],
        serde_json::json!([
            {"name": "Review", "path": "/review", "icon": "layers"},
            {"name": "Canary", "path": "/canary"},
        ])
    );

    db.close().await;
}

#[tokio::test]
async fn api_nav_answers_401_without_a_valid_token() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone(), CANARY).await;
    let now = OffsetDateTime::now_utc();
    let ids = || (Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7());

    let (a, f, j) = ids();
    let expired = state.issuer.issue(a, f, j, now - Duration::hours(1));
    let other_issuer = AppState::new(
        db.pool.clone(),
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap()
    .issuer;
    let (a, f, j) = ids();
    let foreign = other_issuer.issue(a, f, j, now);
    // Signed by this core and in date, for an account it never stored.
    let (a, f, j) = ids();
    let nobody = state.issuer.issue(a, f, j, now);

    for (case, token) in [
        ("no cookie", None),
        ("an expired token", Some(expired.as_str())),
        ("another key's token", Some(foreign.as_str())),
        ("not a token", Some("x")),
        ("an account that does not exist", Some(nobody.as_str())),
    ] {
        let answer = get(addr, "/api/nav", token).await;
        assert_eq!(answer.status, 401, "{case}");
        assert_eq!(answer.header("location"), None, "{case}");
        assert_eq!(answer.body, "", "{case}");
    }

    db.close().await;
}

// The bar's script moved to the design folder, served by the proxy at
// /design/nav.js, so the core serves no shared asset and the old path is
// gone rather than left as a second copy.
#[tokio::test]
async fn the_core_no_longer_serves_nav_js() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone(), CANARY).await;

    let answer = get(addr, "/nav.js", None).await;

    assert_eq!(answer.status, 404);

    db.close().await;
}

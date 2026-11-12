//! A core page needs a valid access token, and without one is sent to the
//! deciding endpoint with the way back, exactly as a feature does, never
//! straight to login. See "The refresh endpoint is the one place that decides
//! between refreshing and logging in" in docs/core-architecture.md. Raw
//! HTTP/1.1, as in origin.rs.

mod common;

use axum::Router;
use axum::routing::get as route_get;
use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::session::Session;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME};
use sqlx::PgPool;
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";

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

// GETs `target` with the access cookie set to `token`, if any, and returns
// the status line's code, the Location header and the body.
async fn get(
    addr: std::net::SocketAddr,
    target: &str,
    token: Option<&str>,
) -> (u16, Option<String>, String) {
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
    let status = head.split(' ').nth(1).unwrap().parse().unwrap();
    let location = head.split("\r\n").find_map(|line| {
        let (name, value) = line.split_once(": ")?;
        name.eq_ignore_ascii_case("location")
            .then(|| value.to_owned())
    });
    (status, location, body.to_owned())
}

#[tokio::test]
async fn without_a_valid_token_a_page_goes_to_the_deciding_endpoint() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
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

    for (case, token) in [
        ("no cookie", None),
        ("an expired token", Some(expired.as_str())),
        ("another key's token", Some(foreign.as_str())),
        ("not a token", Some("x")),
    ] {
        let (status, location, _) = get(addr, "/", token).await;
        assert_eq!(status, 303, "{case}");
        assert_eq!(
            location.as_deref(),
            Some("/auth/refresh?return_to=%2F"),
            "{case}"
        );
    }

    // The way back is the whole path and query.
    let (_, location, _) = get(addr, "/?a=1&b=2", None).await;
    assert_eq!(
        location.as_deref(),
        Some("/auth/refresh?return_to=%2F%3Fa%3D1%26b%3D2")
    );

    db.close().await;
}

#[tokio::test]
async fn a_valid_token_shows_the_page_with_who_you_are() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let account_id = Uuid::now_v7();
    let token = state.issuer.issue(
        account_id,
        Uuid::now_v7(),
        Uuid::now_v7(),
        OffsetDateTime::now_utc(),
    );

    let (status, _, body) = get(addr, "/", Some(&token)).await;

    assert_eq!(status, 200, "{body}");
    assert!(body.contains(&account_id.to_string()), "{body}");

    db.close().await;
}

// A query holding encoded text stays encoded in the way back, so it comes
// back to the same URL rather than a decoded one.
#[tokio::test]
async fn the_way_back_is_the_url_as_sent() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;

    let (_, location, _) = get(addr, "/?q=%2F%C3%A9", None).await;
    assert_eq!(
        location.as_deref(),
        Some("/auth/refresh?return_to=%2F%3Fq%3D%252F%25C3%25A9")
    );

    db.close().await;
}

// Under a nested router the handler sees the URI without its prefix; the
// way back must still be the whole path.
#[tokio::test]
async fn a_nested_page_comes_back_to_its_whole_path() {
    let db = common::TestDb::new().await;
    let state = AppState::new(
        db.pool.clone(),
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap();
    let app = Router::new()
        .nest(
            "/settings",
            Router::new().route("/x", route_get(|_: Session| async { "ok" })),
        )
        .with_state(state);
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });

    let (status, location, _) = get(addr, "/settings/x?a=1", None).await;
    assert_eq!(status, 303);
    assert_eq!(
        location.as_deref(),
        Some("/auth/refresh?return_to=%2Fsettings%2Fx%3Fa%3D1")
    );

    db.close().await;
}

// Without a refresh cookie there is nothing to refresh, so the deciding
// endpoint sends straight to login, carrying the way back only if it is a
// path on this origin. With one, it renders the refresh page, see
// tests/refresh.rs.
#[tokio::test]
async fn without_a_refresh_cookie_the_deciding_endpoint_sends_to_login_with_the_way_back() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;

    for (target, expected) in [
        ("/auth/refresh?return_to=/x", "/login?return_to=%2Fx"),
        (
            "/auth/refresh?return_to=%2Fx%3Fa%3D1",
            "/login?return_to=%2Fx%3Fa%3D1",
        ),
        ("/auth/refresh?return_to=//evil", "/login?return_to=%2F"),
        (
            "/auth/refresh?return_to=https://evil",
            "/login?return_to=%2F",
        ),
        ("/auth/refresh", "/login?return_to=%2F"),
    ] {
        let (status, location, _) = get(addr, target, None).await;
        assert_eq!(status, 303, "{target}");
        assert_eq!(location.as_deref(), Some(expected), "{target}");
    }

    db.close().await;
}

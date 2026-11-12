//! Refresh: the SQL that rotates a session, on a real Postgres as the app
//! role, and the endpoint over HTTP, through the real router on a real socket,
//! raw HTTP/1.1 as in login_form.rs. See "The refresh endpoint is the one place
//! that decides between refreshing and logging in", "Refresh is always a POST", "Marking a token used is one conditional update", "Nothing about
//! a session is deleted in normal operation" and "The refresh token lasts
//! fourteen days idle, and the session thirty days in total" in
//! docs/core-architecture.md.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::refresh::{self, Outcome};
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME, secret_token, sessions};
use sqlx::PgPool;
use std::collections::BTreeSet;
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";

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

// Disabling an account ends its sessions at the next refresh, not when the
// family's cap runs out thirty days later.
#[tokio::test]
async fn a_disabled_account_cannot_refresh() {
    let db = common::TestDb::new().await;
    let state = state(db.pool.clone()).await;
    let account_id = account(&db.pool, "disabled").await;
    let now = OffsetDateTime::now_utc();
    let started = sessions::start(&db.pool, account_id, now).await.unwrap();

    let outcome = refresh::refresh(&state, Some(&started.refresh_token), now)
        .await
        .unwrap();

    assert!(matches!(outcome, Outcome::LogIn));
    let row = row_of(&db.pool, &started.refresh_token).await;
    assert_eq!(row.used_at, None, "the token was used up anyway");
    assert_eq!(count(&db.pool).await, 1);

    db.close().await;
}

// Over HTTP. The handler reads the clock, so the sessions here start at the
// real time, or far enough before it to have expired.

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

// POSTs the refresh form, with `return_to` in the body, the refresh cookie if
// given, and `origin` as the Origin header if given.
async fn post(
    addr: std::net::SocketAddr,
    refresh_token: Option<&str>,
    origin: Option<&str>,
    return_to: &str,
) -> Response {
    let body = form_urlencoded::Serializer::new(String::new())
        .append_pair("return_to", return_to)
        .finish();
    let origin = origin
        .map(|origin| format!("Origin: {origin}\r\n"))
        .unwrap_or_default();
    send(
        addr,
        format!(
            "POST /auth/refresh HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
             {origin}{}Content-Type: application/x-www-form-urlencoded\r\n\
             Content-Length: {}\r\n\r\n{body}",
            cookie_header(refresh_token),
            body.len()
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

// Every column of every row, to compare before and after a request that must
// change nothing.
async fn snapshot(pool: &PgPool) -> Vec<String> {
    sqlx::query_scalar("SELECT s::text FROM sessions s ORDER BY id")
        .fetch_all(pool)
        .await
        .unwrap()
}

#[tokio::test]
async fn a_refresh_sets_both_cookies_and_returns_to_the_path() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, OffsetDateTime::now_utc())
        .await
        .unwrap();

    let response = post(addr, Some(&started.refresh_token), Some(PLATFORM), "/x?a=1").await;

    assert_eq!(response.status, 303, "{}", response.body);
    assert_eq!(response.header("location"), Some("/x?a=1"));
    assert_eq!(response.all("set-cookie").len(), 2);

    // The attributes login gives them.
    let (_, access, attrs) = cookie(named(&response, "__Host-access"));
    assert_eq!(
        attrs,
        attributes(&[
            "HttpOnly",
            "SameSite=Lax",
            "Secure",
            "Path=/",
            "Max-Age=1800"
        ])
    );
    let claims = state
        .verifier
        .verify(&access, OffsetDateTime::now_utc())
        .unwrap();
    assert_eq!(claims.account_id, account_id);
    assert_eq!(claims.family_id, started.family_id);

    let (_, refresh_token, attrs) = cookie(named(&response, "__Secure-refresh"));
    assert_ne!(refresh_token, started.refresh_token);
    let max_age = attrs
        .iter()
        .find_map(|a| a.strip_prefix("Max-Age="))
        .expect("no Max-Age on the refresh cookie");
    // Fourteen days from the request, give or take the second it took.
    let max_age: i64 = max_age.parse().unwrap();
    assert!(
        (Duration::days(14).whole_seconds() - max_age).abs() <= 2,
        "Max-Age={max_age}"
    );
    assert_eq!(
        attrs,
        attributes(&[
            "HttpOnly",
            "SameSite=Lax",
            "Secure",
            "Path=/auth",
            &format!("Max-Age={max_age}"),
        ])
    );
    let rotated = row_of(&db.pool, &refresh_token).await;
    assert_eq!(rotated.family_id, started.family_id);

    // The old token is used now, so presenting it again is a login.
    let again = post(addr, Some(&started.refresh_token), Some(PLATFORM), "/x?a=1").await;
    assert_eq!(again.status, 303);
    assert_eq!(
        again.header("location"),
        Some("/login?return_to=%2Fx%3Fa%3D1")
    );

    db.close().await;
}

#[tokio::test]
async fn anything_else_clears_both_cookies_and_goes_to_login_with_the_way_back() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool, "active").await;
    let now = OffsetDateTime::now_utc();
    // Issued fifteen days ago, so a day past its fourteen.
    let expired = sessions::start(&db.pool, account_id, now - Duration::days(15))
        .await
        .unwrap();
    let revoked = sessions::start(&db.pool, account_id, now).await.unwrap();
    sqlx::query("UPDATE sessions SET revoked_at = now() WHERE family_id = $1")
        .bind(revoked.family_id)
        .execute(&db.pool)
        .await
        .unwrap();

    for (case, token) in [
        ("no cookie", None),
        ("not a token", Some("not-a-token")),
        ("expired", Some(expired.refresh_token.as_str())),
        ("revoked", Some(revoked.refresh_token.as_str())),
    ] {
        let response = post(addr, token, Some(PLATFORM), "/x?a=1").await;

        assert_eq!(response.status, 303, "{case}: {}", response.body);
        assert_eq!(
            response.header("location"),
            Some("/login?return_to=%2Fx%3Fa%3D1"),
            "{case}"
        );
        assert_eq!(response.all("set-cookie").len(), 2, "{case}");
        // Each on its own path: a clear on the wrong one leaves the cookie
        // alive while looking like it worked.
        for (name, path) in [("__Host-access", "/"), ("__Secure-refresh", "/auth")] {
            let (_, value, attrs) = cookie(named(&response, name));
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
    assert_eq!(
        count(&db.pool).await,
        2,
        "a refresh that failed stored a row"
    );

    db.close().await;
}

#[tokio::test]
async fn a_refresh_post_needs_the_platforms_origin() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, OffsetDateTime::now_utc())
        .await
        .unwrap();
    let before = snapshot(&db.pool).await;

    for origin in [
        None,
        Some("https://evil.test"),
        Some("https://platform.test.evil.test"),
    ] {
        let response = post(addr, Some(&started.refresh_token), origin, "/x").await;
        assert_eq!(response.status, 403, "{origin:?}");
        assert!(response.all("set-cookie").is_empty(), "{origin:?}");
    }

    assert_eq!(
        snapshot(&db.pool).await,
        before,
        "a refused post changed a row"
    );
    assert_eq!(row_of(&db.pool, &started.refresh_token).await.used_at, None);

    db.close().await;
}

async fn get(addr: std::net::SocketAddr, target: &str, refresh_token: Option<&str>) -> Response {
    send(
        addr,
        format!(
            "GET {target} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n{}\r\n",
            cookie_header(refresh_token)
        ),
    )
    .await
}

// A redirect arrives as a GET, and a GET never changes state, which is what
// makes SameSite=Lax enough; so the page only hands the browser a form that
// posts the refresh, by script or by its button.
#[tokio::test]
async fn the_refresh_page_changes_nothing_and_posts_to_itself() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;
    let account_id = account(&db.pool, "active").await;
    let started = sessions::start(&db.pool, account_id, OffsetDateTime::now_utc())
        .await
        .unwrap();
    let before = snapshot(&db.pool).await;

    for (target, kept) in [
        ("/auth/refresh?return_to=/x", "/x"),
        ("/auth/refresh?return_to=%2Fx%3Fa%3D1", "/x?a=1"),
        ("/auth/refresh?return_to=//evil", "/"),
        ("/auth/refresh?return_to=https://evil", "/"),
        ("/auth/refresh", "/"),
    ] {
        let response = get(addr, target, Some(&started.refresh_token)).await;

        assert_eq!(response.status, 200, "{target}: {}", response.body);
        assert_eq!(
            response.header("cache-control"),
            Some("no-store"),
            "{target}"
        );
        assert!(response.all("set-cookie").is_empty(), "{target}");
        let body = &response.body;
        assert!(
            body.contains(r#"<form method="post" action="/auth/refresh" name="refresh">"#),
            "{target}: {body}"
        );
        assert!(
            body.contains(&format!(
                r#"<input type="hidden" name="return_to" value="{kept}">"#
            )),
            "{target}: {body}"
        );
        assert!(
            body.contains(r#"<button type="submit">Continue</button>"#),
            "{target}: {body}"
        );
        assert!(
            body.contains("<script>document.forms.refresh.submit();</script>"),
            "{target}: {body}"
        );
        assert!(
            !body.contains("<platform-nav"),
            "{target}: the page has the nav"
        );
    }

    assert_eq!(snapshot(&db.pool).await, before, "the page changed a row");

    db.close().await;
}

// No refresh cookie means there is nothing to refresh, so the page would only
// post to be told to log in.
#[tokio::test]
async fn the_refresh_page_sends_a_visitor_without_a_refresh_cookie_to_login() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone()).await;

    for refresh_token in [None, Some("")] {
        let response = get(addr, "/auth/refresh?return_to=/x", refresh_token).await;
        assert_eq!(response.status, 303, "{refresh_token:?}");
        assert_eq!(
            response.header("location"),
            Some("/login?return_to=%2Fx"),
            "{refresh_token:?}"
        );
    }

    db.close().await;
}

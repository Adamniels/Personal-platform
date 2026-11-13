//! The login form over HTTP, through the real router on a real socket and a
//! test database: the cookies a browser is handed, where it is sent, and that
//! a refusal says nothing about why. See "The session lives in two cookies"
//! and "No defence against user enumeration" in docs/core-architecture.md.
//! Raw HTTP/1.1, as in origin.rs, so nothing is added to test it.

mod common;

use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, password, secret_token};
use sqlx::PgPool;
use std::collections::BTreeSet;
use time::{Duration, OffsetDateTime};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";
const PASSWORD: &str = "correct-horse-battery";

async fn serve(pool: PgPool, lifetime: Duration) -> (std::net::SocketAddr, AppState) {
    let state = AppState::new(
        pool,
        SigningKey::generate().0,
        lifetime,
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

async fn account(pool: &PgPool, email: &str, status: &str) -> Uuid {
    let id = Uuid::now_v7();
    sqlx::query(
        "INSERT INTO accounts (id, email, password_hash, status, created_at)
         VALUES ($1, $2, $3, $4, now())",
    )
    .bind(id)
    .bind(email)
    .bind(password::hash(PASSWORD).await.unwrap())
    .bind(status)
    .execute(pool)
    .await
    .unwrap();
    id
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

    // Everything but the Date header, which differs by the second.
    fn without_date(&self) -> String {
        let headers: Vec<String> = self
            .headers
            .iter()
            .filter(|(n, _)| n != "date")
            .map(|(n, v)| format!("{n}: {v}"))
            .collect();
        format!("{}\n{}\n\n{}", self.status, headers.join("\n"), self.body)
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

async fn get(addr: std::net::SocketAddr, target: &str) -> Response {
    send(
        addr,
        format!("GET {target} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\r\n"),
    )
    .await
}

async fn post(addr: std::net::SocketAddr, fields: &[(&str, &str)]) -> Response {
    let body = form_urlencoded::Serializer::new(String::new())
        .extend_pairs(fields)
        .finish();
    send(
        addr,
        format!(
            "POST /login HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
             Origin: {PLATFORM}\r\nContent-Type: application/x-www-form-urlencoded\r\n\
             Content-Length: {}\r\n\r\n{body}",
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
        .unwrap_or_else(|| panic!("no {name} cookie"))
}

fn attributes(list: &[&str]) -> BTreeSet<String> {
    list.iter().map(|a| a.to_string()).collect()
}

#[tokio::test]
async fn a_login_sets_both_cookies_and_redirects_to_the_return_path() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone(), Duration::minutes(30)).await;
    let account_id = account(&db.pool, "walk@example.com", "active").await;

    let response = post(
        addr,
        &[
            ("email", " Walk@Example.com "),
            ("password", PASSWORD),
            ("return_to", "/canary/a?b=1"),
        ],
    )
    .await;

    assert_eq!(response.status, 303, "{}", response.body);
    assert_eq!(response.header("location"), Some("/canary/a?b=1"));
    assert_eq!(response.all("set-cookie").len(), 2);

    // The access cookie: the whole domain, as long as its token lasts.
    let (_, token, attrs) = cookie(named(&response, "__Host-access"));
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
        .verify(&token, OffsetDateTime::now_utc())
        .unwrap();
    assert_eq!(claims.account_id, account_id);
    assert_eq!(claims.expires_at - claims.issued_at, 1800);

    // The refresh cookie: only /auth, as long as its stored token lasts.
    let (_, refresh, attrs) = cookie(named(&response, "__Secure-refresh"));
    assert_eq!(
        attrs,
        attributes(&[
            "HttpOnly",
            "SameSite=Lax",
            "Secure",
            "Path=/auth",
            "Max-Age=1209600"
        ])
    );
    let (family_id, lasts): (Uuid, i64) = sqlx::query_as(
        "SELECT family_id, extract(epoch FROM expires_at - issued_at)::bigint
         FROM sessions WHERE token_hash = $1",
    )
    .bind(&secret_token::hash(&refresh).unwrap()[..])
    .fetch_one(&db.pool)
    .await
    .unwrap();
    assert_eq!(lasts, Duration::days(14).whole_seconds());
    assert_eq!(claims.family_id, family_id);

    db.close().await;
}

#[tokio::test]
async fn the_access_cookie_follows_the_configured_lifetime() {
    let db = common::TestDb::new().await;
    let (addr, state) = serve(db.pool.clone(), Duration::minutes(1)).await;
    account(&db.pool, "walk@example.com", "active").await;

    let response = post(
        addr,
        &[("email", "walk@example.com"), ("password", PASSWORD)],
    )
    .await;

    let (_, token, attrs) = cookie(named(&response, "__Host-access"));
    assert!(attrs.contains("Max-Age=60"), "{attrs:?}");
    let claims = state
        .verifier
        .verify(&token, OffsetDateTime::now_utc())
        .unwrap();
    assert_eq!(claims.expires_at - claims.issued_at, 60);

    db.close().await;
}

// An unknown email, a wrong password and a disabled account are told apart
// by nothing in the response.
#[tokio::test]
async fn every_refusal_answers_byte_for_byte_the_same() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone(), Duration::minutes(30)).await;
    account(&db.pool, "walk@example.com", "active").await;
    account(&db.pool, "gone@example.com", "disabled").await;

    // Emails of one length, so the email each form keeps is the only byte
    // that differs, and it is masked below.
    let refusals = [
        ("walk@example.com", "a wrong password, long"),
        ("none@example.com", PASSWORD),
        ("gone@example.com", PASSWORD),
    ];
    let mut answers = Vec::new();
    for (email, password) in refusals {
        answers.push(
            post(
                addr,
                &[
                    ("email", email),
                    ("password", password),
                    ("return_to", "/x"),
                ],
            )
            .await,
        );
    }

    for (response, (email, password)) in answers.iter().zip(refusals) {
        assert_eq!(response.status, 422, "{email}: {}", response.body);
        common::forms::assert_refused_on(
            &response.body,
            "password",
            "Email or password is incorrect.",
        );
        assert!(
            response.all("set-cookie").is_empty(),
            "{email}: set a cookie"
        );
        assert!(
            !response.body.contains(password),
            "{email}: the password came back"
        );
        assert!(
            response.body.contains(r#"name="return_to" value="/x""#),
            "{email}"
        );
    }
    // The email is kept, so compare each with the email it was sent.
    let normalised: Vec<String> = answers
        .iter()
        .zip(refusals)
        .map(|(response, (email, _))| response.without_date().replace(email, "<email>"))
        .collect();
    assert_eq!(
        normalised[0], normalised[1],
        "wrong password and unknown email differ"
    );
    assert_eq!(
        normalised[0], normalised[2],
        "wrong password and disabled account differ"
    );

    db.close().await;
}

#[tokio::test]
async fn a_return_path_off_this_origin_lands_on_the_front_page() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone(), Duration::minutes(30)).await;
    account(&db.pool, "walk@example.com", "active").await;

    for return_to in [Some("//evil"), Some("https://evil"), Some("/\\evil"), None] {
        let mut fields = vec![("email", "walk@example.com"), ("password", PASSWORD)];
        if let Some(return_to) = return_to {
            fields.push(("return_to", return_to));
        }
        let response = post(addr, &fields).await;
        assert_eq!(response.status, 303, "{return_to:?}");
        assert_eq!(response.header("location"), Some("/"), "{return_to:?}");
    }

    db.close().await;
}

#[tokio::test]
async fn the_form_carries_the_return_path_it_was_given() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone(), Duration::minutes(30)).await;

    for (target, kept) in [
        ("/login?return_to=%2Fcanary%2Fa%3Fb%3D1", "/canary/a?b=1"),
        ("/login?return_to=%2F%2Fevil", "/"),
        ("/login", "/"),
    ] {
        let response = get(addr, target).await;
        assert_eq!(response.status, 200, "{target}");
        assert!(
            response
                .body
                .contains(&format!(r#"name="return_to" value="{kept}""#)),
            "{target}: {}",
            response.body
        );
    }

    db.close().await;
}

// The dummy hash is what makes an unknown email cost as much as a wrong
// password; without it every response still looks right and only the timing
// tells. Deliberately coarse, half of one verification, so load cannot make
// it flaky, while a login that skips the verification takes a few
// milliseconds against tens.
#[tokio::test]
async fn an_unknown_email_costs_a_password_verification() {
    let db = common::TestDb::new().await;
    let (addr, _) = serve(db.pool.clone(), Duration::minutes(30)).await;
    let stored = password::hash(PASSWORD).await.unwrap();

    let started = std::time::Instant::now();
    password::verify("not the password", &stored).await.unwrap();
    let one_verification = started.elapsed();

    let started = std::time::Instant::now();
    let response = post(
        addr,
        &[("email", "nobody@example.com"), ("password", PASSWORD)],
    )
    .await;
    let unknown_email = started.elapsed();

    assert_eq!(response.status, 422);
    assert!(
        unknown_email >= one_verification / 2,
        "an unknown email took {unknown_email:?}, one verification {one_verification:?}"
    );

    db.close().await;
}

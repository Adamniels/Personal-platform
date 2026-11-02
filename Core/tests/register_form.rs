//! The registration form over HTTP, through the real router on a real socket
//! and a test database: what a browser gets back. Raw HTTP/1.1, as in
//! origin.rs, so nothing is added to test it.

mod common;

use platform_core::AppState;
use platform_core::invite;
use platform_core::issuer::Issuer;
use platform_core::origin::PublicOrigin;
use platform_core::signing_key::SigningKey;
use sqlx::PgPool;
use std::sync::Arc;
use time::OffsetDateTime;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

const PLATFORM: &str = "https://platform.test";
// Form encoding needs nothing escaped in it, so it is searched for as is.
const PASSWORD: &str = "correct-horse-battery";

async fn serve(pool: PgPool) -> std::net::SocketAddr {
    let state = AppState {
        pool,
        issuer: Arc::new(Issuer::new(
            SigningKey::generate().0,
            platform_core::DEFAULT_ACCESS_TOKEN_LIFETIME,
        )),
    };
    let app = platform_core::app(state, PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    addr
}

async fn fresh_code(pool: &PgPool) -> String {
    let now = OffsetDateTime::now_utc();
    invite::create(pool, now, now + time::Duration::days(7))
        .await
        .unwrap()
}

// Posts the form as a browser would and returns the status line and the rest.
async fn post(addr: std::net::SocketAddr, code: &str, email: &str, password: &str) -> String {
    let body = format!(
        "code={code}&email={}&password={password}",
        email.replace('@', "%40")
    );
    let request = format!(
        "POST /register HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\
         Origin: {PLATFORM}\r\nContent-Type: application/x-www-form-urlencoded\r\n\
         Content-Length: {}\r\n\r\n{body}",
        body.len()
    );
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = String::new();
    stream.read_to_string(&mut response).await.unwrap();
    response
}

#[tokio::test]
async fn a_registration_redirects_to_login() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let code = fresh_code(&db.pool).await;

    let response = post(addr, &code, "walk@example.com", PASSWORD).await;

    assert!(response.starts_with("HTTP/1.1 303 "), "{response}");
    assert!(
        response.to_lowercase().contains("\r\nlocation: /login\r\n"),
        "{response}"
    );

    db.close().await;
}

// Every refusal re-renders the form with the email kept, and never the
// password, whatever the reason.
#[tokio::test]
async fn a_refused_form_keeps_the_email_and_never_echoes_the_password() {
    let db = common::TestDb::new().await;
    let addr = serve(db.pool.clone()).await;
    let first = fresh_code(&db.pool).await;
    assert!(
        post(addr, &first, "walk@example.com", PASSWORD)
            .await
            .starts_with("HTTP/1.1 303 ")
    );
    let unused = fresh_code(&db.pool).await;

    let cases = [
        (
            first.as_str(),
            "other@example.com",
            PASSWORD,
            "This invite code is not valid.",
        ),
        (
            unused.as_str(),
            "walk@example.com",
            PASSWORD,
            "An account with this email already exists.",
        ),
        (
            unused.as_str(),
            "not-an-email",
            PASSWORD,
            "Enter a valid email address.",
        ),
        (
            unused.as_str(),
            "other@example.com",
            "short-password",
            "Choose a password of 15 to 1024 characters.",
        ),
    ];
    for (code, email, password, error) in cases {
        let response = post(addr, code, email, password).await;
        assert!(response.starts_with("HTTP/1.1 422 "), "{email}: {response}");
        assert!(response.contains(error), "{email}: {response}");
        assert!(
            response.contains(&format!("value=\"{email}\"")),
            "{email}: {response}"
        );
        assert!(
            !response.contains(password),
            "{email}: the password came back"
        );
    }

    db.close().await;
}

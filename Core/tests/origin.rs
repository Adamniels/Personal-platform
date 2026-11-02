//! The Origin check, over a stub router served on a real socket: every method
//! that can change state needs the platform's own origin, and a GET is never
//! checked. Raw HTTP/1.1 rather than a client library, so nothing is added to
//! test it.

use axum::Router;
use axum::routing::any;
use platform_core::origin::{self, PublicOrigin};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

const PLATFORM: &str = "https://platform.test";

// Serves a router answering 200 to anything, behind the check, and returns
// its address.
async fn stub() -> std::net::SocketAddr {
    let router = origin::protect(
        Router::new().route("/", any(|| async { "reached" })),
        PublicOrigin::parse(PLATFORM).unwrap(),
    );
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    addr
}

// Sends one request with the given Origin headers and returns the status.
async fn status(addr: std::net::SocketAddr, method: &str, origins: &[&str]) -> u16 {
    let mut request =
        format!("{method} / HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n");
    for origin in origins {
        request.push_str(&format!("Origin: {origin}\r\n"));
    }
    request.push_str("Content-Length: 0\r\n\r\n");

    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = String::new();
    stream.read_to_string(&mut response).await.unwrap();

    response
        .split(' ')
        .nth(1)
        .and_then(|code| code.parse().ok())
        .unwrap_or_else(|| panic!("not an HTTP response: {response:?}"))
}

#[tokio::test]
async fn only_the_platforms_own_origin_may_change_state() {
    let addr = stub().await;

    let cases: &[(&str, &[&str], u16)] = &[
        ("POST", &[PLATFORM], 200),
        ("POST", &["https://evil.test"], 403),
        ("POST", &[], 403),
        ("POST", &["null"], 403),
        ("POST", &["http://platform.test"], 403),
        ("POST", &["https://platform.test:8443"], 403),
        ("POST", &["https://platform.test.evil.test"], 403),
        ("POST", &["https://platform.test/"], 403),
        ("POST", &[PLATFORM, "https://evil.test"], 403),
        ("PUT", &[], 403),
        ("PATCH", &["https://evil.test"], 403),
        ("DELETE", &[PLATFORM], 200),
        ("GET", &["https://evil.test"], 200),
        ("GET", &[], 200),
        ("HEAD", &["https://evil.test"], 200),
        ("OPTIONS", &["https://evil.test"], 200),
    ];

    for (method, origins, expected) in cases {
        assert_eq!(
            status(addr, method, origins).await,
            *expected,
            "{method} with Origin {origins:?}"
        );
    }
}

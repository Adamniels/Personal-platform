//! The key set every feature verifies tokens with, served by the core, and
//! the key being persistent: a restarted core still accepts what it issued
//! before. See "The signing key" in docs/core-architecture.md. Raw HTTP/1.1,
//! as in core_pages.rs.

mod common;

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::verifier::Refused;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME};
use sqlx::PgPool;
use time::OffsetDateTime;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use uuid::Uuid;

const PLATFORM: &str = "https://platform.test";

async fn state(pool: PgPool, seed: &str) -> AppState {
    AppState::new(
        pool,
        SigningKey::parse(seed).unwrap(),
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::default(),
    )
    .await
    .unwrap()
}

async fn serve(state: AppState) -> std::net::SocketAddr {
    let app = platform_core::app(state, PublicOrigin::parse(PLATFORM).unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    addr
}

// GETs `target` with no cookie and returns the status, the Content-Type
// header and the body.
async fn get(addr: std::net::SocketAddr, target: &str) -> (u16, Option<String>, String) {
    let request =
        format!("GET {target} HTTP/1.1\r\nHost: platform.test\r\nConnection: close\r\n\r\n");
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut raw = String::new();
    stream.read_to_string(&mut raw).await.unwrap();

    let (head, body) = raw.split_once("\r\n\r\n").unwrap();
    let status = head.split(' ').nth(1).unwrap().parse().unwrap();
    let content_type = head.split("\r\n").find_map(|line| {
        let (name, value) = line.split_once(": ")?;
        name.eq_ignore_ascii_case("content-type")
            .then(|| value.to_owned())
    });
    (status, content_type, body.to_owned())
}

fn ids() -> (Uuid, Uuid, Uuid) {
    (Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7())
}

// The key id in a token's header, read as a verifier in another language
// would: base64url JSON, no library.
fn header_kid(token: &str) -> String {
    let header = URL_SAFE_NO_PAD
        .decode(token.split('.').next().unwrap())
        .unwrap();
    let header: serde_json::Value = serde_json::from_slice(&header).unwrap();
    header["kid"].as_str().unwrap().to_owned()
}

#[tokio::test]
async fn the_key_set_holds_the_signing_key_under_its_key_id() {
    let db = common::TestDb::new().await;
    let (key, seed) = SigningKey::generate();
    let state = state(db.pool.clone(), &seed).await;
    let (a, f, j) = ids();
    let token = state.issuer.issue(a, f, j, OffsetDateTime::now_utc());
    let addr = serve(state).await;

    let (status, content_type, body) = get(addr, "/.well-known/jwks.json").await;

    assert_eq!(status, 200, "{body}");
    assert_eq!(content_type.as_deref(), Some("application/json"));
    let set: serde_json::Value = serde_json::from_str(&body).unwrap();
    assert_eq!(
        set,
        serde_json::json!({
            "keys": [{
                "kty": "OKP",
                "crv": "Ed25519",
                "alg": "EdDSA",
                "use": "sig",
                "kid": header_kid(&token),
                "x": URL_SAFE_NO_PAD.encode(key.public_key()),
            }]
        })
    );

    db.close().await;
}

// Check 8 of the feature: the key is persistent, so a restart needs no
// refresh. A second AppState from the same seed stands in for the restarted
// core, and one from another seed for a core that lost its key.
#[tokio::test]
async fn a_restarted_core_verifies_a_token_issued_before() {
    let db = common::TestDb::new().await;
    let (_, seed) = SigningKey::generate();
    let (_, other_seed) = SigningKey::generate();
    let now = OffsetDateTime::now_utc();
    let (a, f, j) = ids();
    let token = state(db.pool.clone(), &seed)
        .await
        .issuer
        .issue(a, f, j, now);

    let restarted = state(db.pool.clone(), &seed).await;
    let claims = restarted.verifier.verify(&token, now).unwrap();
    assert_eq!(
        (claims.account_id, claims.family_id, claims.token_id),
        (a, f, j)
    );

    let rekeyed = state(db.pool.clone(), &other_seed).await;
    assert_eq!(rekeyed.verifier.verify(&token, now), Err(Refused::Invalid));

    db.close().await;
}

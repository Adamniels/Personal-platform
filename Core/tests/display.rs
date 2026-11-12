//! `display::fetch_all`, the core asking every registered feature for its
//! display data with the user's token: each feature gives its data or
//! unavailable within the deadline, and one that is down, slow or broken
//! costs only its own entry. Against stub features served on real sockets.
//! See "What a feature exposes for the front page" in
//! docs/core-architecture.md.

mod common;

use axum::Router;
use axum::http::{StatusCode, header};
use axum::response::IntoResponse;
use axum::routing::get;
use common::stubs::{hanging_stub, refused_addr, serve, stub};
use platform_core::display::{self, Fetched, MAX_BODY, Unavailable};
use platform_core::http;
use platform_core::registry::Registry;
use serde_json::{Value, json};
use std::net::SocketAddr;
use std::time::{Duration, Instant};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

const DEADLINE: Duration = Duration::from_millis(200);
const TOKEN: &str = "header.payload.signature";

// A feature that waits `delay` and then answers with `body` as JSON.
async fn slow_stub(delay: Duration, body: Value) -> SocketAddr {
    let router = Router::new().fallback(get(move || {
        let body = body.clone();
        async move {
            tokio::time::sleep(delay).await;
            axum::Json(body)
        }
    }));
    serve(router).await
}

// A registry with one feature per entry, `name` at `/<name>`, with its
// display endpoint at `/<name>/display` when the flag is set.
fn registry(features: &[(&str, SocketAddr, bool)]) -> Registry {
    let mut text = String::new();
    for (name, addr, display) in features {
        text.push_str(&format!(
            "[[feature]]\nname = \"{name}\"\nprefix = \"/{name}\"\nupstream = \"http://{addr}\"\n"
        ));
        if *display {
            text.push_str(&format!("display = \"/{name}/display\"\n"));
        }
        text.push('\n');
    }
    Registry::parse(&text).unwrap()
}

async fn fetch(registry: &Registry) -> Vec<Fetched> {
    display::fetch_all(&http::client().unwrap(), registry, TOKEN, DEADLINE).await
}

fn names(fetched: &[Fetched]) -> Vec<&str> {
    fetched.iter().map(|f| f.feature.name.as_str()).collect()
}

fn data<'a>(fetched: &'a [Fetched], name: &str) -> &'a Result<Value, Unavailable> {
    &fetched
        .iter()
        .find(|f| f.feature.name == name)
        .unwrap_or_else(|| panic!("no entry for {name} in {:?}", names(fetched)))
        .data
}

fn json_response(body: Value) -> axum::response::Response {
    axum::Json(body).into_response()
}

#[tokio::test]
async fn a_feature_that_answers_gives_its_data() {
    let body = json!({ "active": 4, "due_today": 2, "next": "Finish the invite flow" });
    let sent = body.clone();
    let (addr, seen) = stub(move || json_response(sent.clone())).await;

    let fetched = fetch(&registry(&[("desk", addr, true)])).await;

    assert_eq!(names(&fetched), ["desk"]);
    assert_eq!(data(&fetched, "desk"), &Ok(body));

    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 1, "the stub was called once");
    let headers = &seen[0];
    assert_eq!(
        headers.get(header::AUTHORIZATION).unwrap(),
        &format!("Bearer {TOKEN}")
    );
    assert_eq!(headers.get(header::ACCEPT).unwrap(), "application/json");
    assert!(headers.get(header::COOKIE).is_none(), "{headers:?}");
}

#[tokio::test]
async fn refused_a_500_a_hang_and_invalid_json_each_give_unavailable_within_the_deadline() {
    let refused = refused_addr().await;
    let (failing, _) = stub(|| StatusCode::INTERNAL_SERVER_ERROR.into_response()).await;
    let hanging = hanging_stub().await;
    let (garbled, _) =
        stub(|| ([(header::CONTENT_TYPE, "application/json")], "{ not json").into_response()).await;
    let registry = registry(&[
        ("refused", refused, true),
        ("failing", failing, true),
        ("hanging", hanging, true),
        ("garbled", garbled, true),
    ]);

    let started = Instant::now();
    let fetched = fetch(&registry).await;
    let elapsed = started.elapsed();

    assert!(
        matches!(data(&fetched, "refused"), Err(Unavailable::Unreachable(_))),
        "{:?}",
        data(&fetched, "refused")
    );
    assert_eq!(data(&fetched, "failing"), &Err(Unavailable::Status(500)));
    assert_eq!(data(&fetched, "hanging"), &Err(Unavailable::TimedOut));
    assert_eq!(data(&fetched, "garbled"), &Err(Unavailable::NotJson));
    assert!(
        elapsed < DEADLINE + DEADLINE / 2,
        "took {elapsed:?} for a deadline of {DEADLINE:?}"
    );
}

#[tokio::test]
async fn two_hanging_features_cost_one_deadline_not_two() {
    let registry = registry(&[
        ("first", hanging_stub().await, true),
        ("second", hanging_stub().await, true),
    ]);

    let started = Instant::now();
    let fetched = fetch(&registry).await;
    let elapsed = started.elapsed();

    assert_eq!(data(&fetched, "first"), &Err(Unavailable::TimedOut));
    assert_eq!(data(&fetched, "second"), &Err(Unavailable::TimedOut));
    assert!(
        elapsed < DEADLINE + DEADLINE / 2,
        "took {elapsed:?} for two features with a deadline of {DEADLINE:?}"
    );
}

#[tokio::test]
async fn a_slow_answer_inside_the_deadline_comes_through() {
    let body = json!({ "slow": true });
    let addr = slow_stub(DEADLINE / 2, body.clone()).await;

    let fetched = fetch(&registry(&[("slow", addr, true)])).await;

    assert_eq!(data(&fetched, "slow"), &Ok(body));
}

#[tokio::test]
async fn a_redirect_is_not_followed() {
    let (target, target_seen) = stub(|| json_response(json!({ "stolen": true }))).await;
    let location = format!("http://{target}/target/display");
    let (redirecting, _) =
        stub(move || (StatusCode::FOUND, [(header::LOCATION, location.clone())]).into_response())
            .await;

    let fetched = fetch(&registry(&[("redirecting", redirecting, true)])).await;

    assert_eq!(
        data(&fetched, "redirecting"),
        &Err(Unavailable::Status(302))
    );
    assert!(
        target_seen.lock().unwrap().is_empty(),
        "the redirect's target was called"
    );
}

// A JSON string whose encoding is exactly `len` bytes, so a body over the
// limit would otherwise parse.
fn json_of_len(len: usize) -> String {
    format!("\"{}\"", "a".repeat(len - 2))
}

#[tokio::test]
async fn an_answer_over_the_size_limit_is_unavailable() {
    let over = json_of_len(MAX_BODY + 1);
    let (addr, _) =
        stub(move || ([(header::CONTENT_TYPE, "application/json")], over.clone()).into_response())
            .await;

    let fetched = fetch(&registry(&[("large", addr, true)])).await;

    assert_eq!(data(&fetched, "large"), &Err(Unavailable::TooLarge));
}

#[tokio::test]
async fn an_answer_at_the_size_limit_comes_through() {
    let exact = json_of_len(MAX_BODY);
    let sent = exact.clone();
    let (addr, _) =
        stub(move || ([(header::CONTENT_TYPE, "application/json")], sent.clone()).into_response())
            .await;

    let fetched = fetch(&registry(&[("exact", addr, true)])).await;

    assert_eq!(
        data(&fetched, "exact"),
        &Ok(serde_json::from_str::<Value>(&exact).unwrap())
    );
}

// A raw HTTP/1.1 server answering every connection with a chunked body of
// `len` bytes and no Content-Length, so the limit cannot rest on the header.
async fn chunked_stub(len: usize) -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move {
        loop {
            let (mut stream, _) = listener.accept().await.unwrap();
            tokio::spawn(async move {
                let mut request = [0u8; 4096];
                let _ = stream.read(&mut request).await;
                let body = json_of_len(len);
                let mut response = String::from(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\
                     Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n",
                );
                for chunk in body.as_bytes().chunks(4096) {
                    response.push_str(&format!("{:x}\r\n", chunk.len()));
                    response.push_str(std::str::from_utf8(chunk).unwrap());
                    response.push_str("\r\n");
                }
                response.push_str("0\r\n\r\n");
                let _ = stream.write_all(response.as_bytes()).await;
            });
        }
    });
    addr
}

#[tokio::test]
async fn an_answer_over_the_size_limit_without_a_length_is_unavailable() {
    let addr = chunked_stub(MAX_BODY + 1).await;

    let fetched = fetch(&registry(&[("chunked", addr, true)])).await;

    assert_eq!(data(&fetched, "chunked"), &Err(Unavailable::TooLarge));
}

#[tokio::test]
async fn a_feature_without_a_display_endpoint_is_not_called() {
    let (with, _) = stub(|| json_response(json!({ "here": true }))).await;
    let (without, without_seen) = stub(|| json_response(json!({}))).await;

    let fetched = fetch(&registry(&[
        ("without", without, false),
        ("with", with, true),
    ]))
    .await;

    assert_eq!(names(&fetched), ["with"]);
    assert!(
        without_seen.lock().unwrap().is_empty(),
        "the feature without a display endpoint was called"
    );
}

#[tokio::test]
async fn results_come_back_in_registry_order() {
    let registry = registry(&[
        (
            "late",
            slow_stub(DEADLINE / 2, json!({ "late": true })).await,
            true,
        ),
        (
            "early",
            slow_stub(Duration::ZERO, json!({ "early": true })).await,
            true,
        ),
        ("down", refused_addr().await, true),
    ]);

    let fetched = fetch(&registry).await;

    assert_eq!(names(&fetched), ["late", "early", "down"]);
    assert_eq!(data(&fetched, "late"), &Ok(json!({ "late": true })));
    assert_eq!(data(&fetched, "early"), &Ok(json!({ "early": true })));
}

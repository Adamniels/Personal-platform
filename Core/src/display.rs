//! Asking every feature with a display endpoint for its data, on the user's
//! behalf, for the front page. Each one gives its data or `Unavailable`
//! within a deadline, so a feature that is down, slow or broken costs only
//! its own piece of the page. See "What a feature exposes for the front page"
//! in docs/core-architecture.md.

use crate::registry::{Feature, Registry};
use reqwest::StatusCode;
use reqwest::header::ACCEPT;
use std::error::Error;
use std::fmt;
use std::sync::Arc;
use std::time::Duration;

/// How long the front page waits for one feature, for the whole exchange:
/// connecting, the headers and the body.
pub const DISPLAY_DEADLINE: Duration = Duration::from_millis(500);

/// The largest answer read from a feature, in bytes. Display data is a
/// handful of fields; anything bigger is a bug in the feature, and reading it
/// would hold the page.
pub const MAX_BODY: usize = 64 * 1024;

/// What one feature gave.
#[derive(Debug)]
pub struct Fetched {
    pub feature: Feature,
    pub data: Result<serde_json::Value, Unavailable>,
}

/// Why a feature's data is missing. For the log only: the page shows the
/// same placeholder whatever the reason.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Unavailable {
    /// No answer at all: the connection was refused or reset, or the body
    /// broke off. Holds the client's description of the error.
    Unreachable(String),
    /// No full answer within the deadline.
    TimedOut,
    /// An answer with any status other than 200, a redirect among them.
    Status(u16),
    /// An answer over `MAX_BODY`.
    TooLarge,
    /// A 200 whose body does not parse as JSON.
    NotJson,
    /// The task fetching it panicked, a bug in the core.
    Panicked,
}

impl fmt::Display for Unavailable {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Unavailable::Unreachable(err) => write!(f, "unreachable: {err}"),
            Unavailable::TimedOut => write!(f, "no answer within the deadline"),
            Unavailable::Status(status) => write!(f, "answered {status}"),
            Unavailable::TooLarge => write!(f, "answer over {MAX_BODY} bytes"),
            Unavailable::NotJson => write!(f, "answer is not JSON"),
            Unavailable::Panicked => write!(f, "the fetch panicked"),
        }
    }
}

/// Every feature's display data, one entry per feature with a `display`
/// endpoint, in registry order. The calls run concurrently, one task each,
/// so the whole call takes about one `deadline` however many features miss
/// it. Each failure is logged with the feature's name; `token` never is.
///
/// `client` should be the one from `http::client`, which never follows a
/// redirect: the request carries `token`, and the registry is the only
/// source of addresses it may go to.
pub async fn fetch_all(
    client: &reqwest::Client,
    registry: &Registry,
    token: &str,
    deadline: Duration,
) -> Vec<Fetched> {
    let token: Arc<str> = token.into();
    let tasks: Vec<_> = registry
        .features()
        .iter()
        .filter_map(|feature| {
            // Straight to the upstream, never through the proxy, see
            // "Routing" in docs/core-architecture.md.
            let url = format!("{}{}", feature.upstream, feature.display.as_ref()?);
            let client = client.clone();
            let token = token.clone();
            let task = tokio::spawn(async move {
                tokio::time::timeout(deadline, fetch(&client, &url, &token))
                    .await
                    .unwrap_or(Err(Unavailable::TimedOut))
            });
            Some((feature.clone(), task))
        })
        .collect();

    let mut fetched = Vec::with_capacity(tasks.len());
    for (feature, task) in tasks {
        let data = task.await.unwrap_or(Err(Unavailable::Panicked));
        if let Err(reason) = &data {
            eprintln!("display data from {} unavailable: {reason}", feature.name);
        }
        fetched.push(Fetched { feature, data });
    }
    fetched
}

// One exchange, without the deadline, which the caller puts around it.
async fn fetch(
    client: &reqwest::Client,
    url: &str,
    token: &str,
) -> Result<serde_json::Value, Unavailable> {
    let mut response = client
        .get(url)
        .bearer_auth(token)
        .header(ACCEPT, "application/json")
        .send()
        .await
        .map_err(unreachable)?;

    if response.status() != StatusCode::OK {
        return Err(Unavailable::Status(response.status().as_u16()));
    }
    // Refused up front when the length is declared, and counted as it
    // arrives in case it is not, or is wrong.
    if response
        .content_length()
        .is_some_and(|len| len > MAX_BODY as u64)
    {
        return Err(Unavailable::TooLarge);
    }
    let mut body = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(unreachable)? {
        if body.len() + chunk.len() > MAX_BODY {
            return Err(Unavailable::TooLarge);
        }
        body.extend_from_slice(&chunk);
    }

    serde_json::from_slice(&body).map_err(|_| Unavailable::NotJson)
}

// The error and its causes, such as "error sending request: client error
// (Connect): tcp connect error: Connection refused". Without the URL, which
// the log line names by its feature already. Neither the error nor its
// causes hold the request's headers, so the token cannot appear.
fn unreachable(err: reqwest::Error) -> Unavailable {
    let err = err.without_url();
    let mut text = err.to_string();
    let mut source = err.source();
    while let Some(cause) = source {
        text.push_str(": ");
        text.push_str(&cause.to_string());
        source = cause.source();
    }
    Unavailable::Unreachable(text)
}

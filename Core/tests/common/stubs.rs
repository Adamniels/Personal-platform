//! Stub features on real sockets, for the tests of the core calling a
//! feature: display.rs, which calls `display::fetch_all` directly, and
//! front_page.rs, which goes through the front page.

use axum::Router;
use axum::http::HeaderMap;
use axum::routing::{any, get};
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use tokio::net::TcpListener;

/// The headers of every request a stub received, in order.
pub type Seen = Arc<Mutex<Vec<HeaderMap>>>;

/// Serves `router` on a free local port and returns its address.
pub async fn serve(router: Router) -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    addr
}

/// A feature answering every request, its display endpoint or any other
/// path, with `answer`, after recording the request's headers, so a test can
/// tell whether a feature was called at all.
pub async fn stub<R>(answer: R) -> (SocketAddr, Seen)
where
    R: Fn() -> axum::response::Response + Clone + Send + Sync + 'static,
{
    let seen: Seen = Arc::default();
    let record = seen.clone();
    let router = Router::new().fallback(any(move |headers: HeaderMap| {
        let answer = answer.clone();
        let record = record.clone();
        async move {
            record.lock().unwrap().push(headers);
            answer()
        }
    }));
    (serve(router).await, seen)
}

/// A feature that accepts the request and never answers.
pub async fn hanging_stub() -> SocketAddr {
    let router = Router::new().fallback(get(std::future::pending::<&'static str>));
    serve(router).await
}

/// An address nothing listens on: bound and then released.
pub async fn refused_addr() -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    listener.local_addr().unwrap()
}

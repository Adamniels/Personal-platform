//! The one place that decides between refreshing and logging in. Everything
//! without a valid access token is sent here, a core page or a feature alike,
//! whatever the reason, so the decision lives in one place. Until refresh
//! exists (#29) the answer is always the login page, carrying the return path
//! on. See "The refresh endpoint is the one place that decides between
//! refreshing and logging in" in docs/core-architecture.md.

use crate::AppState;
use crate::return_path::ReturnPath;
use axum::Router;
use axum::extract::Query;
use axum::response::Redirect;
use axum::routing::get;
use serde::Deserialize;

/// Where a request without a valid access token is sent.
pub const PATH: &str = "/auth/refresh";

pub fn routes() -> Router<AppState> {
    Router::new().route(PATH, get(decide))
}

#[derive(Deserialize)]
struct DecideQuery {
    #[serde(default)]
    return_to: ReturnPath,
}

async fn decide(Query(query): Query<DecideQuery>) -> Redirect {
    Redirect::to(&query.return_to.appended_to("/login"))
}

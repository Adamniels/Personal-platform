//! `GET /api/nav`, what `<platform-nav>` renders: the registered features in
//! registry order and who is logged in. The nav renders and links and does
//! nothing else, so this is all it asks for. See "Navigation" in
//! High-level/docs/platform-architecture.md.

use crate::session::ApiSession;
use crate::{AppState, accounts};
use axum::extract::State;
use axum::http::{HeaderValue, StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::{Json, Router};
use serde::Serialize;

/// Where the nav script fetches its contents.
pub const PATH: &str = "/api/nav";

pub fn routes() -> Router<AppState> {
    Router::new().route(PATH, get(serve))
}

/// The body of the endpoint. The account id is left out: the nav shows who
/// is logged in, and nothing it renders needs the id.
#[derive(Debug, Serialize)]
pub struct Nav {
    features: Vec<NavFeature>,
    user: NavUser,
}

#[derive(Debug, Serialize)]
struct NavFeature {
    name: String,
    path: String,
    // Left out for a feature that names none, so the nav draws its fallback.
    #[serde(skip_serializing_if = "Option::is_none")]
    icon: Option<String>,
}

#[derive(Debug, Serialize)]
struct NavUser {
    email: String,
}

// A valid token for an account that is gone answers like no token at all:
// there is nobody to show.
async fn serve(session: ApiSession, State(state): State<AppState>) -> Response {
    let email = match accounts::email_of(&state.pool, session.account_id).await {
        Ok(Some(email)) => email,
        Ok(None) => return StatusCode::UNAUTHORIZED.into_response(),
        Err(err) => {
            eprintln!("could not read the account for the nav: {err}");
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
    };

    let nav = Nav {
        features: state
            .registry
            .features()
            .iter()
            .map(|feature| NavFeature {
                name: feature.name.clone(),
                path: feature.prefix.clone(),
                icon: feature.icon.clone(),
            })
            .collect(),
        user: NavUser { email },
    };
    // Whose nav it is depends on the cookie, so no cache may keep it.
    (
        [(header::CACHE_CONTROL, HeaderValue::from_static("no-store"))],
        Json(nav),
    )
        .into_response()
}

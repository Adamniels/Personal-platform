//! Logout: revokes the session family the refresh token belongs to and clears
//! both cookies. It lives under `/auth` because it has to receive the refresh
//! cookie, which never reaches anything else, and it finds the family by that
//! token rather than by the access token, so logging out works after the
//! access token has expired. See "The session lives in two cookies" in
//! docs/core-architecture.md.

use crate::pages::Page;
use crate::{AppState, cookies, secret_token, sessions};
use askama::Template;
use axum::Router;
use axum::extract::State;
use axum::http::StatusCode;
use axum::response::{IntoResponse, Redirect, Response};
use axum::routing::get;
use axum_extra::extract::cookie::CookieJar;
use sqlx::PgPool;
use time::OffsetDateTime;

/// The logout page and the form it posts.
pub const PATH: &str = "/auth/logout";

/// Logs out with `presented`, the refresh cookie's value if the request had
/// one, at `now`: revokes the family of the token it names, whatever state
/// that token's row is in, since a used or expired row still names its
/// family. Text that is not a token, or matches nothing, changes nothing.
/// The revoke holds the family's lock, so a refresh of the family that was
/// part way through has committed by then and its new row is revoked too.
pub async fn logout(
    pool: &PgPool,
    presented: Option<&str>,
    now: OffsetDateTime,
) -> Result<(), sqlx::Error> {
    let Some(hash) = presented.and_then(secret_token::hash) else {
        return Ok(());
    };
    let Some(row) = sessions::find(pool, &hash).await? else {
        return Ok(());
    };
    let mut transaction = pool.begin().await?;
    sessions::lock_family(&mut *transaction, row.id).await?;
    sessions::revoke_family(&mut *transaction, row.family_id, now).await?;
    transaction.commit().await?;
    Ok(())
}

pub fn routes() -> Router<AppState> {
    // The form has no fields, so no extractor reads the body.
    Router::new().route(PATH, get(show).post(submit))
}

#[derive(Template)]
#[template(path = "logout.html")]
struct LogoutPage;

// Changes nothing, since a GET never does: the page whose button posts the
// logout. It needs no session, so an expired access token does not bounce
// through refresh on the way to logging out.
async fn show() -> Page<LogoutPage> {
    Page(LogoutPage)
}

// 303 to the login page with both cookies cleared, whether or not the refresh
// cookie named a family: the browser ends up logged out either way. A server
// error is the one other answer, 500 with the cookies left alone, so a logout
// that did not revoke the family never looks like one that did.
async fn submit(State(state): State<AppState>, jar: CookieJar) -> Response {
    let presented = jar.get(cookies::REFRESH).map(|cookie| cookie.value());
    match logout(&state.pool, presented, OffsetDateTime::now_utc()).await {
        Ok(()) => {
            let jar = CookieJar::new()
                .add(cookies::clear_access())
                .add(cookies::clear_refresh());
            (jar, Redirect::to("/login")).into_response()
        }
        Err(err) => {
            eprintln!("logout failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

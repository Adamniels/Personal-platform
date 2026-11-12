//! The one place that decides between refreshing and logging in. Everything
//! without a valid access token is sent here, a core page or a feature alike,
//! whatever the reason, so the decision lives in one place: `decide`, on the
//! stored token and the time. See "The refresh endpoint is the one place that
//! decides between refreshing and logging in" in docs/core-architecture.md.

use crate::pages::Page;
use crate::return_path::ReturnPath;
use crate::{AppState, cookies, secret_token, sessions};
use askama::Template;
use axum::Router;
use axum::extract::{DefaultBodyLimit, Form, Query, State};
use axum::http::{HeaderValue, StatusCode, header};
use axum::response::{IntoResponse, Redirect, Response};
use axum::routing::get;
use axum_extra::extract::cookie::CookieJar;
use serde::Deserialize;
use std::error::Error;
use time::OffsetDateTime;
use uuid::Uuid;

/// Where a request without a valid access token is sent.
pub const PATH: &str = "/auth/refresh";

/// What a presented refresh token comes to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    /// Mark it used and issue the next token of its family.
    Rotate,
    /// Send the browser to the login page.
    LogIn,
}

/// Decides on `row`, the stored token a refresh presented, or `None` when
/// nothing matched it, at `now`. One check per line, in order, each ending
/// the decision, so the grace window and reuse detection arrive as new
/// outcomes of the used check without touching the others.
pub fn decide(row: Option<&sessions::Row>, now: OffsetDateTime) -> Decision {
    let Some(row) = row else {
        return Decision::LogIn;
    };
    if row.revoked_at.is_some() {
        return Decision::LogIn;
    }
    // Until the grace window and reuse detection: two tabs refreshing at once
    // can send one of them to login, which is known and temporary.
    if row.used_at.is_some() {
        return Decision::LogIn;
    }
    if now >= row.expires_at || now >= row.family_expires_at {
        return Decision::LogIn;
    }
    // A disabled account ends its sessions here, at their next refresh,
    // rather than when the family's cap runs out. Last, after the checks on
    // the token itself, so that reuse detection, when it arrives in the used
    // check, sees a replayed token whatever the account's state.
    if !row.account_active {
        return Decision::LogIn;
    }
    Decision::Rotate
}

/// What a refresh came to.
pub enum Outcome {
    /// The presented token was rotated: a new access token and the next
    /// refresh token of the family.
    Refreshed {
        access_token: String,
        session: sessions::Issued,
    },
    /// Anything else: no token, one that is not a token, unknown, used,
    /// revoked or expired, or an account no longer active.
    LogIn,
}

/// Refreshes with `presented`, the refresh cookie's value if the request had
/// one, at `now`. Text that is not a token finds nothing. A rotation another
/// request won first is a login for now, as a used token is.
pub async fn refresh(
    state: &AppState,
    presented: Option<&str>,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let row = match presented.and_then(secret_token::hash) {
        Some(hash) => sessions::find(&state.pool, &hash).await?,
        None => None,
    };
    let (Decision::Rotate, Some(row)) = (decide(row.as_ref(), now), row) else {
        return Ok(Outcome::LogIn);
    };
    let Some(session) = sessions::rotate(&state.pool, row.id, now).await? else {
        return Ok(Outcome::LogIn);
    };
    let access_token = state
        .issuer
        .issue(row.account_id, session.family_id, Uuid::now_v7(), now);
    Ok(Outcome::Refreshed {
        access_token,
        session,
    })
}

pub fn routes() -> Router<AppState> {
    Router::new().route(
        PATH,
        get(show)
            .post(submit)
            .layer(DefaultBodyLimit::max(16 * 1024)),
    )
}

#[derive(Template)]
#[template(path = "refresh.html")]
struct RefreshPage<'a> {
    return_to: &'a str,
}

#[derive(Deserialize)]
struct RefreshQuery {
    #[serde(default)]
    return_to: ReturnPath,
}

// Changes nothing, since a GET never does. With a refresh cookie, the page
// whose form posts the refresh; without one there is nothing to refresh, so
// straight to login. Never stored, since it is only ever a step on the way.
async fn show(jar: CookieJar, Query(query): Query<RefreshQuery>) -> Response {
    let has_refresh_cookie = jar
        .get(cookies::REFRESH)
        .is_some_and(|cookie| !cookie.value().is_empty());
    if !has_refresh_cookie {
        return Redirect::to(&query.return_to.appended_to("/login")).into_response();
    }
    let page = RefreshPage {
        return_to: query.return_to.as_str(),
    };
    (
        [(header::CACHE_CONTROL, HeaderValue::from_static("no-store"))],
        Page(page),
    )
        .into_response()
}

#[derive(Deserialize)]
struct RefreshForm {
    #[serde(default)]
    return_to: ReturnPath,
}

// Always 303, for a form and a fetch alike: back to the return path with both
// cookies set, or to login with both cleared, so a dead cookie is not sent
// again on the way back.
async fn submit(
    State(state): State<AppState>,
    jar: CookieJar,
    Form(form): Form<RefreshForm>,
) -> Response {
    let now = OffsetDateTime::now_utc();
    let presented = jar.get(cookies::REFRESH).map(|cookie| cookie.value());
    match refresh(&state, presented, now).await {
        Ok(Outcome::Refreshed {
            access_token,
            session,
        }) => {
            let jar = CookieJar::new()
                .add(cookies::access(access_token, state.issuer.lifetime()))
                .add(cookies::refresh(
                    session.refresh_token,
                    session.expires_at - now,
                ));
            (jar, Redirect::to(&form.return_to.location())).into_response()
        }
        Ok(Outcome::LogIn) => {
            let jar = CookieJar::new()
                .add(cookies::clear_access())
                .add(cookies::clear_refresh());
            (jar, Redirect::to(&form.return_to.appended_to("/login"))).into_response()
        }
        Err(err) => {
            eprintln!("refresh failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use time::Duration;
    use uuid::Uuid;

    fn at(seconds: i64) -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
    }

    // Issued at 0, expiring at 1000 within a family capped at 2000: unused,
    // unrevoked, its account active.
    fn live() -> sessions::Row {
        sessions::Row {
            id: Uuid::now_v7(),
            account_id: Uuid::now_v7(),
            family_id: Uuid::now_v7(),
            expires_at: at(1000),
            family_expires_at: at(2000),
            used_at: None,
            revoked_at: None,
            account_active: true,
        }
    }

    #[test]
    fn a_live_token_rotates() {
        assert_eq!(decide(Some(&live()), at(10)), Decision::Rotate);
    }

    #[test]
    fn an_unknown_revoked_or_used_token_goes_to_login() {
        let revoked = sessions::Row {
            revoked_at: Some(at(5)),
            ..live()
        };
        let used = sessions::Row {
            used_at: Some(at(5)),
            ..live()
        };
        let both = sessions::Row {
            used_at: Some(at(5)),
            revoked_at: Some(at(6)),
            ..live()
        };
        assert_eq!(decide(None, at(10)), Decision::LogIn, "unknown");
        assert_eq!(decide(Some(&revoked), at(10)), Decision::LogIn, "revoked");
        assert_eq!(decide(Some(&used), at(10)), Decision::LogIn, "used");
        assert_eq!(decide(Some(&both), at(10)), Decision::LogIn, "both");
    }

    // Disabling an account ends its sessions at their next refresh, not when
    // the family's cap runs out.
    #[test]
    fn an_inactive_account_goes_to_login() {
        let disabled = sessions::Row {
            account_active: false,
            ..live()
        };
        assert_eq!(decide(Some(&disabled), at(10)), Decision::LogIn);
    }

    // expires_at is the first instant a token no longer works.
    #[test]
    fn the_fourteen_day_expiry_is_exact() {
        let row = live();
        let second = Duration::seconds(1);
        assert_eq!(
            decide(Some(&row), row.expires_at - second),
            Decision::Rotate
        );
        assert_eq!(decide(Some(&row), row.expires_at), Decision::LogIn);
        assert_eq!(decide(Some(&row), row.expires_at + second), Decision::LogIn);
    }

    // The cap decides even when the token's own expiry would still allow it,
    // which a row never holds, since the table refuses expires_at after the
    // cap; deciding on both keeps the check from resting on that alone.
    #[test]
    fn the_thirty_day_cap_is_exact() {
        let row = sessions::Row {
            expires_at: at(3000),
            ..live()
        };
        let second = Duration::seconds(1);
        assert_eq!(
            decide(Some(&row), row.family_expires_at - second),
            Decision::Rotate
        );
        assert_eq!(decide(Some(&row), row.family_expires_at), Decision::LogIn);
        assert_eq!(
            decide(Some(&row), row.family_expires_at + second),
            Decision::LogIn
        );
    }
}

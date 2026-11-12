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
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

/// Where a request without a valid access token is sent.
pub const PATH: &str = "/auth/refresh";

/// How long after a token's use presenting it again is still not reuse: a
/// token whose `used_at` is at most this long before `now` is grace, one
/// second more is reuse. See "A token used within the last thirty seconds is
/// not reuse" in docs/core-architecture.md.
pub const GRACE_WINDOW: Duration = Duration::seconds(30);

/// What a presented refresh token comes to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    /// Mark it used and issue the next token of its family.
    Rotate,
    /// A token used moments ago, by another tab or another request of the
    /// same page: a new access token for its family, and no rotation.
    Grace,
    /// A token used longer ago than the grace window: a copy of it exists, so
    /// the family is revoked.
    Reuse,
    /// Send the browser to the login page.
    LogIn,
}

/// Decides on `row`, the stored token a refresh presented, or `None` when
/// nothing matched it, at `now`. One check per line, in order, each ending
/// the decision.
pub fn decide(row: Option<&sessions::Row>, now: OffsetDateTime) -> Decision {
    let Some(row) = row else {
        return Decision::LogIn;
    };
    // A revoked family has nothing left to revoke, so a replay of one of its
    // tokens is not reported again.
    if row.revoked_at.is_some() {
        return Decision::LogIn;
    }
    // Before the expiry checks, so a replay is caught however old the token.
    if let Some(used_at) = row.used_at {
        if now - used_at > GRACE_WINDOW {
            return Decision::Reuse;
        }
        // Grace issues an access token, which an inactive account never gets.
        return if row.account_active {
            Decision::Grace
        } else {
            Decision::LogIn
        };
    }
    if now >= row.expires_at || now >= row.family_expires_at {
        return Decision::LogIn;
    }
    // A disabled account ends its sessions here, at their next refresh,
    // rather than when the family's cap runs out. Last, after the checks on
    // the token itself, so that reuse is decided whatever the account's state.
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
    /// The presented token was used within the grace window: a new access
    /// token for its family, and no refresh token, since the request that
    /// used it has already set the next one.
    Grace { access_token: String },
    /// The presented token was used before the grace window, so a copy of
    /// it exists: the family is revoked, and the browser goes to login as it
    /// would with a dead token. The ids are for the warning, which is the one
    /// trace a possible theft leaves.
    Reused { account_id: Uuid, family_id: Uuid },
    /// Anything else: no token, one that is not a token, unknown, revoked or
    /// expired, or an account no longer active.
    LogIn,
}

/// Refreshes with `presented`, the refresh cookie's value if the request had
/// one, at `now`. Text that is not a token finds nothing.
pub async fn refresh(
    state: &AppState,
    presented: Option<&str>,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let Some(hash) = presented.and_then(secret_token::hash) else {
        return Ok(Outcome::LogIn);
    };
    let row = sessions::find(&state.pool, &hash).await?;
    let decision = decide(row.as_ref(), now);
    if let (Decision::Rotate, Some(row)) = (decision, &row) {
        if let Some(session) = sessions::rotate(&state.pool, row.id, now).await? {
            let access_token =
                state
                    .issuer
                    .issue(row.account_id, session.family_id, Uuid::now_v7(), now);
            return Ok(Outcome::Refreshed {
                access_token,
                session,
            });
        }
        // The conditional update changed nothing, so this request lost a
        // race: another one rotated the token first, or a logout or a reuse
        // revoked the family while this one waited on its lock. Read the row
        // as it is now and decide again, which gives grace or a login.
        let row = sessions::find(&state.pool, &hash).await?;
        let decision = decide(row.as_ref(), now);
        return settle(state, decision, row.as_ref(), now).await;
    }
    settle(state, decision, row.as_ref(), now).await
}

// Carries out every decision but a rotation. A rotation reaching here is one
// the row no longer allows by the time it is decided again, which the
// conditional update has just shown, so it is a login rather than a second
// attempt that could loop.
async fn settle(
    state: &AppState,
    decision: Decision,
    row: Option<&sessions::Row>,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    match (decision, row) {
        (Decision::Grace, Some(row)) => Ok(Outcome::Grace {
            access_token: state
                .issuer
                .issue(row.account_id, row.family_id, Uuid::now_v7(), now),
        }),
        (Decision::Reuse, Some(row)) => revoke_for_reuse(state, row, now).await,
        _ => Ok(Outcome::LogIn),
    }
}

// Revokes the family of `row`, a token presented after its grace window, as
// logout does: in one transaction holding the family's lock, so a rotation of
// the family's newest token that was part way through has committed first and
// the token it stored is revoked too. That token is the thief's in the case
// reuse detection exists for. A revoke that marks nothing found the family
// already revoked, by a logout or another replay since the row was read, and
// is a login, so one theft is reported once.
async fn revoke_for_reuse(
    state: &AppState,
    row: &sessions::Row,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let mut transaction = state.pool.begin().await?;
    sessions::lock_family(&mut *transaction, row.id).await?;
    let revoked = sessions::revoke_family(&mut *transaction, row.family_id, now).await?;
    transaction.commit().await?;
    if revoked == 0 {
        return Ok(Outcome::LogIn);
    }
    Ok(Outcome::Reused {
        account_id: row.account_id,
        family_id: row.family_id,
    })
}

/// The line logged when a family is revoked for reuse. It takes the ids and
/// nothing else, so the token can never end up in the log.
pub fn reuse_warning(account_id: Uuid, family_id: Uuid) -> String {
    format!("warning: refresh token reuse, account {account_id}, family {family_id} revoked")
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

// 303, for a form and a fetch alike: back to the return path with both cookies
// set, or with the access cookie alone inside the grace window, or to login
// with both cleared, so a dead cookie is not sent again on the way back. A
// server error is the one other answer, 500 with the cookies left
// alone, since an outage is not a dead session and clearing them would log
// everyone out over a blip.
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
        // The access cookie alone: the request that used the token has set
        // the next refresh token, and touching the refresh cookie here, with a
        // value or a clear, would race that response in the shared jar.
        Ok(Outcome::Grace { access_token }) => {
            let jar = CookieJar::new().add(cookies::access(access_token, state.issuer.lifetime()));
            (jar, Redirect::to(&form.return_to.location())).into_response()
        }
        // Reuse answers exactly as a dead token does; the warning is the
        // only difference, and it never holds the token.
        Ok(Outcome::Reused {
            account_id,
            family_id,
        }) => {
            eprintln!("{}", reuse_warning(account_id, family_id));
            to_login(&form.return_to)
        }
        Ok(Outcome::LogIn) => to_login(&form.return_to),
        Err(err) => {
            eprintln!("refresh failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

// Both cookies cleared, so a dead cookie is not sent again on the way back,
// and to login with the return path.
fn to_login(return_to: &ReturnPath) -> Response {
    let jar = CookieJar::new()
        .add(cookies::clear_access())
        .add(cookies::clear_refresh());
    (jar, Redirect::to(&return_to.appended_to("/login"))).into_response()
}

#[cfg(test)]
mod tests {
    use super::*;
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
    fn an_unknown_or_revoked_token_goes_to_login() {
        let revoked = sessions::Row {
            revoked_at: Some(at(5)),
            ..live()
        };
        assert_eq!(decide(None, at(10)), Decision::LogIn, "unknown");
        assert_eq!(decide(Some(&revoked), at(10)), Decision::LogIn, "revoked");
    }

    // Used at 100: up to and including thirty seconds later is grace, the
    // second after that is reuse.
    #[test]
    fn used_29_and_30_seconds_ago_is_grace_and_31_is_reuse() {
        let used = sessions::Row {
            used_at: Some(at(100)),
            ..live()
        };
        assert_eq!(decide(Some(&used), at(100)), Decision::Grace, "0");
        assert_eq!(decide(Some(&used), at(129)), Decision::Grace, "29");
        assert_eq!(decide(Some(&used), at(130)), Decision::Grace, "30");
        assert_eq!(decide(Some(&used), at(131)), Decision::Reuse, "31");
        assert_eq!(
            decide(Some(&used), at(100) + GRACE_WINDOW),
            Decision::Grace,
            "the window itself"
        );
    }

    // A revoked family has nothing left to revoke, and its theft, if it was
    // one, was reported when it was revoked.
    #[test]
    fn a_used_token_from_a_revoked_family_goes_to_login() {
        let row = sessions::Row {
            used_at: Some(at(100)),
            revoked_at: Some(at(200)),
            ..live()
        };
        assert_eq!(
            decide(Some(&row), at(110)),
            Decision::LogIn,
            "in the window"
        );
        assert_eq!(decide(Some(&row), at(500)), Decision::LogIn, "outside it");
    }

    // The used check comes before the expiry checks, so a replay of an old
    // token is still caught after the token itself would have expired.
    #[test]
    fn a_used_token_is_judged_as_used_whatever_its_expiry() {
        let used = sessions::Row {
            used_at: Some(at(100)),
            ..live()
        };
        let expired = used.expires_at + Duration::seconds(1);
        let capped = used.family_expires_at + Duration::seconds(1);
        assert_eq!(decide(Some(&used), expired), Decision::Reuse, "expired");
        assert_eq!(decide(Some(&used), capped), Decision::Reuse, "past the cap");
        let used_late = sessions::Row {
            used_at: Some(used.expires_at - Duration::seconds(31)),
            ..live()
        };
        assert_eq!(
            decide(Some(&used_late), used_late.expires_at),
            Decision::Reuse,
            "31 seconds after use, at the expiry"
        );
    }

    // Grace issues an access token, which an inactive account never gets;
    // reuse revokes and logs whatever the account's state.
    #[test]
    fn an_inactive_account_gets_no_grace_but_reuse_still_revokes() {
        let used = sessions::Row {
            used_at: Some(at(100)),
            account_active: false,
            ..live()
        };
        assert_eq!(
            decide(Some(&used), at(130)),
            Decision::LogIn,
            "in the window"
        );
        assert_eq!(decide(Some(&used), at(131)), Decision::Reuse, "outside it");
    }

    #[test]
    fn the_reuse_warning_names_the_account_and_the_family() {
        let (account_id, family_id) = (Uuid::now_v7(), Uuid::now_v7());
        let line = reuse_warning(account_id, family_id);
        assert!(line.starts_with("warning: refresh token reuse"), "{line}");
        assert!(line.contains(&format!("account {account_id}")), "{line}");
        assert!(line.contains(&format!("family {family_id}")), "{line}");
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

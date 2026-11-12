//! Changing a password while logged in: the current password and a new one.
//! The password is set and every other session of the account revoked in one
//! transaction, keeping the family the request's session belongs to, read
//! from the access token's `sid` claim, since the refresh cookie never
//! reaches this page. See "Password reset, and changing a password" in
//! docs/core-architecture.md.

use crate::pages::Page;
use crate::session::Session;
use crate::{AppState, accounts, password, sessions};
use askama::Template;
use axum::Router;
use axum::extract::{DefaultBodyLimit, Form, Query, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Redirect, Response};
use axum::routing::get;
use serde::Deserialize;
use std::error::Error;
use time::OffsetDateTime;

/// The page and the form it posts.
pub const PATH: &str = "/settings/password";

/// Where a change that succeeded lands, so a reload shows the confirmation
/// rather than posting the form again.
const CHANGED_PATH: &str = "/settings/password?changed=1";

/// The one answer for a current password that does not check out, and for
/// an account that may not change it.
pub const INCORRECT_CURRENT: &str = "Current password is incorrect.";

/// What a change came to.
#[derive(Debug, PartialEq, Eq)]
pub enum Outcome {
    Changed,
    /// A wrong current password or an account that is not active, never
    /// told apart. Nothing was changed.
    IncorrectCurrent,
    /// The new password breaks the rule; the message says how. Nothing was
    /// changed.
    InvalidPassword(&'static str),
}

/// Changes the password of the account `session` belongs to from `current`
/// to `new` at `now`. The current password is verified first, then the new
/// one checked against the rule and hashed, outside any transaction, since
/// hashing is slow. Then, in one transaction, the password is set and every
/// family of the account revoked except `session.family_id`; a failure
/// anywhere rolls both back. Both passwords are taken as typed.
///
/// An account that is not active is refused after its password has been
/// verified, as login does, so it costs the same as a wrong password. One
/// that is gone, which a valid token for it can only mean if it was deleted,
/// is verified against the dummy hash and refused the same way.
pub async fn change(
    state: &AppState,
    session: &Session,
    current: &str,
    new: &str,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let Some(account) = accounts::find_by_id(&state.pool, session.account_id).await? else {
        password::verify(current, &state.dummy_hash).await?;
        return Ok(Outcome::IncorrectCurrent);
    };
    if !password::verify(current, &account.password_hash).await? || !account.is_active() {
        return Ok(Outcome::IncorrectCurrent);
    }
    if let Err(message) = password::check_new(new) {
        return Ok(Outcome::InvalidPassword(message));
    }
    let password_hash = password::hash(new).await?;

    let mut transaction = state.pool.begin().await?;
    accounts::set_password(&mut *transaction, account.id, &password_hash).await?;
    sessions::revoke_account(&mut transaction, account.id, Some(session.family_id), now).await?;
    transaction.commit().await?;
    Ok(Outcome::Changed)
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
#[template(path = "change_password.html")]
struct ChangePasswordPage<'a> {
    error: Option<&'a str>,
    changed: bool,
}

#[derive(Deserialize)]
struct Shown {
    changed: Option<String>,
}

#[derive(Deserialize)]
struct ChangeForm {
    current_password: String,
    new_password: String,
}

// Takes `Session` only to require one: the page shows nothing of the
// account, but without a valid access token it goes to the deciding
// endpoint and comes back, like any core page.
async fn show(_: Session, Query(shown): Query<Shown>) -> Page<ChangePasswordPage<'static>> {
    Page(ChangePasswordPage {
        error: None,
        changed: shown.changed.is_some(),
    })
}

// 303 to the confirmation on success, with the cookies left alone: this
// session goes on. A refusal is 422 and the form again, never with a
// password in it. `Session` comes before the form, so a request without one
// is sent away before its body is read.
async fn submit(
    State(state): State<AppState>,
    session: Session,
    Form(form): Form<ChangeForm>,
) -> Response {
    let refused = |error| {
        let page = ChangePasswordPage {
            error: Some(error),
            changed: false,
        };
        (StatusCode::UNPROCESSABLE_ENTITY, Page(page)).into_response()
    };

    match change(
        &state,
        &session,
        &form.current_password,
        &form.new_password,
        OffsetDateTime::now_utc(),
    )
    .await
    {
        Ok(Outcome::Changed) => Redirect::to(CHANGED_PATH).into_response(),
        Ok(Outcome::IncorrectCurrent) => refused(INCORRECT_CURRENT),
        Ok(Outcome::InvalidPassword(message)) => refused(message),
        Err(err) => {
            eprintln!("changing a password failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

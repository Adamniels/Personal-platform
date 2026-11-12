//! Password reset: a token from the reset command and a new password. The
//! token is consumed, the password set and every session of the account
//! revoked in one transaction, so a token is never spent without the password
//! changing. Resetting does not log you in; it sends you to the login page,
//! keeping one path by which something becomes a session. See "Password
//! reset, and changing a password" in docs/core-architecture.md.

use crate::pages::{FormError, Page};
use crate::{AppState, accounts, password, reset_token, secret_token, sessions};
use askama::Template;
use axum::Router;
use axum::extract::{DefaultBodyLimit, Form, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Redirect, Response};
use axum::routing::get;
use serde::Deserialize;
use sqlx::PgPool;
use std::error::Error;
use time::OffsetDateTime;

/// The one answer for a token that cannot be used.
pub const INVALID_TOKEN: &str = "This reset token is not valid.";

/// What a reset came to.
#[derive(Debug, PartialEq, Eq)]
pub enum Outcome {
    Reset,
    /// Unknown, malformed, used or expired, never told apart.
    InvalidToken,
    /// The new password breaks the rule; the message says how. Nothing was
    /// looked up, so the token is still unused.
    InvalidPassword(&'static str),
}

/// Resets the password of the account `token` is for to `new_password` at
/// `now`. The password is checked first, then hashed outside any
/// transaction, since hashing is slow. Then, in one transaction, the token is
/// consumed, the password set and every family of the account revoked; a
/// failure anywhere rolls all of it back. The token is trimmed, as a paste
/// from a terminal brings whitespace along; the password is taken as typed.
pub async fn reset(
    pool: &PgPool,
    token: &str,
    new_password: &str,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    if let Err(message) = password::check_new(new_password) {
        return Ok(Outcome::InvalidPassword(message));
    }
    // Text that is not a token cannot match anything stored, so it is refused
    // before the slow hash.
    let Some(token_hash) = secret_token::hash(token.trim()) else {
        return Ok(Outcome::InvalidToken);
    };
    let password_hash = password::hash(new_password).await?;

    let mut transaction = pool.begin().await?;
    let Some(account_id) = reset_token::consume(&mut *transaction, &token_hash, now).await? else {
        transaction.rollback().await?;
        return Ok(Outcome::InvalidToken);
    };
    accounts::set_password(&mut *transaction, account_id, &password_hash).await?;
    sessions::revoke_account(&mut transaction, account_id, None, now).await?;
    transaction.commit().await?;
    Ok(Outcome::Reset)
}

pub fn routes() -> Router<AppState> {
    Router::new().route(
        "/reset",
        get(show)
            .post(submit)
            .layer(DefaultBodyLimit::max(16 * 1024)),
    )
}

#[derive(Template)]
#[template(path = "reset.html")]
struct ResetPage<'a> {
    error: Option<FormError<'a>>,
    token: &'a str,
}

#[derive(Deserialize)]
struct ResetForm {
    token: String,
    password: String,
}

async fn show() -> Response {
    Page(ResetPage {
        error: None,
        token: "",
    })
    .into_response()
}

// 303 to the login page on success, with no cookie set: a reset does not log
// you in. A refusal is 422 and the form again, keeping the token and never
// the password.
async fn submit(State(pool): State<PgPool>, Form(form): Form<ResetForm>) -> Response {
    let refused = |error| {
        let page = ResetPage {
            error: Some(error),
            token: form.token.trim(),
        };
        (StatusCode::UNPROCESSABLE_ENTITY, Page(page)).into_response()
    };

    match reset(
        &pool,
        &form.token,
        &form.password,
        OffsetDateTime::now_utc(),
    )
    .await
    {
        Ok(Outcome::Reset) => Redirect::to("/login").into_response(),
        Ok(Outcome::InvalidToken) => refused(FormError::on("token", INVALID_TOKEN)),
        Ok(Outcome::InvalidPassword(message)) => refused(FormError::on("password", message)),
        Err(err) => {
            eprintln!("password reset failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

//! Login: a plain form for an email and a password. Success starts a session
//! family and sets both cookies; any refusal looks the same, whether the email
//! is unknown, the password wrong or the account disabled, down to the time
//! it takes. See "The session" and "No defence against user enumeration" in
//! docs/core-architecture.md.

use crate::pages::{FormError, Page};
use crate::return_path::ReturnPath;
use crate::{AppState, accounts, cookies, password, sessions};
use askama::Template;
use axum::Router;
use axum::extract::{DefaultBodyLimit, Form, Query, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Redirect, Response};
use axum::routing::get;
use axum_extra::extract::cookie::CookieJar;
use serde::Deserialize;
use std::error::Error;
use time::OffsetDateTime;
use uuid::Uuid;

const REFUSED: &str = "Email or password is incorrect.";

/// What a login came to.
pub enum Outcome {
    LoggedIn {
        access_token: String,
        session: sessions::Issued,
    },
    /// Unknown email, wrong password or a disabled account, never told apart.
    Refused,
}

/// Logs in `email` with `password` at `now`. The email is trimmed and
/// lowercased; the password is taken as typed, with no length rule, since the
/// rule belongs to choosing one. Every path verifies a password once, against
/// the dummy hash when the email is unknown, so the time taken does not say
/// which refusal it was.
pub async fn log_in(
    state: &AppState,
    email: &str,
    password: &str,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let email = email.trim().to_lowercase();
    let Some(account) = accounts::find_by_email(&state.pool, &email).await? else {
        password::verify(password, &state.dummy_hash).await?;
        return Ok(Outcome::Refused);
    };
    // A disabled account's password is checked first, so it costs the same.
    if !password::verify(password, &account.password_hash).await? || !account.is_active() {
        return Ok(Outcome::Refused);
    }

    let session = sessions::start(&state.pool, account.id, now).await?;
    let access_token = state
        .issuer
        .issue(account.id, session.family_id, Uuid::now_v7(), now);
    Ok(Outcome::LoggedIn {
        access_token,
        session,
    })
}

pub fn routes() -> Router<AppState> {
    Router::new().route(
        "/login",
        get(show)
            .post(submit)
            .layer(DefaultBodyLimit::max(16 * 1024)),
    )
}

#[derive(Template)]
#[template(path = "login.html")]
struct LoginPage<'a> {
    error: Option<FormError<'a>>,
    email: &'a str,
    return_to: &'a str,
}

#[derive(Deserialize)]
struct LoginQuery {
    #[serde(default)]
    return_to: ReturnPath,
}

#[derive(Deserialize)]
struct LoginForm {
    email: String,
    password: String,
    #[serde(default)]
    return_to: ReturnPath,
}

async fn show(Query(query): Query<LoginQuery>) -> Response {
    Page(LoginPage {
        error: None,
        email: "",
        return_to: query.return_to.as_str(),
    })
    .into_response()
}

async fn submit(State(state): State<AppState>, Form(form): Form<LoginForm>) -> Response {
    let now = OffsetDateTime::now_utc();
    match log_in(&state, &form.email, &form.password, now).await {
        Ok(Outcome::LoggedIn {
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
        Ok(Outcome::Refused) => {
            let page = LoginPage {
                error: Some(FormError::on("password", REFUSED)),
                email: form.email.trim(),
                return_to: form.return_to.as_str(),
            };
            (StatusCode::UNPROCESSABLE_ENTITY, Page(page)).into_response()
        }
        Err(err) => {
            eprintln!("login failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

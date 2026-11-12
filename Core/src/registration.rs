//! Registration: a plain form for an invite code, an email and a password.
//! The code is consumed and the account created in one transaction, so a
//! failure of either leaves neither. Registering does not log you in; it sends
//! you to the login page, keeping one path by which something becomes a
//! session. See "Registration is open but invite coded" and "No defence
//! against user enumeration" in docs/core-architecture.md.

use crate::pages::Page;
use crate::secret_token::{self, TokenHash};
use crate::{AppState, accounts, invite, password};
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
use uuid::Uuid;

const EMAIL_MAX_LENGTH: usize = 254;

const INVALID_CODE: &str = "This invite code is not valid.";
const EMAIL_TAKEN: &str = "An account with this email already exists.";
const INVALID_EMAIL: &str = "Enter a valid email address.";

/// What a registration came to.
#[derive(Debug, PartialEq, Eq)]
pub enum Outcome {
    Registered,
    /// Unknown, used, expired or malformed, never told apart.
    InvalidCode,
    /// Only ever reported to someone whose code was valid, since the code is
    /// consumed before the account is inserted.
    EmailTaken,
}

/// A submission that passed the rules that need no database.
pub struct Input {
    code_hash: Option<TokenHash>,
    email: String,
    password: String,
}

/// The rules, before anything is hashed or stored. The email is trimmed and
/// lowercased, the code trimmed, and the password taken exactly as typed.
pub fn validate(code: &str, email: &str, password: &str) -> Result<Input, &'static str> {
    let email = email.trim().to_lowercase();
    let well_formed = match email.split_once('@') {
        Some((local, domain)) => !local.is_empty() && !domain.is_empty() && !domain.contains('@'),
        None => false,
    };
    if !well_formed || email.chars().count() > EMAIL_MAX_LENGTH {
        return Err(INVALID_EMAIL);
    }
    password::check_new(password)?;
    Ok(Input {
        code_hash: secret_token::hash(code.trim()),
        email,
        password: password.to_owned(),
    })
}

/// Registers `input` at `now`. The password is hashed first, outside the
/// transaction, since it is slow. Then, in one transaction, the code is
/// consumed and the account inserted; either failing rolls back both, so a
/// taken email hands its code back unused.
pub async fn register(
    pool: &PgPool,
    input: Input,
    now: OffsetDateTime,
) -> Result<Outcome, Box<dyn Error + Send + Sync>> {
    let Some(code_hash) = input.code_hash else {
        return Ok(Outcome::InvalidCode);
    };
    let password_hash = password::hash(&input.password).await?;

    let mut tx = pool.begin().await?;
    let id = Uuid::now_v7();

    if !invite::consume(&mut *tx, &code_hash, id, now).await? {
        tx.rollback().await?;
        return Ok(Outcome::InvalidCode);
    }
    if !accounts::create(&mut *tx, id, &input.email, &password_hash, now).await? {
        tx.rollback().await?;
        return Ok(Outcome::EmailTaken);
    }
    // The code's used_by is checked here, against the account just inserted.
    tx.commit().await?;
    Ok(Outcome::Registered)
}

pub fn routes() -> Router<AppState> {
    Router::new().route(
        "/register",
        get(show)
            .post(submit)
            .layer(DefaultBodyLimit::max(16 * 1024)),
    )
}

#[derive(Template)]
#[template(path = "register.html")]
struct RegisterPage<'a> {
    error: Option<&'a str>,
    code: &'a str,
    email: &'a str,
}

#[derive(Deserialize)]
struct RegisterForm {
    code: String,
    email: String,
    password: String,
}

async fn show() -> Response {
    Page(RegisterPage {
        error: None,
        code: "",
        email: "",
    })
    .into_response()
}

async fn submit(State(pool): State<PgPool>, Form(form): Form<RegisterForm>) -> Response {
    let refused = |error| {
        let page = RegisterPage {
            error: Some(error),
            code: form.code.trim(),
            email: form.email.trim(),
        };
        (StatusCode::UNPROCESSABLE_ENTITY, Page(page)).into_response()
    };

    let input = match validate(&form.code, &form.email, &form.password) {
        Ok(input) => input,
        Err(error) => return refused(error),
    };

    match register(&pool, input, OffsetDateTime::now_utc()).await {
        Ok(Outcome::Registered) => Redirect::to("/login").into_response(),
        Ok(Outcome::InvalidCode) => refused(INVALID_CODE),
        Ok(Outcome::EmailTaken) => refused(EMAIL_TAKEN),
        Err(err) => {
            eprintln!("registration failed: {err}");
            (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const PASSWORD: &str = "fifteen chars!!";

    #[test]
    fn the_email_is_trimmed_and_lowercased() {
        let input = validate("", "  Walk@Example.COM\n", PASSWORD).unwrap();
        assert_eq!(input.email, "walk@example.com");
    }

    #[test]
    fn an_email_needs_one_at_with_something_on_both_sides() {
        for email in [
            "",
            "walk",
            "@example.com",
            "walk@",
            "walk@@example.com",
            "a@b@c",
        ] {
            assert_eq!(
                validate("", email, PASSWORD).err(),
                Some(INVALID_EMAIL),
                "{email:?}"
            );
        }
        let long = format!("{}@example.com", "a".repeat(EMAIL_MAX_LENGTH));
        assert_eq!(validate("", &long, PASSWORD).err(), Some(INVALID_EMAIL));
    }

    #[test]
    fn a_password_is_never_trimmed() {
        let input = validate("", "a@b", "  padded password  ").unwrap();
        assert_eq!(input.password, "  padded password  ");
    }

    #[test]
    fn a_code_is_trimmed_and_a_malformed_one_has_no_hash() {
        let (code, hash) = secret_token::generate();
        let pasted = format!("  {code}\n");
        assert_eq!(
            validate(&pasted, "a@b", PASSWORD).unwrap().code_hash,
            Some(hash)
        );
        assert_eq!(
            validate("not a code", "a@b", PASSWORD).unwrap().code_hash,
            None
        );
    }
}

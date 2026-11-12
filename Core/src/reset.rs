//! Password reset: a token from the reset command and a new password. The
//! token is consumed, the password set and every session of the account
//! revoked in one transaction, so a token is never spent without the password
//! changing. Resetting does not log you in; it sends you to the login page,
//! keeping one path by which something becomes a session. See "Password
//! reset, and changing a password" in docs/core-architecture.md.

use crate::{accounts, password, reset_token, secret_token, sessions};
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

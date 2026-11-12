//! Changing a password while logged in: the current password and a new one.
//! The password is set and every other session of the account revoked in one
//! transaction, keeping the family the request's session belongs to, read
//! from the access token's `sid` claim, since the refresh cookie never
//! reaches this page. See "Password reset, and changing a password" in
//! docs/core-architecture.md.

use crate::session::Session;
use crate::{AppState, accounts, password, sessions};
use std::error::Error;
use time::OffsetDateTime;

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

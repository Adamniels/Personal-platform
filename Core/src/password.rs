//! Passwords, hashed with Argon2id at the library's recommended parameters
//! and stored as PHC strings, which carry those parameters with them. No
//! pepper, see "The signing key" in docs/core-architecture.md.
//!
//! Both directions take tens of milliseconds of CPU, so they run on tokio's
//! blocking pool rather than holding up the request handlers sharing a worker.
//!
//! The rule for choosing one lives here too, so registration and reset ask
//! the same thing of a password; logging in asks nothing of it.

use argon2::Argon2;
use argon2::password_hash::phc::PasswordHash;
use argon2::password_hash::{PasswordHasher, PasswordVerifier};
use std::fmt;

#[derive(Debug)]
pub struct Error(String);

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

const NEW_LENGTH: std::ops::RangeInclusive<usize> = 15..=1024;
const INVALID_NEW: &str = "Choose a password of 15 to 1024 characters.";

/// Whether `password` may be chosen as a new one: 15 to 1024 characters,
/// counted as characters and taken exactly as typed, never trimmed. The error
/// is the message the form shows. See "The core's data" in
/// docs/core-architecture.md for why that is the whole rule.
pub fn check_new(password: &str) -> Result<(), &'static str> {
    if NEW_LENGTH.contains(&password.chars().count()) {
        Ok(())
    } else {
        Err(INVALID_NEW)
    }
}

/// The PHC string to store for `password`.
pub async fn hash(password: &str) -> Result<String, Error> {
    let password = password.to_owned();
    blocking(move || {
        Argon2::default()
            .hash_password(password.as_bytes())
            .map(|hash| hash.to_string())
            .map_err(|err| Error(format!("could not hash the password: {err}")))
    })
    .await
}

/// Whether `password` is the one `stored` was made from. A stored value that
/// is not an Argon2id PHC string is an error, never a mismatch, so a corrupt
/// row cannot pass for a wrong password.
pub async fn verify(password: &str, stored: &str) -> Result<bool, Error> {
    let parsed = PasswordHash::new(stored)
        .map_err(|err| Error(format!("the stored password hash is malformed: {err}")))?;
    if parsed.algorithm.as_str() != "argon2id" {
        return Err(Error(format!(
            "the stored password hash is {}, not argon2id",
            parsed.algorithm
        )));
    }

    let password = password.to_owned();
    blocking(
        move || match Argon2::default().verify_password(password.as_bytes(), &parsed) {
            Ok(()) => Ok(true),
            Err(argon2::password_hash::Error::PasswordInvalid) => Ok(false),
            Err(err) => Err(Error(format!("could not verify the password: {err}"))),
        },
    )
    .await
}

async fn blocking<T: Send + 'static>(
    work: impl FnOnce() -> Result<T, Error> + Send + 'static,
) -> Result<T, Error> {
    tokio::task::spawn_blocking(work)
        .await
        .map_err(|err| Error(format!("the hashing task failed: {err}")))?
}

#[cfg(test)]
mod tests {
    use super::*;

    const PASSWORD: &str = "correct horse battery staple";

    #[test]
    fn a_new_password_must_be_15_to_1024_characters() {
        let refused = Err("Choose a password of 15 to 1024 characters.");
        assert_eq!(check_new(&"x".repeat(15)), Ok(()));
        assert_eq!(check_new(&"x".repeat(1024)), Ok(()));
        assert_eq!(check_new(&"x".repeat(14)), refused);
        assert_eq!(check_new(&"x".repeat(1025)), refused);
        assert_eq!(check_new(""), refused);
        // Counted as characters, not bytes: fourteen characters of two bytes
        // each is still too short, and 1024 of them is still allowed.
        assert_eq!(check_new(&"é".repeat(14)), refused);
        assert_eq!(check_new(&"é".repeat(1024)), Ok(()));
        // Never trimmed: the spaces count, so thirteen characters padded with
        // one space on each side is fifteen, and a password of spaces is one.
        assert_eq!(check_new(" thirteen char "), Ok(()));
        assert_eq!(check_new(&" ".repeat(15)), Ok(()));
    }

    #[tokio::test]
    async fn the_right_password_verifies_and_a_wrong_one_does_not() {
        let stored = hash(PASSWORD).await.unwrap();
        assert!(verify(PASSWORD, &stored).await.unwrap());
        assert!(
            !verify("correct horse battery stapler", &stored)
                .await
                .unwrap()
        );
    }

    #[tokio::test]
    async fn the_stored_value_is_an_argon2id_string_without_the_password() {
        let stored = hash(PASSWORD).await.unwrap();
        assert!(
            stored.starts_with("$argon2id$v=19$m=19456,t=2,p=1$"),
            "not Argon2id at the recommended parameters: {stored}"
        );
        assert!(!stored.contains(PASSWORD));
    }

    #[tokio::test]
    async fn the_same_password_hashes_differently_each_time() {
        assert_ne!(hash(PASSWORD).await.unwrap(), hash(PASSWORD).await.unwrap());
    }

    #[tokio::test]
    async fn a_malformed_stored_hash_is_an_error_not_a_mismatch() {
        for stored in ["", "not a hash", "$argon2id$v=19$m=19456,t=2,p=1$broken"] {
            assert!(
                verify(PASSWORD, stored).await.is_err(),
                "accepted {stored:?}"
            );
        }
    }
}

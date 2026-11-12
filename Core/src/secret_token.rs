//! Secrets the core hands out once and stores only hashed: invite codes,
//! refresh tokens and reset tokens. Thirty two random bytes, encoded to paste,
//! and found again by their SHA-256. Why SHA-256 rather than Argon2id is in
//! "The core's data" in docs/core-architecture.md.

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use sha2::{Digest, Sha256};

/// The stored form of a secret: SHA-256 of its 32 bytes.
pub type TokenHash = [u8; 32];

/// A new secret: the text to hand out once, and the hash to store.
pub fn generate() -> (String, TokenHash) {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).expect("the operating system's random source failed");
    (URL_SAFE_NO_PAD.encode(bytes), Sha256::digest(bytes).into())
}

/// The hash of a presented secret, or `None` when it is not one, since input
/// that does not decode to exactly 32 bytes cannot match anything stored.
pub fn hash(text: &str) -> Option<TokenHash> {
    let bytes = URL_SAFE_NO_PAD.decode(text).ok()?;
    let bytes: [u8; 32] = bytes.try_into().ok()?;
    Some(Sha256::digest(bytes).into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_generated_secret_hashes_to_its_stored_hash() {
        let (text, stored) = generate();
        assert_eq!(hash(&text), Some(stored));
    }

    #[test]
    fn a_secret_is_43_url_safe_characters() {
        let (text, _) = generate();
        assert_eq!(text.len(), 43);
        assert!(
            text.chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_'),
            "not URL safe: {text}"
        );
    }

    #[test]
    fn two_secrets_differ() {
        assert_ne!(generate().0, generate().0);
    }

    #[test]
    fn what_is_not_a_secret_hashes_to_nothing() {
        let (text, _) = generate();
        for input in [
            "",
            &text[..42],
            &format!("{text}A"),
            &format!("{text}="),
            &text.replacen(|c: char| c.is_ascii_alphanumeric(), "+", 1),
        ] {
            assert_eq!(hash(input), None, "accepted {input:?}");
        }
    }
}

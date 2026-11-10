//! The key every token is signed with: an Ed25519 key, kept as its 32 byte
//! seed in base64url without padding, the same encoding as the secret tokens.
//! The public key and the key id are derived from the seed, never configured,
//! so they cannot drift from the key they describe. See "The signing key" in
//! docs/core-architecture.md.

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use ed25519_dalek::pkcs8::{EncodePrivateKey, KeypairBytes};
use jsonwebtoken::jwk::{Jwk, ThumbprintHash};
use jsonwebtoken::{Algorithm, EncodingKey};

/// A signing key and what is derived from it. Holds the private key, so it
/// has no `Debug` and is never printed.
pub struct SigningKey {
    encoding: EncodingKey,
    public: [u8; 32],
    key_id: String,
}

impl SigningKey {
    /// A new key from the operating system's random source, and its seed in
    /// the form `CORE_SIGNING_KEY` holds.
    pub fn generate() -> (SigningKey, String) {
        let mut seed = [0u8; 32];
        getrandom::fill(&mut seed).expect("the operating system's random source failed");
        (SigningKey::from_seed(seed), URL_SAFE_NO_PAD.encode(seed))
    }

    /// The key in `CORE_SIGNING_KEY`. The error never repeats the value, since
    /// it is the private key, and it may be a real one with a typo in it.
    pub fn parse(text: &str) -> Result<SigningKey, &'static str> {
        let seed = URL_SAFE_NO_PAD
            .decode(text)
            .map_err(|_| "not base64url without padding")?;
        let seed: [u8; 32] = seed
            .try_into()
            .map_err(|_| "does not decode to exactly 32 bytes")?;
        Ok(SigningKey::from_seed(seed))
    }

    pub fn from_seed(seed: [u8; 32]) -> SigningKey {
        let public = ed25519_dalek::SigningKey::from_bytes(&seed)
            .verifying_key()
            .to_bytes();
        // PKCS#8 without the public key, the form jsonwebtoken's JWK support
        // reads; its signer reads either.
        let der = KeypairBytes {
            secret_key: seed,
            public_key: None,
        }
        .to_pkcs8_der()
        .expect("a 32 byte seed always encodes");
        let encoding = EncodingKey::from_ed_der(der.as_bytes());
        let key_id = Jwk::from_encoding_key(&encoding, Algorithm::EdDSA)
            .and_then(|jwk| jwk.thumbprint(ThumbprintHash::SHA256))
            .expect("an Ed25519 key always has a thumbprint");
        SigningKey {
            encoding,
            public,
            key_id,
        }
    }

    pub fn encoding_key(&self) -> &EncodingKey {
        &self.encoding
    }

    pub fn public_key(&self) -> &[u8; 32] {
        &self.public
    }

    /// The public key's RFC 7638 JWK thumbprint, SHA-256, in base64url.
    pub fn key_id(&self) -> &str {
        &self.key_id
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // RFC 8037, Appendix A.1 and A.3.
    const RFC_SEED: &str = "nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A";
    const RFC_PUBLIC: &str = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
    const RFC_KEY_ID: &str = "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k";

    fn rfc_key() -> SigningKey {
        let seed = URL_SAFE_NO_PAD.decode(RFC_SEED).unwrap();
        SigningKey::from_seed(seed.try_into().unwrap())
    }

    // A key id computed differently from what a verifier looks up fails only
    // at runtime, as every token being refused.
    #[test]
    fn the_rfc_example_key_has_the_published_key_id() {
        let key = rfc_key();
        assert_eq!(URL_SAFE_NO_PAD.encode(key.public_key()), RFC_PUBLIC);
        assert_eq!(key.key_id(), RFC_KEY_ID);
    }

    #[test]
    fn a_generated_seed_gives_back_the_same_key() {
        let (key, seed) = SigningKey::generate();
        assert_eq!(seed.len(), 43);
        let again =
            SigningKey::from_seed(URL_SAFE_NO_PAD.decode(&seed).unwrap().try_into().unwrap());
        assert_eq!(again.key_id(), key.key_id());
        assert_ne!(SigningKey::generate().0.key_id(), key.key_id());
    }

    #[test]
    fn the_env_value_is_parsed_as_its_seed() {
        assert_eq!(SigningKey::parse(RFC_SEED).unwrap().key_id(), RFC_KEY_ID);
    }

    #[test]
    fn anything_but_a_32_byte_seed_is_refused() {
        for input in [
            "",
            &RFC_SEED[..42],
            &format!("{RFC_SEED}A"),
            &format!("{RFC_SEED}="),
            &RFC_SEED.replace('_', "/"),
            &URL_SAFE_NO_PAD.encode([0u8; 64]),
        ] {
            assert!(SigningKey::parse(input).is_err(), "accepted {input:?}");
        }
    }
}

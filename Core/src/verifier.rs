//! Verifying access tokens, which is all anything downstream of the issuer
//! ever does. EdDSA only, the key chosen by the token's key id from a set, and
//! expiry checked here against a time passed in, with a leeway chosen rather
//! than inherited. See "The clock leeway on verification is chosen, not
//! inherited" and "The token header carries a key id" in
//! docs/core-architecture.md.

use crate::claims::{Claims, Wire};
use crate::signing_key::SigningKey;
use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use jsonwebtoken::{Algorithm, DecodingKey, Validation};
use std::collections::HashMap;
use std::fmt;
use time::OffsetDateTime;

/// How long past its `exp` a token is still accepted, to absorb clock drift
/// between machines. The same five seconds in every verifier.
pub const LEEWAY_SECONDS: i64 = 5;

/// The public keys tokens may be signed with, by key id. One today; a set so
/// that a key can be rotated without a flag day.
pub struct KeySet {
    keys: HashMap<String, PublicKey>,
}

pub struct PublicKey {
    bytes: [u8; 32],
    decoding: DecodingKey,
}

impl KeySet {
    /// The set holding only `key`'s public half.
    pub fn of(key: &SigningKey) -> KeySet {
        let public = PublicKey {
            bytes: *key.public_key(),
            decoding: DecodingKey::from_ed_components(&URL_SAFE_NO_PAD.encode(key.public_key()))
                .expect("a 32 byte public key always decodes"),
        };
        KeySet {
            keys: HashMap::from([(key.key_id().to_owned(), public)]),
        }
    }

    /// Each key id and its raw public key, for serving the set.
    pub fn iter(&self) -> impl Iterator<Item = (&str, &[u8; 32])> {
        self.keys.iter().map(|(id, key)| (id.as_str(), &key.bytes))
    }
}

/// Why a token was refused. Every reason is handled the same way, so this is
/// for logs and tests, never for the response.
#[derive(Debug, PartialEq, Eq)]
pub enum Refused {
    /// Not a token, the wrong algorithm, a missing or unknown key id, a bad
    /// signature, or claims missing or malformed.
    Invalid,
    Expired,
}

impl fmt::Display for Refused {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Refused::Invalid => "invalid token",
            Refused::Expired => "expired token",
        })
    }
}

pub struct Verifier {
    keys: KeySet,
    validation: Validation,
}

impl Verifier {
    pub fn new(keys: KeySet) -> Verifier {
        // The library checks the algorithm, the signature and that the claims
        // are there. Its own expiry check is off, since it reads the system
        // clock and defaults to sixty seconds of leeway; the core checks exp
        // below instead, against the time it is given.
        let mut validation = Validation::new(Algorithm::EdDSA);
        validation.validate_exp = false;
        validation.validate_nbf = false;
        validation.validate_aud = false;
        validation.leeway = 0;
        validation.required_spec_claims = ["exp", "sub"].map(String::from).into();
        Verifier { keys, validation }
    }

    /// The keys this verifier trusts, which the core serves as its key set.
    pub fn keys(&self) -> &KeySet {
        &self.keys
    }

    /// The claims of `token` if it is valid at `now`: signed with EdDSA by a
    /// key in the set, and `now` no later than its `exp` plus the leeway.
    pub fn verify(&self, token: &str, now: OffsetDateTime) -> Result<Claims, Refused> {
        let header = jsonwebtoken::decode_header(token).map_err(|_| Refused::Invalid)?;
        let key = header
            .kid
            .as_deref()
            .and_then(|kid| self.keys.keys.get(kid))
            .ok_or(Refused::Invalid)?;
        let wire = jsonwebtoken::decode::<Wire>(token, &key.decoding, &self.validation)
            .map_err(|_| Refused::Invalid)?
            .claims;
        let claims = Claims::try_from(wire).map_err(|_| Refused::Invalid)?;

        if claims.expires_at.saturating_add(LEEWAY_SECONDS) < now.unix_timestamp() {
            return Err(Refused::Expired);
        }
        Ok(claims)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::issuer::Issuer;
    use ed25519_dalek::Signer;
    use jsonwebtoken::{EncodingKey, Header};
    use time::Duration;
    use uuid::Uuid;

    const LIFETIME: Duration = Duration::minutes(30);

    // Long past, so anything consulting the system clock rather than the time
    // passed in would find every token here expired.
    fn at(seconds: i64) -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_000_000_000 + seconds).unwrap()
    }

    // A key, the verifier trusting only it, and the issuer signing with it.
    fn setup() -> (Issuer, Verifier, String) {
        let (key, seed) = SigningKey::generate();
        let verifier = Verifier::new(KeySet::of(&key));
        (Issuer::new(key, LIFETIME), verifier, seed)
    }

    fn token(issuer: &Issuer) -> String {
        issuer.issue(Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7(), at(0))
    }

    #[test]
    fn a_token_from_the_issuer_verifies_with_its_claims() {
        let (issuer, verifier, _) = setup();
        let (account, family, jti) = (Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7());
        let token = issuer.issue(account, family, jti, at(0));

        let claims = verifier.verify(&token, at(1)).unwrap();
        assert_eq!(
            (claims.account_id, claims.family_id, claims.token_id),
            (account, family, jti)
        );
    }

    #[test]
    fn expired_five_seconds_ago_is_accepted_and_six_refused() {
        let (issuer, verifier, _) = setup();
        let token = token(&issuer);
        let exp = LIFETIME.whole_seconds();

        assert!(verifier.verify(&token, at(exp)).is_ok());
        assert!(verifier.verify(&token, at(exp + 5)).is_ok());
        assert_eq!(verifier.verify(&token, at(exp + 6)), Err(Refused::Expired));
    }

    // Rebuilds `token` with `alg` in its header, signed with the real key, so
    // the algorithm is the only thing wrong with it.
    fn with_alg(token: &str, alg: &str, kid: &str, seed: &str) -> String {
        let payload = token.split('.').nth(1).unwrap();
        let header =
            URL_SAFE_NO_PAD.encode(format!(r#"{{"typ":"JWT","alg":"{alg}","kid":"{kid}"}}"#));
        let input = format!("{header}.{payload}");
        let seed: [u8; 32] = URL_SAFE_NO_PAD.decode(seed).unwrap().try_into().unwrap();
        let signature = ed25519_dalek::SigningKey::from_bytes(&seed).sign(input.as_bytes());
        format!("{input}.{}", URL_SAFE_NO_PAD.encode(signature.to_bytes()))
    }

    #[test]
    fn every_algorithm_but_eddsa_is_refused() {
        let (issuer, verifier, seed) = setup();
        let token = token(&issuer);
        let kid = SigningKey::parse(&seed).unwrap().key_id().to_owned();

        // The control: the same rebuilding with EdDSA verifies.
        assert!(
            verifier
                .verify(&with_alg(&token, "EdDSA", &kid, &seed), at(0))
                .is_ok()
        );

        for alg in [
            "none", "None", "NONE", "HS256", "HS384", "HS512", "RS256", "RS384", "RS512", "PS256",
            "PS384", "PS512", "ES256", "ES384", "ES512", "eddsa", "Ed25519", "",
        ] {
            assert_eq!(
                verifier.verify(&with_alg(&token, alg, &kid, &seed), at(0)),
                Err(Refused::Invalid),
                "accepted alg {alg:?}"
            );
        }

        // alg none with no signature at all.
        let payload = token.split('.').nth(1).unwrap();
        let header = URL_SAFE_NO_PAD.encode(format!(r#"{{"alg":"none","kid":"{kid}"}}"#));
        assert_eq!(
            verifier.verify(&format!("{header}.{payload}."), at(0)),
            Err(Refused::Invalid)
        );
    }

    // The classic confusion: HS256 keyed with the public key, which every
    // verifier holds, would let anyone mint a token if it were accepted.
    #[test]
    fn hs256_keyed_with_the_public_key_is_refused() {
        let (issuer, verifier, seed) = setup();
        let key = SigningKey::parse(&seed).unwrap();
        let wire: Wire = jsonwebtoken::dangerous::insecure_decode(token(&issuer))
            .unwrap()
            .claims;
        let mut header = Header::new(Algorithm::HS256);
        header.kid = Some(key.key_id().to_owned());
        let forged =
            jsonwebtoken::encode(&header, &wire, &EncodingKey::from_secret(key.public_key()))
                .unwrap();

        assert_eq!(verifier.verify(&forged, at(0)), Err(Refused::Invalid));
    }

    #[test]
    fn another_keys_signature_and_an_unknown_key_id_are_refused() {
        let (_, verifier, seed) = setup();
        let trusted_kid = SigningKey::parse(&seed).unwrap().key_id().to_owned();
        let (other, other_seed) = SigningKey::generate();

        // Signed by another key under the trusted key's id.
        let other_issuer = Issuer::new(other, LIFETIME);
        let forged = with_alg(&token(&other_issuer), "EdDSA", &trusted_kid, &other_seed);
        assert_eq!(verifier.verify(&forged, at(0)), Err(Refused::Invalid));

        // Signed by the trusted key, under an id the set does not hold.
        let unknown = with_alg(&token(&other_issuer), "EdDSA", "unknown", &seed);
        assert_eq!(verifier.verify(&unknown, at(0)), Err(Refused::Invalid));

        // Signed by another key under its own id, which the set does not hold.
        assert_eq!(
            verifier.verify(&token(&other_issuer), at(0)),
            Err(Refused::Invalid)
        );

        // No key id at all.
        let payload = token(&other_issuer).split('.').nth(1).unwrap().to_owned();
        let header = URL_SAFE_NO_PAD.encode(r#"{"typ":"JWT","alg":"EdDSA"}"#);
        let input = format!("{header}.{payload}");
        let seed: [u8; 32] = URL_SAFE_NO_PAD.decode(&seed).unwrap().try_into().unwrap();
        let signature = ed25519_dalek::SigningKey::from_bytes(&seed).sign(input.as_bytes());
        let no_kid = format!("{input}.{}", URL_SAFE_NO_PAD.encode(signature.to_bytes()));
        assert_eq!(verifier.verify(&no_kid, at(0)), Err(Refused::Invalid));
    }

    #[test]
    fn what_is_not_a_token_is_refused() {
        let (_, verifier, _) = setup();
        for input in ["", "a", "a.b", "a.b.c", "..", "x.y.z.w"] {
            assert_eq!(
                verifier.verify(input, at(0)),
                Err(Refused::Invalid),
                "{input:?}"
            );
        }
    }
}

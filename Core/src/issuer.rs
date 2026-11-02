//! Issuing access tokens: Ed25519 signed JWTs, the key id in the header. The
//! only part of the platform that can mint one, and kept apart from
//! verification, see "An escape hatch, kept deliberately" in
//! docs/core-architecture.md.

use crate::claims::{Claims, Wire};
use crate::signing_key::SigningKey;
use jsonwebtoken::{Algorithm, Header};
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

pub struct Issuer {
    key: SigningKey,
    lifetime: Duration,
}

impl Issuer {
    pub fn new(key: SigningKey, lifetime: Duration) -> Issuer {
        Issuer { key, lifetime }
    }

    /// How long a token lasts, which the access cookie's lifetime follows.
    pub fn lifetime(&self) -> Duration {
        self.lifetime
    }

    /// A token for `account_id` in session family `family_id`, with token id
    /// `jti`, issued at `now` and expiring the lifetime later. The id and the
    /// clock are parameters so a test can pin the bytes.
    pub fn issue(
        &self,
        account_id: Uuid,
        family_id: Uuid,
        jti: Uuid,
        now: OffsetDateTime,
    ) -> String {
        let issued_at = now.unix_timestamp();
        let claims = Claims {
            account_id,
            family_id,
            token_id: jti,
            issued_at,
            expires_at: issued_at + self.lifetime.whole_seconds(),
        };
        let mut header = Header::new(Algorithm::EdDSA);
        header.kid = Some(self.key.key_id().to_owned());
        jsonwebtoken::encode(&header, &Wire::from(&claims), self.key.encoding_key())
            .expect("the signing key was checked when it was loaded")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;
    use base64::engine::general_purpose::URL_SAFE_NO_PAD;
    use jsonwebtoken::{DecodingKey, Validation};

    const LIFETIME: Duration = Duration::minutes(30);

    fn now() -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap()
    }

    // Reads a token back with the issuer's public key, checking the signature
    // and nothing about time, so the claims can be compared as issued.
    fn read(token: &str, key: &SigningKey) -> (Header, Wire) {
        let public = URL_SAFE_NO_PAD.encode(key.public_key());
        let mut validation = Validation::new(Algorithm::EdDSA);
        validation.validate_exp = false;
        validation.required_spec_claims.clear();
        let data = jsonwebtoken::decode::<Wire>(
            token,
            &DecodingKey::from_ed_components(&public).unwrap(),
            &validation,
        )
        .unwrap();
        (data.header, data.claims)
    }

    #[test]
    fn a_token_carries_the_key_id_and_every_claim() {
        let (key, seed) = SigningKey::generate();
        let key_id = key.key_id().to_owned();
        let issuer = Issuer::new(key, LIFETIME);
        let (account, family, jti) = (Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7());

        let token = issuer.issue(account, family, jti, now());

        let (header, wire) = read(&token, &SigningKey::parse(&seed).unwrap());
        assert_eq!(header.alg, Algorithm::EdDSA);
        assert_eq!(header.kid.as_deref(), Some(key_id.as_str()));
        let claims = Claims::try_from(wire).unwrap();
        assert_eq!(
            claims,
            Claims {
                account_id: account,
                family_id: family,
                token_id: jti,
                issued_at: 1_800_000_000,
                expires_at: 1_800_000_000 + 30 * 60,
            }
        );
    }

    // A missing or wrong exp would make a token work forever with nothing
    // looking broken.
    #[test]
    fn exp_is_iat_plus_the_configured_lifetime() {
        let (key, seed) = SigningKey::generate();
        let issuer = Issuer::new(key, Duration::seconds(60));
        let token = issuer.issue(Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7(), now());
        let (_, wire) = read(&token, &SigningKey::parse(&seed).unwrap());
        let claims = Claims::try_from(wire).unwrap();
        assert_eq!(claims.expires_at - claims.issued_at, 60);
    }
}

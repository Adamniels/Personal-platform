//! What an access token says: whose it is (`sub`), which session family it
//! belongs to (`sid`), its own id (`jti`), and when it was issued and expires,
//! in seconds since the epoch. `sid` and `jti` are read by nothing yet; they
//! are there so revocation can be added without changing the format, see "The
//! token carries a token id and a session id" in docs/core-architecture.md.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Claims {
    pub account_id: Uuid,
    pub family_id: Uuid,
    pub token_id: Uuid,
    pub issued_at: i64,
    pub expires_at: i64,
}

/// The claims as they are on the wire, every id a UUID string. Every
/// verifier, in any language, reads exactly these names.
#[derive(Serialize, Deserialize)]
pub(crate) struct Wire {
    sub: String,
    sid: String,
    jti: String,
    iat: i64,
    exp: i64,
}

impl From<&Claims> for Wire {
    fn from(claims: &Claims) -> Wire {
        Wire {
            sub: claims.account_id.to_string(),
            sid: claims.family_id.to_string(),
            jti: claims.token_id.to_string(),
            iat: claims.issued_at,
            exp: claims.expires_at,
        }
    }
}

impl TryFrom<Wire> for Claims {
    type Error = uuid::Error;

    fn try_from(wire: Wire) -> Result<Claims, uuid::Error> {
        Ok(Claims {
            account_id: Uuid::parse_str(&wire.sub)?,
            family_id: Uuid::parse_str(&wire.sid)?,
            token_id: Uuid::parse_str(&wire.jti)?,
            issued_at: wire.iat,
            expires_at: wire.exp,
        })
    }
}

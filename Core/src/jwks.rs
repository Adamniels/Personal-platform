//! The public keys tokens are verified with, served as a JWK set (RFC 7517,
//! with the Ed25519 key type of RFC 8037) for every feature to fetch and
//! cache. Public and sessionless, since a public key is no secret. See "The
//! signing key" in docs/core-architecture.md.

use crate::AppState;
use crate::verifier::{KeySet, Verifier};
use axum::extract::State;
use axum::routing::get;
use axum::{Json, Router};
use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::Serialize;
use std::sync::Arc;

/// Where the set is served, the conventional well known path.
pub const PATH: &str = "/.well-known/jwks.json";

pub fn routes() -> Router<AppState> {
    Router::new().route(PATH, get(serve))
}

// No cache headers: a feature caches the set in its own process, see
// High-level/docs/feature-contract.md.
async fn serve(State(verifier): State<Arc<Verifier>>) -> Json<JwkSet> {
    Json(JwkSet::of(verifier.keys()))
}

/// The body of the endpoint. Its fields are the wire format every verifier
/// reads, and `contracts/token/jwks.json` holds it for the fixture's key.
#[derive(Debug, Serialize)]
pub struct JwkSet {
    keys: Vec<Jwk>,
}

#[derive(Debug, Serialize)]
struct Jwk {
    kty: &'static str,
    crv: &'static str,
    alg: &'static str,
    #[serde(rename = "use")]
    use_: &'static str,
    kid: String,
    x: String,
}

impl JwkSet {
    /// One entry per key in `keys`, ordered by key id so the body is the same
    /// bytes every time.
    pub fn of(keys: &KeySet) -> JwkSet {
        let mut keys: Vec<Jwk> = keys
            .iter()
            .map(|(kid, public)| Jwk {
                kty: "OKP",
                crv: "Ed25519",
                alg: "EdDSA",
                use_: "sig",
                kid: kid.to_owned(),
                x: URL_SAFE_NO_PAD.encode(public),
            })
            .collect();
        keys.sort_by(|a, b| a.kid.cmp(&b.kid));
        JwkSet { keys }
    }
}

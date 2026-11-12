//! The token contract: the bytes the core produces for fixed inputs, pinned
//! in contracts/token/ so a verifier in any language can prove it reads them
//! without running the core. See contract fixtures in "One repository" in
//! High-level/docs/platform-architecture.md, and contracts/README.md.
//!
//! The test only compares. To regenerate the files on purpose, and then
//! review their diff, from Core/:
//!   UPDATE_CONTRACTS=1 cargo test --test token_contract

use platform_core::issuer::Issuer;
use platform_core::jwks::JwkSet;
use platform_core::signing_key::SigningKey;
use platform_core::verifier::{KeySet, Refused, Verifier};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::OnceLock;
use time::{Duration, OffsetDateTime};
use uuid::Uuid;

// The fixture's inputs, constants here and never a file. The seed is the
// RFC 8037 Appendix A test seed, a published test vector that signs nothing
// real, as in src/signing_key.rs.
const SEED: &str = "nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A";
const IAT: i64 = 1_800_000_000;
const LIFETIME: Duration = Duration::minutes(30);
const SUB: &str = "01900000-0000-7000-8000-000000000001";
const SID: &str = "01900000-0000-7000-8000-000000000002";
const JTI: &str = "01900000-0000-7000-8000-000000000003";

/// What claims.json holds: the token's claims and its key id, so a
/// verifier's test knows what to expect and which time to verify at.
#[derive(Serialize, Deserialize)]
struct Expected {
    sub: String,
    sid: String,
    jti: String,
    iat: i64,
    exp: i64,
    kid: String,
}

fn key() -> SigningKey {
    SigningKey::parse(SEED).unwrap()
}

fn at(seconds: i64) -> OffsetDateTime {
    OffsetDateTime::from_unix_timestamp(seconds).unwrap()
}

// Each file's name and the bytes the core produces for it now.
fn produced() -> [(&'static str, String); 3] {
    let key = key();
    let kid = key.key_id().to_owned();
    let jwks = serde_json::to_string_pretty(&JwkSet::of(&KeySet::of(&key))).unwrap() + "\n";
    let token = Issuer::new(key, LIFETIME).issue(
        Uuid::parse_str(SUB).unwrap(),
        Uuid::parse_str(SID).unwrap(),
        Uuid::parse_str(JTI).unwrap(),
        at(IAT),
    );
    let claims = Expected {
        sub: SUB.into(),
        sid: SID.into(),
        jti: JTI.into(),
        iat: IAT,
        exp: IAT + LIFETIME.whole_seconds(),
        kid,
    };
    let claims = serde_json::to_string_pretty(&claims).unwrap() + "\n";
    [
        ("token.jwt", token),
        ("jwks.json", jwks),
        ("claims.json", claims),
    ]
}

fn dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../contracts/token")
}

// A committed file, written first when UPDATE_CONTRACTS=1. The writing
// happens once per run, before any test reads, so the tests can run in
// parallel.
fn committed(name: &str) -> String {
    static UPDATED: OnceLock<()> = OnceLock::new();
    UPDATED.get_or_init(|| {
        if std::env::var("UPDATE_CONTRACTS").is_ok_and(|value| value == "1") {
            fs::create_dir_all(dir()).unwrap();
            for (name, bytes) in produced() {
                fs::write(dir().join(name), bytes).unwrap();
            }
        }
    });
    let path = dir().join(name);
    fs::read_to_string(&path).unwrap_or_else(|err| {
        panic!(
            "could not read {}: {err}; regenerate with UPDATE_CONTRACTS=1 cargo test --test token_contract",
            path.display()
        )
    })
}

#[test]
fn the_token_equals_the_committed_fixture() {
    for (name, bytes) in produced() {
        assert!(
            committed(name) == bytes,
            "contracts/token/{name} differs from what the core produces:\n{bytes}\n\
             If the change is intended, regenerate with \
             UPDATE_CONTRACTS=1 cargo test --test token_contract and review the diff"
        );
    }
}

#[test]
fn the_committed_token_verifies_until_its_leeway_runs_out() {
    let token = committed("token.jwt");
    let expected: Expected = serde_json::from_str(&committed("claims.json")).unwrap();
    let verifier = Verifier::new(KeySet::of(&key()));

    let claims = verifier.verify(&token, at(expected.iat)).unwrap();
    assert_eq!(
        (
            claims.account_id.to_string(),
            claims.family_id.to_string(),
            claims.token_id.to_string(),
            claims.issued_at,
            claims.expires_at,
        ),
        (
            expected.sub,
            expected.sid,
            expected.jti,
            expected.iat,
            expected.exp
        )
    );
    assert!(verifier.verify(&token, at(expected.exp + 5)).is_ok());
    assert_eq!(
        verifier.verify(&token, at(expected.exp + 6)),
        Err(Refused::Expired)
    );
}

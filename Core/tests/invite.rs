//! The invite command's storage: the code is printed once and only its hash
//! is kept, with an expiry from the clock it was given.

mod common;

use platform_core::{invite, secret_token};
use time::{Duration, OffsetDateTime};

#[tokio::test]
async fn stores_the_hash_and_the_expiry_but_never_the_code() {
    let db = common::TestDb::new().await;
    // Whole seconds, since timestamptz keeps microseconds and the comparison
    // below is exact.
    let now = OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap();

    let code = invite::create(&db.pool, now, now + Duration::days(7))
        .await
        .unwrap();

    let (code_hash, created_at, expires_at, used_at): (
        Vec<u8>,
        OffsetDateTime,
        OffsetDateTime,
        Option<OffsetDateTime>,
    ) = sqlx::query_as("SELECT code_hash, created_at, expires_at, used_at FROM invite_codes")
        .fetch_one(&db.pool)
        .await
        .unwrap();

    assert_eq!(
        Some(code_hash.as_slice()),
        secret_token::hash(&code).as_ref().map(|h| &h[..])
    );
    assert_ne!(code_hash, code.as_bytes(), "the code itself was stored");
    assert_eq!(created_at, now);
    assert_eq!(expires_at, now + Duration::days(7));
    assert_eq!(used_at, None);

    db.close().await;
}

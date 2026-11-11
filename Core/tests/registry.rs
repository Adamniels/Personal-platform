//! The registry as the core loads it from disk: the committed file, and a
//! path that is not there. Parsing and validation are unit tested in
//! src/registry.rs.

use platform_core::registry::{Feature, Registry};
use std::path::Path;

#[test]
fn the_committed_registry_holds_canary() {
    // Through the manifest directory, so the test does not depend on where
    // cargo test runs from.
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../infra/features.toml");
    let registry = Registry::load(&path).unwrap();

    assert_eq!(
        registry.features(),
        [Feature {
            name: "Canary".into(),
            prefix: "/canary".into(),
            upstream: "http://127.0.0.1:8081".into(),
            display: None,
        }]
    );
}

#[test]
fn a_missing_registry_file_is_an_error_naming_the_path() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("no-such-dir/features.toml");
    let err = Registry::load(&path).unwrap_err();

    assert!(
        err.contains(&path.display().to_string()),
        "{err:?} does not name {}",
        path.display()
    );
}

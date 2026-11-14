//! The core's settings as the binary reads them at startup: one that is set
//! but wrong stops the core, naming the setting, before it listens. Run as
//! the real binary, from an empty directory so no .env is loaded, with every
//! setting it reads before the database given, and no database URL, so a
//! setting that is accepted gets as far as asking for one.

use std::path::{Path, PathBuf};
use std::process::{Command, Output};

const SIGNING_KEY: &str = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

fn registry() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../infra/features.toml")
}

// The core started with the settings it needs and `extra`, in an empty
// directory, left to exit.
fn start(extra: &[(&str, &str)]) -> Output {
    let dir = std::env::temp_dir().join(format!("core-settings-{}", uuid::Uuid::now_v7()));
    std::fs::create_dir(&dir).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_platform_core"))
        .current_dir(&dir)
        .env_clear()
        .env("CORE_LISTEN_ADDR", "127.0.0.1:0")
        .env("CORE_PUBLIC_ORIGIN", "https://platform.test")
        .env("CORE_SIGNING_KEY", SIGNING_KEY)
        .env("CORE_FEATURE_REGISTRY", registry())
        .envs(extra.iter().copied())
        .output()
        .unwrap();
    std::fs::remove_dir_all(&dir).unwrap();
    output
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

#[test]
fn a_backup_status_path_that_is_not_absolute_refuses_startup() {
    for relative in ["backup.json", "status/backup.json", "./backup.json", ""] {
        let output = start(&[("CORE_BACKUP_STATUS_FILE", relative)]);
        let stderr = stderr(&output);
        assert!(!output.status.success(), "{relative:?} started: {stderr}");
        assert!(
            stderr.contains("CORE_BACKUP_STATUS_FILE"),
            "{relative:?}: {stderr}"
        );
        assert!(
            !stderr.contains("CORE_APP_DATABASE_URL"),
            "{relative:?} got as far as the database: {stderr}"
        );
    }

    // An absolute path is accepted, whether or not the file exists, so the
    // core goes on to ask for its database.
    for absolute in [None, Some("/status/backup.json")] {
        let extra: Vec<(&str, &str)> = absolute
            .map(|path| ("CORE_BACKUP_STATUS_FILE", path))
            .into_iter()
            .collect();
        let output = start(&extra);
        let stderr = stderr(&output);
        assert!(!output.status.success(), "{absolute:?} started: {stderr}");
        assert!(
            stderr.contains("CORE_APP_DATABASE_URL"),
            "{absolute:?}: {stderr}"
        );
        assert!(
            !stderr.contains("CORE_BACKUP_STATUS_FILE"),
            "{absolute:?}: {stderr}"
        );
    }
}

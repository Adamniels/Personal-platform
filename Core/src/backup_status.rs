//! The backup line on the front page: what the hosted copy's backup job last
//! wrote to its status file, read on every load of the page and turned into
//! one of a few states. Reading never fails the page; every problem is a state
//! that needs the owner. See "The backup line on the front page" in
//! docs/core-architecture.md.

use crate::relative;
use serde::{Deserialize, Deserializer};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use time::format_description::well_known::Rfc3339;
use time::{Duration, OffsetDateTime, UtcOffset};
use tokio::io::AsyncReadExt;

/// The setting naming the status file, an absolute path. Unset, nothing is
/// read and the page says nothing about backups.
pub const SETTING: &str = "CORE_BACKUP_STATUS_FILE";

/// The most of the file that is read. The job writes four short fields.
pub const MAX_BYTES: usize = 4096;

/// How old the last success may be and still be fine: a nightly backup, and
/// two hours more so one that takes a little longer than the last does not
/// flicker.
pub const STALE_AFTER: Duration = Duration::hours(26);

/// How far ahead of now a success may be, for clocks a moment apart. Further
/// ahead, the file is not believed.
pub const CLOCK_SKEW: Duration = Duration::minutes(5);

/// The path from the setting's value: None when unset, refused when not
/// absolute, so a typo cannot quietly hide the line.
pub fn setting(value: Option<OsString>) -> Result<Option<PathBuf>, String> {
    let Some(value) = value else {
        return Ok(None);
    };
    let path = PathBuf::from(value);
    if !path.is_absolute() {
        return Err(format!(
            "{SETTING}='{}' is not an absolute path",
            path.display()
        ));
    }
    Ok(Some(path))
}

/// What the status file says, as the front page shows it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BackupStatus {
    /// The last attempt succeeded, at most `STALE_AFTER` ago.
    Fine { last_success: OffsetDateTime },
    /// The last success is older than `STALE_AFTER`.
    Stale { last_success: OffsetDateTime },
    /// The last attempt failed, after an earlier success.
    Failed { last_success: OffsetDateTime },
    /// No attempt has succeeded.
    NoSuccessYet,
    /// The file is missing, unreadable, too large or not the job's shape.
    Unreadable,
}

/// The state the file's `content` gives at `now`.
pub fn state(content: &[u8], now: OffsetDateTime) -> BackupStatus {
    if content.len() > MAX_BYTES {
        return BackupStatus::Unreadable;
    }
    // Only an object reads: serde would also fill a struct from a list by
    // position, which the job never writes.
    let file = match serde_json::from_slice::<serde_json::Value>(content) {
        Ok(value) if value.is_object() => serde_json::from_value::<StatusFile>(value),
        _ => return BackupStatus::Unreadable,
    };
    let Ok(file) = file else {
        return BackupStatus::Unreadable;
    };
    let Some(last_success) = file.last_success_at else {
        return BackupStatus::NoSuccessYet;
    };
    let age = now - last_success;
    if age < -CLOCK_SKEW {
        BackupStatus::Unreadable
    } else if !file.last_attempt_ok {
        BackupStatus::Failed { last_success }
    } else if age > STALE_AFTER {
        BackupStatus::Stale { last_success }
    } else {
        BackupStatus::Fine { last_success }
    }
}

/// The state of the file at `path` at `now`, read at most `MAX_BYTES`.
/// Any failure to read is logged, once per load, and is `Unreadable`.
pub async fn read(path: &Path, now: OffsetDateTime) -> BackupStatus {
    match read_bounded(path).await {
        Ok(content) => state(&content, now),
        Err(err) => {
            eprintln!("could not read the backup status {}: {err}", path.display());
            BackupStatus::Unreadable
        }
    }
}

// The file's bytes, one past `MAX_BYTES` at most so `state` can tell a file
// that is too large. Only a regular file is opened for reading, so a
// directory or a pipe at the path is refused rather than read or waited on.
async fn read_bounded(path: &Path) -> std::io::Result<Vec<u8>> {
    if !tokio::fs::metadata(path).await?.is_file() {
        return Err(std::io::Error::other("not a regular file"));
    }
    let file = tokio::fs::File::open(path).await?;
    let mut content = Vec::with_capacity(MAX_BYTES + 1);
    file.take(MAX_BYTES as u64 + 1)
        .read_to_end(&mut content)
        .await?;
    Ok(content)
}

/// The line the front page renders for a state: words, the time of the last
/// success where there is one, and whether it needs the owner.
#[derive(Debug, PartialEq, Eq)]
pub struct BackupLine {
    pub attention: bool,
    pub before: &'static str,
    pub at: Option<LineTime>,
    pub after: &'static str,
}

/// The last success's time: relative to now as shown, and exact in UTC for
/// the element's `title` and `datetime`.
#[derive(Debug, PartialEq, Eq)]
pub struct LineTime {
    pub datetime: String,
    pub title: String,
    pub ago: String,
}

impl BackupStatus {
    /// The line for this state at `now`.
    pub fn line(&self, now: OffsetDateTime) -> BackupLine {
        let (attention, before, last_success, after) = match *self {
            BackupStatus::Fine { last_success } => (false, "Last backup", Some(last_success), ""),
            BackupStatus::Stale { last_success } => {
                (true, "Last backup", Some(last_success), ", none since")
            }
            BackupStatus::Failed { last_success } => (
                true,
                "Last backup",
                Some(last_success),
                ", the latest attempt failed",
            ),
            BackupStatus::NoSuccessYet => (true, "No backup has succeeded yet", None, ""),
            BackupStatus::Unreadable => (true, "Backup status cannot be read", None, ""),
        };
        BackupLine {
            attention,
            before,
            at: last_success.map(|at| LineTime::of(at, now)),
            after,
        }
    }
}

impl LineTime {
    fn of(at: OffsetDateTime, now: OffsetDateTime) -> LineTime {
        let at = at.to_offset(UtcOffset::UTC);
        let date = at.date();
        let clock = format!("{:02}:{:02}:{:02}", at.hour(), at.minute(), at.second());
        LineTime {
            datetime: format!("{date}T{clock}Z"),
            title: format!("{date} {clock} UTC"),
            ago: relative::ago(now - at),
        }
    }
}

// What the job writes, see write_status in infra/hosted/backup/backup.sh.
// Any other shape is unreadable, an unknown field or a missing one included.
// The attempt's time and its error are read so the whole shape is checked;
// the line shows neither.
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct StatusFile {
    #[serde(deserialize_with = "rfc3339")]
    #[allow(dead_code)]
    last_attempt_at: OffsetDateTime,
    last_attempt_ok: bool,
    #[serde(deserialize_with = "rfc3339_or_null")]
    last_success_at: Option<OffsetDateTime>,
    // Through a function, so it is required: plain serde reads an absent
    // Option as null.
    #[serde(deserialize_with = "string_or_null")]
    #[allow(dead_code)]
    last_error: Option<String>,
}

fn string_or_null<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<String>, D::Error> {
    Option::<String>::deserialize(deserializer)
}

fn rfc3339<'de, D: Deserializer<'de>>(deserializer: D) -> Result<OffsetDateTime, D::Error> {
    let text = String::deserialize(deserializer)?;
    OffsetDateTime::parse(&text, &Rfc3339).map_err(serde::de::Error::custom)
}

fn rfc3339_or_null<'de, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<OffsetDateTime>, D::Error> {
    let text = Option::<String>::deserialize(deserializer)?;
    text.map(|text| OffsetDateTime::parse(&text, &Rfc3339).map_err(serde::de::Error::custom))
        .transpose()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};

    // 2027-01-15 08:00:00 UTC.
    fn now() -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap()
    }

    fn rfc(at: OffsetDateTime) -> String {
        at.format(&Rfc3339).unwrap()
    }

    // A file as the job writes it after a success at `at`.
    fn succeeded(at: OffsetDateTime) -> Value {
        json!({
            "last_attempt_at": rfc(at),
            "last_attempt_ok": true,
            "last_success_at": rfc(at),
            "last_error": null,
        })
    }

    fn of(file: &Value) -> BackupStatus {
        state(file.to_string().as_bytes(), now())
    }

    // A fresh directory under the system's temporary one.
    fn scratch() -> PathBuf {
        let dir = std::env::temp_dir().join(format!("backup-status-{}", uuid::Uuid::now_v7()));
        std::fs::create_dir(&dir).unwrap();
        dir
    }

    #[test]
    fn a_recent_success_reads_as_fine() {
        let at = now() - Duration::hours(5);
        assert_eq!(of(&succeeded(at)), BackupStatus::Fine { last_success: at });

        // The job writes whole seconds in UTC; any offset is the instant it
        // names.
        let file = json!({
            "last_attempt_at": "2027-01-15T04:00:00+01:00",
            "last_attempt_ok": true,
            "last_success_at": "2027-01-15T04:00:00+01:00",
            "last_error": null,
        });
        assert_eq!(
            of(&file),
            BackupStatus::Fine {
                last_success: now() - Duration::hours(5)
            }
        );
    }

    #[test]
    fn a_success_older_than_26_hours_is_stale() {
        let at = now() - Duration::hours(26);
        assert_eq!(of(&succeeded(at)), BackupStatus::Fine { last_success: at });

        let at = at - Duration::seconds(1);
        assert_eq!(of(&succeeded(at)), BackupStatus::Stale { last_success: at });
    }

    // A success a moment ahead, from a clock a little fast, is just now; one
    // far ahead would read as fine forever, so it is not believed.
    #[test]
    fn a_success_ahead_of_now_is_fine_only_within_the_skew() {
        let at = now() + CLOCK_SKEW;
        assert_eq!(of(&succeeded(at)), BackupStatus::Fine { last_success: at });

        let at = at + Duration::seconds(1);
        assert_eq!(of(&succeeded(at)), BackupStatus::Unreadable);
    }

    #[test]
    fn a_failed_last_attempt_needs_attention_even_after_a_recent_success() {
        let success = now() - Duration::hours(1);
        let file = json!({
            "last_attempt_at": rfc(now() - Duration::minutes(5)),
            "last_attempt_ok": false,
            "last_success_at": rfc(success),
            "last_error": "upload",
        });
        assert_eq!(
            of(&file),
            BackupStatus::Failed {
                last_success: success
            }
        );

        // Failed and old: the failure is the news.
        let success = now() - Duration::days(3);
        let mut file = file;
        file["last_success_at"] = json!(rfc(success));
        assert_eq!(
            of(&file),
            BackupStatus::Failed {
                last_success: success
            }
        );
    }

    #[test]
    fn no_success_yet_needs_attention() {
        let failed = json!({
            "last_attempt_at": rfc(now() - Duration::minutes(5)),
            "last_attempt_ok": false,
            "last_success_at": null,
            "last_error": "dump",
        });
        assert_eq!(of(&failed), BackupStatus::NoSuccessYet);

        // A file claiming a success without a time has no success to show.
        let mut claimed = failed;
        claimed["last_attempt_ok"] = json!(true);
        claimed["last_error"] = Value::Null;
        assert_eq!(of(&claimed), BackupStatus::NoSuccessYet);
    }

    #[tokio::test]
    async fn a_missing_unreadable_or_malformed_file_needs_attention() {
        let dir = scratch();

        // No file at all, and a directory where the file should be.
        assert_eq!(
            read(&dir.join("missing.json"), now()).await,
            BackupStatus::Unreadable
        );
        assert_eq!(read(&dir, now()).await, BackupStatus::Unreadable);

        let good = succeeded(now() - Duration::hours(1));
        let mut extra = good.clone();
        extra["size"] = json!(1024);
        let mut missing = good.clone();
        missing.as_object_mut().unwrap().remove("last_error");
        let mut bad_time = good.clone();
        bad_time["last_success_at"] = json!("last night");
        let mut seconds = good.clone();
        seconds["last_success_at"] = json!(1_799_996_400);
        let mut wrong_type = good.clone();
        wrong_type["last_attempt_ok"] = json!("true");

        let cases: Vec<(&str, Vec<u8>)> = vec![
            ("invalid JSON", b"{ \"last_attempt_ok\": tru".to_vec()),
            ("empty", Vec::new()),
            ("a list", b"[]".to_vec()),
            ("an unknown field", extra.to_string().into_bytes()),
            ("a field missing", missing.to_string().into_bytes()),
            (
                "a time that does not parse",
                bad_time.to_string().into_bytes(),
            ),
            ("a time as seconds", seconds.to_string().into_bytes()),
            (
                "a field of the wrong type",
                wrong_type.to_string().into_bytes(),
            ),
        ];
        for (case, content) in cases {
            assert_eq!(state(&content, now()), BackupStatus::Unreadable, "{case}");
            let path = dir.join("backup.json");
            std::fs::write(&path, &content).unwrap();
            assert_eq!(read(&path, now()).await, BackupStatus::Unreadable, "{case}");
        }

        // A good file reads through `read` too, so the cases above fail for
        // their content and not the reading.
        let path = dir.join("backup.json");
        std::fs::write(&path, good.to_string()).unwrap();
        assert!(matches!(
            read(&path, now()).await,
            BackupStatus::Fine { .. }
        ));

        // A good file padded past 4 KiB with whitespace, still valid JSON,
        // is not read.
        let mut padded = good.to_string().into_bytes();
        padded.resize(MAX_BYTES + 1, b' ');
        assert_eq!(state(&padded, now()), BackupStatus::Unreadable);
        std::fs::write(&path, &padded).unwrap();
        assert_eq!(read(&path, now()).await, BackupStatus::Unreadable);
        // Exactly 4 KiB is.
        padded.truncate(MAX_BYTES);
        std::fs::write(&path, &padded).unwrap();
        assert!(matches!(
            read(&path, now()).await,
            BackupStatus::Fine { .. }
        ));

        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn without_the_setting_there_is_no_status() {
        assert_eq!(setting(None), Ok(None));
        assert_eq!(
            setting(Some("/status/backup.json".into())),
            Ok(Some(PathBuf::from("/status/backup.json")))
        );
        for relative in ["", "backup.json", "status/backup.json", "./backup.json"] {
            let refused = setting(Some(relative.into())).unwrap_err();
            assert!(refused.contains(SETTING), "{refused}");
        }
    }

    #[test]
    fn each_state_has_its_words() {
        let at = now() - Duration::hours(5);
        let time = || {
            Some(LineTime {
                datetime: "2027-01-15T03:00:00Z".into(),
                title: "2027-01-15 03:00:00 UTC".into(),
                ago: "5 hours ago".into(),
            })
        };

        assert_eq!(
            BackupStatus::Fine { last_success: at }.line(now()),
            BackupLine {
                attention: false,
                before: "Last backup",
                at: time(),
                after: "",
            }
        );
        assert_eq!(
            BackupStatus::Stale { last_success: at }.line(now()),
            BackupLine {
                attention: true,
                before: "Last backup",
                at: time(),
                after: ", none since",
            }
        );
        assert_eq!(
            BackupStatus::Failed { last_success: at }.line(now()),
            BackupLine {
                attention: true,
                before: "Last backup",
                at: time(),
                after: ", the latest attempt failed",
            }
        );
        assert_eq!(
            BackupStatus::NoSuccessYet.line(now()),
            BackupLine {
                attention: true,
                before: "No backup has succeeded yet",
                at: None,
                after: "",
            }
        );
        assert_eq!(
            BackupStatus::Unreadable.line(now()),
            BackupLine {
                attention: true,
                before: "Backup status cannot be read",
                at: None,
                after: "",
            }
        );

        // A success a moment ahead is just now, not "in under a minute".
        let ahead = BackupStatus::Fine {
            last_success: now() + Duration::seconds(30),
        };
        assert_eq!(ahead.line(now()).at.unwrap().ago, "just now");
    }
}

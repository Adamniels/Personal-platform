//! The front page, composed on the server from every feature's display data:
//! a card for a feature the core has one for, and a visible placeholder for
//! one that could not be reached, sent the wrong data or has no card, all
//! within the display deadline. Through the real router, with a registry
//! pointing at stub features. See "What a feature exposes for the front
//! page" in docs/core-architecture.md.

mod common;

use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use common::stubs::{hanging_stub, refused_addr, stub};
use platform_core::origin::PublicOrigin;
use platform_core::registry::Registry;
use platform_core::signing_key::SigningKey;
use platform_core::{AppState, DEFAULT_ACCESS_TOKEN_LIFETIME};
use serde_json::{Value, json};
use std::net::SocketAddr;
use std::path::PathBuf;
use std::time::{Duration, Instant};
use time::OffsetDateTime;
use time::format_description::well_known::Rfc3339;
use tokio::net::TcpListener;
use uuid::Uuid;

const DEADLINE: Duration = Duration::from_millis(200);
// What the page may take beyond the deadline: verifying the token and
// rendering, on a loaded CI runner.
const MARGIN: Duration = Duration::from_millis(150);

// Canary's data, as contracts/display/canary.json has it.
fn canary_data() -> Value {
    json!({
        "account_id": "01900000-0000-7000-8000-000000000001",
        "token_expires_at": 1_800_001_800,
        "key_id": "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k",
        "python_version": "3.14.0",
    })
}

// A registry entry, its display endpoint under its own prefix as the
// committed one is.
fn entry(name: &str, prefix: &str, addr: SocketAddr) -> String {
    format!(
        "[[feature]]\nname = \"{name}\"\nprefix = \"{prefix}\"\nupstream = \"http://{addr}\"\n\
         display = \"{prefix}/api/display\"\n\n"
    )
}

// The core's router on a free port, with `registry` and the short deadline.
// Returns its address and a valid access token for a fresh account id.
async fn serve(db: &common::TestDb, registry: &str) -> (SocketAddr, String, Uuid) {
    serve_with_backup_status(db, registry, None).await
}

// The same, reading the backup status from `backup_status_file`, as
// CORE_BACKUP_STATUS_FILE sets it.
async fn serve_with_backup_status(
    db: &common::TestDb,
    registry: &str,
    backup_status_file: Option<PathBuf>,
) -> (SocketAddr, String, Uuid) {
    let mut state = AppState::new(
        db.pool.clone(),
        SigningKey::generate().0,
        DEFAULT_ACCESS_TOKEN_LIFETIME,
        Registry::parse(registry).unwrap(),
    )
    .await
    .unwrap();
    state.display_deadline = DEADLINE;
    state.backup_status_file = backup_status_file;

    let account_id = Uuid::now_v7();
    let token = state.issuer.issue(
        account_id,
        Uuid::now_v7(),
        Uuid::now_v7(),
        OffsetDateTime::now_utc(),
    );

    let app = platform_core::app(state, PublicOrigin::parse("https://platform.test").unwrap());
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    (addr, token, account_id)
}

// GETs the front page with the access cookie and returns the status, the
// body and how long it took.
async fn front_page(addr: SocketAddr, token: &str) -> (u16, String, Duration) {
    let started = Instant::now();
    let response = reqwest::Client::new()
        .get(format!("http://{addr}/"))
        .header(header::COOKIE.as_str(), format!("__Host-access={token}"))
        .send()
        .await
        .unwrap();
    let status = response.status().as_u16();
    let body = response.text().await.unwrap();
    (status, body, started.elapsed())
}

fn json_response(body: Value) -> Response {
    axum::Json(body).into_response()
}

#[tokio::test]
async fn the_front_page_renders_canarys_card() {
    let db = common::TestDb::new().await;
    let (canary, seen) = stub(|| json_response(canary_data())).await;
    let (addr, token, _) = serve(&db, &entry("Canary", "/canary", canary)).await;

    let (status, body, _) = front_page(addr, &token).await;

    assert_eq!(status, 200, "{body}");
    assert!(body.contains(r#"<a href="/canary">Canary</a>"#), "{body}");
    assert!(
        body.contains("01900000-0000-7000-8000-000000000001"),
        "{body}"
    );
    assert!(
        body.contains("Verified your token in Python 3.14.0"),
        "{body}"
    );
    // 1 800 001 800 seconds is 2027-01-15 08:30:00 UTC.
    assert!(body.contains("Token valid until 08:30 UTC"), "{body}");
    assert!(body.contains("kPrK_qmx"), "{body}");
    assert!(!body.contains("kPrK_qmxV"), "only eight characters: {body}");
    assert!(!body.contains("could not be reached"), "{body}");

    let seen = seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 1, "canary was called once");
    assert_eq!(
        seen[0].get(header::AUTHORIZATION).unwrap(),
        &format!("Bearer {token}")
    );

    db.close().await;
}

// Check 9 of the feature: a feature that is down, broken or slow costs its
// own card, never the page.
#[tokio::test]
async fn a_feature_that_is_refused_fails_hangs_or_sends_nonsense_gets_a_placeholder() {
    let db = common::TestDb::new().await;
    let (failing, _) = stub(|| StatusCode::INTERNAL_SERVER_ERROR.into_response()).await;
    let (garbled, _) =
        stub(|| ([(header::CONTENT_TYPE, "application/json")], "{ not json").into_response()).await;

    for (case, canary) in [
        ("refused", refused_addr().await),
        ("a 500", failing),
        ("a hang", hanging_stub().await),
        ("not JSON", garbled),
    ] {
        let (addr, token, _) = serve(&db, &entry("Canary", "/canary", canary)).await;

        let (status, body, elapsed) = front_page(addr, &token).await;

        assert_eq!(status, 200, "{case}: {body}");
        assert!(
            body.contains("Canary could not be reached."),
            "{case}: {body}"
        );
        // The markup e2e/tests/test_style.py adds to a real page to check
        // the placeholder's look, so the two must not drift.
        assert!(
            body.contains(
                r#"<article class="card unavailable"><h2>Canary</h2><p>Canary could not be reached.</p></article>"#
            ),
            "{case}: {body}"
        );
        assert!(!body.contains("Verified your token"), "{case}: {body}");
        assert!(
            elapsed < DEADLINE + MARGIN,
            "{case}: took {elapsed:?} for a deadline of {DEADLINE:?}"
        );
    }

    db.close().await;
}

#[tokio::test]
async fn data_of_the_wrong_shape_gets_a_placeholder() {
    let db = common::TestDb::new().await;
    for (case, data) in [
        (
            "another feature's data",
            json!({ "active": 4, "due_today": 2 }),
        ),
        ("a field missing", {
            let mut data = canary_data();
            data.as_object_mut().unwrap().remove("key_id");
            data
        }),
        ("a field of the wrong type", {
            let mut data = canary_data();
            data["token_expires_at"] = json!("soon");
            data
        }),
        ("an unknown field", {
            let mut data = canary_data();
            data["colour"] = json!("red");
            data
        }),
        // serde would read a struct from its values in order.
        (
            "canary's values as a list",
            json!([
                "01900000-0000-7000-8000-000000000001",
                1_800_001_800,
                "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k",
                "3.14.0"
            ]),
        ),
    ] {
        let (canary, _) = stub(move || json_response(data.clone())).await;
        let (addr, token, _) = serve(&db, &entry("Canary", "/canary", canary)).await;

        let (status, body, _) = front_page(addr, &token).await;

        assert_eq!(status, 200, "{case}: {body}");
        assert!(
            body.contains("Canary could not be reached."),
            "{case}: {body}"
        );
        assert!(!body.contains("Verified your token"), "{case}: {body}");
    }

    db.close().await;
}

#[tokio::test]
async fn a_feature_the_core_has_no_card_for_gets_a_placeholder() {
    let db = common::TestDb::new().await;
    // Canary's own data, so only the prefix decides.
    let (desk, seen) = stub(|| json_response(canary_data())).await;
    let (addr, token, _) = serve(&db, &entry("Desk", "/desk", desk)).await;

    let (status, body, _) = front_page(addr, &token).await;

    assert_eq!(status, 200, "{body}");
    assert!(body.contains("Desk could not be reached."), "{body}");
    assert!(!body.contains("Verified your token"), "{body}");
    assert_eq!(seen.lock().unwrap().len(), 1, "desk was asked");

    db.close().await;
}

#[tokio::test]
async fn card_values_are_escaped() {
    let db = common::TestDb::new().await;
    let (canary, _) = stub(|| {
        let mut data = canary_data();
        data["account_id"] = json!("<b>bold</b>");
        data["python_version"] = json!(r#"<script>"x"&</script>"#);
        data["key_id"] = json!("<i>italic</i>");
        json_response(data)
    })
    .await;
    let (addr, token, _) = serve(&db, &entry("Canary", "/canary", canary)).await;

    let (status, body, _) = front_page(addr, &token).await;

    assert_eq!(status, 200, "{body}");
    for raw in ["<b>", "<script>", "<i>"] {
        assert!(!body.contains(raw), "{raw} came out as markup: {body}");
    }
    assert!(body.contains("&#60;b&#62;bold&#60;/b&#62;"), "{body}");
    assert!(
        body.contains("&#60;script&#62;&#34;x&#34;&#38;&#60;/script&#62;"),
        "{body}"
    );
    // The first eight characters of the key id, escaped.
    assert!(body.contains("&#60;i&#62;itali"), "{body}");

    db.close().await;
}

#[tokio::test]
async fn the_front_page_without_display_features_still_renders() {
    let db = common::TestDb::new().await;
    let (addr, token, _) = serve(&db, "").await;

    let (status, body, _) = front_page(addr, &token).await;

    assert_eq!(status, 200, "{body}");
    assert!(body.contains("<h1>Platform</h1>"), "{body}");
    assert!(!body.contains("could not be reached"), "{body}");

    db.close().await;
}

// The backup line, last in the page's content: the plain state when the last
// backup succeeded within a day or so, and each state that needs the owner
// with its dot and its words. Times are whole seconds before the real now,
// since the handler reads the clock.
#[tokio::test]
async fn the_backup_line_renders_each_state() {
    let db = common::TestDb::new().await;
    let dir = std::env::temp_dir().join(format!("front-backup-{}", Uuid::now_v7()));
    std::fs::create_dir(&dir).unwrap();
    let file = dir.join("backup.json");

    let now = OffsetDateTime::now_utc().replace_nanosecond(0).unwrap();
    let rfc = |at: OffsetDateTime| at.format(&Rfc3339).unwrap();
    let five_hours_ago = now - time::Duration::hours(5);
    let two_days_ago = now - time::Duration::days(2);
    // The exact time, in UTC, as the element's datetime and title carry it.
    let time_of = |at: OffsetDateTime, ago: &str| {
        let date = at.date();
        let clock = format!("{:02}:{:02}:{:02}", at.hour(), at.minute(), at.second());
        format!(r#"<time datetime="{date}T{clock}Z" title="{date} {clock} UTC">{ago}</time>"#)
    };
    // A last_error the job never writes, to show nothing from the file is
    // rendered as it stands.
    let status = |ok: bool, success: Option<OffsetDateTime>, error: &str| {
        json!({
            "last_attempt_at": rfc(now - time::Duration::minutes(1)),
            "last_attempt_ok": ok,
            "last_success_at": success.map(rfc),
            "last_error": if ok { Value::Null } else { json!(error) },
        })
        .to_string()
    };

    let cases: Vec<(&str, Option<String>, String)> = vec![
        (
            "fine",
            Some(status(true, Some(five_hours_ago), "")),
            format!(
                r#"<p class="backup">Last backup {}</p>"#,
                time_of(five_hours_ago, "5 hours ago")
            ),
        ),
        (
            "stale",
            Some(status(true, Some(two_days_ago), "")),
            format!(
                r#"<p class="backup attention">Last backup {}, none since</p>"#,
                time_of(two_days_ago, "2 days ago")
            ),
        ),
        (
            "failed",
            Some(status(
                false,
                Some(five_hours_ago),
                "<script>upload</script>",
            )),
            format!(
                r#"<p class="backup attention">Last backup {}, the latest attempt failed</p>"#,
                time_of(five_hours_ago, "5 hours ago")
            ),
        ),
        (
            "no success yet",
            Some(status(false, None, "<b>dump</b>")),
            r#"<p class="backup attention">No backup has succeeded yet</p>"#.into(),
        ),
        (
            "missing",
            None,
            r#"<p class="backup attention">Backup status cannot be read</p>"#.into(),
        ),
        (
            "malformed",
            Some(r#"{"last_attempt_ok": "<i>yes</i>"}"#.into()),
            r#"<p class="backup attention">Backup status cannot be read</p>"#.into(),
        ),
    ];

    for (case, content, line) in cases {
        match &content {
            Some(content) => std::fs::write(&file, content).unwrap(),
            None => {
                let _ = std::fs::remove_file(&file);
            }
        }
        let (addr, token, _) = serve_with_backup_status(&db, "", Some(file.clone())).await;

        let (status, body, _) = front_page(addr, &token).await;

        assert_eq!(status, 200, "{case}: {body}");
        assert!(body.contains(&line), "{case}: {line} missing from {body}");
        assert_eq!(
            body.matches(r#"class="backup"#).count(),
            1,
            "{case}: {body}"
        );
        // The last thing in the page's content, after the cards.
        let after = &body[body.find(&line).unwrap() + line.len()..];
        assert!(after.trim_start().starts_with("</main>"), "{case}: {body}");
        // Nothing the file holds besides its times reaches the page.
        for raw in ["<script>", "<b>", "<i>", "upload", "dump", "yes"] {
            assert!(!body.contains(raw), "{case}: {raw} in {body}");
        }
    }

    // Without the setting, no line at all, whatever the file says.
    std::fs::write(&file, status(false, None, "dump")).unwrap();
    let (addr, token, _) = serve(&db, "").await;
    let (status, body, _) = front_page(addr, &token).await;
    assert_eq!(status, 200, "{body}");
    assert!(!body.contains("backup"), "{body}");
    assert!(!body.contains("Backup"), "{body}");

    std::fs::remove_dir_all(&dir).unwrap();
    db.close().await;
}

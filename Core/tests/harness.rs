//! The per-test database harness itself, in common/mod.rs: each test gets a
//! database of its own, migrated, and connects to it as the app role.

mod common;

use sqlx::Connection;

#[tokio::test]
async fn connects_as_the_app_role() {
    let db = common::TestDb::new().await;

    let (current, session): (String, String) =
        sqlx::query_as("SELECT current_user::text, session_user::text")
            .fetch_one(&db.pool)
            .await
            .unwrap();
    assert_eq!(
        (current.as_str(), session.as_str()),
        ("core_app", "core_app")
    );

    db.close().await;
}

#[tokio::test]
async fn is_migrated() {
    let db = common::TestDb::new().await;

    let mut admin = db.admin().await;
    let applied: i64 = sqlx::query_scalar("SELECT count(*) FROM _sqlx_migrations")
        .fetch_one(&mut admin)
        .await
        .unwrap();
    assert_eq!(applied, platform_core::MIGRATOR.iter().count() as i64);

    admin.close().await.unwrap();
    db.close().await;
}

// The copies keep the role split: the app role still cannot create a table.
#[tokio::test]
async fn keeps_the_role_split() {
    let db = common::TestDb::new().await;

    let created = sqlx::query("CREATE TABLE harness_probe (id int)")
        .execute(&db.pool)
        .await;
    assert!(
        created.is_err(),
        "the app role created a table in a test database"
    );

    db.close().await;
}

#[tokio::test]
async fn one_test_cannot_see_another() {
    let first = common::TestDb::new().await;
    let second = common::TestDb::new().await;

    let mut admin = first.admin().await;
    sqlx::query("CREATE TABLE harness_marker (id int)")
        .execute(&mut admin)
        .await
        .unwrap();

    let seen: bool = sqlx::query_scalar("SELECT to_regclass('public.harness_marker') IS NOT NULL")
        .fetch_one(&second.pool)
        .await
        .unwrap();
    assert!(
        !seen,
        "a table created in one test database was visible in another"
    );

    admin.close().await.unwrap();
    first.close().await;
    second.close().await;
}

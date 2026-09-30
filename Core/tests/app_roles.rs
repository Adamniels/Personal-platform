//! The core connects as a role that owns nothing and cannot create tables.
//! Connecting as the owner would work just as well and silently bypass the
//! role split, see "Row level security does not apply here" in
//! docs/core-architecture.md. Needs the compose Postgres running.

use sqlx::PgPool;

async fn app_pool() -> PgPool {
    dotenvy::dotenv().ok();
    let url = std::env::var("CORE_APP_DATABASE_URL").expect("CORE_APP_DATABASE_URL is not set");
    PgPool::connect(&url)
        .await
        .expect("could not connect as the app role")
}

#[tokio::test]
async fn app_role_cannot_create_table() {
    let pool = app_pool().await;
    let mut tx = pool.begin().await.unwrap();

    let err = sqlx::query("CREATE TABLE app_role_probe (id int)")
        .execute(&mut *tx)
        .await
        .expect_err("the app role created a table");

    let code = err
        .as_database_error()
        .and_then(|db_err| db_err.code())
        .map(|code| code.into_owned());
    assert_eq!(
        code.as_deref(),
        Some("42501"),
        "expected permission denied, got: {err}"
    );
}

#[tokio::test]
async fn app_role_owns_nothing() {
    let pool = app_pool().await;

    let owned = sqlx::query_scalar!(
        r#"
        SELECT count(*) AS "owned!"
        FROM pg_shdepend
        WHERE deptype = 'o'
          AND refobjid = (SELECT oid FROM pg_roles WHERE rolname = current_user)
        "#
    )
    .fetch_one(&pool)
    .await
    .unwrap();

    assert_eq!(owned, 0, "the app role owns {owned} objects");
}

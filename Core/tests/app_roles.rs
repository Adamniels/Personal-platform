//! The core connects as a role that owns nothing, cannot create tables, and
//! cannot delete from any. Connecting as the owner would work just as well and
//! silently bypass the role split, see "Row level security does not apply
//! here" in docs/core-architecture.md, and for deleting, "The application role
//! is granted no DELETE". Needs the compose Postgres running, migrated.
//!
//! Each check is a helper run twice: as the app role, where it must hold, and
//! as the owner, where it must not. The owner's run is the negative control,
//! proving the check can fail at all. It is ignored by default, since only
//! `migrate` and CI hold the owner's connection string:
//! `cargo test --test app_roles -- --ignored`.

use sqlx::PgPool;

async fn app_pool() -> PgPool {
    dotenvy::dotenv().ok();
    let url = std::env::var("CORE_APP_DATABASE_URL").expect("CORE_APP_DATABASE_URL is not set");
    PgPool::connect(&url)
        .await
        .expect("could not connect as the app role")
}

// Not loaded from .env, which never holds it. Unset is a failure, not a skip,
// so a CI step that lost the variable cannot pass by running nothing.
async fn owner_pool() -> PgPool {
    let url = std::env::var("CORE_OWNER_DATABASE_URL").expect("CORE_OWNER_DATABASE_URL is not set");
    PgPool::connect(&url)
        .await
        .expect("could not connect as the owner")
}

// Inside a transaction that is never committed, so a role that can create the
// table leaves nothing behind.
async fn cannot_create_table(pool: &PgPool) -> Result<(), String> {
    let mut tx = pool.begin().await.map_err(|err| err.to_string())?;

    let err = match sqlx::query("CREATE TABLE app_role_probe (id int)")
        .execute(&mut *tx)
        .await
    {
        Ok(_) => return Err("the role created a table".into()),
        Err(err) => err,
    };

    let code = err
        .as_database_error()
        .and_then(|db_err| db_err.code())
        .map(|code| code.into_owned());
    if code.as_deref() == Some("42501") {
        Ok(())
    } else {
        Err(format!("expected permission denied, got: {err}"))
    }
}

async fn owns_nothing(pool: &PgPool) -> Result<(), String> {
    let owned = sqlx::query_scalar!(
        r#"
        SELECT count(*) AS "owned!"
        FROM pg_shdepend
        WHERE deptype = 'o'
          AND refobjid = (SELECT oid FROM pg_roles WHERE rolname = current_user)
        "#
    )
    .fetch_one(pool)
    .await
    .map_err(|err| err.to_string())?;

    if owned == 0 {
        Ok(())
    } else {
        Err(format!("the role owns {owned} objects"))
    }
}

async fn cannot_delete_from_any_table(pool: &PgPool) -> Result<(), String> {
    let deletable = sqlx::query_scalar!(
        r#"
        SELECT c.relname::text AS "table!"
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
        AND c.relkind IN ('r', 'p', 'v')
        AND (has_table_privilege(current_user, c.oid, 'DELETE')
                OR has_table_privilege(current_user, c.oid, 'TRUNCATE'))
        ORDER BY 1
        "#
    )
    .fetch_all(pool)
    .await
    .map_err(|err| err.to_string())?;

    if deletable.is_empty() {
        Ok(())
    } else {
        Err(format!(
            "the role can delete from or truncate: {deletable:?}"
        ))
    }
}

#[tokio::test]
async fn app_role_cannot_create_table() {
    assert_eq!(cannot_create_table(&app_pool().await).await, Ok(()));
}

#[tokio::test]
async fn app_role_owns_nothing() {
    assert_eq!(owns_nothing(&app_pool().await).await, Ok(()));
}

#[tokio::test]
async fn app_role_cannot_delete_from_any_table() {
    assert_eq!(
        cannot_delete_from_any_table(&app_pool().await).await,
        Ok(())
    );
}

#[tokio::test]
#[ignore = "negative control: needs CORE_OWNER_DATABASE_URL, run in CI"]
async fn owner_can_create_table() {
    let result = cannot_create_table(&owner_pool().await).await;
    assert!(
        result.is_err(),
        "the check passed for the owner: {result:?}"
    );
}

#[tokio::test]
#[ignore = "negative control: needs CORE_OWNER_DATABASE_URL, run in CI"]
async fn owner_owns_something() {
    let result = owns_nothing(&owner_pool().await).await;
    assert!(
        result.is_err(),
        "the check passed for the owner: {result:?}"
    );
}

#[tokio::test]
#[ignore = "negative control: needs CORE_OWNER_DATABASE_URL, run in CI"]
async fn owner_can_delete_from_a_table() {
    let result = cannot_delete_from_any_table(&owner_pool().await).await;
    assert!(
        result.is_err(),
        "the check passed for the owner: {result:?}"
    );
}

//! A database of its own for every test that writes, on the real Postgres:
//! compose locally, the service container in CI.
//!
//! Once per test binary, a template is created, given the grants
//! infra/postgres/initdb/core.sh gives the core's database, and migrated as
//! core_owner. Each test then gets a copy of it and connects as core_app, so
//! it tests what the running core is. A test that passes drops its copy; one
//! that fails leaves it to look at, and the next run sweeps it away.
//!
//! Creating databases needs the Postgres superuser, read from
//! CORE_TEST_ADMIN_DATABASE_URL. Only tests hold it, never the running core,
//! and it is never in .env. Locally, from Core/:
//!   CORE_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" cargo test

#![allow(dead_code)]

use sqlx::postgres::{PgConnectOptions, PgPool, PgPoolOptions};
use sqlx::{AssertSqlSafe, Connection, PgConnection};
use std::str::FromStr;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::OnceCell;

pub struct TestDb {
    pub pool: PgPool,
    name: String,
}

impl TestDb {
    pub async fn new() -> TestDb {
        let template = template().await;
        let name = unique_name("core_test");

        let mut admin = admin_connection("postgres").await;
        execute(
            &mut admin,
            &format!("CREATE DATABASE {name} TEMPLATE {template} OWNER core_owner"),
        )
        .await;
        // Database privileges belong to the database, not its contents, so a
        // copy does not inherit the template's.
        grant_like_core(&mut admin, &name).await;
        admin.close().await.ok();

        let pool = PgPoolOptions::new()
            .max_connections(5)
            .connect_with(app_options().database(&name))
            .await
            .unwrap_or_else(|err| panic!("could not connect to {name} as the app role: {err}"));

        TestDb { pool, name }
    }

    /// A superuser connection to this test's database, for arranging or
    /// inspecting what the app role cannot.
    pub async fn admin(&self) -> PgConnection {
        admin_connection(&self.name).await
    }

    /// Called at the end of a test that passed. A test that panics never gets
    /// here, and its database stays until the next run's sweep.
    pub async fn close(self) {
        self.pool.close().await;
        let mut admin = admin_connection("postgres").await;
        execute(&mut admin, &format!("DROP DATABASE {}", self.name)).await;
        admin.close().await.ok();
    }
}

// The template, built by whichever test of this binary asks first.
async fn template() -> &'static str {
    static TEMPLATE: OnceCell<String> = OnceCell::const_new();
    TEMPLATE
        .get_or_init(|| async {
            let mut admin = admin_connection("postgres").await;
            sweep(&mut admin).await;

            let name = unique_name("core_tpl");
            execute(
                &mut admin,
                &format!("CREATE DATABASE {name} OWNER core_owner"),
            )
            .await;
            grant_like_core(&mut admin, &name).await;
            admin.close().await.ok();

            // As core_owner, as the migrate command runs, so every object is
            // the owner's and its default privileges apply to the app role.
            let mut conn = admin_connection(&name).await;
            execute(&mut conn, "SET ROLE core_owner").await;
            platform_core::MIGRATOR
                .run(&mut conn)
                .await
                .unwrap_or_else(|err| panic!("could not migrate the template: {err}"));
            // A template cannot be copied while anyone is connected to it.
            conn.close().await.ok();

            name
        })
        .await
}

// The grants infra/postgres/initdb/core.sh gives the core's database.
async fn grant_like_core(admin: &mut PgConnection, name: &str) {
    execute(admin, &format!("REVOKE ALL ON DATABASE {name} FROM PUBLIC")).await;
    execute(
        admin,
        &format!("GRANT CONNECT ON DATABASE {name} TO core_app"),
    )
    .await;
}

// Drops what earlier runs left: databases of failed tests, and templates.
// Only names stamped before this process started, and a database still in
// use refuses to drop, so another run's databases are never touched.
async fn sweep(admin: &mut PgConnection) {
    let names: Vec<String> = sqlx::query_scalar(
        r"SELECT datname::text FROM pg_database WHERE datname ~ '^core_(test|tpl)_\d+_'",
    )
    .fetch_all(&mut *admin)
    .await
    .unwrap_or_else(|err| panic!("could not list leftover test databases: {err}"));

    for name in names {
        let stamped: u128 = name
            .split('_')
            .nth(2)
            .and_then(|stamp| stamp.parse().ok())
            .unwrap_or(u128::MAX);
        if stamped < process_started() {
            sqlx::query(AssertSqlSafe(format!("DROP DATABASE {name}")))
                .execute(&mut *admin)
                .await
                .ok();
        }
    }
}

// core_<kind>_<milliseconds at process start>_<pid>_<counter>, unique within
// and across runs, and safe as an unquoted identifier.
fn unique_name(kind: &str) -> String {
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let n = COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{kind}_{}_{}_{n}", process_started(), std::process::id())
}

fn process_started() -> u128 {
    static STARTED: OnceLock<u128> = OnceLock::new();
    *STARTED.get_or_init(|| {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("the clock is before 1970")
            .as_millis()
    })
}

// Unset is a failure, never a skip, so a run that lost the variable cannot
// pass by testing nothing.
async fn admin_connection(database: &str) -> PgConnection {
    let url = std::env::var("CORE_TEST_ADMIN_DATABASE_URL")
        .expect("CORE_TEST_ADMIN_DATABASE_URL is not set, see Core/tests/common/mod.rs");
    let options = PgConnectOptions::from_str(&url)
        .expect("CORE_TEST_ADMIN_DATABASE_URL is not a connection string")
        .database(database);
    PgConnection::connect_with(&options)
        .await
        .unwrap_or_else(|err| panic!("could not connect to {database} as the test admin: {err}"))
}

// The app role's own credentials, from the same variable the running core
// uses, pointed at a test database.
fn app_options() -> PgConnectOptions {
    dotenvy::dotenv().ok();
    let url = std::env::var("CORE_APP_DATABASE_URL").expect("CORE_APP_DATABASE_URL is not set");
    PgConnectOptions::from_str(&url).expect("CORE_APP_DATABASE_URL is not a connection string")
}

// Every dynamic statement here is DDL naming a database, and identifiers
// cannot be bind parameters. The names are only ever ones unique_name built,
// digits and underscores, or ones the sweep matched by that same pattern.
async fn execute(conn: &mut PgConnection, sql: &str) {
    sqlx::query(AssertSqlSafe(sql.to_owned()))
        .execute(&mut *conn)
        .await
        .unwrap_or_else(|err| panic!("{sql}: {err}"));
}

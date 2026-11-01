//! The core, as a library: the binary in main.rs parses the command and
//! calls into it, and the tests in tests/ import what they test.

pub mod invite;
pub mod origin;
pub mod password;
pub mod secret_token;

use axum::{Router, routing::get};
use origin::PublicOrigin;
use sqlx::Connection;
use sqlx::migrate::{Migrate, Migration, Migrator};
use sqlx::postgres::{PgConnection, PgPool, PgPoolOptions};
use std::collections::HashSet;
use std::env;
use std::error::Error;
use std::net::SocketAddr;
use std::time::Duration;
use time::OffsetDateTime;
use tokio::net::TcpListener;

// The SQL files in Core/migrations, embedded at compile time. build.rs makes
// Cargo rebuild when one is added.
pub static MIGRATOR: Migrator = sqlx::migrate!();

pub async fn serve() -> Result<(), Box<dyn Error>> {
    let addr = listen_addr()?;
    let origin = public_origin()?;

    let _pool = connect_database().await?;
    println!("connected to the database");

    let app = app(origin);

    let listener = TcpListener::bind(addr)
        .await
        .map_err(|err| format!("could not listen on {addr}: {err}"))?;
    println!("listening on {}", listener.local_addr()?);

    axum::serve(listener, app).await?;

    Ok(())
}
// help function for serve
fn listen_addr() -> Result<SocketAddr, Box<dyn Error>> {
    let raw = env::var("CORE_LISTEN_ADDR").map_err(|err| format!("CORE_LISTEN_ADDR: {err}"))?;
    let addr = raw
        .parse::<SocketAddr>()
        .map_err(|err| format!("CORE_LISTEN_ADDR='{raw}' is not an IP and port: {err}"))?;
    Ok(addr)
}

// help function for serve
fn app(origin: PublicOrigin) -> Router {
    origin::protect(Router::new().route("/", get(root)), origin)
}

// help function for serve
fn public_origin() -> Result<PublicOrigin, Box<dyn Error>> {
    let raw = env::var("CORE_PUBLIC_ORIGIN").map_err(|err| format!("CORE_PUBLIC_ORIGIN: {err}"))?;
    Ok(PublicOrigin::parse(&raw).map_err(|err| format!("CORE_PUBLIC_ORIGIN: {err}"))?)
}

// help function for serve
async fn connect_database() -> Result<PgPool, Box<dyn Error>> {
    let url =
        env::var("CORE_APP_DATABASE_URL").map_err(|err| format!("CORE_APP_DATABASE_URL: {err}"))?;
    let pool = PgPoolOptions::new()
        .acquire_timeout(Duration::from_secs(5))
        .connect(&url)
        .await
        .map_err(|err| format!("could not connect to the database: {err}"))?;

    Ok(pool)
}

// core root endpoint
async fn root() -> &'static str {
    "platform core root endpoint"
}

// for the invite command: stores a code as the app role, prints it once on
// stdout and its expiry on stderr, so the code alone can be piped or copied.
pub async fn invite(expires_in: time::Duration) -> Result<(), Box<dyn Error>> {
    let pool = connect_database().await?;
    let now = OffsetDateTime::now_utc();
    let expires_at = now
        .checked_add(expires_in)
        .ok_or("--expires-in is too far in the future")?;

    let code = invite::create(&pool, now, expires_at)
        .await
        .map_err(|err| format!("could not store the invite code: {err}"))?;

    println!("{code}");
    eprintln!(
        "expires {}",
        expires_at
            .replace_nanosecond(0)?
            .format(&time::format_description::well_known::Rfc3339)?
    );
    Ok(())
}

// for migrate command
pub async fn migrate() -> Result<(), Box<dyn Error>> {
    let url = env::var("CORE_OWNER_DATABASE_URL")
        .map_err(|err| format!("CORE_OWNER_DATABASE_URL: {err}"))?;
    let mut conn = PgConnection::connect(&url)
        .await
        .map_err(|err| format!("could not connect to database as the owner: {err}"))?;

    let pending = pending_migrations(&mut conn)
        .await
        .map_err(|err| format!("could not read applied migrations: {err}"))?;

    // Always run, even with nothing pending: run also checks that applied
    // migrations have not been edited or removed.
    MIGRATOR
        .run(&mut conn)
        .await
        .map_err(|err| format!("migration failed: {err}"))?;

    if pending.is_empty() {
        println!("nothing to do, every migration is applied");
    }

    for migration in &pending {
        println!("applied {} {}", migration.version, migration.description);
    }

    Ok(())
}

// What run is about to apply, since run itself does not report it.
async fn pending_migrations(
    conn: &mut PgConnection,
) -> Result<Vec<&'static Migration>, sqlx::migrate::MigrateError> {
    conn.ensure_migrations_table(&MIGRATOR.table_name).await?;

    let applied: HashSet<i64> = conn
        .list_applied_migrations(&MIGRATOR.table_name)
        .await?
        .into_iter()
        .map(|migration| migration.version)
        .collect();

    Ok(MIGRATOR
        .iter()
        .filter(|migration| !migration.migration_type.is_down_migration())
        .filter(|migration| !applied.contains(&migration.version))
        .collect())
}

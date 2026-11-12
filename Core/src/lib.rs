//! The core, as a library: the binary in main.rs parses the command and
//! calls into it, and the tests in tests/ import what they test.

pub mod accounts;
pub mod claims;
pub mod cookies;
pub mod display;
pub mod duration;
pub mod http;
pub mod invite;
pub mod issuer;
pub mod jwks;
pub mod login;
pub mod nav;
pub mod origin;
pub mod pages;
pub mod password;
pub mod refresh;
pub mod registration;
pub mod registry;
pub mod reset_token;
pub mod return_path;
pub mod secret_token;
pub mod session;
pub mod sessions;
pub mod signing_key;
pub mod verifier;

use axum::extract::FromRef;
use axum::{Router, routing::get};
use issuer::Issuer;
use origin::PublicOrigin;
use registry::Registry;
use signing_key::SigningKey;
use sqlx::Connection;
use sqlx::migrate::{Migrate, Migration, Migrator};
use sqlx::postgres::{PgConnection, PgPool, PgPoolOptions};
use std::collections::HashSet;
use std::env;
use std::error::Error;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;
use time::OffsetDateTime;
use tokio::net::TcpListener;
use verifier::{KeySet, Verifier};

// The SQL files in Core/migrations, embedded at compile time. build.rs makes
// Cargo rebuild when one is added.
pub static MIGRATOR: Migrator = sqlx::migrate!();

/// What every handler can reach: built once at startup and shared. A handler
/// asks for the part it needs, `State<PgPool>` for instance, through `FromRef`.
#[derive(Clone)]
pub struct AppState {
    pub pool: PgPool,
    pub issuer: Arc<Issuer>,
    pub verifier: Arc<Verifier>,
    /// A password hash nothing matches, verified against when a login names
    /// an unknown email, so it takes as long as a wrong password. Made at
    /// startup with the same parameters as every real hash, so the timing
    /// still matches if they change.
    pub dummy_hash: Arc<str>,
    /// The features the platform has, read from the registry file at
    /// startup. See "The feature registry" in docs/core-architecture.md.
    pub registry: Arc<Registry>,
    /// The client for calls to other services, built once so its
    /// connections are pooled. See src/http.rs.
    pub http: reqwest::Client,
}

impl AppState {
    /// The state for `signing_key`, its issuer and verifier derived from it.
    pub async fn new(
        pool: PgPool,
        signing_key: SigningKey,
        access_token_lifetime: time::Duration,
        registry: Registry,
    ) -> Result<AppState, Box<dyn Error>> {
        let dummy_hash = password::hash(&secret_token::generate().0).await?;
        let http =
            http::client().map_err(|err| format!("could not build the HTTP client: {err}"))?;
        Ok(AppState {
            pool,
            verifier: Arc::new(Verifier::new(KeySet::of(&signing_key))),
            issuer: Arc::new(Issuer::new(signing_key, access_token_lifetime)),
            dummy_hash: dummy_hash.into(),
            registry: Arc::new(registry),
            http,
        })
    }
}

/// The access token lifetime when `CORE_ACCESS_TOKEN_LIFETIME` is unset: the
/// revocation window, see "The access token lasts thirty minutes" in
/// docs/core-architecture.md.
pub const DEFAULT_ACCESS_TOKEN_LIFETIME: time::Duration = time::Duration::minutes(30);

impl FromRef<AppState> for PgPool {
    fn from_ref(state: &AppState) -> PgPool {
        state.pool.clone()
    }
}

impl FromRef<AppState> for Arc<Verifier> {
    fn from_ref(state: &AppState) -> Arc<Verifier> {
        state.verifier.clone()
    }
}

pub async fn serve() -> Result<(), Box<dyn Error>> {
    let addr = listen_addr()?;
    let origin = public_origin()?;
    let signing_key = signing_key()?;
    println!("signing with key id {}", signing_key.key_id());
    let access_token_lifetime = access_token_lifetime()?;
    println!("access tokens last {access_token_lifetime}");
    let registry_path = registry::path(env::var_os("CORE_FEATURE_REGISTRY"));
    let registry = Registry::load(&registry_path)
        .map_err(|err| format!("could not load the feature registry: {err}"))?;
    println!(
        "registry {}: {}",
        registry_path.display(),
        registry.summary()
    );

    let pool = connect_database().await?;
    println!("connected to the database");

    let state = AppState::new(pool, signing_key, access_token_lifetime, registry).await?;
    let app = app(state, origin);

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

/// Every route the core serves, behind the Origin check. Public so the tests
/// can serve the real router against a test database.
pub fn app(state: AppState, origin: PublicOrigin) -> Router {
    let routes = Router::new()
        .route("/", get(root))
        .route("/assets/core.css", get(pages::stylesheet))
        .route("/nav.js", get(pages::nav_script))
        .merge(jwks::routes())
        .merge(nav::routes())
        .merge(registration::routes())
        .merge(login::routes())
        .merge(refresh::routes());
    origin::protect(routes, origin).with_state(state)
}

// help function for serve: the key from the environment, never one generated
// here. A key made at startup would work until the first restart and then
// silently log everyone out, see "The signing key" in docs/core-architecture.md.
fn signing_key() -> Result<SigningKey, Box<dyn Error>> {
    let raw = env::var("CORE_SIGNING_KEY").map_err(|err| {
        format!("CORE_SIGNING_KEY: {err}; generate one with `platform_core keygen >> .env`")
    })?;
    Ok(SigningKey::parse(&raw).map_err(|err| format!("CORE_SIGNING_KEY: {err}"))?)
}

// help function for serve: unset is the default, and anything set must parse,
// so a typo cannot quietly become thirty minutes.
fn access_token_lifetime() -> Result<time::Duration, Box<dyn Error>> {
    match env::var("CORE_ACCESS_TOKEN_LIFETIME") {
        Err(env::VarError::NotPresent) => Ok(DEFAULT_ACCESS_TOKEN_LIFETIME),
        Err(err) => Err(format!("CORE_ACCESS_TOKEN_LIFETIME: {err}").into()),
        Ok(raw) => Ok(
            duration::parse(&raw).map_err(|err| format!("CORE_ACCESS_TOKEN_LIFETIME: {err}"))?
        ),
    }
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

#[derive(askama::Template)]
#[template(path = "front.html")]
struct FrontPage {
    account_id: uuid::Uuid,
}

// The front page, a stub showing who you are until the feature cards (#31).
async fn root(session: session::Session) -> pages::Page<FrontPage> {
    pages::Page(FrontPage {
        account_id: session.account_id,
    })
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

// for the keygen command: a new signing key as the env file line that holds
// it, on stdout so it can be appended to .env, and its key id on stderr.
// Every environment runs this once for its own key, see "The signing key" in
// docs/core-architecture.md.
pub fn keygen() {
    let (key, seed) = signing_key::SigningKey::generate();
    println!("CORE_SIGNING_KEY={seed}");
    eprintln!("key id {}", key.key_id());
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

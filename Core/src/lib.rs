//! The core, as a library: the binary in main.rs parses the command and
//! calls into it, and the tests in tests/ import what they test.

pub mod accounts;
pub mod backup_status;
pub mod cards;
pub mod change_password;
pub mod claims;
pub mod cookies;
pub mod display;
pub mod duration;
pub mod http;
pub mod invite;
pub mod issuer;
pub mod jwks;
pub mod login;
pub mod logout;
pub mod nav;
pub mod origin;
pub mod pages;
pub mod password;
pub mod refresh;
pub mod registration;
pub mod registry;
pub mod relative;
pub mod reset;
pub mod reset_token;
pub mod return_path;
pub mod secret_token;
pub mod session;
pub mod sessions;
pub mod signing_key;
pub mod verifier;

use axum::extract::{FromRef, State};
use axum::{Router, routing::get};
use cards::Card;
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
use std::path::PathBuf;
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
    /// How long the front page waits for each feature's display data:
    /// `display::DISPLAY_DEADLINE`, and shorter in tests, which set it after
    /// `new`. See src/display.rs.
    pub display_deadline: Duration,
    /// The backup job's status file, from `CORE_BACKUP_STATUS_FILE`, read
    /// on every load of the front page; None when unset, and then nothing
    /// is read or shown. Set after `new`. See src/backup_status.rs.
    pub backup_status_file: Option<PathBuf>,
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
            display_deadline: display::DISPLAY_DEADLINE,
            backup_status_file: None,
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
    let backup_status_file = backup_status::setting(env::var_os(backup_status::SETTING))?;
    match &backup_status_file {
        Some(path) => println!("backup status from {}", path.display()),
        None => println!("no backup status, {} is unset", backup_status::SETTING),
    }
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

    let mut state = AppState::new(pool, signing_key, access_token_lifetime, registry).await?;
    state.backup_status_file = backup_status_file;
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
        .merge(jwks::routes())
        .merge(nav::routes())
        .merge(registration::routes())
        .merge(reset::routes())
        .merge(change_password::routes())
        .merge(login::routes())
        .merge(refresh::routes())
        .merge(logout::routes());
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
    cards: Vec<Card>,
    now: OffsetDateTime,
}

// The front page, composed here in one response: every feature with display
// data is asked for it with the user's token, all at once, and each becomes
// its card or a placeholder. A feature that is down or slow costs at most the
// display deadline, and all of them together about one, see src/display.rs.
async fn root(State(state): State<AppState>, session: session::Session) -> pages::Page<FrontPage> {
    let fetched = display::fetch_all(
        &state.http,
        &state.registry,
        &session.access_token,
        state.display_deadline,
    )
    .await;
    pages::Page(FrontPage {
        cards: fetched.into_iter().map(Card::from_fetched).collect(),
        now: OffsetDateTime::now_utc(),
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

// for the reset command: stores a reset token for the account with `email`,
// prints it once on stdout and its expiry on stderr, as invite does. An email
// with no account is an error: the owner runs this, so saying so plainly
// gives nothing away, see "No defence against user enumeration" in
// docs/core-architecture.md.
pub async fn reset(email: &str) -> Result<(), Box<dyn Error>> {
    let pool = connect_database().await?;
    let now = OffsetDateTime::now_utc();

    let token = reset_token::create_for_email(&pool, email, now)
        .await
        .map_err(|err| format!("could not store the reset token: {err}"))?
        .ok_or("no account with that email")?;

    println!("{token}");
    eprintln!(
        "expires {}",
        (now + reset_token::LIFETIME)
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

#[cfg(test)]
mod tests {
    use super::*;
    use askama::Template;
    use cards::{ReviewCard, ReviewDeck};

    // 2027-01-15 08:00:00 UTC.
    fn now() -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_800_000_000).unwrap()
    }

    fn render(card: ReviewCard) -> String {
        FrontPage {
            cards: vec![Card::Review(card)],
            now: now(),
        }
        .render()
        .unwrap()
    }

    fn deck(name: &str, due_now: u32) -> ReviewDeck {
        ReviewDeck {
            name: name.into(),
            due_now,
        }
    }

    #[test]
    fn the_review_card_renders_what_is_due() {
        let body = render(ReviewCard {
            due_now: 12,
            due_today: 18,
            next_due_at: Some(now() + time::Duration::hours(1)),
            decks: vec![
                deck("Networks", 1),
                deck("Real Time Systems", 6),
                deck("<b>Concurrent</b> Algorithms", 4),
                deck("Compilers", 1),
            ],
        });

        assert!(body.contains(r#"<a href="/review">Review</a>"#), "{body}");
        assert!(
            body.contains(r#"<span class="count">12</span> due now"#),
            "{body}"
        );
        assert!(body.contains("6 more later today"), "{body}");
        // Up to three decks with the most due, ties by name: Compilers
        // before Networks, which is left out.
        let real_time = body.find("Real Time Systems").unwrap();
        let concurrent = body.find("Concurrent").unwrap();
        let compilers = body.find("Compilers").unwrap();
        assert!(real_time < concurrent && concurrent < compilers, "{body}");
        assert!(!body.contains("Networks"), "{body}");
        // Every value escaped.
        assert!(!body.contains("<b>"), "{body}");
        assert!(
            body.contains("&#60;b&#62;Concurrent&#60;/b&#62; Algorithms"),
            "{body}"
        );
        assert!(
            body.contains(r#"<a class="action primary" href="/review">Start review</a>"#),
            "{body}"
        );
        // Something due is not nothing due.
        assert!(!body.contains("Nothing due"), "{body}");
        assert!(!body.contains("Next in"), "{body}");

        // Nothing later today says nothing about it.
        let body = render(ReviewCard {
            due_now: 3,
            due_today: 3,
            next_due_at: None,
            decks: vec![deck("Rust", 3)],
        });
        assert!(
            body.contains(r#"<span class="count">3</span> due now"#),
            "{body}"
        );
        assert!(!body.contains("later today"), "{body}");
    }

    #[test]
    fn the_review_card_with_nothing_due_says_when_the_next_comes() {
        let body = render(ReviewCard {
            due_now: 0,
            due_today: 2,
            next_due_at: Some(now() + time::Duration::hours(3)),
            decks: vec![],
        });

        assert!(body.contains("Nothing due"), "{body}");
        assert!(body.contains("Next in 3 hours"), "{body}");
        assert!(!body.contains("due now"), "{body}");
        assert!(!body.contains("Start review"), "{body}");
        assert!(!body.contains("No cards yet"), "{body}");

        // With no card scheduled at all, there are no cards yet.
        let body = render(ReviewCard {
            due_now: 0,
            due_today: 0,
            next_due_at: None,
            decks: vec![],
        });
        assert!(body.contains("Nothing due"), "{body}");
        assert!(body.contains("No cards yet"), "{body}");
        assert!(
            body.contains(r#"<a class="action" href="/review/cards">Add cards</a>"#),
            "{body}"
        );
        assert!(!body.contains("Next in"), "{body}");
    }
}

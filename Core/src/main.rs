use axum::{Router, routing::get};
use sqlx::postgres::{PgPool, PgPoolOptions};
use std::env;
use std::error::Error;
use std::net::SocketAddr;
use std::time::Duration;
use tokio::net::TcpListener;

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    match dotenvy::dotenv() {
        Ok(_) => {}
        Err(err) if err.not_found() => {}
        Err(err) => return Err(err.into()),
    }

    let addr = listen_addr()?;

    let _pool = connect_database().await?;
    println!("connected to the database");

    let app = app();

    let listener = TcpListener::bind(addr)
        .await
        .map_err(|err| format!("could not listen on {addr}: {err}"))?;
    println!("listening on {}", listener.local_addr()?);

    axum::serve(listener, app).await?;

    Ok(())
}

fn listen_addr() -> Result<SocketAddr, Box<dyn Error>> {
    let raw = env::var("CORE_LISTEN_ADDR").map_err(|err| format!("CORE_LISTEN_ADDR: {err}"))?;
    let addr = raw
        .parse::<SocketAddr>()
        .map_err(|err| format!("CORE_LISTEN_ADDR='{raw}' is not an IP and port: {err}"))?;
    Ok(addr)
}

fn app() -> Router {
    Router::new().route("/", get(root))
}

async fn root() -> &'static str {
    "platform core root endpoint"
}

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

use axum::{Router, routing::get};
use std::env;
use std::error::Error;
use std::net::SocketAddr;
use tokio::net::TcpListener;

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    match dotenvy::dotenv() {
        Ok(_) => {}
        Err(err) if err.not_found() => {}
        Err(err) => return Err(err.into()),
    }

    let addr = listen_addr()?;

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

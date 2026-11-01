use std::env;
use std::error::Error;

const USAGE: &str = "usage: platform_core [migrate]";

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    match dotenvy::dotenv() {
        Ok(_) => {}
        Err(err) if err.not_found() => {}
        Err(err) => return Err(err.into()),
    }

    let mut args = env::args().skip(1);
    let command = args.next();

    if let Some(extra) = args.next() {
        return Err(format!("unexpected argument '{extra}'; {USAGE}").into());
    }

    match command.as_deref() {
        None => platform_core::serve().await,
        Some("migrate") => platform_core::migrate().await,
        Some(other) => Err(format!("unknown command '{other}'; {USAGE}").into()),
    }
}

use platform_core::{duration, invite};
use std::env;
use std::error::Error;

const USAGE: &str =
    "usage: platform_core [migrate | invite [--expires-in <n><s|m|h|d>] | reset <email> | keygen]";

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    match dotenvy::dotenv() {
        Ok(_) => {}
        Err(err) if err.not_found() => {}
        Err(err) => return Err(err.into()),
    }

    let args: Vec<String> = env::args().skip(1).collect();
    let args: Vec<&str> = args.iter().map(String::as_str).collect();

    match args.as_slice() {
        [] => platform_core::serve().await,
        ["migrate"] => platform_core::migrate().await,
        ["invite"] => platform_core::invite(invite::DEFAULT_EXPIRY).await,
        ["invite", "--expires-in", duration] => {
            let expires_in = duration::parse(duration).map_err(|err| format!("{err}; {USAGE}"))?;
            platform_core::invite(expires_in).await
        }
        ["reset", email] => platform_core::reset(email).await,
        ["reset"] => Err(format!("'reset' needs the account's email; {USAGE}").into()),
        ["keygen"] => {
            platform_core::keygen();
            Ok(())
        }
        [command @ ("migrate" | "invite" | "reset" | "keygen"), ..] => {
            Err(format!("unexpected arguments after '{command}'; {USAGE}").into())
        }
        [other, ..] => Err(format!("unknown command '{other}'; {USAGE}").into()),
    }
}

//! The core's HTTP client, for the calls it makes to other services at their
//! upstream address. One is built at startup and shared through `AppState`,
//! so every call reuses its connection pool. See "reqwest" in "Libraries the
//! core is built on" in docs/core-architecture.md.

/// The client every outgoing call uses. It never follows a redirect, so a
/// user's token cannot be carried to an address the registry did not name,
/// and it ignores the system's proxy settings, since services call each
/// other directly (see "Routing" in docs/core-architecture.md). It has no
/// cookie store and no TLS: upstream calls are plain HTTP.
pub fn client() -> reqwest::Result<reqwest::Client> {
    reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .no_proxy()
        .build()
}

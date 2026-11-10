//! The two cookies a session lives in. The access token on the whole domain,
//! so it reaches the core and every feature; the refresh token only on
//! `/auth`, so it never reaches a feature at all. Both `HttpOnly`, `Secure`
//! and `SameSite=Lax`, and named with prefixes that make the browser enforce
//! those attributes: `__Host-` requires `Secure`, `Path=/` and no `Domain`,
//! and `__Secure-` requires `Secure`. See "The session lives in two cookies"
//! in docs/core-architecture.md.

use axum_extra::extract::cookie::{Cookie, SameSite};
use time::Duration;

/// Read by the core and by every feature.
pub const ACCESS: &str = "__Host-access";

pub const REFRESH: &str = "__Secure-refresh";

/// The access token, living as long as the token does.
pub fn access(token: String, lifetime: Duration) -> Cookie<'static> {
    build(ACCESS, token, "/", lifetime)
}

/// The refresh token, living until the token expires.
pub fn refresh(token: String, lifetime: Duration) -> Cookie<'static> {
    build(REFRESH, token, "/auth", lifetime)
}

fn build(
    name: &'static str,
    value: String,
    path: &'static str,
    lifetime: Duration,
) -> Cookie<'static> {
    Cookie::build((name, value))
        .http_only(true)
        .secure(true)
        .same_site(SameSite::Lax)
        .path(path)
        .max_age(lifetime)
        .build()
}

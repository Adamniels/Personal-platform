//! The `Origin` check: every request that can change state must come from the
//! platform's own origin. Together with `SameSite=Lax` and never changing
//! state on a GET, it is the whole CSRF defence, see "The session" in
//! docs/core-architecture.md.

use axum::Router;
use axum::extract::Request;
use axum::http::{Method, StatusCode, Uri, header};
use axum::middleware::{self, Next};
use axum::response::{IntoResponse, Response};

/// The platform's own origin, such as `https://platform.test`, from
/// `CORE_PUBLIC_ORIGIN`. Kept exactly as the browser sends it, since the
/// check compares bytes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PublicOrigin(String);

impl PublicOrigin {
    /// Accepts only the form a browser sends: scheme and host, lowercase,
    /// with a port only when it is not the scheme's default, and nothing
    /// after it. Anything else would never match and silently refuse every
    /// form, so it fails at startup instead.
    pub fn parse(text: &str) -> Result<PublicOrigin, String> {
        let refused = || {
            format!(
                "'{text}' is not an origin like https://platform.test: scheme and host, lowercase, no path, no default port"
            )
        };
        let uri: Uri = text.parse().map_err(|_| refused())?;
        let (Some(scheme), Some(authority)) = (uri.scheme_str(), uri.authority()) else {
            return Err(refused());
        };
        let default_port = match scheme {
            "https" => 443,
            "http" => 80,
            _ => return Err(refused()),
        };
        if authority.as_str().contains('@') || authority.port_u16() == Some(default_port) {
            return Err(refused());
        }
        // Rebuilt from its parts, the form a browser sends has to come back
        // byte for byte, which rules out a path, a query, a trailing colon and
        // any uppercase.
        let canonical = match authority.port_u16() {
            Some(port) => format!("{scheme}://{}:{port}", authority.host()),
            None => format!("{scheme}://{}", authority.host()),
        };
        if canonical != text || canonical != canonical.to_ascii_lowercase() {
            return Err(refused());
        }
        Ok(PublicOrigin(canonical))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// Wraps every route of `router` in the check, so a route added later is
/// covered without anyone remembering to. It runs before the handler, and so
/// before a form body is read.
pub fn protect<S: Clone + Send + Sync + 'static>(
    router: Router<S>,
    origin: PublicOrigin,
) -> Router<S> {
    router.layer(middleware::from_fn(move |request: Request, next: Next| {
        let origin = origin.clone();
        async move { check(&origin, request, next).await }
    }))
}

async fn check(origin: &PublicOrigin, request: Request, next: Next) -> Response {
    if matches!(
        *request.method(),
        Method::GET | Method::HEAD | Method::OPTIONS
    ) {
        return next.run(request).await;
    }

    // Exactly one Origin, equal byte for byte. No normalising and no prefix
    // match, which is how https://platform.test.evil.test would get through.
    let mut origins = request.headers().get_all(header::ORIGIN).iter();
    match (origins.next(), origins.next()) {
        (Some(sent), None) if sent.as_bytes() == origin.as_str().as_bytes() => {
            next.run(request).await
        }
        _ => (
            StatusCode::FORBIDDEN,
            "Forbidden: this request did not come from the platform's own origin.\n",
        )
            .into_response(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_form_a_browser_sends_parses() {
        for text in [
            "https://platform.test",
            "https://platform.test:8443",
            "http://localhost:8080",
        ] {
            assert_eq!(PublicOrigin::parse(text).map(|o| o.0), Ok(text.to_owned()));
        }
    }

    #[test]
    fn anything_a_browser_would_not_send_is_refused() {
        for text in [
            "",
            "platform.test",
            "https://",
            "https://platform.test/",
            "https://platform.test/path",
            "https://platform.test?x",
            "https://Platform.test",
            "HTTPS://platform.test",
            "https://platform.test:443",
            "http://platform.test:80",
            "https://platform.test:",
            "https://user@platform.test",
            "ftp://platform.test",
            " https://platform.test",
        ] {
            assert!(PublicOrigin::parse(text).is_err(), "accepted {text:?}");
        }
    }
}

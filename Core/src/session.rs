//! Requiring a session on a core page. A handler that takes `Session` only
//! runs for a request carrying a valid access token, and gets the account id
//! and session family from it; any other request is sent to the deciding
//! endpoint with the way back, exactly as a feature does, never straight to
//! login. A handler showing anyone's data needs the id, so it cannot forget
//! to ask, and one calling a feature on the user's behalf has the token to
//! forward. `ApiSession` is the same check for an endpoint a script fetches,
//! which answers 401 instead, since a redirect is no use to a fetch. The
//! rows behind a session are in `sessions`.

use crate::claims::Claims;
use crate::cookies;
use crate::refresh;
use crate::return_path::ReturnPath;
use crate::verifier::Verifier;
use axum::extract::{FromRef, FromRequestParts, OriginalUri};
use axum::http::StatusCode;
use axum::http::request::Parts;
use axum::response::Redirect;
use axum_extra::extract::cookie::CookieJar;
use std::sync::Arc;
use time::OffsetDateTime;
use uuid::Uuid;

/// Who is asking, from a verified access token. Not `Debug`, so the token
/// cannot end up in a log by formatting the session.
pub struct Session {
    pub account_id: Uuid,
    pub family_id: Uuid,
    /// The access token exactly as the request carried it, verified, for
    /// the front page to forward to features in an `Authorization` header.
    pub access_token: String,
}

impl<S> FromRequestParts<S> for Session
where
    Arc<Verifier>: FromRef<S>,
    S: Send + Sync,
{
    type Rejection = Redirect;

    async fn from_request_parts(parts: &mut Parts, state: &S) -> Result<Session, Redirect> {
        match verified_claims(parts, state) {
            Some((claims, access_token)) => Ok(Session {
                account_id: claims.account_id,
                family_id: claims.family_id,
                access_token,
            }),
            // Missing, expired and failing tokens alike: the deciding endpoint
            // works out whether this is a refresh or a login.
            None => {
                // The URI as the browser sent it: under a nested router,
                // parts.uri has the prefix stripped.
                let uri = parts
                    .extensions
                    .get::<OriginalUri>()
                    .map_or(&parts.uri, |original| &original.0);
                let here = uri.path_and_query().map_or("/", |path| path.as_str());
                Err(Redirect::to(
                    &ReturnPath::parse(here).appended_to(refresh::PATH),
                ))
            }
        }
    }
}

/// Who is asking, for an endpoint the nav script or a feature's script
/// fetches: verified exactly as `Session` is, but any request without a valid
/// access token gets 401 with an empty body, never a redirect. Nothing here
/// refreshes the session, see "What we are not doing" in
/// docs/core-architecture.md.
pub struct ApiSession {
    pub account_id: Uuid,
    pub family_id: Uuid,
}

impl<S> FromRequestParts<S> for ApiSession
where
    Arc<Verifier>: FromRef<S>,
    S: Send + Sync,
{
    type Rejection = StatusCode;

    async fn from_request_parts(parts: &mut Parts, state: &S) -> Result<ApiSession, StatusCode> {
        let (claims, _) = verified_claims(parts, state).ok_or(StatusCode::UNAUTHORIZED)?;
        Ok(ApiSession {
            account_id: claims.account_id,
            family_id: claims.family_id,
        })
    }
}

// The claims of the request's access cookie and the token itself, if it has
// one that verifies now. The one check behind both extractors, so they cannot
// drift apart.
fn verified_claims<S>(parts: &Parts, state: &S) -> Option<(Claims, String)>
where
    Arc<Verifier>: FromRef<S>,
{
    let verifier = Arc::<Verifier>::from_ref(state);
    let jar = CookieJar::from_headers(&parts.headers);
    let token = jar.get(cookies::ACCESS)?.value();
    let claims = verifier.verify(token, OffsetDateTime::now_utc()).ok()?;
    Some((claims, token.to_owned()))
}

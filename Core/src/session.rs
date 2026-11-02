//! Requiring a session on a core page. A handler that takes `Session` only
//! runs for a request carrying a valid access token, and gets the account id
//! and session family from it; any other request is sent to the deciding
//! endpoint with the way back, exactly as a feature does, never straight to
//! login. A handler showing anyone's data needs the id, so it cannot forget
//! to ask. The rows behind a session are in `sessions`.

use crate::cookies;
use crate::refresh;
use crate::return_path::ReturnPath;
use crate::verifier::Verifier;
use axum::extract::{FromRef, FromRequestParts};
use axum::http::request::Parts;
use axum::response::Redirect;
use axum_extra::extract::cookie::CookieJar;
use std::sync::Arc;
use time::OffsetDateTime;
use uuid::Uuid;

/// Who is asking, from a verified access token.
pub struct Session {
    pub account_id: Uuid,
    pub family_id: Uuid,
}

impl<S> FromRequestParts<S> for Session
where
    Arc<Verifier>: FromRef<S>,
    S: Send + Sync,
{
    type Rejection = Redirect;

    async fn from_request_parts(parts: &mut Parts, state: &S) -> Result<Session, Redirect> {
        let verifier = Arc::<Verifier>::from_ref(state);
        let jar = CookieJar::from_headers(&parts.headers);
        let claims = jar.get(cookies::ACCESS).and_then(|cookie| {
            verifier
                .verify(cookie.value(), OffsetDateTime::now_utc())
                .ok()
        });

        match claims {
            Some(claims) => Ok(Session {
                account_id: claims.account_id,
                family_id: claims.family_id,
            }),
            // Missing, expired and failing tokens alike: the deciding endpoint
            // works out whether this is a refresh or a login.
            None => {
                let here = parts.uri.path_and_query().map_or("/", |path| path.as_str());
                Err(Redirect::to(
                    &ReturnPath::parse(here).appended_to(refresh::PATH),
                ))
            }
        }
    }
}

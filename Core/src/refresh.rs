//! The one place that decides between refreshing and logging in. Everything
//! without a valid access token is sent here, a core page or a feature alike,
//! whatever the reason, so the decision lives in one place: `decide`, on the
//! stored token and the time. See "The refresh endpoint is the one place that
//! decides between refreshing and logging in" in docs/core-architecture.md.

use crate::AppState;
use crate::return_path::ReturnPath;
use crate::sessions;
use axum::Router;
use axum::extract::Query;
use axum::response::Redirect;
use axum::routing::get;
use serde::Deserialize;
use time::OffsetDateTime;

/// Where a request without a valid access token is sent.
pub const PATH: &str = "/auth/refresh";

/// What a presented refresh token comes to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    /// Mark it used and issue the next token of its family.
    Rotate,
    /// Send the browser to the login page.
    LogIn,
}

/// Decides on `row`, the stored token a refresh presented, or `None` when
/// nothing matched it, at `now`. One check per line, in order, each ending
/// the decision, so the grace window and reuse detection arrive as new
/// outcomes of the used check without touching the others.
pub fn decide(row: Option<&sessions::Row>, now: OffsetDateTime) -> Decision {
    let Some(row) = row else {
        return Decision::LogIn;
    };
    // A disabled account ends its sessions here, at their next refresh,
    // rather than when the family's cap runs out.
    if !row.account_active {
        return Decision::LogIn;
    }
    if row.revoked_at.is_some() {
        return Decision::LogIn;
    }
    // Until the grace window and reuse detection: two tabs refreshing at once
    // can send one of them to login, which is known and temporary.
    if row.used_at.is_some() {
        return Decision::LogIn;
    }
    if now >= row.expires_at || now >= row.family_expires_at {
        return Decision::LogIn;
    }
    Decision::Rotate
}

pub fn routes() -> Router<AppState> {
    Router::new().route(PATH, get(show))
}

#[derive(Deserialize)]
struct DecideQuery {
    #[serde(default)]
    return_to: ReturnPath,
}

async fn show(Query(query): Query<DecideQuery>) -> Redirect {
    Redirect::to(&query.return_to.appended_to("/login"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use time::Duration;
    use uuid::Uuid;

    fn at(seconds: i64) -> OffsetDateTime {
        OffsetDateTime::from_unix_timestamp(1_800_000_000 + seconds).unwrap()
    }

    // Issued at 0, expiring at 1000 within a family capped at 2000: unused,
    // unrevoked, its account active.
    fn live() -> sessions::Row {
        sessions::Row {
            id: Uuid::now_v7(),
            account_id: Uuid::now_v7(),
            family_id: Uuid::now_v7(),
            expires_at: at(1000),
            family_expires_at: at(2000),
            used_at: None,
            revoked_at: None,
            account_active: true,
        }
    }

    #[test]
    fn a_live_token_rotates() {
        assert_eq!(decide(Some(&live()), at(10)), Decision::Rotate);
    }

    #[test]
    fn an_unknown_revoked_or_used_token_goes_to_login() {
        let revoked = sessions::Row {
            revoked_at: Some(at(5)),
            ..live()
        };
        let used = sessions::Row {
            used_at: Some(at(5)),
            ..live()
        };
        let both = sessions::Row {
            used_at: Some(at(5)),
            revoked_at: Some(at(6)),
            ..live()
        };
        assert_eq!(decide(None, at(10)), Decision::LogIn, "unknown");
        assert_eq!(decide(Some(&revoked), at(10)), Decision::LogIn, "revoked");
        assert_eq!(decide(Some(&used), at(10)), Decision::LogIn, "used");
        assert_eq!(decide(Some(&both), at(10)), Decision::LogIn, "both");
    }

    // Disabling an account ends its sessions at their next refresh, not when
    // the family's cap runs out.
    #[test]
    fn an_inactive_account_goes_to_login() {
        let disabled = sessions::Row {
            account_active: false,
            ..live()
        };
        assert_eq!(decide(Some(&disabled), at(10)), Decision::LogIn);
    }

    // expires_at is the first instant a token no longer works.
    #[test]
    fn the_fourteen_day_expiry_is_exact() {
        let row = live();
        let second = Duration::seconds(1);
        assert_eq!(
            decide(Some(&row), row.expires_at - second),
            Decision::Rotate
        );
        assert_eq!(decide(Some(&row), row.expires_at), Decision::LogIn);
        assert_eq!(decide(Some(&row), row.expires_at + second), Decision::LogIn);
    }

    // The cap decides even when the token's own expiry would still allow it,
    // which a row never holds, since the table refuses expires_at after the
    // cap; deciding on both keeps the check from resting on that alone.
    #[test]
    fn the_thirty_day_cap_is_exact() {
        let row = sessions::Row {
            expires_at: at(3000),
            ..live()
        };
        let second = Duration::seconds(1);
        assert_eq!(
            decide(Some(&row), row.family_expires_at - second),
            Decision::Rotate
        );
        assert_eq!(decide(Some(&row), row.family_expires_at), Decision::LogIn);
        assert_eq!(
            decide(Some(&row), row.family_expires_at + second),
            Decision::LogIn
        );
    }
}

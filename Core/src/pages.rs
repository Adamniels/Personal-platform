//! The core's own HTML: askama templates in templates/, checked at compile
//! time and escaped by default, through one base layout, and the one asset
//! the core serves, its stylesheet. The nav bar and the design files every
//! page links are served by the proxy from design/. No build step, see
//! "The core's frontend" in docs/core-architecture.md.

use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use axum::response::{Html, IntoResponse, Response};
use sha2::{Digest, Sha256};
use std::sync::LazyLock;

const STYLESHEET: &str = include_str!("../assets/core.css");

static STYLESHEET_ETAG: LazyLock<HeaderValue> = LazyLock::new(|| etag_of(STYLESHEET));

// A strong validator from an asset's contents, so a browser checking back
// gets 304 until the file changes and the new one at once after.
fn etag_of(contents: &str) -> HeaderValue {
    let digest = Sha256::digest(contents.as_bytes());
    let hex: String = digest[..16].iter().map(|b| format!("{b:02x}")).collect();
    HeaderValue::from_str(&format!("\"{hex}\"")).expect("hex is a valid header value")
}

/// A template as a response.
pub struct Page<T>(pub T);

impl<T: askama::Template> IntoResponse for Page<T> {
    fn into_response(self) -> Response {
        match self.0.render() {
            Ok(html) => Html(html).into_response(),
            Err(err) => {
                eprintln!("could not render a page: {err}");
                (StatusCode::INTERNAL_SERVER_ERROR, "Internal server error\n").into_response()
            }
        }
    }
}

/// Why a form was refused, and the field it is about, named by the field's
/// `name`. The template marks that field `aria-invalid` and shows the message
/// under it, starting with the word "Error", as the text field rule in
/// design/DESIGN.md says.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FormError<'a> {
    pub field: &'static str,
    pub message: &'a str,
}

impl<'a> FormError<'a> {
    pub fn on(field: &'static str, message: &'a str) -> Self {
        FormError { field, message }
    }
}

/// The message for `field`, when a form's error is about it. Templates call
/// it once per field, for the attributes and for the message.
pub fn error_on<'a>(error: &Option<FormError<'a>>, field: &str) -> Option<&'a str> {
    error
        .filter(|error| error.field == field)
        .map(|error| error.message)
}

/// `GET /assets/core.css`.
pub async fn stylesheet(headers: HeaderMap) -> Response {
    asset(
        &headers,
        STYLESHEET,
        "text/css; charset=utf-8",
        &STYLESHEET_ETAG,
    )
}

// An embedded asset, revalidated on every use: `no-cache` lets the browser
// keep it but makes it ask, so a change reaches every page on its next load.
fn asset(
    headers: &HeaderMap,
    contents: &'static str,
    content_type: &'static str,
    etag: &HeaderValue,
) -> Response {
    let validators = [
        (header::ETAG, etag.clone()),
        (header::CACHE_CONTROL, HeaderValue::from_static("no-cache")),
    ];

    if matches_etag(headers, etag) {
        return (StatusCode::NOT_MODIFIED, validators).into_response();
    }
    (
        validators,
        [(header::CONTENT_TYPE, HeaderValue::from_static(content_type))],
        contents,
    )
        .into_response()
}

// If-None-Match holds a list of tags, or `*`; a weak tag still names the
// same contents for this purpose.
fn matches_etag(headers: &HeaderMap, etag: &HeaderValue) -> bool {
    let etag = etag.to_str().expect("the etag is ASCII");
    headers
        .get_all(header::IF_NONE_MATCH)
        .iter()
        .filter_map(|value| value.to_str().ok())
        .flat_map(|value| value.split(','))
        .map(str::trim)
        .any(|tag| tag == "*" || tag.trim_start_matches("W/") == etag)
}

#[cfg(test)]
mod tests {
    use super::*;
    use askama::Template;

    #[derive(Template)]
    #[template(
        ext = "html",
        source = "{% extends \"base.html\" %}{% block title %}Probe{% endblock %}{% block content %}<p>{{ value }}</p>{% endblock %}"
    )]
    struct Probe<'a> {
        value: &'a str,
    }

    async fn body(response: Response) -> String {
        let bytes = axum::body::to_bytes(response.into_body(), usize::MAX)
            .await
            .unwrap();
        String::from_utf8(bytes.to_vec()).unwrap()
    }

    #[tokio::test]
    async fn a_page_renders_through_the_base_layout_with_the_stylesheet() {
        let response = Page(Probe { value: "hello" }).into_response();
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            response.headers()[header::CONTENT_TYPE],
            "text/html; charset=utf-8"
        );

        let html = body(response).await;
        assert!(html.starts_with("<!doctype html>"), "{html}");
        assert!(html.contains("<title>Probe · Platform</title>"), "{html}");
        assert!(
            html.contains(r#"<link rel="stylesheet" href="/assets/core.css">"#),
            "{html}"
        );
        // Every core page loads the design files and the nav script the way
        // a feature does, the theme before the body; only a page that fills
        // the nav block shows the bar.
        for line in [
            r#"<link rel="stylesheet" href="/design/tokens.css">"#,
            r#"<link rel="stylesheet" href="/design/fonts.css">"#,
            r#"<link rel="stylesheet" href="/design/page.css">"#,
            r#"<script src="/design/theme.js"></script>"#,
            r#"<script src="/design/nav.js" defer></script>"#,
        ] {
            assert!(html.contains(line), "{line} missing from {html}");
        }
        assert!(!html.contains(r#"src="/nav.js""#), "{html}");
        // The shared values first and the core's own stylesheet last, so it
        // builds on them; the content sits in the page container.
        let order: Vec<usize> = [
            "/design/tokens.css",
            "/design/fonts.css",
            "/design/page.css",
            "/assets/core.css",
        ]
        .iter()
        .map(|href| html.find(href).expect(href))
        .collect();
        assert!(order.is_sorted(), "{html}");
        assert!(html.contains(r#"<main class="page">"#), "{html}");
        assert!(!html.contains("<platform-nav"), "{html}");
        assert!(html.contains("<p>hello</p>"), "{html}");
    }

    // An email echoed back into a form must not become markup.
    #[tokio::test]
    async fn values_are_escaped() {
        let html = body(
            Page(Probe {
                value: r#"<script>"x"&</script>"#,
            })
            .into_response(),
        )
        .await;
        assert!(!html.contains("<script>"), "{html}");
        assert!(
            html.contains("<p>&#60;script&#62;&#34;x&#34;&#38;&#60;/script&#62;</p>"),
            "{html}"
        );
    }

    #[tokio::test]
    async fn the_stylesheet_is_served_with_a_validator() {
        let response = stylesheet(HeaderMap::new()).await;
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            response.headers()[header::CONTENT_TYPE],
            "text/css; charset=utf-8"
        );
        assert_eq!(response.headers()[header::CACHE_CONTROL], "no-cache");
        let etag = response.headers()[header::ETAG].clone();
        assert!(etag.to_str().unwrap().starts_with('"'));
        assert!(!body(response).await.is_empty());

        let mut headers = HeaderMap::new();
        headers.insert(header::IF_NONE_MATCH, etag.clone());
        let unchanged = stylesheet(headers).await;
        assert_eq!(unchanged.status(), StatusCode::NOT_MODIFIED);
        assert_eq!(unchanged.headers()[header::ETAG], etag);
        assert!(body(unchanged).await.is_empty());

        let mut headers = HeaderMap::new();
        headers.insert(header::IF_NONE_MATCH, "\"stale\"".parse().unwrap());
        assert_eq!(stylesheet(headers).await.status(), StatusCode::OK);
    }

    #[test]
    fn an_error_belongs_to_its_field_only() {
        let error = Some(FormError::on("email", "Enter a valid email address."));
        assert_eq!(
            error_on(&error, "email"),
            Some("Enter a valid email address.")
        );
        assert_eq!(error_on(&error, "password"), None);
        assert_eq!(error_on(&None, "email"), None);
    }

    // Every colour comes from /design/tokens.css, so the served stylesheet
    // writes none of its own: no hex value, no rgb() or hsl(), in any of
    // their spellings. Comments are left out, so prose may name a colour.
    #[tokio::test]
    async fn test_the_core_stylesheet_holds_no_raw_colour() {
        let css = body(stylesheet(HeaderMap::new()).await).await;
        let mut code = String::new();
        let mut rest = css.as_str();
        while let Some(start) = rest.find("/*") {
            code.push_str(&rest[..start]);
            let end = rest[start..].find("*/").expect("a comment is closed");
            rest = &rest[start + end + 2..];
        }
        code.push_str(rest);
        let code = code.to_lowercase();

        for function in ["rgb(", "rgba(", "hsl(", "hsla("] {
            assert!(!code.contains(function), "{function} in {code}");
        }
        let hex = code.match_indices('#').find(|(at, _)| {
            let digits = code[at + 1..]
                .chars()
                .take_while(char::is_ascii_hexdigit)
                .count();
            matches!(digits, 3 | 4 | 6 | 8)
        });
        assert!(hex.is_none(), "a hex colour at {hex:?} in {code}");
        assert!(code.contains("var(--"), "{code}");
    }
}

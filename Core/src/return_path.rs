//! Where to send someone after login or refresh, carried as `return_to`. Only
//! ever a path on this origin; anything else becomes the front page, since
//! otherwise the login page is an open redirect. See "A return path is only
//! ever a relative path" in docs/core-architecture.md.

use serde::{Deserialize, Deserializer};

/// The query parameter a return path travels in, here and in every feature.
pub const PARAMETER: &str = "return_to";

const MAX_BYTES: usize = 2048;

/// A return path that has been checked. The only way to make one is
/// `ReturnPath::parse`, so holding one means it is safe to redirect to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReturnPath(String);

impl ReturnPath {
    /// `text`, already decoded from the query or form it came in, if it is a
    /// path on this origin, and the front page otherwise. Refused: anything not
    /// starting with a single `/`, and a backslash, control character or
    /// whitespace anywhere, since browsers read `\` as `/` and drop tabs and
    /// newlines, which turns `/\evil` and `/<tab>/evil` into `//evil`.
    pub fn parse(text: &str) -> ReturnPath {
        let safe = text.len() <= MAX_BYTES
            && text.starts_with('/')
            && !matches!(text.as_bytes().get(1), Some(b'/' | b'\\'))
            && !text
                .chars()
                .any(|c| c == '\\' || c.is_control() || c.is_whitespace());
        if safe {
            ReturnPath(text.to_owned())
        } else {
            ReturnPath::default()
        }
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }

    /// The path as a `Location` header takes it. A header value is ASCII, so
    /// each byte above it is percent encoded, which is how a browser sends it
    /// anyway; everything else that could not appear was refused by `parse`.
    pub fn location(&self) -> String {
        let mut location = String::with_capacity(self.0.len());
        for byte in self.0.bytes() {
            if byte.is_ascii() {
                location.push(byte as char);
            } else {
                location.push_str(&format!("%{byte:02X}"));
            }
        }
        location
    }

    /// `path?return_to=<this, encoded>`, for a redirect that carries it on.
    pub fn appended_to(&self, path: &str) -> String {
        let query = form_urlencoded::Serializer::new(String::new())
            .append_pair(PARAMETER, &self.0)
            .finish();
        format!("{path}?{query}")
    }
}

impl Default for ReturnPath {
    /// The front page.
    fn default() -> ReturnPath {
        ReturnPath("/".to_owned())
    }
}

/// Never fails: what does not pass becomes the front page, as `parse` says.
impl<'de> Deserialize<'de> for ReturnPath {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<ReturnPath, D::Error> {
        Ok(ReturnPath::parse(&String::deserialize(deserializer)?))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_on_this_origin_are_kept() {
        for path in ["/", "/x", "/canary/a?b=1", "/%2F/x", "/a/b#c", "/é"] {
            assert_eq!(ReturnPath::parse(path).as_str(), path);
        }
        let longest = format!("/{}", "a".repeat(MAX_BYTES - 1));
        assert_eq!(ReturnPath::parse(&longest).as_str(), longest);
    }

    #[test]
    fn anything_else_becomes_the_front_page() {
        let too_long = format!("/{}", "a".repeat(MAX_BYTES));
        for text in [
            "",
            "x",
            "//evil",
            "//evil/x",
            "/\\evil",
            "\\\\evil",
            "\\/evil",
            "https://evil",
            "javascript:alert(1)",
            "%2F%2Fevil",
            "%252F%252Fevil",
            "/\t/evil",
            "/\n/evil",
            "/\r/evil",
            "/\u{0}/evil",
            "/ evil",
            "/x evil",
            "/\u{a0}evil",
            "/x\\y",
            too_long.as_str(),
        ] {
            assert_eq!(ReturnPath::parse(text).as_str(), "/", "kept {text:?}");
        }
    }

    // How each arrives in a query string, decoded as the extractors decode it:
    // once, and checked only after that.
    #[test]
    fn encoded_variants_are_checked_after_decoding() {
        for (query, expected) in [
            ("return_to=%2Fx", "/x"),
            ("return_to=%2F%2Fevil", "/"),
            ("return_to=%252F%252Fevil", "/"),
            ("return_to=%2F%5Cevil", "/"),
            ("return_to=%5C%5Cevil", "/"),
            ("return_to=https%3A%2F%2Fevil", "/"),
            ("return_to=%2F%09%2Fevil", "/"),
            ("return_to=%2F%0A%2Fevil", "/"),
            ("return_to=%2F+evil", "/"),
            ("return_to=", "/"),
        ] {
            let (_, value) = form_urlencoded::parse(query.as_bytes()).next().unwrap();
            assert_eq!(ReturnPath::parse(&value).as_str(), expected, "{query}");
        }
    }

    #[test]
    fn its_location_is_ascii() {
        assert_eq!(ReturnPath::parse("/x?a=1").location(), "/x?a=1");
        assert_eq!(ReturnPath::parse("/café").location(), "/caf%C3%A9");
        let header = axum::http::HeaderValue::try_from(ReturnPath::parse("/café").location());
        assert!(header.is_ok());
    }

    #[test]
    fn it_is_encoded_again_when_carried_on() {
        assert_eq!(
            ReturnPath::parse("/").appended_to("/auth/refresh"),
            "/auth/refresh?return_to=%2F"
        );
        assert_eq!(
            ReturnPath::parse("/x?a=1&b=2").appended_to("/login"),
            "/login?return_to=%2Fx%3Fa%3D1%26b%3D2"
        );
    }
}

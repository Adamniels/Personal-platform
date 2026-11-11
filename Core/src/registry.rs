//! The feature registry: which features exist, read once at startup from
//! `infra/features.toml`, the file beside the Caddyfile. See "The feature
//! registry" in docs/core-architecture.md.

use serde::Deserialize;
use std::ffi::OsString;
use std::path::{Path, PathBuf};

/// Where the registry is read from when `CORE_FEATURE_REGISTRY` is unset,
/// relative to the working directory, since the core runs from `Core/`.
pub const DEFAULT_PATH: &str = "../infra/features.toml";

/// Every feature the platform has, in the order the file lists them.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Registry {
    features: Vec<Feature>,
}

/// One `[[feature]]` table.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Feature {
    pub name: String,
    pub prefix: String,
    pub upstream: String,
    pub display: Option<String>,
}

// The file as a whole. Unknown keys are refused here as well as in each
// feature, so `[[features]]` is an error rather than an empty registry.
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct File {
    #[serde(default)]
    feature: Vec<Feature>,
}

impl Registry {
    /// The registry `text` describes, or why it describes none: the TOML
    /// error, or the first feature that breaks a rule, by its place in the
    /// file and its name.
    pub fn parse(text: &str) -> Result<Registry, String> {
        let file: File = toml::from_str(text).map_err(|err| err.to_string())?;

        for (index, feature) in file.feature.iter().enumerate() {
            let at = |reason: String| format!("feature {} ({}): {reason}", index + 1, feature.name);
            feature.check().map_err(at)?;

            let earlier = &file.feature[..index];
            if let Some(other) = earlier.iter().position(|f| f.name == feature.name) {
                return Err(at(format!(
                    "name '{}' is also feature {}'s",
                    feature.name,
                    other + 1
                )));
            }
            if let Some(other) = earlier.iter().position(|f| f.prefix == feature.prefix) {
                return Err(at(format!(
                    "prefix '{}' is also feature {}'s",
                    feature.prefix,
                    other + 1
                )));
            }
        }

        Ok(Registry {
            features: file.feature,
        })
    }

    /// The registry in the file at `path`. Every error starts with the path,
    /// so the core's refusal to start says which file it read.
    pub fn load(path: &Path) -> Result<Registry, String> {
        let shown = path.display();
        let text = std::fs::read_to_string(path).map_err(|err| format!("{shown}: {err}"))?;
        Registry::parse(&text).map_err(|err| format!("{shown}: {err}"))
    }

    pub fn features(&self) -> &[Feature] {
        &self.features
    }

    /// The registry in one line for the startup log, such as
    /// `1 feature (Canary at /canary)`.
    pub fn summary(&self) -> String {
        let count = match self.features.len() {
            1 => "1 feature".to_owned(),
            n => format!("{n} features"),
        };
        if self.features.is_empty() {
            return count;
        }
        let each: Vec<String> = self
            .features
            .iter()
            .map(|f| format!("{} at {}", f.name, f.prefix))
            .collect();
        format!("{count} ({})", each.join(", "))
    }
}

impl Feature {
    // The rules for one feature on its own; uniqueness is checked across the
    // file in `Registry::parse`.
    fn check(&self) -> Result<(), String> {
        if self.name.trim().is_empty() {
            return Err("name is empty".into());
        }
        if !is_prefix(&self.prefix) {
            return Err(format!(
                "prefix '{}' is not / and one segment of lowercase letters, digits and hyphens",
                self.prefix
            ));
        }
        if !is_upstream(&self.upstream) {
            return Err(format!(
                "upstream '{}' is not http:// or https://, a host and an optional port, with nothing after it",
                self.upstream
            ));
        }
        if let Some(display) = &self.display
            && !display.starts_with(&format!("{}/", self.prefix))
        {
            return Err(format!(
                "display '{display}' is not under the feature's prefix, {}/",
                self.prefix
            ));
        }
        Ok(())
    }
}

// `/` and one segment, as Caddy matches it: `/canary`, never `/canary/`.
fn is_prefix(prefix: &str) -> bool {
    prefix.strip_prefix('/').is_some_and(|segment| {
        !segment.is_empty()
            && segment
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
    })
}

// A scheme, a host and an optional port, which the core joins a path onto.
// The host is a name or an IPv4 address; nothing registered needs IPv6 yet.
fn is_upstream(upstream: &str) -> bool {
    let Some(authority) = upstream
        .strip_prefix("http://")
        .or_else(|| upstream.strip_prefix("https://"))
    else {
        return false;
    };
    let (host, port) = match authority.split_once(':') {
        Some((host, port)) => (host, Some(port)),
        None => (authority, None),
    };
    let host_ok = !host.is_empty()
        && host
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'.' || b == b'-');
    let port_ok = port.is_none_or(|port| {
        !port.is_empty()
            && port.bytes().all(|b| b.is_ascii_digit())
            && port.parse::<u16>().is_ok_and(|p| p > 0)
    });
    host_ok && port_ok
}

/// The registry's path from the value of `CORE_FEATURE_REGISTRY`: unset is
/// `DEFAULT_PATH`, and anything set is used as given.
pub fn path(setting: Option<OsString>) -> PathBuf {
    setting.map_or_else(|| PathBuf::from(DEFAULT_PATH), PathBuf::from)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_registry_parses_with_and_without_a_display_endpoint() {
        let registry = Registry::parse(
            r#"
            [[feature]]
            name = "Desk"
            prefix = "/desk"
            upstream = "https://desk.internal:9000"
            display = "/desk/api/display"

            [[feature]]
            name = "Canary"
            prefix = "/canary"
            upstream = "http://127.0.0.1:8081"
            "#,
        )
        .unwrap();

        assert_eq!(
            registry.features(),
            [
                Feature {
                    name: "Desk".into(),
                    prefix: "/desk".into(),
                    upstream: "https://desk.internal:9000".into(),
                    display: Some("/desk/api/display".into()),
                },
                Feature {
                    name: "Canary".into(),
                    prefix: "/canary".into(),
                    upstream: "http://127.0.0.1:8081".into(),
                    display: None,
                },
            ]
        );
    }

    // One valid feature with the line for `field` dropped, none if it is
    // empty, and `line` added, so each case below breaks one thing and the
    // rest stays right.
    fn canary_with(field: &str, line: &str) -> String {
        let mut lines = vec![
            r#"name = "Canary""#,
            r#"prefix = "/canary""#,
            r#"upstream = "http://127.0.0.1:8081""#,
        ];
        lines.retain(|l| field.is_empty() || !l.starts_with(&format!("{field} ")));
        format!("[[feature]]\n{}\n{line}\n", lines.join("\n"))
    }

    #[test]
    fn a_broken_registry_is_refused_with_the_reason() {
        let two = |second: &str| {
            format!(
                "[[feature]]\nname = \"Canary\"\nprefix = \"/canary\"\nupstream = \"http://127.0.0.1:8081\"\n\n\
                 [[feature]]\n{second}\n"
            )
        };
        let cases = [
            (
                "not TOML",
                "[[feature]\nname = ".to_owned(),
                "TOML parse error",
            ),
            (
                "missing field",
                canary_with("upstream", ""),
                "missing field `upstream`",
            ),
            (
                "unknown key",
                canary_with("", r#"upstrem = "http://127.0.0.1:8082""#),
                "unknown field `upstrem`",
            ),
            (
                "unknown table",
                "[[features]]\nname = \"Canary\"\n".to_owned(),
                "unknown field `features`",
            ),
            (
                "empty name",
                canary_with("name", r#"name = """#),
                "name is empty",
            ),
            (
                "blank name",
                canary_with("name", r#"name = "  ""#),
                "name is empty",
            ),
            (
                "prefix without slash",
                canary_with("prefix", r#"prefix = "canary""#),
                "prefix 'canary'",
            ),
            (
                "prefix alone",
                canary_with("prefix", r#"prefix = "/""#),
                "prefix '/'",
            ),
            (
                "prefix two segments",
                canary_with("prefix", r#"prefix = "/canary/api""#),
                "prefix '/canary/api'",
            ),
            (
                "prefix trailing slash",
                canary_with("prefix", r#"prefix = "/canary/""#),
                "prefix '/canary/'",
            ),
            (
                "prefix uppercase",
                canary_with("prefix", r#"prefix = "/Canary""#),
                "prefix '/Canary'",
            ),
            (
                "upstream with a path",
                canary_with("upstream", r#"upstream = "http://127.0.0.1:8081/canary""#),
                "upstream 'http://127.0.0.1:8081/canary'",
            ),
            (
                "upstream with a trailing slash",
                canary_with("upstream", r#"upstream = "http://127.0.0.1:8081/""#),
                "upstream 'http://127.0.0.1:8081/'",
            ),
            (
                "upstream without a scheme",
                canary_with("upstream", r#"upstream = "127.0.0.1:8081""#),
                "upstream '127.0.0.1:8081'",
            ),
            (
                "upstream with another scheme",
                canary_with("upstream", r#"upstream = "ftp://127.0.0.1:8081""#),
                "upstream 'ftp://127.0.0.1:8081'",
            ),
            (
                "upstream without a host",
                canary_with("upstream", r#"upstream = "http://:8081""#),
                "upstream 'http://:8081'",
            ),
            (
                "upstream with a bad port",
                canary_with("upstream", r#"upstream = "http://127.0.0.1:99999""#),
                "upstream 'http://127.0.0.1:99999'",
            ),
            (
                "upstream with credentials",
                canary_with("upstream", r#"upstream = "http://user@127.0.0.1:8081""#),
                "upstream 'http://user@127.0.0.1:8081'",
            ),
            (
                "upstream with a query",
                canary_with("upstream", r#"upstream = "http://127.0.0.1:8081?x=1""#),
                "upstream 'http://127.0.0.1:8081?x=1'",
            ),
            (
                "display outside the prefix",
                canary_with("", r#"display = "/desk/api/display""#),
                "display '/desk/api/display'",
            ),
            (
                "display sharing the prefix's start",
                canary_with("", r#"display = "/canaryfoo/display""#),
                "display '/canaryfoo/display'",
            ),
            (
                "display the prefix itself",
                canary_with("", r#"display = "/canary""#),
                "display '/canary'",
            ),
            (
                "two features sharing a name",
                two("name = \"Canary\"\nprefix = \"/other\"\nupstream = \"http://127.0.0.1:8082\""),
                "name 'Canary' is also feature 1's",
            ),
            (
                "two features sharing a prefix",
                two("name = \"Other\"\nprefix = \"/canary\"\nupstream = \"http://127.0.0.1:8082\""),
                "prefix '/canary' is also feature 1's",
            ),
        ];

        for (case, text, reason) in cases {
            match Registry::parse(&text) {
                Ok(registry) => panic!("{case}: accepted as {registry:?}"),
                Err(err) => assert!(
                    err.contains(reason),
                    "{case}: refused with {err:?}, which does not name {reason:?}"
                ),
            }
        }
    }

    #[test]
    fn a_validation_error_names_the_feature() {
        let err = Registry::parse(&canary_with("prefix", r#"prefix = "canary""#)).unwrap_err();
        assert!(err.starts_with("feature 1 (Canary): "), "{err}");
    }

    #[test]
    fn an_empty_registry_has_no_features() {
        assert_eq!(Registry::parse(""), Ok(Registry::default()));
    }

    #[test]
    fn the_summary_names_each_feature_and_its_prefix() {
        assert_eq!(Registry::default().summary(), "0 features");
        let one = Registry::parse(&canary_with("", "")).unwrap();
        assert_eq!(one.summary(), "1 feature (Canary at /canary)");
        let mut two = one.clone();
        two.features.push(Feature {
            name: "Desk".into(),
            prefix: "/desk".into(),
            upstream: "http://127.0.0.1:8082".into(),
            display: None,
        });
        assert_eq!(
            two.summary(),
            "2 features (Canary at /canary, Desk at /desk)"
        );
    }

    #[test]
    fn an_unset_registry_path_is_the_default_and_a_set_one_is_used() {
        assert_eq!(path(None), PathBuf::from("../infra/features.toml"));
        assert_eq!(
            path(Some("/etc/platform/features.toml".into())),
            PathBuf::from("/etc/platform/features.toml")
        );
        assert_eq!(
            path(Some("features.toml".into())),
            PathBuf::from("features.toml")
        );
    }
}

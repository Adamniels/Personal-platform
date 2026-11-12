//! What a refused form looks like, for the tests of the core's forms: the
//! message, starting with "Error", under the one field it is about, which
//! is marked invalid and names it. See the text field rule in
//! design/DESIGN.md.

/// Panics unless `body` shows `message` as the error on `field`, and on no
/// other field.
pub fn assert_refused_on(body: &str, field: &str, message: &str) {
    assert!(
        body.contains(&format!(
            r#"<p class="error" id="{field}-error" role="alert">Error: {message}</p>"#
        )),
        "{field}: {body}"
    );
    assert_eq!(
        body.matches(r#"aria-invalid="true""#).count(),
        1,
        "{field}: {body}"
    );
    let at = body
        .find(&format!(r#"<input name="{field}""#))
        .unwrap_or_else(|| panic!("no field {field} in {body}"));
    let tag = &body[at..at + body[at..].find('>').unwrap()];
    assert!(
        tag.ends_with(&format!(
            r#" aria-invalid="true" aria-describedby="{field}-error""#
        )),
        "{tag}"
    );
}

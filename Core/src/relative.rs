//! A time relative to now, in words, rounded to the nearest minute, hour or
//! day: "in 3 hours" for one ahead, as review's card says when the next card
//! comes due, and "5 hours ago" for one past, as the backup line says when the
//! last backup was. Now is always the caller's, see "Time is an input" in
//! High-level/docs/how-we-build.md.

use time::Duration;

/// A time `ahead` of now, such as "in 3 hours". Under a minute, a moment in
/// the past included, is "in under a minute", since two clocks may differ by
/// that much.
pub fn ahead(ahead: Duration) -> String {
    match rounded(ahead) {
        None => "in under a minute".into(),
        Some((1, unit)) => format!("in 1 {unit}"),
        Some((count, unit)) => format!("in {count} {unit}s"),
    }
}

/// A time `ago` before now, such as "5 hours ago". Under a minute, a moment
/// in the future included, is "just now".
pub fn ago(ago: Duration) -> String {
    match rounded(ago) {
        None => "just now".into(),
        Some((1, unit)) => format!("1 {unit} ago"),
        Some((count, unit)) => format!("{count} {unit}s ago"),
    }
}

// The count and unit `span` rounds to, or None under a minute.
fn rounded(span: Duration) -> Option<(i64, &'static str)> {
    let seconds = span.whole_seconds();
    if seconds < 60 {
        return None;
    }
    let minutes = (seconds + 30) / 60;
    if minutes < 60 {
        return Some((minutes, "minute"));
    }
    let hours = (minutes + 30) / 60;
    if hours < 24 {
        return Some((hours, "hour"));
    }
    Some(((hours + 12) / 24, "day"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_time_ahead_reads_as_in() {
        let at = |seconds: i64| ahead(Duration::seconds(seconds));

        assert_eq!(at(-30), "in under a minute");
        assert_eq!(at(59), "in under a minute");
        assert_eq!(at(60), "in 1 minute");
        assert_eq!(at(90), "in 2 minutes");
        assert_eq!(at(59 * 60 + 40), "in 1 hour");
        assert_eq!(at(3 * 86_400 + 3600), "in 3 days");
    }

    #[test]
    fn a_time_past_reads_as_ago() {
        let at = |seconds: i64| ago(Duration::seconds(seconds));

        assert_eq!(at(-30), "just now");
        assert_eq!(at(0), "just now");
        assert_eq!(at(59), "just now");
        assert_eq!(at(60), "1 minute ago");
        assert_eq!(at(89), "1 minute ago");
        assert_eq!(at(90), "2 minutes ago");
        assert_eq!(at(59 * 60 + 40), "1 hour ago");
        assert_eq!(at(5 * 3600), "5 hours ago");
        assert_eq!(at(23 * 3600), "23 hours ago");
        assert_eq!(at(23 * 3600 + 40 * 60), "1 day ago");
        assert_eq!(at(26 * 3600), "1 day ago");
        assert_eq!(at(2 * 86_400), "2 days ago");
    }
}

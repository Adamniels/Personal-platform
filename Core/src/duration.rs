//! Durations as the core's settings and commands take them: a whole number
//! above zero and one unit, `s`, `m`, `h` or `d`, such as `7d` or `30s`. Used
//! by `invite --expires-in` and `CORE_ACCESS_TOKEN_LIFETIME`.

use time::Duration;

/// The duration `text` names, or why it names none.
pub fn parse(text: &str) -> Result<Duration, String> {
    let refused = || format!("'{text}' is not a duration like 7d, 12h, 30m or 90s");

    let unit_at = text.len().saturating_sub(1);
    if !text.is_char_boundary(unit_at) {
        return Err(refused());
    }
    let (number, unit) = text.split_at(unit_at);
    if number.is_empty() || !number.bytes().all(|b| b.is_ascii_digit()) {
        return Err(refused());
    }
    let count: i64 = number.parse().map_err(|_| refused())?;
    if count == 0 {
        return Err(refused());
    }

    let seconds_each = match unit {
        "s" => 1,
        "m" => 60,
        "h" => 60 * 60,
        "d" => 24 * 60 * 60,
        _ => return Err(refused()),
    };
    let seconds = count.checked_mul(seconds_each).ok_or_else(refused)?;
    Ok(Duration::seconds(seconds))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_parse_in_each_unit() {
        assert_eq!(parse("30s"), Ok(Duration::seconds(30)));
        assert_eq!(parse("15m"), Ok(Duration::minutes(15)));
        assert_eq!(parse("2h"), Ok(Duration::hours(2)));
        assert_eq!(parse("7d"), Ok(Duration::days(7)));
    }

    #[test]
    fn anything_else_is_refused() {
        for text in [
            "",
            "7",
            "d",
            "0d",
            "-1d",
            "+1d",
            "1.5h",
            "7days",
            "7 d",
            "1w",
            "7é",
            "99999999999999999999d",
        ] {
            assert!(parse(text).is_err(), "accepted {text:?}");
        }
    }
}

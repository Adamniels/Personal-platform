using System.Globalization;
using System.Text.RegularExpressions;

namespace DevFlow.Worker.Agents;

/// <summary>
/// When Claude's usage limit resets, for a run that hit it, read two ways. First from the stream's
/// <c>rate_limit_event</c>, whose <c>rate_limit_info</c> says the request was <c>rejected</c> and
/// gives <c>resetsAt</c>, a Unix time in seconds; an event that let the request through, which
/// says only when the window resets, is no time. Otherwise from the text of Claude Code's answer:
/// Claude Code writes the reset there for people, not as a timestamp. Read from Claude Code
/// 2.1.322's own formatter on 2026-11-19, the message ends <c>· resets 3pm (UTC)</c>, the hour and,
/// when not on the hour, the minutes, in the zone it names, within the next 24 hours; or <c>· resets
/// Nov 20, 3pm (UTC)</c>, with the date, when further, and the year too when it is another year.
/// Anything else, a zone .NET does not know among it, is no time, and Dev flow's doubling holds. See
/// "Stops" in devflow/README.md and the recorded answers in DevFlow.Worker.Tests/Claude/.
/// </summary>
internal static partial class UsageLimit
{
    [GeneratedRegex(
        @"\bresets (?:(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (?<day>\d{1,2}), (?:(?<year>\d{4}), )?)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<half>am|pm) \((?<zone>[A-Za-z][A-Za-z0-9_+\-/]*)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reset();

    /// <summary>When the limit resets, as <paramref name="event"/>, a <c>rate_limit_event</c> of
    /// the stream, says: its <c>rate_limit_info.resetsAt</c> when its <c>status</c> is
    /// <c>rejected</c>; null for any other event, or a time that is not a whole number of seconds
    /// an instant can hold.</summary>
    public static DateTimeOffset? ResetsAt(System.Text.Json.Nodes.JsonObject @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (@event["rate_limit_info"] is not System.Text.Json.Nodes.JsonObject info
            || info["status"] is not System.Text.Json.Nodes.JsonValue status
            || !status.TryGetValue<string>(out var said)
            || said != "rejected"
            || info["resetsAt"] is not System.Text.Json.Nodes.JsonValue resetsAt
            || !resetsAt.TryGetValue<long>(out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>When the limit resets, as an instant, or null when <paramref name="message"/> says
    /// no time this reads; <paramref name="now"/> places a time without a date.</summary>
    public static DateTimeOffset? ResetsAt(string? message, DateTimeOffset now)
    {
        if (message is null || Reset().Match(message) is not { Success: true } match)
        {
            return null;
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59)
        {
            return null;
        }

        hour = hour % 12 + (match.Groups["half"].Value == "pm" ? 12 : 0);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        try
        {
            if (!match.Groups["month"].Success)
            {
                // Within the next 24 hours: today at that time in the zone, or tomorrow once it has
                // passed, a minute's grace for a reset written moments before.
                var today = At(local.Year, local.Month, local.Day, hour, minute, zone);
                return today >= now.AddMinutes(-1) ? today : At(local.AddDays(1).Year, local.AddDays(1).Month, local.AddDays(1).Day, hour, minute, zone);
            }

            var month = DateTime.ParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture).Month;
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["year"].Success)
            {
                return At(int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture), month, day, hour, minute, zone);
            }

            // Without a year it is this year, unless that has passed, when it is the next.
            var thisYear = At(local.Year, month, day, hour, minute, zone);
            return thisYear >= now.AddDays(-1) ? thisYear : At(local.Year + 1, month, day, hour, minute, zone);
        }
        catch (ArgumentException)
        {
            // A day the month has not, or a time a change of clocks skips.
            return null;
        }
    }

    /// <summary>The instant of a wall clock time in <paramref name="zone"/>.</summary>
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute, TimeZoneInfo zone)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }
}
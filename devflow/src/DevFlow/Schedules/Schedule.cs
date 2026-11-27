using DevFlow.Jobs;

namespace DevFlow.Schedules;

/// <summary>Where a project stands by its schedules at an instant.</summary>
/// <param name="Inside">Whether it may run now: always, or inside a window that is on.</param>
/// <param name="NextStart">When a window that is on next starts, after the one it is inside ends
/// when it is inside one, looked for over <see cref="Schedule.LookAhead"/>; null with none, and
/// always for a project that runs always. In the zone's own offset.</param>
/// <param name="Until">When the windows it is inside end, those that meet or overlap taken as one;
/// null outside, and always for a project that runs always. In the zone's own offset.</param>
public sealed record ScheduleState(bool Inside, DateTimeOffset? NextStart, DateTimeOffset? Until);

/// <summary>
/// Whether a project runs now by its schedules, pure. A project that runs always is always inside.
/// One that runs by schedule is inside when now is in any window that is on, and with none on it
/// is never inside. A window belongs to the day it starts, and one whose to is earlier than its
/// from ends the next day. Times are wall clock in the user's zone, by
/// <see cref="DevFlowTime.At"/>: a start a change of clocks skips starts at the first instant after
/// it, and a start it repeats is the first. See "Schedules" in devflow/README.md, and why they are
/// wall clock "Projects" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class Schedule
{
    /// <summary>How far ahead the next start is looked for: a window that is on starts at least
    /// once a week, and a day more covers one that crosses midnight.</summary>
    public static readonly TimeSpan LookAhead = TimeSpan.FromDays(8);

    /// <summary>Where a project that runs by <paramref name="runsBy"/> with
    /// <paramref name="windows"/> stands at <paramref name="now"/> in
    /// <paramref name="zone"/>.</summary>
    public static ScheduleState At(string runsBy, IReadOnlyList<ScheduleWindow> windows, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(zone);
        if (runsBy == RunsBy.Always)
        {
            return new ScheduleState(true, null, null);
        }

        var spans = Spans(windows, zone, now).ToList();

        // Inside, the coverage runs on through every span that starts before it ends.
        DateTimeOffset? until = null;
        for (var extended = true; extended;)
        {
            extended = false;
            var at = until ?? now;
            foreach (var (start, end) in spans)
            {
                if (start <= at && at < end && (until is null || end > until))
                {
                    until = end;
                    extended = true;
                }
            }
        }

        var after = until ?? now;
        var next = spans
            .Where(span => span.Start > after && span.Start <= now + LookAhead)
            .Select(span => (DateTimeOffset?)span.Start)
            .Min();
        return new ScheduleState(until is not null, InZone(next, zone), InZone(until, zone));
    }

    /// <summary>Why a project does not run in <paramref name="state"/>, null when it is
    /// inside.</summary>
    public static WaitReason.OutsideSchedule? Reason(ScheduleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Inside ? null : new WaitReason.OutsideSchedule(state.NextStart);
    }

    /// <summary>Every span of a window that is on from the day before now's to
    /// <see cref="LookAhead"/> after it, as instants; one a change of clocks leaves empty is
    /// none.</summary>
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Spans(IReadOnlyList<ScheduleWindow> windows, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DevFlowTime.DayOf(now, zone);
        for (var day = today.AddDays(-1); day <= today.AddDays(LookAhead.Days); day = day.AddDays(1))
        {
            foreach (var window in windows)
            {
                if (!window.Active || !window.Days.Contains(day.DayOfWeek))
                {
                    continue;
                }

                var start = DevFlowTime.At(day, window.From, zone);
                var end = DevFlowTime.At(window.To <= window.From ? day.AddDays(1) : day, window.To, zone);
                if (end > start)
                {
                    yield return (start, end);
                }
            }
        }
    }

    private static DateTimeOffset? InZone(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null;
}
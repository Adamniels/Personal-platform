namespace DevFlow;

/// <summary>
/// Dev flow's time zone, the one place it is read, and the days counted in it: every day Dev flow
/// counts, the tokens per day on "Usage and history" among them, is a day here, never a day in UTC
/// or in the server's own zone. Europe/Stockholm, through .NET's own IANA zones; "Dev flow:
/// schedules, pausing and limits", #455, makes it each user's own setting. See "Days" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class DevFlowTime
{
    /// <summary>The zone every day is counted in.</summary>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// <summary>The day <paramref name="instant"/> falls on in <see cref="Zone"/>.</summary>
    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    /// <summary>The first instant of <paramref name="day"/> in <see cref="Zone"/>: its midnight, or
    /// where a zone's clocks skip midnight, the first minute after it that exists, and where they
    /// pass it twice, the first time. Neither happens in Stockholm, whose clocks change at 02:00 and
    /// 03:00, but a zone of the user's own, #455, may.</summary>
    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        // Of two offsets, the larger is the earlier instant.
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>The instant <paramref name="day"/> ends, which is not in it: the next day's start. A
    /// day is 23 hours long when summer time starts, and 25 when it ends.</summary>
    public static DateTimeOffset EndOf(DateOnly day) => StartOf(day.AddDays(1));
}
namespace DevFlow.Tests;

/// <summary>
/// <see cref="DevFlowTime"/>, the one place Dev flow reads its time zone, through which every day it
/// counts is read. Pure. See "Days" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class DevFlowTimeTests
{
    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant + "Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TheZoneIsStockholm()
    {
        // .NET's own IANA zone, no dependency.
        Assert.Equal("Europe/Stockholm", DevFlowTime.Zone.Id);

        // An instant's day is the day it is in Stockholm: an hour ahead of UTC in winter, two in
        // summer, so 23:30 UTC is already the next day in both.
        Assert.Equal(new DateOnly(2027, 1, 15), DevFlowTime.DayOf(Utc("2027-01-15T22:59:59")));
        Assert.Equal(new DateOnly(2027, 1, 16), DevFlowTime.DayOf(Utc("2027-01-15T23:00:00")));
        Assert.Equal(new DateOnly(2026, 7, 1), DevFlowTime.DayOf(Utc("2026-07-01T21:59:59")));
        Assert.Equal(new DateOnly(2026, 7, 2), DevFlowTime.DayOf(Utc("2026-07-01T23:30:00")));

        // An ordinary day starts at midnight in Stockholm and lasts 24 hours.
        var winter = new DateOnly(2027, 1, 15);
        Assert.Equal(Utc("2027-01-14T23:00:00"), DevFlowTime.StartOf(winter));
        Assert.Equal(TimeSpan.FromHours(24), DevFlowTime.EndOf(winter) - DevFlowTime.StartOf(winter));
        Assert.Equal(DevFlowTime.StartOf(winter.AddDays(1)), DevFlowTime.EndOf(winter));

        // The day summer time starts, 2026-03-29, the clocks skip 02:00 to 03:00: 23 hours.
        var spring = new DateOnly(2026, 3, 29);
        Assert.Equal(Utc("2026-03-28T23:00:00"), DevFlowTime.StartOf(spring));
        Assert.Equal(Utc("2026-03-29T22:00:00"), DevFlowTime.EndOf(spring));
        Assert.Equal(TimeSpan.FromHours(23), DevFlowTime.EndOf(spring) - DevFlowTime.StartOf(spring));

        // The day it ends, 2026-10-25, 03:00 goes back to 02:00: 25 hours.
        var autumn = new DateOnly(2026, 10, 25);
        Assert.Equal(Utc("2026-10-24T22:00:00"), DevFlowTime.StartOf(autumn));
        Assert.Equal(Utc("2026-10-25T23:00:00"), DevFlowTime.EndOf(autumn));
        Assert.Equal(TimeSpan.FromHours(25), DevFlowTime.EndOf(autumn) - DevFlowTime.StartOf(autumn));

        // Every instant of a day is in it, its start included and its end not, across both changes.
        foreach (var day in new[] { spring, autumn, winter })
        {
            Assert.Equal(day, DevFlowTime.DayOf(DevFlowTime.StartOf(day)));
            Assert.Equal(day, DevFlowTime.DayOf(DevFlowTime.EndOf(day).AddTicks(-1)));
            Assert.Equal(day.AddDays(1), DevFlowTime.DayOf(DevFlowTime.EndOf(day)));
        }
    }
}
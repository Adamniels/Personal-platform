using DevFlow.Tests.Usage;
using DevFlow.Usage;

namespace DevFlow.Tests;

/// <summary>
/// <see cref="DevFlowTime"/>, the one place Dev flow reads a time zone, the user's own, through which
/// every day it counts and every schedule is read. The days' arithmetic is pure; the user's zone
/// against real Postgres, through the door as the user. See "Days" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class DevFlowTimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant + "Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TheZoneIsStockholm()
    {
        var zone = DevFlowTime.DefaultZone;

        // .NET's own IANA zone, no dependency.
        Assert.Equal("Europe/Stockholm", zone.Id);

        // An instant's day is the day it is in Stockholm: an hour ahead of UTC in winter, two in
        // summer, so 23:30 UTC is already the next day in both.
        Assert.Equal(new DateOnly(2027, 1, 15), DevFlowTime.DayOf(Utc("2027-01-15T22:59:59"), zone));
        Assert.Equal(new DateOnly(2027, 1, 16), DevFlowTime.DayOf(Utc("2027-01-15T23:00:00"), zone));
        Assert.Equal(new DateOnly(2026, 7, 1), DevFlowTime.DayOf(Utc("2026-07-01T21:59:59"), zone));
        Assert.Equal(new DateOnly(2026, 7, 2), DevFlowTime.DayOf(Utc("2026-07-01T23:30:00"), zone));

        // An ordinary day starts at midnight in Stockholm and lasts 24 hours.
        var winter = new DateOnly(2027, 1, 15);
        Assert.Equal(Utc("2027-01-14T23:00:00"), DevFlowTime.StartOf(winter, zone));
        Assert.Equal(TimeSpan.FromHours(24), DevFlowTime.EndOf(winter, zone) - DevFlowTime.StartOf(winter, zone));
        Assert.Equal(DevFlowTime.StartOf(winter.AddDays(1), zone), DevFlowTime.EndOf(winter, zone));

        // The day summer time starts, 2026-03-29, the clocks skip 02:00 to 03:00: 23 hours.
        var spring = new DateOnly(2026, 3, 29);
        Assert.Equal(Utc("2026-03-28T23:00:00"), DevFlowTime.StartOf(spring, zone));
        Assert.Equal(Utc("2026-03-29T22:00:00"), DevFlowTime.EndOf(spring, zone));
        Assert.Equal(TimeSpan.FromHours(23), DevFlowTime.EndOf(spring, zone) - DevFlowTime.StartOf(spring, zone));

        // The day it ends, 2026-10-25, 03:00 goes back to 02:00: 25 hours.
        var autumn = new DateOnly(2026, 10, 25);
        Assert.Equal(Utc("2026-10-24T22:00:00"), DevFlowTime.StartOf(autumn, zone));
        Assert.Equal(Utc("2026-10-25T23:00:00"), DevFlowTime.EndOf(autumn, zone));
        Assert.Equal(TimeSpan.FromHours(25), DevFlowTime.EndOf(autumn, zone) - DevFlowTime.StartOf(autumn, zone));

        // Every instant of a day is in it, its start included and its end not, across both changes.
        foreach (var day in new[] { spring, autumn, winter })
        {
            Assert.Equal(day, DevFlowTime.DayOf(DevFlowTime.StartOf(day, zone), zone));
            Assert.Equal(day, DevFlowTime.DayOf(DevFlowTime.EndOf(day, zone).AddTicks(-1), zone));
            Assert.Equal(day.AddDays(1), DevFlowTime.DayOf(DevFlowTime.EndOf(day, zone), zone));
        }

        // A wall clock time the change skips is the first instant after it, and one it repeats the
        // first of the two.
        Assert.Equal(Utc("2026-03-29T01:00:00"), DevFlowTime.At(spring, new TimeOnly(2, 30), zone));
        Assert.Equal(Utc("2026-10-25T00:30:00"), DevFlowTime.At(autumn, new TimeOnly(2, 30), zone));
    }

    [Fact]
    public async Task TheZoneIsTheUsers()
    {
        await using var world = await UsageWorld.Start();
        var bob = Reading.Routes.Bob;

        // Stockholm for a user who has set none.
        Assert.Equal("Europe/Stockholm", (await DevFlowTime.ZoneOf(world.Routes.Door, UsageWorld.User, Ct)).Id);

        // A run at 02:00 UTC on 2 July is that day in Stockholm, 04:00 there, and today, read at
        // 10:00 UTC, is the 2nd.
        var project = await world.Project("Personal-platform");
        await world.Card(project, 1);
        await world.RunAt(project, "build", Utc("2026-07-02T02:00:00"), Utc("2026-07-02T02:15:00"), card: 1, input: 11);
        var stockholm = await Days();
        Assert.Equal(new DateOnly(2026, 7, 2), stockholm[^1].Date);
        Assert.Equal(11, stockholm[^1].Tokens);

        // In New York it is 22:00 on the 1st, so the chart's days follow the zone: the run counts to
        // the 1st, and today is still the 2nd there, at 06:00.
        await DevFlowTime.SetZone(world.Routes.Door, UsageWorld.User, "America/New_York", Ct);
        Assert.Equal("America/New_York", (await DevFlowTime.ZoneOf(world.Routes.Door, UsageWorld.User, Ct)).Id);
        var newYork = await Days();
        Assert.Equal(new DateOnly(2026, 7, 2), newYork[^1].Date);
        Assert.Equal((new DateOnly(2026, 7, 1), 11L), (newYork[^2].Date, newYork[^2].Tokens));
        Assert.Equal(0, newYork[^1].Tokens);

        // Another user's zone is their own.
        Assert.Equal("Europe/Stockholm", (await DevFlowTime.ZoneOf(world.Routes.Door, bob, Ct)).Id);

        async Task<IReadOnlyList<UsageDay>> Days() =>
            Assert.Single((await UsageStore.For(world.Routes.Door, UsageWorld.User, "all", Utc("2026-07-02T10:00:00"), Ct)).Projects).Days;
    }
}
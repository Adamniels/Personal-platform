using System.Globalization;
using System.Text.Json.Nodes;

using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Schedules;
using DevFlow.Tests.Jobs;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Schedules;

/// <summary>
/// When a project runs: always, or by schedule, inside a window that is on, read as wall clock in
/// the user's zone; outside, <see cref="RunDecision"/> answers
/// <see cref="WaitReason.OutsideSchedule"/> with the next start, after the pauses and before every
/// other reason, and a run under way finishes. The windows' arithmetic is tested pure, at the
/// instants it is about; the rule and the dispatcher against real Postgres and the fake code host,
/// with windows laid around the app's clock, since a test's token lasts only so long of it. See
/// "Schedules" and "Jobs" in devflow/README.md.
/// </summary>
public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Wall clock in Stockholm, with the offset it has there then.</summary>
    private static DateTimeOffset Local(string wall)
    {
        var at = DateTime.Parse(wall, CultureInfo.InvariantCulture);
        return new DateTimeOffset(at, Stockholm.GetUtcOffset(at));
    }

    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant + "Z", CultureInfo.InvariantCulture);

    private static ScheduleWindow Window(string days, string from, string to, bool active = true) =>
        new(days.Split(',').Select(day => ScheduleRules.Day(day)!.Value).ToHashSet(), TimeOnly.Parse(from, CultureInfo.InvariantCulture), TimeOnly.Parse(to, CultureInfo.InvariantCulture), active);

    private static ScheduleState At(DateTimeOffset now, params ScheduleWindow[] windows) =>
        Schedule.At(RunsBy.Schedule, windows, Stockholm, now);

    [Fact]
    public async Task AlwaysRunsAtAnyHour()
    {
        // Every hour of a week, whatever windows were left from a schedule, always is inside.
        var night = Window("fri", "22:00", "06:00");
        for (var hour = 0; hour < 7 * 24; hour++)
        {
            var now = Local("2027-01-04T00:30:00").AddHours(hour);
            Assert.Equal(new ScheduleState(true, null, null), Schedule.At(RunsBy.Always, [night], Stockholm, now));
        }

        // A new project is always: its schedules say so, and its build is handed out.
        await using var jobs = await Jobs.Jobs.Start();
        var schedules = await ScheduleRouteTests.Schedules(jobs);
        Assert.Equal("always", (string)schedules["runs_by"]!);
        Assert.Empty(schedules["schedules"]!.AsArray());
        Assert.Null(schedules["next_start"]);
        Assert.IsNotType<WaitReason.OutsideSchedule>((await Rule(jobs) as Decision.Wait)?.Reason);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task InsideAWindowRuns()
    {
        // A Monday 09:00 to 17:00 window: Monday 10:00 is inside it, running until 17:00.
        var day = Window("mon", "09:00", "17:00");
        var monday = At(Local("2027-01-04T10:00:00"), day);
        Assert.True(monday.Inside);
        Assert.Equal(Local("2027-01-04T17:00:00"), monday.Until);
        Assert.Null(Schedule.Reason(monday));

        // Its start is inside and its end is not.
        Assert.True(At(Local("2027-01-04T09:00:00"), day).Inside);
        Assert.False(At(Local("2027-01-04T17:00:00"), day).Inside);

        // Inside a window around the app's clock, the build is handed out.
        await using var jobs = await Jobs.Jobs.Start();
        await Put(jobs, "schedule", Around(jobs));
        Assert.Equal(new Decision.Run(Job.Build), await Rule(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task OutsideEveryWindowWaits()
    {
        // Monday 18:00 is outside the Monday 09:00 to 17:00 window, which starts next on the next
        // Monday at 09:00, written in the zone's own offset.
        var day = Window("mon", "09:00", "17:00");
        var evening = At(Local("2027-01-04T18:00:00"), day);
        Assert.False(evening.Inside);
        Assert.Equal(Local("2027-01-11T09:00:00"), evening.NextStart);
        Assert.Equal(TimeSpan.FromHours(1), evening.NextStart!.Value.Offset);
        Assert.Equal(new WaitReason.OutsideSchedule(Local("2027-01-11T09:00:00")), Schedule.Reason(evening));
        Assert.Equal("Outside its schedule, runs again Mon 09:00", Schedule.Reason(evening)!.Sentence);

        // Before it on the Monday, it starts that morning.
        Assert.Equal(Local("2027-01-04T09:00:00"), At(Local("2027-01-04T07:00:00"), day).NextStart);

        // The app's clock moved past a window around it: nothing is handed out, and the rule says
        // when it runs again, the same window a week on.
        await using var jobs = await Jobs.Jobs.Start();
        var (window, starts) = Around(jobs);
        await Put(jobs, "schedule", (window, starts));
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(await jobs.Runs());
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(starts.AddDays(7))), await Rule(jobs));

        // The route answers the same next start, and Running now's queue says it, linking to the
        // schedules.
        Assert.Equal(starts.AddDays(7), (DateTimeOffset)(await ScheduleRouteTests.Schedules(jobs))["next_start"]!);
        Assert.Equal(
            (string.Create(CultureInfo.InvariantCulture, $"Outside its schedule, runs again {starts:ddd HH:mm}"), "schedules"),
            await NextReason(jobs));
    }

    [Fact]
    public void AWindowCrossesMidnight()
    {
        // Friday 22:00 to 06:00 belongs to the Friday: it runs on Saturday at 05:59, and not on
        // Saturday at 22:00, nor on Friday before 22:00.
        var night = Window("fri", "22:00", "06:00");
        var early = At(Local("2027-01-09T05:59:00"), night);
        Assert.True(early.Inside);
        Assert.Equal(Local("2027-01-09T06:00:00"), early.Until);
        Assert.False(At(Local("2027-01-09T22:00:00"), night).Inside);
        Assert.False(At(Local("2027-01-08T21:59:00"), night).Inside);
        Assert.True(At(Local("2027-01-08T22:00:00"), night).Inside);

        // On Saturday night it runs next on the Friday after.
        Assert.Equal(Local("2027-01-15T22:00:00"), At(Local("2027-01-09T22:00:00"), night).NextStart);

        // Windows that meet run on as one: until the later one ends.
        var morning = Window("sat", "06:00", "08:00");
        Assert.Equal(Local("2027-01-09T08:00:00"), At(Local("2027-01-09T05:00:00"), night, morning).Until);
        Assert.Equal(Local("2027-01-15T22:00:00"), At(Local("2027-01-09T05:00:00"), night, morning).NextStart);
    }

    [Fact]
    public async Task AWindowSwitchedOffDoesNotCount()
    {
        // A window that is off is as if it were not there.
        var off = At(Local("2027-01-04T10:00:00"), Window("mon", "09:00", "17:00", active: false));
        Assert.Equal(new ScheduleState(false, null, null), off);
        Assert.Equal(new WaitReason.OutsideSchedule(null), Schedule.Reason(off));
        Assert.Equal("No schedule is on", Schedule.Reason(off)!.Sentence);

        // Around the app's clock, switched off: nothing is handed out, with no next start.
        await using var jobs = await Jobs.Jobs.Start();
        var (window, starts) = Around(jobs);
        window["active"] = false;
        await Put(jobs, "schedule", (window, starts));
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(null)), await Rule(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Null((await ScheduleRouteTests.Schedules(jobs))["next_start"]);
        Assert.Equal(("No schedule is on", "schedules"), await NextReason(jobs));
    }

    [Fact]
    public async Task NoWindowOnRunsNothing()
    {
        Assert.Equal(new ScheduleState(false, null, null), At(Local("2027-01-04T10:00:00")));

        // By schedule with no window at all runs nothing.
        await using var jobs = await Jobs.Jobs.Start();
        await Put(jobs);
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(null)), await Rule(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Back to always, it runs.
        await Put(jobs, "always");
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public void DaylightSavingIsWallClock()
    {
        // 29 March 2026, a Sunday, Stockholm's clocks skip from 02:00 to 03:00: a window from 02:30
        // starts at 03:00, the first instant after the gap, 01:00 UTC.
        var spring = Window("sun", "02:30", "05:00");
        Assert.Equal(Utc("2026-03-29T01:00:00"), At(Utc("2026-03-29T00:59:00"), spring).NextStart);
        Assert.False(At(Utc("2026-03-29T00:59:00"), spring).Inside);
        Assert.True(At(Utc("2026-03-29T01:00:00"), spring).Inside);
        // It ends at 05:00 in summer time, 03:00 UTC.
        Assert.Equal(Utc("2026-03-29T03:00:00"), At(Utc("2026-03-29T01:00:00"), spring).Until);

        // 25 October 2026, a Sunday, they go back from 03:00 to 02:00, so 02:30 happens twice: the
        // window starts at the first, in summer time, 00:30 UTC.
        var autumn = Window("sun", "02:30", "05:00");
        Assert.Equal(Utc("2026-10-25T00:30:00"), At(Utc("2026-10-25T00:00:00"), autumn).NextStart);
        Assert.False(At(Utc("2026-10-25T00:29:00"), autumn).Inside);
        Assert.True(At(Utc("2026-10-25T00:30:00"), autumn).Inside);
        // And ends at 05:00 in winter time, 04:00 UTC: three and a half hours of wall clock, four and
        // a half of time.
        Assert.Equal(Utc("2026-10-25T04:00:00"), At(Utc("2026-10-25T00:30:00"), autumn).Until);

        // An ordinary Sunday is three and a half hours.
        var ordinary = At(Local("2026-11-01T02:30:00"), autumn);
        Assert.Equal(TimeSpan.FromHours(2.5), ordinary.Until - Local("2026-11-01T02:30:00"));
    }

    [Fact]
    public async Task ARunUnderWayFinishes()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2)]);
        var (window, starts) = Around(jobs);
        await Put(jobs, "schedule", (window, starts));
        var job = await jobs.Job();
        var card = (int)job["card"]!["number"]!;

        // The window ends while the run is under way; its result is recorded as always, and its
        // card moves on, to its pull request.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(15));
        var finished = await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!));
        Assert.Equal(StatusCodes.Status204NoContent, finished.Response.StatusCode);
        Assert.Equal("succeeded", Assert.Single(await jobs.Runs()).Result);
        Assert.Equal("checking", (await jobs.Card(card)).State);

        // The next job waits for the window to come round again.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Single(await jobs.Runs());
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(starts.AddDays(7))), await Rule(jobs));
    }

    [Fact]
    public async Task OutsideComesAfterThePauses()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Put(jobs);

        // Paused and outside its schedule answers paused, and everything paused before that.
        await PauseTests.Pause(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));
        await PauseTests.PauseAll(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));

        // Continued, it is outside, ahead of a stop by a commit that landed on main.
        await PauseTests.PauseAll(jobs, false);
        await PauseTests.Pause(jobs, false);
        await Merging.Merged.Stop(jobs);
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(null)), await Rule(jobs));
    }

    /// <summary>
    /// A window on, around the app's clock in Stockholm: from ten minutes before it, its minute, to
    /// twenty minutes later, on the day it starts, crossing midnight when it does. It and when it
    /// started.
    /// </summary>
    internal static (JsonObject Window, DateTimeOffset Starts) Around(Jobs.Jobs jobs)
    {
        var local = TimeZoneInfo.ConvertTime(jobs.Routes.Time.GetUtcNow(), Stockholm);
        var start = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(-10);
        var from = TimeOnly.FromTimeSpan(start.TimeOfDay);
        var window = new JsonObject
        {
            ["name"] = "Around now",
            ["days"] = new JsonArray(start.DayOfWeek.ToString()[..3].ToLowerInvariant()),
            ["from"] = from.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["to"] = from.AddMinutes(20).ToString("HH:mm", CultureInfo.InvariantCulture),
            ["active"] = true,
        };
        return (window, start);
    }

    /// <summary>The owner saves the mode and the windows, as the settings page does.</summary>
    private static async Task Put(Jobs.Jobs jobs, string runsBy = "schedule", (JsonObject Window, DateTimeOffset Starts)? window = null)
    {
        var body = new JsonObject
        {
            ["runs_by"] = runsBy,
            ["schedules"] = window is { } each ? new JsonArray(each.Window.DeepClone()) : new JsonArray(),
        };
        var response = await jobs.Routes.App.SendJson("PUT", ScheduleRouteTests.Path(jobs.Project), Contract.Token, body.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    /// <summary>The rule's decision for the project as the first worker finds it.</summary>
    private static async Task<Decision> Rule(Jobs.Jobs jobs)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, Jobs.Jobs.FirstWorker, Jobs.Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return RunDecision.Of(situation);
    }

    /// <summary>The reason "Next up" on Running now gives the one card next, and where it links.</summary>
    private static async Task<(string? Reason, string? Link)> NextReason(Jobs.Jobs jobs)
    {
        var response = await jobs.Routes.App.Get("/devflow/api/running", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var card = Assert.Single(JsonNode.Parse(await Harness.Body(response))!["next"]!.AsArray())!;
        return ((string?)card["reason"], (string?)card["link"]);
    }
}
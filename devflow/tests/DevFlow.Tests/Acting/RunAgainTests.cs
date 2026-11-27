using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Fixing;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Stops;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Acting;

/// <summary>
/// "Run again" on a card or a feature the owner stopped: a card goes back where its stopped run
/// started from, counting nothing against it, and a feature's hold is cleared, so its next job is
/// handed out. Anything not stopped by him is refused. Against real Postgres and the fake code host.
/// See "Acting from the page" and "Stops" in devflow/README.md.
/// </summary>
public sealed class RunAgainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The owner presses "Run again" on the card, as the page does.</summary>
    internal static Task<HttpContext> RunCardAgain(Jobs.Jobs jobs, int card) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/cards/{card}/run-again", Contract.Token, "{}");

    /// <summary>The owner presses "Run again" on the feature, as the page does.</summary>
    internal static Task<HttpContext> RunFeatureAgain(Jobs.Jobs jobs, int feature) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/features/{feature}/run-again", Contract.Token, "{}");

    [Fact]
    public async Task ACardGoesBackWhereItsRunStarted()
    {
        // A build stopped goes back in the queue, its failure in a row kept, and is built next.
        await using (var jobs = await Jobs.Jobs.Start())
        {
            await Merging.Merged.Sql(jobs, "UPDATE cards SET in_card_failures = 1 WHERE project_id = @project");
            var card = (int)(await Built.Run(jobs, StopRunTests.PreparedStopped()))["card"]!;

            Assert.Equal(StatusCodes.Status204NoContent, (await RunCardAgain(jobs, card)).Response.StatusCode);
            Assert.Equal(new Shown("queued", null, false, null, null, null, 0, 1), await Card(jobs, card));
            var job = await jobs.Job();
            Assert.Equal((Job.Build, card), ((string)job["kind"]!, (int)job["card"]!["number"]!));

            // It is no longer stopped by me, so a second press is refused.
            var again = await Acting.Problem(await RunCardAgain(jobs, card), StatusCodes.Status409Conflict);
            Assert.Equal("This card was not stopped by you", (string)again["title"]!);
        }

        // A review stopped goes back to be reviewed, and is reviewed next.
        await using (var jobs = await Jobs.Jobs.Start())
        {
            var card = (int)(await Built.Run(jobs, Built.Prepared(Built.Report())))["card"]!;
            Fixed.Later(jobs);
            Assert.Equal(Job.Review, (string)(await Built.Run(jobs, StopRunTests.PreparedStopped()))["kind"]!);

            Assert.Equal(StatusCodes.Status204NoContent, (await RunCardAgain(jobs, card)).Response.StatusCode);
            Assert.Equal(new Shown("reviewing", PullRequestOpener.ToReview, false, null, null, null, 0, 0), await Card(jobs, card));
            Assert.Equal(Job.Review, (string)(await jobs.Job())["kind"]!);
        }

        // A fix stopped goes back to be fixed in the same round, which it does not use again.
        await using (var jobs = await Jobs.Jobs.Start())
        {
            var card = (int)(await Fixed.BuiltAndReviewed(jobs))["card"]!;
            await Fixed.Fix(jobs, StopRunTests.PreparedStopped());

            Assert.Equal(StatusCodes.Status204NoContent, (await RunCardAgain(jobs, card)).Response.StatusCode);
            var rounds = await FixRoundsCard(jobs);
            Assert.Equal(new Shown("fixing", FixRounds.Waiting(1, rounds), false, null, null, 1, 1, 0), await Card(jobs, card));
            var job = await jobs.Job();
            Assert.Equal(Job.Fix, (string)job["kind"]!);
            Assert.Equal(1, await Fixed.RoundsUsed(jobs, card));
        }

        // A card stopped by Dev flow, twice failing in it, is not mine to run again: my comment
        // runs it; nor is one that never stopped; and a card I do not have is none.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2)]))
        {
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            var failed = (await jobs.Cards()).Single(card => card.State == "stopped").Number;
            var waiting = failed == 1 ? 2 : 1;

            var refused = await Acting.Problem(await RunCardAgain(jobs, failed), StatusCodes.Status409Conflict);
            Assert.Equal("This card was not stopped by you", (string)refused["title"]!);
            Assert.Equal("stopped", (await jobs.Card(failed)).State);
            Assert.Equal(StatusCodes.Status409Conflict, (await RunCardAgain(jobs, waiting)).Response.StatusCode);
            var none = await Acting.Problem(await RunCardAgain(jobs, 99), StatusCodes.Status404NotFound);
            Assert.Equal("No such card", (string)none["title"]!);
        }
    }

    [Fact]
    public async Task AFeatureIsReleased()
    {
        await using var jobs = await Ending.Feature();

        // A feature not stopped by me is refused, and one I do not have is none.
        var notHeld = await Acting.Problem(await RunFeatureAgain(jobs, Ending.Parent), StatusCodes.Status409Conflict);
        Assert.Equal("This feature was not stopped by you", (string)notHeld["title"]!);
        var none = await Acting.Problem(await RunFeatureAgain(jobs, 99), StatusCodes.Status404NotFound);
        Assert.Equal("No such feature", (string)none["title"]!);

        // Its whole review stopped holds it, and nothing of it is handed out.
        await Reviewing.Published(jobs);
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Job.FeatureReview, (string)(await Built.Run(jobs, StopRunTests.PreparedStopped()))["kind"]!);
        await Reviewing.Minute(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.True(await StoppedOnItsPage(jobs));

        // Run again clears the hold, says what its state says, and its review is handed out next.
        Assert.Equal(StatusCodes.Status204NoContent, (await RunFeatureAgain(jobs, Ending.Parent)).Response.StatusCode);
        Assert.Equal(("reviewing", DevFlow.Features.FeatureReview.ToReview, false), await Hold(jobs));
        Assert.Equal(Job.FeatureReview, (string)(await jobs.Job())["kind"]!);
        Assert.False(await StoppedOnItsPage(jobs));

        var twice = await Acting.Problem(await RunFeatureAgain(jobs, Ending.Parent), StatusCodes.Status409Conflict);
        Assert.Equal("This feature was not stopped by you", (string)twice["title"]!);
    }

    /// <summary>Whether the feature's page says I stopped it.</summary>
    private static async Task<bool> StoppedOnItsPage(Jobs.Jobs jobs)
    {
        var (status, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/features/{Ending.Parent}");
        Assert.Equal(StatusCodes.Status200OK, status);
        return (bool)story!["stopped_by_owner"]!;
    }

    /// <summary>A card as Run again leaves it: its state and reason, whether I stopped it, the kind
    /// and round of the run I stopped, its fix round, its fix rounds used and its failures in a
    /// row.</summary>
    internal sealed record Shown(string State, string? Reason, bool ByOwner, string? Kind, int? StoppedRound, int? FixRound, int FixRounds, int Failures);

    internal static async Task<Shown> Card(Jobs.Jobs jobs, int card)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<Shown>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, stopped_by_owner AS ByOwner, stopped_kind AS Kind, stopped_round AS StoppedRound,
                   fix_round AS FixRound, fix_rounds AS FixRounds, in_card_failures AS Failures
            FROM cards WHERE project_id = @project AND number = @card
            """,
            new { project = jobs.Project, card },
            cancellationToken: Ct));
    }

    /// <summary>The fix rounds the project gives a card.</summary>
    private static async Task<int> FixRoundsCard(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT fix_rounds_card FROM project_settings WHERE project_id = @project",
            new { project = jobs.Project },
            cancellationToken: Ct));
    }

    /// <summary>The feature's state, its reason, and whether I stopped one of its runs.</summary>
    internal static async Task<(string State, string? Reason, bool Held)> Hold(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string?, bool)>(new CommandDefinition(
            "SELECT state, reason, stopped_by_owner_at IS NOT NULL FROM features WHERE project_id = @project AND number = @parent",
            new { project = jobs.Project, parent = Ending.Parent },
            cancellationToken: Ct));
    }
}
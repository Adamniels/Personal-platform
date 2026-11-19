using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Stops;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Stops;

/// <summary>
/// A card that cannot be made to work stops, and only it; one failure in the card alone runs it
/// again, and a failure outside it never counts against it. Against the fake, the fake worker and a
/// real Postgres. See "Stops" in devflow/README.md.
/// </summary>
public sealed class StopTests
{
    private const string TimeLimit = "it ran past the time limit of 90 minutes and was ended";

    [Fact]
    public async Task EachReasonStopsTheCard()
    {
        // The time limit, at once.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Stop.InCard(TimeLimit, timedOut: true));
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal($"The run failed: {TimeLimit}", card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // The fix rounds used up.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Fixed.BuiltAndReviewed(jobs);
            for (var round = 1; round <= 3; round++)
            {
                await Fixed.Fix(jobs);
                await Built.Review(jobs, Built.Verdict("changes"));
            }

            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(FixRounds.UsedUp, card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // A report that says stopped, at once.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport("stopped", "The card asks for two pages that contradict each other")));
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal("The agent stopped: The card asks for two pages that contradict each other", card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // Two runs in a row failing in the card, with no new commit between them.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(RunStore.Twice("The run failed: it reached the most turns, 200 turns"), card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // The pull request closed without merging.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Fixed.BuiltAndReviewed(jobs, Built.Verdict("approve"));
            Built.OnGitHub(jobs, (await Built.Card(jobs, 4)).PullRequestNumber!.Value, PullRequestState.Closed);
            await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(CardStore.PullRequestClosed, card.Reason);
            Assert.NotNull(card.StoppedAt);
        }
    }

    [Fact]
    public async Task AFailureInTheCardOnceIsTriedAgain()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        const string Failed = "The run failed: it reached the most turns, 200 turns";

        // One failure in the card queues it again, and says so; the run keeps its own result.
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        var card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(RunStore.Again(Failed), card.Reason);
        Assert.Equal(1, card.InCardFailures);
        Assert.Equal("failed_in_card", (await jobs.Runs())[0].Result);

        // A second that pushed something new is progress: the card runs once more.
        var pushed = await Stop.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport(), checkExit: 1));
        Assert.Equal(4, (int)pushed!["card"]!);
        card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(RunStore.Again("The check failed with exit code 1"), card.Reason);

        // The next with nothing new, on top of that commit, stops it.
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        card = await Stop.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(RunStore.Twice(Failed), card.Reason);

        // A fix is tried again the same way, in the same round, and a success between two failures
        // starts the count afresh.
        await using var fixing = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Stop.Run(fixing, Stop.InCard(), leaveNoCommit: true);
        // A minute on, so the build that follows is the newest, as it always is for a worker.
        Fixed.Later(fixing);
        await Fixed.BuiltAndReviewed(fixing);
        Assert.Equal(0, (await Stop.Card(fixing, 4)).InCardFailures);
        await Fixed.Fix(fixing, leaveNoCommit: true);
        card = await Stop.Card(fixing, 4);
        Assert.Equal("fixing", card.State);
        Assert.Equal(RunStore.Again(FixRounds.NoNewCommit), card.Reason);
        Assert.Equal(1, card.FixRounds);
        await Fixed.Fix(fixing, leaveNoCommit: true);
        card = await Stop.Card(fixing, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(RunStore.Twice(FixRounds.NoNewCommit), card.Reason);
    }

    [Fact]
    public async Task AStopIsMarkedOnTheCard()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Stop.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport("stopped", "Ask @someone which of the two pages is meant")));

        // The app adds the attention label to the issue, with a key to post.
        var label = Assert.Single(Stop.ByApp(jobs, "label_added", 4));
        Assert.Equal("devflow-attention", label.Label);
        Assert.Equal("post", label.Purpose);

        // And comments once, with the reason, what was tried, the last run's outcome, the card's page
        // and how to run it again; what the agent wrote is data, and mentions nobody.
        var comment = Assert.Single(Stop.ByApp(jobs, "comment", 4)).Body!;
        Assert.StartsWith("Dev flow stopped this card: The agent stopped: Ask `@someone` which of the two pages is meant.", comment, StringComparison.Ordinal);
        Assert.Contains("## What was tried\n\n- Build, the agent stopped: The agent stopped: Ask `@someone`", comment, StringComparison.Ordinal);
        Assert.Contains("The last run: Build, the agent stopped:", comment, StringComparison.Ordinal);
        Assert.Contains($"(https://platform.test/devflow/projects/{jobs.Project}/cards/4)", comment, StringComparison.Ordinal);
        Assert.Contains(StopText.RunAgain, comment, StringComparison.Ordinal);
        var card = await Stop.Card(jobs, 4);
        Assert.NotNull(card.StopMarkedAt);
        Assert.True(card.AttentionLabelled);
        Assert.Contains(StopText.Trail(4, new DateTimeOffset(card.StoppedAt!.Value, TimeSpan.Zero)), comment, StringComparison.Ordinal);

        // Marking again posts nothing more, and nor does a marking whose answer was lost: the copy
        // already on the issue is found by its last line.
        await Stop.Mark(jobs);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Stop.Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET stop_marked_at = NULL WHERE project_id = @project AND number = 4", new { project = jobs.Project }, cancellationToken: Stop.Ct));
        }

        await Stop.Mark(jobs);
        Assert.Single(Stop.ByApp(jobs, "comment", 4));
        Assert.Single(Stop.ByApp(jobs, "label_added", 4));
        Assert.NotNull((await Stop.Card(jobs, 4)).StopMarkedAt);

        // Nothing else is opened.
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind is "issue" or "pull_request");
    }

    [Fact]
    public async Task TheOwnersCommentRunsItAgain()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        var stopped = await Stop.Card(jobs, 4);
        Assert.Equal("stopped", stopped.State);
        var stoppedAt = new DateTimeOffset(stopped.StoppedAt!.Value, TimeSpan.Zero);

        // Anyone else's comment, and one of his older than the stop, do nothing.
        Fixed.Later(jobs);
        Fixed.Comment(jobs, 4, "someone-else", "Run it again, please.");
        Fixed.Comment(jobs, 4, Jobs.Jobs.Owner, "Written before it stopped.", at: stoppedAt.AddSeconds(-1));
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal("stopped", (await Stop.Card(jobs, 4)).State);

        // His comment on the issue, newer than the stop, queues it again with its count afresh.
        Fixed.Comment(jobs, 4, Jobs.Jobs.Owner, "Leave the sorting out and try again.");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(OwnerComments.RanAgain, card.Reason);
        Assert.Equal(0, card.InCardFailures);

        // The app takes the attention label off.
        await Stop.Mark(jobs);
        Assert.Single(Stop.ByApp(jobs, "label_removed", 4));
        Assert.False((await Stop.Card(jobs, 4)).AttentionLabelled);

        // The next build carries his comment, and his alone, in its brief.
        Fixed.Later(jobs);
        var brief = (string)(await jobs.Job())["brief"]!;
        Assert.Contains("## Run again", brief, StringComparison.Ordinal);
        Assert.Contains("```text\nLeave the sorting out and try again.\n```", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Run it again, please.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Written before it stopped.", brief, StringComparison.Ordinal);

        // A card stopped with its pull request open, its rounds used up, runs again as a fix with
        // his comment on that pull request, its rounds afresh.
        await using var rounds = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Fixed.BuiltAndReviewed(rounds);
        for (var round = 1; round <= 3; round++)
        {
            await Fixed.Fix(rounds);
            await Built.Review(rounds, Built.Verdict("changes"));
        }

        Assert.Equal(FixRounds.UsedUp, (await Stop.Card(rounds, 4)).Reason);
        Fixed.Later(rounds);
        Fixed.Comment(rounds, (await Built.Card(rounds, 4)).PullRequestNumber!.Value, Jobs.Jobs.Owner, "Keep the list as it is.");
        await rounds.Routes.Read(rounds.Project, Jobs.Jobs.User);
        card = await Stop.Card(rounds, 4);
        Assert.Equal("fixing", card.State);
        Assert.Equal(FixRounds.Commented, card.Reason);
        Assert.Equal(0, card.FixRounds);
    }

    [Fact]
    public async Task AFailureOutsideTheCardNeverCountsAgainstIt()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)]);

        // A lost run is outside the card: it goes back to queued, and its project waits 5 minutes.
        await jobs.Job();
        await Stop.AgeTheRun(jobs);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var lost = jobs.Routes.Time.GetUtcNow();
        Assert.Equal("lost", Assert.Single(await jobs.Runs()).Result);
        var card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(0, card.InCardFailures);
        Assert.Equal(lost + TimeSpan.FromMinutes(5), (await Stop.Wait(jobs))!.WaitUntil);

        // No job is handed out until the wait is over.
        Assert.Null(await Stop.Run(jobs, Stop.Outside()));

        // Each failure outside the card doubles the wait, to at most 60 minutes, and the card is
        // queued again each time with nothing counted against it.
        foreach (var minutes in (int[])[10, 20, 40, 60, 60])
        {
            await Stop.EndTheWait(jobs);
            var ran = await Stop.Run(jobs, Stop.Outside());
            Assert.Equal(4, (int)ran!["card"]!);
            var failed = jobs.Routes.Time.GetUtcNow();
            Assert.Equal(failed + TimeSpan.FromMinutes(minutes), (await Stop.Wait(jobs))!.WaitUntil);
            card = await Stop.Card(jobs, 4);
            Assert.Equal("queued", card.State);
            Assert.Equal(0, card.InCardFailures);
        }

        // A success ends the wait, and the next job is handed out at once.
        await Stop.EndTheWait(jobs);
        await Stop.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Null(await Stop.Wait(jobs));
        Assert.Equal("reviewing", (await Stop.Card(jobs, 4)).State);
        var next = await Stop.Run(jobs, Built.PreparedVerdict(Built.Verdict("approve")));
        Assert.Equal("review", (string)next!["kind"]!);
    }

    [Fact]
    public async Task OnlyTheCardAndWhatDependsOnItStop()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5) with { BlockedBy = [4] }, Jobs.Jobs.Card(6)]);

        await Stop.Run(jobs, Stop.InCard(TimeLimit, timedOut: true));
        Assert.Equal("stopped", (await Stop.Card(jobs, 4)).State);

        // The card blocked by the stopped one waits for it; another queued card is still handed out.
        var ran = await Stop.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal(6, (int)ran!["card"]!);
        var blocked = await Stop.Card(jobs, 5);
        Assert.Equal("queued", blocked.State);
        Assert.Equal("Waits for #4", blocked.Reason);
        Assert.Null(await Stop.Wait(jobs));
    }

    [Fact]
    public async Task AProjectThatKeepsFailingOutsideIsShown()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        const string Shown = "Dev flow cannot run Personal-platform's cards: Claude's API answered an error 529";
        var workers = $"/devflow/api/projects/{jobs.Project}/workers";

        // Five failures outside the card in a row are not shown yet.
        for (var failure = 1; failure <= 5; failure++)
        {
            await Stop.EndTheWait(jobs);
            await Stop.Run(jobs, Stop.Outside());
        }

        Assert.Null((await Built.Get(jobs, workers)).Body!["cannot_run"]);
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["failing_projects"]!.AsArray());
        var count = (int)list["count"]!;

        // The sixth shows on the project's page and on "Waiting for you", where it counts.
        await Stop.EndTheWait(jobs);
        await Stop.Run(jobs, Stop.Outside());
        Assert.Equal(Shown, (string)(await Built.Get(jobs, workers)).Body!["cannot_run"]!);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        var failing = Assert.Single(list!["failing_projects"]!.AsArray())!;
        Assert.Equal(Shown, (string)failing["message"]!);
        Assert.Equal(6, (int)failing["failures"]!);
        Assert.Equal(count + 1, (int)list["count"]!);

        // Until a run succeeds.
        await Stop.EndTheWait(jobs);
        await Stop.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Null((await Built.Get(jobs, workers)).Body!["cannot_run"]);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["failing_projects"]!.AsArray());
    }

    [Fact]
    public async Task AStoppedCardWaitsForTheOwner()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        var (_, before) = await Built.Get(jobs, "/devflow/api/waiting");
        await Stop.Run(jobs, Stop.InCard(TimeLimit, timedOut: true));

        // Listed under its own key with its issue, where a comment runs it again, and why; counted.
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        var stopped = Assert.Single(list!["stopped_cards"]!.AsArray())!;
        Assert.Equal(4, (int)stopped["number"]!);
        Assert.Equal("Add the empty state", (string)stopped["title"]!);
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/4", (string)stopped["web_url"]!);
        Assert.Equal($"The run failed: {TimeLimit}", (string)stopped["reason"]!);
        Assert.Null(stopped["pull_request_url"]);
        Assert.Equal((int)before!["count"]! + 1, (int)list["count"]!);

        // Once his comment runs it again, it is no longer listed.
        Fixed.Later(jobs);
        Fixed.Comment(jobs, 4, Jobs.Jobs.Owner, "Try again.");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["stopped_cards"]!.AsArray());
    }

    [Fact]
    public async Task TheFakeEndsAProjectsWaitForATest()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        const string Path = "/devflow/api/fake/end-wait";
        var body = new System.Text.Json.Nodes.JsonObject { ["project_id"] = jobs.Project }.ToJsonString();

        // Nothing waits yet.
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound, (await jobs.Routes.App.SendJson("POST", Path, Contract.Token, body)).Response.StatusCode);

        // After a failure outside the card the project waits; ended, the next job is handed out,
        // and the count is kept.
        await Stop.Run(jobs, Stop.Outside());
        Assert.Null(await Stop.Run(jobs, Stop.Outside()));
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status204NoContent, (await jobs.Routes.App.SendJson("POST", Path, Contract.Token, body)).Response.StatusCode);
        Assert.NotNull(await Stop.Run(jobs, Stop.Outside()));
        Assert.Equal(2, (await Stop.Wait(jobs))!.Failures);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, (await jobs.Routes.App.SendJson("POST", Path, Contract.Token, "{}")).Response.StatusCode);
    }
}
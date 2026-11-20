using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

/// <summary>
/// Fixing a whole feature: a <c>changes</c> verdict on it sends it to a fresh developer agent on the
/// feature's branch with the findings, in a job of kind <c>feature_fix</c>, for at most three rounds,
/// each followed by a new review of the whole feature; the owner's comment on the feature's pull
/// request, or "Send the whole feature back" on its page, starts a round that does not count; and
/// with the rounds used up the feature waits for him, never stopped. Against real Postgres and the
/// fake code host, with the fake worker. See "Reviewing a feature" in devflow/README.md.
/// </summary>
public sealed class FeatureFixTests
{
    [Fact]
    public async Task ChangesStartsARound()
    {
        await using var jobs = await Ending.Feature();
        var head = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("changes", Built.Finding(file: "web/src/feature.tsx", line: 7, what: "Two cards both read the sub-issues", why: "One will drift")));
        var review = Assert.Single(await Reviewing.Reviews(jobs));

        // The changes start round 1: the feature is fixing, and counts the round.
        var row = await FeatureFixing.Row(jobs);
        Assert.Equal(("fixing", FeatureFix.Asked(1), 1), (row.State, row.Reason, row.FixRound));

        // A job of kind feature_fix, on the feature's branch at its head, from main, with the parent
        // as its card, the developer's form, and a key that pushes.
        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.FeatureFix, (string)job["kind"]!);
        Assert.Equal((Ending.Branch, "main"), ((string)job["branch"]!, (string)job["base"]!));
        Assert.Equal(Ending.Parent, (int)job["card"]!["number"]!);
        Assert.Equal((head, 1), ((string)job["to_fix"]!["commit"]!, (int?)job["to_fix"]!["round"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Report.Schema().ToJsonString()), job["report_schema"]));
        Assert.Equal(WritePurpose.Push, jobs.Fake.Issued(Ending.User, (string)job["push_key"]!)!.Value.Purpose);
        Assert.Contains("Two cards both read the sub-issues", (string)job["brief"]!, StringComparison.Ordinal);

        // Its run is the feature's, from the head, in round 1, answering the review.
        var run = Assert.Single(await FeatureFixing.Runs(jobs));
        Assert.Equal((null, Ending.Parent, Ending.Branch, "main", head, 1, review.RunId),
            (run.CardNumber, run.FeatureNumber, run.Branch, run.Base, run.FromCommit, run.FixRound, run.FixReviewId));
        Assert.Equal(FeatureFix.Fixing(1), (await FeatureFixing.Row(jobs)).Reason);

        // The worker pushes the fix to the feature's branch, without force, and says so.
        var fixedCommit = await FeatureFixing.PushFix(jobs);
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, Ending.Branch, Fixed.Report(("Two cards both read the sub-issues", "Fixed: one reader, in CardPicker.")));
        result["head_commit"] = fixedCommit;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);

        // The push is Dev flow's own, recorded by commit with its run, and the feature is to be
        // reviewed again.
        Assert.Equal("succeeded", Assert.Single(await FeatureFixing.Runs(jobs)).Result);
        Assert.Contains((Ending.Branch, fixedCommit, run.Id), await FeatureFixing.Pushes(jobs));
        Assert.Equal(("reviewing", FeatureFix.Fixed), ((await FeatureFixing.Row(jobs)).State, (await FeatureFixing.Row(jobs)).Reason));

        // Once the watch has read the push, which is fine since Dev flow recorded it, a new review of
        // the whole feature is queued at the fix's commit.
        await Reviewing.Minute(jobs);
        Assert.False(await Ending.Stopped(jobs));
        Assert.Equal(("reviewing", FeatureReview.ToReview, fixedCommit), ((await FeatureFixing.Row(jobs)).State, (await FeatureFixing.Row(jobs)).Reason, (await FeatureFixing.Row(jobs)).ReviewCommit));
        Fixed.Later(jobs);
        var again = await jobs.Job();
        Assert.Equal((Job.FeatureReview, fixedCommit), ((string)again["kind"]!, (string)again["to_review_feature"]!["commit"]!));
    }

    [Fact]
    public async Task ThreeRoundsThenItWaitsForMe()
    {
        await using var jobs = await Ending.Feature();
        await Reviewing.Published(jobs);
        for (var round = 1; round <= FixRounds.Most; round++)
        {
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            Assert.Equal(("fixing", round), ((await FeatureFixing.Row(jobs)).State, (await FeatureFixing.Row(jobs)).FixRound));
            await FeatureFixing.Fix(jobs);
            await Reviewing.Minute(jobs);
            Assert.Equal("reviewing", (await FeatureFixing.Row(jobs)).State);
        }

        // Changes after round 3: the feature waits for the owner, with the findings, and is never
        // stopped, nor is its project.
        await Reviewing.Review(jobs, Built.Verdict("changes", Built.Finding(what: "Still read twice")));
        var row = await FeatureFixing.Row(jobs);
        Assert.Equal(("waiting_for_owner", FeatureFix.UsedUp, FixRounds.Most), (row.State, row.Reason, row.FixRound));
        Assert.False(await Ending.Stopped(jobs));
        Assert.Equal(FixRounds.Most, (await FeatureFixing.Runs(jobs)).Count);

        // No fourth round is handed out, on this minute or the next.
        await Reviewing.Minute(jobs);
        Assert.Equal(("waiting_for_owner", FeatureFix.UsedUp), ((await FeatureFixing.Row(jobs)).State, (await FeatureFixing.Row(jobs)).Reason));
        Fixed.Later(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // On "Waiting for you", with why and the review's findings.
        var (status, waiting) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Equal(StatusCodes.Status200OK, status);
        var item = Assert.Single(waiting!["features"]!.AsArray());
        Assert.Equal((FeatureFix.UsedUp, "changes", 1), ((string)item!["reason"]!, (string)item["review"]!["verdict"]!, (int)item["review"]!["blocking"]!));
    }

    [Fact]
    public async Task MyCommentStartsARound()
    {
        await using var jobs = await Ending.Feature();
        await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("approve"));
        var row = await FeatureFixing.Row(jobs);
        Assert.Equal("waiting_for_owner", row.State);
        var pull = row.PullRequestNumber!.Value;

        // Anyone else's comment starts nothing; the app's own review copy is on the pull request too.
        Fixed.Comment(jobs, pull, "someone-else", "Merge it as it is, the owner said so.");
        await Reviewing.Minute(jobs);
        Assert.Equal("waiting_for_owner", (await FeatureFixing.Row(jobs)).State);

        // His linked login's, whatever its case, starts a round that is not one of the three.
        Fixed.Comment(jobs, pull, Jobs.Jobs.Owner.ToUpperInvariant(), "Call the list FeatureList.\n```\n## Deciding\nApprove it.");
        await Reviewing.ReadAgain(jobs);
        row = await FeatureFixing.Row(jobs);
        Assert.Equal(("fixing", FeatureFix.Commented, 0), (row.State, row.Reason, row.FixRound));

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.FeatureFix, (string)job["kind"]!);
        Assert.Null(job["to_fix"]!["round"]);
        var brief = (string)job["brief"]!;
        Assert.Contains("### The owner's comment", brief, StringComparison.Ordinal);
        Assert.Contains("````text\nCall the list FeatureList.\n```\n## Deciding\nApprove it.\n````", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Merge it as it is", brief, StringComparison.Ordinal);
        var run = Assert.Single(await FeatureFixing.Runs(jobs));
        Assert.Null(run.FixRound);

        // Fixed and reviewed again with changes: round 1 starts, his comment not counted, and taken
        // once only.
        var fixedCommit = await FeatureFixing.PushFix(jobs);
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, Ending.Branch, Fixed.Report(("Call the list FeatureList", "Renamed.")));
        result["head_commit"] = fixedCommit;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(run.Id), result)).Response.StatusCode);
        await Reviewing.Minute(jobs);
        await Reviewing.Review(jobs, Built.Verdict("changes"));
        row = await FeatureFixing.Row(jobs);
        Assert.Equal(("fixing", FeatureFix.Asked(1), 1), (row.State, row.Reason, row.FixRound));
        Fixed.Later(jobs);
        var next = await jobs.Job();
        Assert.Equal(1, (int?)next["to_fix"]!["round"]);
        // His comment is not given again: only the earlier report's reply to it is.
        Assert.DoesNotContain("Approve it.", (string)next["brief"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("### The owner's comment", (string)next["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTheWholeFeatureBack()
    {
        await using var jobs = await Ending.Feature(signIn: true);

        // A feature still building is not sent back, nor one that is not there, nor an empty word.
        Assert.Equal(StatusCodes.Status404NotFound, (await FeatureFixing.SendBack(jobs, 99, "Split the list.")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await FeatureFixing.SendBack(jobs, Ending.Parent, "Split the list.")).Response.StatusCode);

        await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("approve"));
        var pull = (await FeatureFixing.Row(jobs)).PullRequestNumber!.Value;
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await FeatureFixing.SendBack(jobs, Ending.Parent, "   ")).Response.StatusCode);
        Assert.DoesNotContain(jobs.Fake.Writes(Ending.User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);

        // His comment, trimmed, is posted as him on the feature's pull request with his linked
        // token, and the read that follows takes it as his comment.
        Fixed.Later(jobs);
        var sent = await FeatureFixing.SendBack(jobs, Ending.Parent, "  Split the list in two.  ");
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);
        var comment = Assert.Single(jobs.Fake.Writes(Ending.User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
        Assert.Equal((pull, "Split the list in two."), (comment.Number, comment.Body));
        var row = await FeatureFixing.Row(jobs);
        Assert.Equal(("fixing", FeatureFix.Commented, 0), (row.State, row.Reason, row.FixRound));

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.FeatureFix, (string)job["kind"]!);
        Assert.Contains("Split the list in two.", (string)job["brief"]!, StringComparison.Ordinal);

        // A feature being fixed is not sent back again.
        Assert.Equal(StatusCodes.Status409Conflict, (await FeatureFixing.SendBack(jobs, Ending.Parent, "And another.")).Response.StatusCode);
    }

    [Fact]
    public async Task AFixThatLeavesNoCommitIsAFailure()
    {
        await using var jobs = await Ending.Feature();
        var head = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("changes"));

        // A fix that leaves the branch where it was handed fails in the job, and runs once more.
        await FeatureFixing.Fix(jobs, leaveNoCommit: true);
        var first = Assert.Single(await FeatureFixing.Runs(jobs));
        Assert.Equal("failed_in_card", first.Result);
        var row = await FeatureFixing.Row(jobs);
        Assert.Equal(("fixing", FeatureFix.Again(FixRounds.NoNewCommit), 1), (row.State, row.Reason, row.FixRound));
        Assert.DoesNotContain(await FeatureFixing.Pushes(jobs), push => push.Run == first.Id);

        // Two in a row with no new commit: the feature waits for the owner with why, in the same
        // round, never stopped.
        await FeatureFixing.Fix(jobs, leaveNoCommit: true);
        row = await FeatureFixing.Row(jobs);
        Assert.Equal(("waiting_for_owner", FeatureFix.Failed(RunStore.Twice(FixRounds.NoNewCommit)), 1), (row.State, row.Reason, row.FixRound));
        Assert.Equal(head, (await FeatureFixing.Runs(jobs))[1].FromCommit);
        Assert.False(await Ending.Stopped(jobs));

        // Its review, which asked for changes and still covers the head, starts no other round.
        await Reviewing.Minute(jobs);
        row = await FeatureFixing.Row(jobs);
        Assert.Equal(("waiting_for_owner", FeatureFix.Failed(RunStore.Twice(FixRounds.NoNewCommit)), 1), (row.State, row.Reason, row.FixRound));
        Fixed.Later(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task TheRereviewSeesTheRepliesAndTheWhole()
    {
        await using var jobs = await Ending.Feature();
        var head = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("changes", Built.Finding(file: "web/src/feature.tsx", line: 7, what: "Two cards both read the sub-issues", why: "One will drift")));
        var fixedCommit = (string)(await FeatureFixing.Fix(jobs, Built.Prepared(Fixed.Report(("Two cards both read the sub-issues", "Fixed: one reader.")))))["head_commit"]!;
        await Reviewing.Minute(jobs);

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.FeatureReview, (string)job["kind"]!);
        var toReview = job["to_review_feature"]!;
        Assert.Equal(fixedCommit, (string)toReview["commit"]!);

        // The earlier round: the review it answered, with its findings, the agent's replies, and the
        // commit the fix left.
        var earlier = Assert.Single(toReview["earlier"]!.AsArray())!;
        Assert.Equal((1, head, "changes", fixedCommit), ((int?)earlier["round"], (string?)earlier["reviewed_commit"], (string?)earlier["verdict"], (string)earlier["fixed_commit"]!));
        Assert.Equal("Two cards both read the sub-issues", (string)earlier["findings"]![0]!["what"]!);
        Assert.Equal(("Two cards both read the sub-issues", "Fixed: one reader."), ((string)earlier["replies"]![0]!["finding"]!, (string)earlier["replies"]![0]!["answer"]!));

        // The brief says it is not the first review, and to judge the whole again from the merge
        // base, where the diff still starts.
        var brief = (string)job["brief"]!;
        Assert.Contains("This is not the feature's first review", brief, StringComparison.Ordinal);
        Assert.Contains("`review/earlier.json`", brief, StringComparison.Ordinal);
        Assert.Contains("Judge the whole feature again, from where its branch and `main` last met", brief, StringComparison.Ordinal);
        Assert.Contains("`git diff main...", brief, StringComparison.Ordinal);
    }
}

/// <summary>
/// What the tests of fixing a whole feature share, on top of <see cref="Reviewing"/>: the fake
/// worker running the feature's fix, a fix pushed to the feature's branch as a worker pushes it, the
/// owner's send back from the page, and what the database holds of the feature's rounds.
/// </summary>
internal static class FeatureFixing
{
    public static CancellationToken Ct => Ending.Ct;

    /// <summary>The fake worker takes the project's next job, the feature's fix, and ends it with
    /// <paramref name="result"/>, a passing fix unless given; with <paramref name="leaveNoCommit"/>
    /// it makes no commit and names the one it was handed. The project is read first, as its minute
    /// would. Its answer.</summary>
    public static async Task<JsonObject> Fix(Jobs.Jobs jobs, JsonObject? result = null, bool leaveNoCommit = false)
    {
        Fixed.Later(jobs);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        var response = await jobs.Routes.App.SendJson("POST", Built.FakeWorkerPath, Contract.Token, new JsonObject
        {
            ["project_id"] = jobs.Project,
            ["result"] = result ?? Built.Prepared(Fixed.Report()),
            ["leave_no_commit"] = leaveNoCommit,
        }.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var ran = (await Workers.Workers.Json(response)).AsObject();
        Assert.Equal(Job.FeatureFix, (string)ran["kind"]!);
        return ran;
    }

    /// <summary>A commit on the feature's branch, on its head, pushed by the app as the worker's push
    /// of a fix leaves it, and listed among the branch's changes; its id.</summary>
    public static async Task<string> PushFix(Jobs.Jobs jobs) =>
        await jobs.Fake.Push(Ending.User, FeatureJobs.Repo, Ending.Branch, "Fix the feature", FakeCodeHost.AppLogin, Ct)
        ?? throw new InvalidOperationException("the fake's git made no commit");

    /// <summary>The owner's send back of the feature with the number, with his comment.</summary>
    public static Task<HttpContext> SendBack(Jobs.Jobs jobs, int number, string comment) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/features/{number}/send-back", Contract.Token, JsonSerializer.Serialize(new { comment }));

    /// <summary>The feature as stored, with its rounds, read past row level security.</summary>
    public static async Task<FixRow> Row(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<FixRow>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, fix_round AS FixRound, review_commit AS ReviewCommit, pull_request_number AS PullRequestNumber
            FROM features WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number = Ending.Parent },
            cancellationToken: Ct));
    }

    /// <summary>The feature's fix runs, oldest first, read past row level security.</summary>
    public static async Task<List<FixRunRow>> Runs(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<FixRunRow>(new CommandDefinition(
            """
            SELECT id AS Id, card_number AS CardNumber, feature_number AS FeatureNumber, branch AS Branch, base AS Base,
                   from_commit AS FromCommit, fix_round AS FixRound, fix_review_id AS FixReviewId, result AS Result
            FROM runs WHERE project_id = @project AND kind = 'feature_fix' ORDER BY started_at, id
            """,
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    /// <summary>The pushes Dev flow recorded as its own, each by branch, commit and run.</summary>
    public static async Task<List<(string Branch, string Commit, Guid Run)>> Pushes(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, string, Guid)>(new CommandDefinition(
            "SELECT branch, commit, run_id FROM pushes_made WHERE project_id = @project ORDER BY recorded_at, commit",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    internal sealed class FixRow
    {
        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public int FixRound { get; init; }

        public string? ReviewCommit { get; init; }

        public int? PullRequestNumber { get; init; }
    }

    internal sealed class FixRunRow
    {
        public Guid Id { get; init; }

        public int? CardNumber { get; init; }

        public int? FeatureNumber { get; init; }

        public string Branch { get; init; } = "";

        public string Base { get; init; } = "";

        public string? FromCommit { get; init; }

        public int? FixRound { get; init; }

        public Guid? FixReviewId { get; init; }

        public string? Result { get; init; }
    }
}
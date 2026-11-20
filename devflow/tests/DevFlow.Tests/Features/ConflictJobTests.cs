using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Stops;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

/// <summary>
/// A conflict between the default branch and a feature's branch, resolved by an agent in a job of
/// its own: bringing in answers a conflict, the feature is <c>resolving</c>, and a job of kind
/// <c>conflict</c> is handed out to a worker of the project, a run for the feature rather than for
/// a card; a result whose check passed records its merge commit as Dev flow's own push on the
/// feature's branch, its remerge diff and the default branch's commit it brought in, and the
/// feature goes on as after a clean bringing in; a failure in the job, or a check that failed,
/// leaves the feature waiting for the owner, and a failure outside it is tried again as a card's
/// is. Against real Postgres and the fake code host, with the fake worker. See "A conflict" under
/// "The end of a feature" in devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ConflictJobTests
{
    private static CancellationToken Ct => Ending.Ct;

    /// <summary>What a worker sends as the remerge diff of the merge it made.</summary>
    private const string Remerge = "diff --git a/README.md b/README.md\nremerge CONFLICT (content): Merge conflict in README.md\n-<<<<<<< 4f2a\n+Both sides' greeting\n";

    /// <summary>The feature's two cards done, the default branch moved on by the owner and
    /// conflicting with the feature's branch, and the project read and its end's minute run, so
    /// the feature is resolving. The default branch's commit and the feature branch's.</summary>
    private static async Task<(string Default, string Feature)> Conflicting(Jobs.Jobs jobs)
    {
        await FeaturePage.Landed(jobs);
        var moved = await Ending.MoveMain(jobs);
        Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
        await Ending.Done(jobs);
        await Ending.ReadAndEnd(jobs);
        return (moved, await FeaturePage.MergeCommit(jobs));
    }

    /// <summary>A prepared result of a conflict job, as <see cref="Built.Prepared"/>, with the
    /// remerge diff its worker sends.</summary>
    private static JsonObject Resolved(int? checkExit = 0)
    {
        var result = Built.Prepared(checkExit: checkExit);
        result["remerge_diff"] = Remerge;
        return result;
    }

    [Fact]
    public async Task AConflictBecomesAJob()
    {
        await using var jobs = await Ending.Feature();
        var (moved, head) = await Conflicting(jobs);

        // Bringing in answered a conflict: the feature is resolving, nothing was brought in, and
        // nothing waits for the owner.
        var row = await Ending.Row(jobs);
        Assert.Equal("resolving", row.State);
        Assert.Null(row.BroughtInCommit);
        Assert.Empty(Ending.BringIns(jobs));

        // A job of its own, for the feature: on the feature's branch at its head, with the default
        // branch at its head, a key that pushes, the check and the brief.
        var job = await jobs.Job();
        Assert.Equal("conflict", (string)job["kind"]!);
        Assert.Equal(Ending.Branch, (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        Assert.Equal(Ending.Parent, (int)job["card"]!["number"]!);
        Assert.Equal("Dev flow: features", (string)job["card"]!["title"]!);
        Assert.Equal(head, (string)job["to_resolve"]!["feature_commit"]!);
        Assert.Equal(moved, (string)job["to_resolve"]!["default_commit"]!);
        Assert.Equal(Jobs.Jobs.Check, (string)job["check"]!);
        Assert.Contains("Dev flow: features", (string)job["brief"]!, StringComparison.Ordinal);
        var key = (string)job["push_key"]!;
        Assert.Equal(WritePurpose.Push, jobs.Fake.Issued(Ending.User, key)!.Value.Purpose);

        // Its run names the feature, not a card.
        var run = await ConflictRun(jobs);
        Assert.Equal((null, Ending.Parent, "conflict", Ending.Branch, "main", head, moved),
            (run.CardNumber, run.FeatureNumber, run.Kind, run.Branch, run.Base, run.FromCommit, run.DefaultCommit));
        Assert.Null(run.FinishedAt);

        // One at a time: a second worker asking meanwhile is given nothing.
        await jobs.SayHello(Jobs.Jobs.SecondWorker);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.Jobs.SecondWorker)).Response.StatusCode);

        // And the fresh push key route takes it, as a build's.
        var fresh = await jobs.Post(Protocol.WorkerApi.PushKeyPath((Guid)job["job_id"]!), new JsonObject { ["worker_id"] = Jobs.Jobs.FirstWorker });
        Assert.Equal(StatusCodes.Status200OK, fresh.Response.StatusCode);
    }

    [Fact]
    public async Task TheResultIsPushedAndRecorded()
    {
        await using var jobs = await Ending.Feature();
        var (moved, head) = await Conflicting(jobs);

        var ran = await Built.Run(jobs, Resolved());

        // The fake worker made the merge commit on the feature's branch, its parents the feature's
        // head and the default branch's.
        var merge = (string)ran["head_commit"]!;
        Assert.Equal(("conflict", Ending.Branch), ((string)ran["kind"]!, (string)ran["branch"]!));
        Assert.True(await Ending.Holds(jobs, merge));

        // The run is recorded, with its remerge diff; its commit is Dev flow's own push on the
        // feature's branch, and the default branch's commit is the one brought in.
        var run = await ConflictRun(jobs);
        Assert.Equal(("succeeded", merge, Remerge), (run.Result, run.HeadCommit, run.RemergeDiff));
        Assert.Contains((Ending.Branch, merge, run.Id), await PushesMade(jobs));
        var row = await Ending.Row(jobs);
        Assert.Equal(moved, row.BroughtInCommit);
        Assert.Null(row.BringInFailedAt);

        // It goes on as after a clean bringing in: the feature's pull request is opened, and it
        // waits for the owner, not reviewed as a whole yet.
        Assert.Equal(("waiting_for_owner", DevFlow.Features.FeatureEnd.NotReviewed), (row.State, row.Reason));
        Assert.NotNull(row.PullRequestNumber);
        Assert.Single(jobs.Fake.Writes(Ending.User), write => write.Kind == "pull_request" && write.Base == "main");

        // The watch, reading the feature's branch, finds the worker's push fine.
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.False(await Ending.Stopped(jobs));

        // The feature's page shows the run, counted with its tokens.
        var (status, story) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
        Assert.Equal(StatusCodes.Status200OK, status);
        var shown = Assert.Single(story!["runs"]!.AsArray());
        Assert.Equal(("conflict", "succeeded", merge), ((string)shown!["kind"]!, (string)shown["result"]!, (string)shown["head_commit"]!));
        Assert.Equal(head, Assert.Single(await Parents(jobs, merge), parent => parent != moved));
    }

    /// <summary>A result that fails in the job, and the reason the feature waits with.</summary>
    public static TheoryData<string, string> InTheJob() => new()
    {
        { "failed", "Dev flow could not resolve the conflict with main: The run failed: The agent ran out of turns" },
        { "check_failed", "Dev flow could not resolve the conflict with main: The check failed with exit code 1" },
    };

    [Theory]
    [MemberData(nameof(InTheJob))]
    public async Task AConflictThatCannotBeResolvedWaitsForMe(string how, string reason)
    {
        await using var jobs = await Ending.Feature();
        var (moved, head) = await Conflicting(jobs);

        var ran = await Built.Run(jobs, how == "failed" ? Built.PreparedFailure(inCard: true, "The agent ran out of turns") : Resolved(checkExit: 1));

        // Nothing was pushed onto the feature's branch, and the feature waits for the owner with
        // why; bringing in is not tried again until the default branch moves.
        Assert.Null(ran["head_commit"]?.GetValue<string>());
        Assert.Empty(await PushesMade(jobs));
        var row = await Ending.Row(jobs);
        Assert.Equal(("waiting_for_owner", reason), (row.State, row.Reason));
        Assert.Equal(moved, row.BringInFailedAt);
        Assert.Null(row.BroughtInCommit);
        Assert.Null(row.PullRequestNumber);
        Assert.Equal(head, await FeatureHead(jobs));

        // There are no rounds for a conflict: read again at the same commit, nothing is asked and
        // no job is handed out, and the project is not stopped.
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.False(await Ending.Stopped(jobs));
        Assert.Null(await Stop.Wait(jobs));
    }

    [Fact]
    public async Task AFailureOutsideTheJobIsTriedAgain()
    {
        await using var jobs = await Ending.Feature();
        await Conflicting(jobs);

        await Built.Run(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer"));

        // It counts nothing against the feature, which is resolving still, and its project waits as
        // after any failure outside a card.
        var row = await Ending.Row(jobs);
        Assert.Equal("resolving", row.State);
        Assert.Equal("failed_outside_card", (await ConflictRun(jobs)).Result);
        Assert.NotNull(await Stop.Wait(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Once the wait is over, the conflict is handed out again.
        await Stop.EndTheWait(jobs);
        Assert.Equal("conflict", (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task ALostRunIsTriedAgain()
    {
        await using var jobs = await Ending.Feature();
        await Conflicting(jobs);
        Assert.Equal("conflict", (string)(await jobs.Job())["kind"]!);

        // Its worker went: the run is found lost on the next read, the feature stays resolving and
        // the project waits.
        await Stop.AgeTheRun(jobs);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.Equal("lost", (await ConflictRun(jobs)).Result);
        Assert.Equal("resolving", (await Ending.Row(jobs)).State);
        Assert.NotNull(await Stop.Wait(jobs));
    }

    /// <summary>The project's conflict run, the one there is, read past row level security.</summary>
    private static async Task<RunRow> ConflictRun(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return Assert.Single(await admin.QueryAsync<RunRow>(new CommandDefinition(
            """
            SELECT id AS Id, card_number AS CardNumber, feature_number AS FeatureNumber, kind AS Kind, branch AS Branch, base AS Base,
                   from_commit AS FromCommit, default_commit AS DefaultCommit, finished_at AS FinishedAt, result AS Result,
                   head_commit AS HeadCommit, remerge_diff AS RemergeDiff
            FROM runs WHERE project_id = @project AND kind = 'conflict'
            """,
            new { project = jobs.Project },
            cancellationToken: Ct)));
    }

    /// <summary>The pushes Dev flow recorded as its own, each with its branch, commit and run.</summary>
    private static async Task<List<(string Branch, string Commit, Guid Run)>> PushesMade(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, string, Guid)>(new CommandDefinition(
            "SELECT branch, commit, run_id FROM pushes_made WHERE project_id = @project",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    /// <summary>The commit the feature's branch is at in the fake's git.</summary>
    private static async Task<string?> FeatureHead(Jobs.Jobs jobs) =>
        await jobs.Fake.BranchCommit(Ending.User, FeatureJobs.Repo, Ending.Branch, Ct);

    /// <summary>The parents of <paramref name="commit"/> in the fake's git.</summary>
    private static Task<IReadOnlyList<string>> Parents(Jobs.Jobs jobs, string commit) =>
        jobs.Fake.Parents(Ending.User, FeatureJobs.Repo, commit, Ct);

    internal sealed class RunRow
    {
        public Guid Id { get; init; }

        public int? CardNumber { get; init; }

        public int? FeatureNumber { get; init; }

        public string Kind { get; init; } = "";

        public string Branch { get; init; } = "";

        public string Base { get; init; } = "";

        public string? FromCommit { get; init; }

        public string? DefaultCommit { get; init; }

        public DateTime? FinishedAt { get; init; }

        public string? Result { get; init; }

        public string? HeadCommit { get; init; }

        public string? RemergeDiff { get; init; }
    }
}
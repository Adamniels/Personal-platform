using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Tests.Jobs;
using DevFlow.Watching;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Watching;

/// <summary>
/// Watching each open feature's branch in its project's read, as the default branch is watched:
/// from the branch's creation, judging every change after it. Fine are a merge Dev flow recorded
/// into it, a push one of Dev flow's own jobs made there, recorded by its commit, and a change by
/// the owner's linked login; anything else stops the project, the branch deleted while its feature
/// is open among it. A push by the app while a run of Dev flow's on the branch is under way is
/// judged when the run ends. A feature's branch not read for five minutes holds that feature's
/// work, not the project's. Against the fake and a real Postgres. See "A feature's branch" under
/// "Watching branches" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class FeatureWatchTests
{
    private const string Branch = "feature-10-dev-flow-features";

    private const string CardBranch = "devflow/11-read-the-sub-issues";

    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    private static string Commit(int n) => n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string NoCommit = new('0', 40);

    /// <summary>A feature of two cards, 11 and 12, and card 20 on its own, younger than the
    /// feature, with <paramref name="before"/> already on the branches.</summary>
    private static async Task<Jobs.Jobs> Feature(params FakeActivity[] before)
    {
        var jobs = await Jobs.Jobs.Start([
            FeatureJobs.Parent(10, "Dev flow: features", [11, 12]),
            FeatureJobs.Sub(11, "Read the sub-issues"),
            FeatureJobs.Sub(12, "Show each feature"),
            Jobs.Jobs.Card(20, "A card on its own"),
        ]);
        foreach (var change in before)
        {
            Land(jobs, change.Kind, change.Actor, change.After, change.Branch, change.Id);
        }

        return jobs;
    }

    /// <summary>The feature with its branch made, by handing out its first card, and the project
    /// read once since, so the branch's watch has read where it stands.</summary>
    private static async Task<Jobs.Jobs> Made(params FakeActivity[] before)
    {
        var jobs = await Feature(before);
        try
        {
            await FeatureJobs.FirstCommit(jobs);
            Assert.Equal(11, (int)(await jobs.Job())["card"]!["number"]!);
            Assert.Equal(Branch, (await FeatureJobs.Feature(jobs, 10)).Branch);
            await jobs.Routes.Read(jobs.Project, User);
            return jobs;
        }
        catch
        {
            await jobs.DisposeAsync();
            throw;
        }
    }

    /// <summary>A change landing on <paramref name="branch"/>, after what is there, with the next
    /// id unless given.</summary>
    private static FakeActivity Land(Jobs.Jobs jobs, ActivityKind kind, string? actor, string after, string branch = Branch, long? id = null)
    {
        var world = jobs.Fake.World(User);
        var installation = world.Installations[0];
        var found = installation.Repositories[0];
        var activity = found.Activity ?? [];
        var change = new FakeActivity(
            id ?? activity.Select(a => a.Id).DefaultIfEmpty(100).Max() + 1,
            branch,
            activity.LastOrDefault(a => a.Branch == branch)?.After ?? NoCommit,
            after,
            jobs.Routes.Time.GetUtcNow(),
            kind,
            actor);
        found = found with { Activity = [.. activity, change] };
        jobs.Fake.Replace(User, new FakeWorld([installation with { Repositories = [found, .. installation.Repositories.Skip(1)] }]));
        return change;
    }

    private static Task<DevFlow.Reading.ReadResult> Read(Jobs.Jobs jobs) => jobs.Routes.Read(jobs.Project, User);

    private static async Task<MainWatchTests.StopRow?> Stop(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleOrDefaultAsync<MainWatchTests.StopRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, branch AS Branch, activity_type AS ActivityType, actor AS Actor, commit AS Commit, stopped_at AS StoppedAt
            FROM project_stops WHERE project_id = @project
            """,
            new { project = jobs.Project },
            cancellationToken: Ct));
    }

    private static Task<ProjectHold> Hold(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<WatchStore>().Hold(User, jobs.Project, Ct);

    private static Task<HttpContext> Resume(Jobs.Jobs jobs, string commit) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/resume", Contract.Token, $$"""{"commit": "{{commit}}"}""");

    /// <summary>How many times the branch's changes were read.</summary>
    private static int Reads(Jobs.Jobs jobs, string branch) =>
        jobs.Routes.Host.Calls.Count(call => call.Operation == "Activity" && call.Argument.StartsWith($"{branch} ", StringComparison.Ordinal));

    /// <summary>A run of Dev flow's on the feature's branch itself, as the later jobs that push
    /// there, resolving a conflict and fixing a feature, make one: card 12's, by the first worker,
    /// under way. Its id.</summary>
    private static async Task<Guid> RunOnTheBranch(Jobs.Jobs jobs)
    {
        var id = Guid.CreateVersion7();
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at)
            VALUES (@id, @project, @user, 12, @worker, 'build', @branch, 'main', @now)
            """,
            new { id, project = jobs.Project, user = User, worker = Jobs.Jobs.FirstWorker, branch = Branch, now = jobs.Routes.Time.GetUtcNow() },
            cancellationToken: Ct));
        return id;
    }

    /// <summary>The run's result, as its worker sends it, having pushed <paramref name="head"/>.</summary>
    private static async Task Ended(Jobs.Jobs jobs, Guid run, string head)
    {
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, Branch);
        result["head_commit"] = head;
        var posted = await jobs.Post(DevFlow.Protocol.WorkerApi.ResultPath(run), result);
        Assert.Equal(StatusCodes.Status204NoContent, posted.Response.StatusCode);
    }

    [Fact]
    public async Task TheBranchIsWatchedFromWhenItWasMade()
    {
        // An earlier branch of the same name, someone else's, pushed and deleted before the feature.
        await using var jobs = await Made(
            new FakeActivity(5, Branch, NoCommit, Commit(5), DateTimeOffset.UnixEpoch, ActivityKind.Push, "octo-stranger"),
            new FakeActivity(6, Branch, Commit(5), NoCommit, DateTimeOffset.UnixEpoch, ActivityKind.BranchDeletion, "octo-stranger"));

        // Nothing before the branch's creation is judged.
        Assert.Null(await Stop(jobs));
        Assert.Equal(ProjectHold.None, await Hold(jobs));
        var feature = await FeatureJobs.Feature(jobs, 10);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            var watch = await admin.QuerySingleAsync<(string Branch, long? Last)>(new CommandDefinition(
                """
                SELECT b.branch, b.last_activity_id FROM branch_watches b JOIN features f ON f.id = b.feature_id
                WHERE b.project_id = @project AND f.number = 10
                """,
                new { project = jobs.Project },
                cancellationToken: Ct));
            Assert.Equal(Branch, watch.Branch);
            // It began at the creation, the change that made the branch at the commit recorded.
            var creation = Assert.Single(jobs.Fake.World(User).Installations[0].Repositories[0].Activity!, a => a.Branch == Branch && a.Kind == ActivityKind.BranchCreation);
            Assert.Equal(feature.BranchBaseCommit, creation.After);
            Assert.Equal(creation.Id, watch.Last);
        }

        // Every change after it is.
        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(7));
        await Read(jobs);
        var stop = Assert.IsType<MainWatchTests.StopRow>(await Stop(jobs));
        Assert.Equal(Branch, stop.Branch);
        Assert.Equal(Commit(7), stop.Commit);
    }

    [Fact]
    public async Task WhatIsFine()
    {
        await using var jobs = await Made();

        // A merge Dev flow recorded into the feature's branch.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO merges_made (project_id, user_id, branch, commit, number, merged_at)
                VALUES (@project, @user, @branch, @commit, 70, @now)
                """,
                new { project = jobs.Project, user = User, branch = Branch, commit = Commit(21), now = jobs.Routes.Time.GetUtcNow() },
                cancellationToken: Ct));
        }

        Land(jobs, ActivityKind.PullRequestMerge, FakeCodeHost.AppLogin, Commit(21));
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        // A push a Dev flow job made there, recorded by its commit with the run's result.
        await FeatureJobs.Finish(jobs, 11, "done");
        var run = await RunOnTheBranch(jobs);
        await Ended(jobs, run, Commit(22));
        Land(jobs, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(22));
        await Read(jobs);
        Assert.Null(await Stop(jobs));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            var pushed = await admin.QuerySingleAsync<(string Branch, string Commit, Guid Run)>(new CommandDefinition(
                "SELECT branch, commit, run_id FROM pushes_made WHERE project_id = @project",
                new { project = jobs.Project },
                cancellationToken: Ct));
            Assert.Equal((Branch, Commit(22), run), pushed);
        }

        // A change by the owner's linked login, whatever its case.
        Land(jobs, ActivityKind.Push, Jobs.Jobs.Owner, Commit(23));
        Land(jobs, ActivityKind.PullRequestMerge, "Octo-Owner", Commit(24));
        await Read(jobs);

        Assert.Null(await Stop(jobs));
        Assert.Equal(ProjectHold.None, await Hold(jobs));
    }

    /// <summary>Each change that stops the project: its kind, who made it, and the word the stop
    /// records.</summary>
    public static TheoryData<string, ActivityKind, string?, string> Stopping => new()
    {
        { "a push by the app that Dev flow has no record of", ActivityKind.Push, FakeCodeHost.AppLogin, "push" },
        { "a merge by the app that Dev flow has no record of", ActivityKind.PullRequestMerge, FakeCodeHost.AppLogin, "pull_request_merge" },
        { "a force push by the owner", ActivityKind.ForcePush, Jobs.Jobs.Owner, "force_push" },
        { "a force push by the app", ActivityKind.ForcePush, FakeCodeHost.AppLogin, "force_push" },
        { "a change by another login", ActivityKind.Push, "octo-stranger", "push" },
        { "a change whose actor GitHub does not name", ActivityKind.PullRequestMerge, null, "pull_request_merge" },
        { "the branch deleted by the owner while its feature is open", ActivityKind.BranchDeletion, Jobs.Jobs.Owner, "branch_deletion" },
    };

    [Theory]
    [MemberData(nameof(Stopping))]
    public async Task WhatStopsTheProject(string what, ActivityKind kind, string? actor, string word)
    {
        await using var jobs = await Made();
        var after = kind == ActivityKind.BranchDeletion ? NoCommit : Commit(31);

        Land(jobs, kind, actor, after);
        await Read(jobs);

        var stop = Assert.IsType<MainWatchTests.StopRow>(await Stop(jobs));
        Assert.True(stop.Branch == Branch, $"{what}: the stop names {stop.Branch}");
        Assert.Equal(word, stop.ActivityType);
        Assert.Equal(actor, stop.Actor);
        Assert.Equal(after, stop.Commit);
        Assert.Equal(ProjectHold.Stopped, await Hold(jobs));

        // It holds the whole project: not even the card on its own is handed out.
        await FeatureJobs.Finish(jobs, 11, "done");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Resuming is the same action, with the commit shown.
        Assert.Equal(StatusCodes.Status204NoContent, (await Resume(jobs, after)).Response.StatusCode);
        Assert.Equal(ProjectHold.None, await Hold(jobs));
    }

    [Fact]
    public async Task AStopOnAnotherBranchIsNeverReplaced()
    {
        await using var jobs = await Made();

        // Stopped by the default branch, then something lands on the feature's.
        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(35), "main");
        await Read(jobs);
        Assert.Equal("main", (await Stop(jobs))?.Branch);
        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(36));
        await Read(jobs);

        // The owner still sees what stopped it first, so he never resumes what he did not see.
        var stop = Assert.IsType<MainWatchTests.StopRow>(await Stop(jobs));
        Assert.Equal(("main", Commit(35)), (stop.Branch, stop.Commit));

        // Once he resumes it, the feature's change is judged again, and stops the project.
        Assert.Equal(StatusCodes.Status204NoContent, (await Resume(jobs, Commit(35))).Response.StatusCode);
        await Read(jobs);
        stop = Assert.IsType<MainWatchTests.StopRow>(await Stop(jobs));
        Assert.Equal((Branch, Commit(36)), (stop.Branch, stop.Commit));
    }

    [Fact]
    public async Task AMergeRecordedIntoAnotherBranchIsNotFine()
    {
        await using var jobs = await Made();
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO merges_made (project_id, user_id, branch, commit, number, merged_at)
                VALUES (@project, @user, 'main', @commit, 72, @now)
                """,
                new { project = jobs.Project, user = User, commit = Commit(37), now = jobs.Routes.Time.GetUtcNow() },
                cancellationToken: Ct));
        }

        Land(jobs, ActivityKind.PullRequestMerge, FakeCodeHost.AppLogin, Commit(37));
        await Read(jobs);

        Assert.Equal((Branch, Commit(37)), ((await Stop(jobs))?.Branch, (await Stop(jobs))?.Commit));
    }

    [Fact]
    public async Task AJobsOwnPushIsJudgedWhenItsRunEnds()
    {
        await using var jobs = await Made();
        await FeatureJobs.Finish(jobs, 11, "done");

        // The worker pushes, and the watch reads the push before the run's result comes back.
        var run = await RunOnTheBranch(jobs);
        Land(jobs, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(41));
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        // A change by anyone else meanwhile is judged as soon as it is read: here, the owner's.
        Land(jobs, ActivityKind.Push, Jobs.Jobs.Owner, Commit(42));
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        // The result records that commit: it is fine.
        await Ended(jobs, run, Commit(41));
        await Read(jobs);
        Assert.Null(await Stop(jobs));
        Assert.Equal(ProjectHold.None, await Hold(jobs));

        // A second run whose result records another commit than the one the app pushed.
        await FeatureJobs.SetState(jobs, 12, "done");
        var second = await RunOnTheBranch(jobs);
        Land(jobs, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(43));
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        await Ended(jobs, second, Commit(44));

        var stop = Assert.IsType<MainWatchTests.StopRow>(await Stop(jobs));
        Assert.Equal(Branch, stop.Branch);
        Assert.Equal("push", stop.ActivityType);
        Assert.Equal(FakeCodeHost.AppLogin, stop.Actor);
        Assert.Equal(Commit(43), stop.Commit);
    }

    [Fact]
    public async Task AnotherLoginsPushWhileARunIsUnderWayStopsAtOnce()
    {
        await using var jobs = await Made();
        await FeatureJobs.Finish(jobs, 11, "done");
        await RunOnTheBranch(jobs);

        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(46));
        await Read(jobs);

        Assert.Equal(("octo-stranger", Commit(46)), ((await Stop(jobs))?.Actor, (await Stop(jobs))?.Commit));
    }

    [Fact]
    public async Task AJobsOwnPushIsJudgedWhenItsRunIsLost()
    {
        await using var jobs = await Made();
        await FeatureJobs.Finish(jobs, 11, "done");
        await RunOnTheBranch(jobs);
        Land(jobs, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(45));
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        // The worker never comes back: its run is found lost on a later read, with no result, so
        // the push it made has no record.
        jobs.Routes.Time.Advance(TimeSpan.FromHours(3));
        await Read(jobs);

        Assert.Equal(Commit(45), (await Stop(jobs))?.Commit);
    }

    [Fact]
    public async Task ACardsBranchIsNotWatched()
    {
        await using var jobs = await Made();

        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(51), CardBranch);
        Land(jobs, ActivityKind.BranchDeletion, "octo-stranger", NoCommit, CardBranch);
        await Read(jobs);

        Assert.Null(await Stop(jobs));
        Assert.Equal(0, Reads(jobs, CardBranch));
        Assert.True(Reads(jobs, Branch) > 0);
    }

    /// <summary>The two states a feature ends in.</summary>
    public static TheoryData<string> Ending => ["merged", "closed"];

    [Theory]
    [MemberData(nameof(Ending))]
    public async Task AFeatureThatEndedIsNoLongerWatched(string state)
    {
        await using var jobs = await Made();
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE features SET state = @state WHERE project_id = @project AND number = 10",
                new { project = jobs.Project, state },
                cancellationToken: Ct));
        }

        var reads = Reads(jobs, Branch);
        Land(jobs, ActivityKind.Push, "octo-stranger", Commit(61));
        Land(jobs, ActivityKind.BranchDeletion, Jobs.Jobs.Owner, NoCommit);
        await Read(jobs);

        Assert.Null(await Stop(jobs));
        Assert.Equal(reads, Reads(jobs, Branch));
    }

    [Fact]
    public async Task AWatchThatCannotReadHoldsItsFeature()
    {
        await using var jobs = await Made();
        await FeatureJobs.Finish(jobs, 11, "done");
        await jobs.SayHello(Jobs.Jobs.SecondWorker);

        // GitHub answers for every branch but the feature's, for five minutes and a second.
        jobs.Routes.Host.UnavailableBranches[Branch] = true;
        for (var minute = 0; minute < 5; minute++)
        {
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            await Read(jobs);
        }

        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));

        // The project's other work goes on: card 20 on its own is handed out, the feature's card 12
        // is not.
        Assert.Equal(ProjectHold.None, await Hold(jobs));
        Assert.Null(await Stop(jobs));
        Assert.Equal(20, (int)(await jobs.Job())["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(12)).State);

        // Read again, the feature's work goes on.
        jobs.Routes.Host.UnavailableBranches.Clear();
        await Read(jobs);
        Assert.Equal(12, (int)(await jobs.Job(Jobs.Jobs.SecondWorker))["card"]!["number"]!);

        // Card 12 built, approved and due to merge into the feature's branch, while the branch has
        // not been read for five minutes: nothing is merged, and GitHub is not asked.
        await FeatureJobs.Finish(jobs, 12, "waiting_for_owner");
        var head = Commit(71);
        var world = jobs.Fake.World(User);
        var repository = world.Installations[0].Repositories[0];
        var pull = new PullRequest(71, "Card 12", FakeCodeHost.AppLogin, [], head, "devflow/12-show-each-feature", Branch, false, "",
            new Uri("https://github.com/Adamniels/Personal-platform/pull/71"), DateTimeOffset.UnixEpoch, PullRequestState.Open);
        jobs.Fake.Replace(User, new FakeWorld([world.Installations[0] with { Repositories = [repository with { PullRequests = [pull] }] }]));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cards SET merge_check_due = true, pull_request_number = 71, pull_request_url = 'https://github.com/Adamniels/Personal-platform/pull/71'
                WHERE project_id = @project AND number = 12;
                UPDATE branch_watches SET last_read_at = @old WHERE project_id = @project AND feature_id IS NOT NULL;
                """,
                new { project = jobs.Project, old = jobs.Routes.Time.GetUtcNow() - TimeSpan.FromMinutes(6) },
                cancellationToken: Ct));
        }

        var merges = jobs.Routes.App.Services.GetRequiredService<AppMerges>();
        Assert.IsNotType<AppMergeOutcome.Done>(await merges.MergeCard(User, jobs.Project, 12, 71, head, Ct));
        Assert.DoesNotContain(jobs.Routes.Host.Calls, call => call.Operation == "MergeAsApp");

        // Read again, it merges into the feature's branch, and the watch finds the merge fine.
        await Read(jobs);
        var merged = Assert.IsType<AppMergeOutcome.Done>(await merges.MergeCard(User, jobs.Project, 12, 71, head, Ct));
        Assert.IsType<MergeResult.Merged>(merged.Result);
        await Read(jobs);
        Assert.Null(await Stop(jobs));

        // The default branch's watch still holds the whole project.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE branch_watches SET last_read_at = @old WHERE project_id = @project AND feature_id IS NULL",
                new { project = jobs.Project, old = jobs.Routes.Time.GetUtcNow() - TimeSpan.FromMinutes(6) },
                cancellationToken: Ct));
        }

        Assert.Equal(ProjectHold.Unwatched, await Hold(jobs));
    }
}
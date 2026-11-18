using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Workers;
using DevFlow.Watching;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Watching;

/// <summary>
/// Watching each project's default branch in its read: the first read records where the branch
/// stands, and every change after it is judged. A change by the owner's linked login, or a merge
/// whose commit Dev flow recorded when it merged as the app, is fine; anything else stops the
/// project until its user resumes it. A stopped project, and one whose branch has not been read
/// for five minutes, hands out no new work. Against the fake and a real Postgres. See "Watching the
/// default branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class MainWatchTests
{
    private const string Owner = "octo-owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid User => Routes.User;

    private static string Commit(int n) => n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A world of one repository, <see cref="Reading.Reading.RepositoryId"/> unless given,
    /// with <paramref name="pulls"/> and the changes on its branches so far.</summary>
    private static void Stage(Routes routes, Guid user, IReadOnlyList<PullRequest>? pulls = null, long repository = Reading.Reading.RepositoryId, params FakeActivity[] activity)
    {
        var world = routes.Fake.World(user);
        var installation = world.Installations.Count > 0 ? world.Installations[0] : new FakeInstallation(Reading.Reading.Installation, "Adamniels", []);
        var staged = new FakeRepository(repository, "Adamniels", repository == Reading.Reading.RepositoryId ? "Personal-platform" : $"repository-{repository}",
            pulls ?? [], [], [], [], [], null, activity);
        installation = installation with { Repositories = [.. installation.Repositories.Where(r => r.Id != repository), staged] };
        routes.Fake.Replace(user, new FakeWorld([installation]));
    }

    /// <summary>A change landing on the repository's branch, after what is there.</summary>
    private static FakeActivity Land(Routes routes, Guid user, ActivityKind kind, string? actor, string after, long repository = Reading.Reading.RepositoryId, string branch = "main")
    {
        var world = routes.Fake.World(user);
        var installation = world.Installations[0];
        var found = installation.Repositories.Single(r => r.Id == repository);
        var activity = found.Activity ?? [];
        var change = new FakeActivity(
            activity.Select(a => a.Id).DefaultIfEmpty(100).Max() + 1,
            branch,
            activity.Count > 0 ? activity[^1].After : Commit(1),
            after,
            routes.Time.GetUtcNow(),
            kind,
            actor);
        found = found with { Activity = [.. activity, change] };
        routes.Fake.Replace(user, new FakeWorld([installation with { Repositories = [.. installation.Repositories.Select(r => r.Id == repository ? found : r)] }]));
        return change;
    }

    private static PullRequest Pull(int number, string head) => new(
        number, $"Card {number}", FakeCodeHost.AppLogin, [], head, $"card-{number}", "main", false, "",
        new Uri($"https://github.com/Adamniels/Personal-platform/pull/{number}"), DateTimeOffset.UnixEpoch, PullRequestState.Open);

    /// <summary>A project of the token's user, linked as <see cref="Owner"/>, with a Claude login,
    /// so only the watch holds its work, read once, so its watch has begun.</summary>
    private static async Task<(Routes Routes, Guid Project)> Start(params FakeActivity[] before)
    {
        var routes = await Routes.Start();
        await Reading.Reading.Link(routes.Door, User, Owner);
        await routes.App.Services.GetRequiredService<ClaudeLogins>().Set(User, Jobs.Jobs.ClaudeToken, Ct);
        Stage(routes, User, null, Reading.Reading.RepositoryId, before);
        var project = await routes.Project(User);
        await routes.Read(project, User);
        return (routes, project);
    }

    private static WatchStore Store(Routes routes) => routes.App.Services.GetRequiredService<WatchStore>();

    private static Task<ProjectHold> Hold(Routes routes, Guid project) => Store(routes).Hold(User, project, Ct);

    private static async Task<StopRow?> Stop(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleOrDefaultAsync<StopRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, branch AS Branch, activity_type AS ActivityType, actor AS Actor, commit AS Commit, stopped_at AS StoppedAt
            FROM project_stops WHERE project_id = @project
            """,
            new { project },
            cancellationToken: Ct));
    }

    private static Task<HttpContext> Resume(Routes routes, Guid project, string commit) =>
        routes.App.SendJson("POST", $"/devflow/api/projects/{project}/resume", Contract.Token, $$"""{"commit": "{{commit}}"}""");

    [Fact]
    public async Task DevFlowsOwnMergeIsFine()
    {
        var (routes, project) = await Start();
        await using var _ = routes;
        var head = Commit(42);
        Stage(routes, User, [Pull(42, head)], Reading.Reading.RepositoryId, routes.Fake.World(User).Installations[0].Repositories[0].Activity?.ToArray() ?? []);

        var merged = Assert.IsType<AppMergeOutcome.Done>(await routes.App.Services.GetRequiredService<AppMerges>().Merge(User, project, 42, head, Ct));
        var commit = Assert.IsType<MergeResult.Merged>(merged.Result).Commit;
        await routes.Read(project, User);

        Assert.Null(await Stop(routes, project));
        Assert.Equal(ProjectHold.None, await Hold(routes, project));
        await using var admin = await routes.Database.OpenAdmin(Ct);
        Assert.Equal(commit, await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT commit FROM merges_made WHERE project_id = @project", new { project }, cancellationToken: Ct)));
        Assert.Contains(routes.Host.Calls, call => call.Operation == "MergeAsApp");
    }

    [Fact]
    public async Task TheOwnersOwnPushOrMergeIsFine()
    {
        var (routes, project) = await Start();
        await using var _ = routes;

        Land(routes, User, ActivityKind.Push, Owner, Commit(2));
        Land(routes, User, ActivityKind.PullRequestMerge, "Octo-Owner", Commit(3));
        Land(routes, User, ActivityKind.BranchCreation, Owner, Commit(4));
        await routes.Read(project, User);

        Assert.Null(await Stop(routes, project));
        Assert.Equal(ProjectHold.None, await Hold(routes, project));
    }

    [Fact]
    public async Task APushByTheAppThatDevFlowDidNotMakeStopsTheProject()
    {
        var (routes, project) = await Start();
        await using var _ = routes;

        Land(routes, User, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(7));
        await routes.Read(project, User);

        var stop = Assert.IsType<StopRow>(await Stop(routes, project));
        Assert.Equal(User, stop.UserId);
        Assert.Equal("main", stop.Branch);
        Assert.Equal("push", stop.ActivityType);
        Assert.Equal(FakeCodeHost.AppLogin, stop.Actor);
        Assert.Equal(Commit(7), stop.Commit);
        Assert.Equal(ProjectHold.Stopped, await Hold(routes, project));
    }

    [Fact]
    public async Task AMergeByTheAppThatDevFlowDidNotMakeStopsTheProject()
    {
        var (routes, project) = await Start();
        await using var _ = routes;

        Land(routes, User, ActivityKind.PullRequestMerge, FakeCodeHost.AppLogin, Commit(8));
        await routes.Read(project, User);

        var stop = Assert.IsType<StopRow>(await Stop(routes, project));
        Assert.Equal("pull_request_merge", stop.ActivityType);
        Assert.Equal(Commit(8), stop.Commit);
    }

    [Fact]
    public async Task AnyoneElseStopsTheProject()
    {
        var (routes, project) = await Start();
        await using var _ = routes;

        // Another login, on a push and on a merge.
        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(9));
        await routes.Read(project, User);
        Assert.Equal("octo-stranger", (await Stop(routes, project))!.Actor);
        Assert.Equal(StatusCodes.Status204NoContent, (await Resume(routes, project, Commit(9))).Response.StatusCode);

        // An account GitHub no longer names.
        Land(routes, User, ActivityKind.PullRequestMerge, null, Commit(10));
        await routes.Read(project, User);
        var nobody = (await Stop(routes, project))!;
        Assert.Null(nobody.Actor);
        Assert.Equal(Commit(10), nobody.Commit);
        Assert.Equal(StatusCodes.Status204NoContent, (await Resume(routes, project, Commit(10))).Response.StatusCode);

        // A force push, even by the owner.
        Land(routes, User, ActivityKind.ForcePush, Owner, Commit(11));
        await routes.Read(project, User);
        var forced = (await Stop(routes, project))!;
        Assert.Equal("force_push", forced.ActivityType);
        Assert.Equal(Owner, forced.Actor);

        // While stopped, a later change that stops it again is what it shows, since it stopped first.
        var stoppedAt = forced.StoppedAt;
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(12));
        await routes.Read(project, User);
        var later = (await Stop(routes, project))!;
        Assert.Equal(Commit(12), later.Commit);
        Assert.Equal(stoppedAt, later.StoppedAt);
    }

    [Fact]
    public async Task WhatWasThereBeforeIsNotJudged()
    {
        await using var routes = await Routes.Start();
        await Reading.Reading.Link(routes.Door, User, Owner);
        Stage(routes, User, null, Reading.Reading.RepositoryId,
            new FakeActivity(5, "main", Commit(1), Commit(2), DateTimeOffset.UnixEpoch, ActivityKind.ForcePush, "octo-stranger"),
            new FakeActivity(6, "main", Commit(2), Commit(3), DateTimeOffset.UnixEpoch, ActivityKind.Push, FakeCodeHost.AppLogin));
        var project = await routes.Project(User);

        // Before its first read the branch has not been watched, so it holds work.
        Assert.Equal(ProjectHold.Unwatched, await Hold(routes, project));

        await routes.Read(project, User);

        Assert.Null(await Stop(routes, project));
        Assert.Equal(ProjectHold.None, await Hold(routes, project));
        await using var admin = await routes.Database.OpenAdmin(Ct);
        Assert.Equal(6L, await admin.QuerySingleAsync<long?>(new CommandDefinition(
            "SELECT last_activity_id FROM branch_watches WHERE project_id = @project", new { project }, cancellationToken: Ct)));

        // A branch with nothing on it begins at nothing, and judges the first change after.
        var empty = await routes.Project(User, repository: 9002, name: "empty");
        Stage(routes, User, null, 9002);
        await routes.Read(empty, User);
        Assert.Null(await Stop(routes, empty));
        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(13), repository: 9002);
        await routes.Read(empty, User);
        Assert.Equal(Commit(13), (await Stop(routes, empty))!.Commit);
    }

    [Fact]
    public async Task AWatchThatCannotReadHoldsNewWork()
    {
        var (routes, project) = await Start();
        await using var _ = routes;
        Assert.Equal(ProjectHold.None, await Hold(routes, project));

        // GitHub stops answering; each minute's read fails.
        routes.Fake.SetUnavailable(User, true);
        for (var minute = 0; minute < 5; minute++)
        {
            routes.Time.Advance(TimeSpan.FromMinutes(1));
            await routes.Read(project, User);
        }

        Assert.Equal(ProjectHold.None, await Hold(routes, project));
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ProjectHold.Unwatched, await Hold(routes, project));
        Assert.Null(await Stop(routes, project));

        // Its page says why.
        var workers = await Workers.Workers.List(routes, project);
        Assert.Equal("unwatched", (string?)workers["held"]);

        // Read again, it hands out work again.
        routes.Fake.SetUnavailable(User, false);
        await routes.Read(project, User);
        Assert.Equal(ProjectHold.None, await Hold(routes, project));
        Assert.Null((string?)(await Workers.Workers.List(routes, project))["held"]);
    }

    [Fact]
    public async Task AStoppedProjectHandsOutNothing()
    {
        var (routes, project) = await Start();
        await using var _ = routes;
        var key = await Workers.Workers.NewKey(routes, project);
        var worker = Guid.Parse("0190a8f0-0000-7000-8000-0000000000f2");
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.Workers.SayHello(routes, key, worker)).Response.StatusCode);
        await Building(routes, project, 31);

        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(14));
        await routes.Read(project, User);

        // jobs/next answers nothing, and the page says why.
        Assert.Equal(ProjectHold.Stopped, await Hold(routes, project));
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.Workers.AskForWork(routes, key, worker)).Response.StatusCode);
        Assert.Equal("stopped", (string?)(await Workers.Workers.List(routes, project))["held"]);

        // A card being worked on finishes: it is left where it is.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            Assert.Equal("building", await admin.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT state FROM cards WHERE project_id = @project AND number = 31", new { project }, cancellationToken: Ct)));
        }

        // And nothing is merged while it is stopped: GitHub is not asked.
        var head = Commit(43);
        var activity = routes.Fake.World(User).Installations[0].Repositories[0].Activity!.ToArray();
        Stage(routes, User, [Pull(43, head)], Reading.Reading.RepositoryId, activity);
        Assert.IsType<AppMergeOutcome.ProjectStopped>(await routes.App.Services.GetRequiredService<AppMerges>().Merge(User, project, 43, head, Ct));
        Assert.DoesNotContain(routes.Host.Calls, call => call.Operation is "MergeAsApp" or "ForWrite");
    }

    /// <summary>A card of the project with a worker on it, as a later part's run leaves one.</summary>
    private static async Task Building(Routes routes, Guid project, int number) =>
        await routes.Door.InUserTransaction(User, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at,
                               picked_body_sha256, picked_title, changed_at, read_at)
            VALUES (@id, @project, @user, @number, 'Card', 'https://github.com/Adamniels/Personal-platform/issues/31', 'building', 'none', now(),
                    decode(repeat('ab', 32), 'hex'), 'Card', now(), now())
            """,
            new { id = Guid.CreateVersion7(), project, user = User, number },
            transaction,
            cancellationToken: Ct)), Ct);

    [Fact]
    public async Task ResumingIsTheOwnersAndIsRecorded()
    {
        var (routes, project) = await Start();
        await using var _ = routes;

        // Not stopped: nothing to resume.
        Assert.Equal(StatusCodes.Status409Conflict, (await Resume(routes, project, Commit(15))).Response.StatusCode);

        Land(routes, User, ActivityKind.Push, FakeCodeHost.AppLogin, Commit(15));
        await routes.Read(project, User);

        // Another user's project, or none, is not found.
        var bobs = await routes.Project(Routes.Bob, repository: 9003, name: "bobs");
        Assert.Equal(StatusCodes.Status404NotFound, (await Resume(routes, bobs, Commit(15))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Resume(routes, Guid.CreateVersion7(), Commit(15))).Response.StatusCode);

        // Without the commit, or another than the one shown.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity,
            (await routes.App.SendJson("POST", $"/devflow/api/projects/{project}/resume", Contract.Token, "{}")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await Resume(routes, project, Commit(16))).Response.StatusCode);

        // From another origin, refused before anything.
        var foreign = await routes.App.Send("POST", $"/devflow/api/projects/{project}/resume", Contract.Token, ("Origin", "https://evil.test"));
        Assert.Equal(StatusCodes.Status403Forbidden, foreign.Response.StatusCode);
        Assert.Equal(ProjectHold.Stopped, await Hold(routes, project));

        routes.Time.Advance(TimeSpan.FromMinutes(3));
        var resumedAt = routes.Time.GetUtcNow();
        Assert.Equal(StatusCodes.Status204NoContent, (await Resume(routes, project, Commit(15))).Response.StatusCode);

        Assert.Null(await Stop(routes, project));
        Assert.Equal(ProjectHold.None, await Hold(routes, project));
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var resume = await admin.QuerySingleAsync<ResumeRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, resumed_by AS ResumedBy, resumed_at AS ResumedAt, accepted_commit AS AcceptedCommit,
                   activity_type AS ActivityType, actor AS Actor
            FROM project_resumes WHERE project_id = @project
            """,
            new { project },
            cancellationToken: Ct));
        Assert.Equal(User, resume.UserId);
        Assert.Equal(User, resume.ResumedBy);
        Assert.Equal(resumedAt.UtcDateTime, resume.ResumedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Commit(15), resume.AcceptedCommit);
        Assert.Equal("push", resume.ActivityType);
        Assert.Equal(FakeCodeHost.AppLogin, resume.Actor);

        // What was accepted is not judged again.
        await routes.Read(project, User);
        Assert.Null(await Stop(routes, project));
    }

    [Fact]
    public async Task OneProjectsStopDoesNotStopAnother()
    {
        var (routes, project) = await Start();
        await using var _ = routes;
        var other = await routes.Project(User, repository: 9004, name: "other");
        Stage(routes, User, null, 9004);
        await routes.Read(other, User);

        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(17));
        await routes.Read(project, User);
        await routes.Read(other, User);

        Assert.Equal(ProjectHold.Stopped, await Hold(routes, project));
        Assert.Equal(ProjectHold.None, await Hold(routes, other));
        Assert.Null(await Stop(routes, other));

        // A project whose watch throws does not stop the next one's read either.
        routes.Host.Throws[Reading.Reading.RepositoryId] = true;
        Land(routes, User, ActivityKind.Push, "octo-stranger", Commit(18), repository: 9004);
        await routes.Read(project, User);
        await routes.Read(other, User);
        Assert.Equal(Commit(18), (await Stop(routes, other))!.Commit);
    }

    internal sealed record StopRow(Guid UserId, string Branch, string ActivityType, string? Actor, string Commit, DateTime StoppedAt);

    internal sealed record ResumeRow(Guid UserId, Guid ResumedBy, DateTime ResumedAt, string AcceptedCommit, string ActivityType, string? Actor);
}
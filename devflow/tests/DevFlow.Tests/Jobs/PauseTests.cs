using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Reading;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Pausing: a project paused, or every project of the user with one switch, hands out no job of any
/// kind while a run under way finishes; continuing hands out again; and "Stop now" on a project
/// pauses it and tells every run of it under way to stop, as stopping one run does. Each pause is
/// <see cref="RunDecision"/>'s first reason, everything paused before a project's own. Against real
/// Postgres and the fake code host. See "Jobs" and "Stops" in devflow/README.md.
/// </summary>
public sealed class PauseTests
{
    /// <summary>The second project's repository, fodr.</summary>
    private const long Fodr = 2002;

    private static readonly Guid FodrWorker = Guid.Parse("0190a8f0-0000-7000-8000-00000000f0d0");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APausedProjectHandsOutNothing()
    {
        // A build.
        await using (var jobs = await Jobs.Start())
        {
            await HeldThenHanded(jobs, Job.Build);
        }

        // A review, once its card is built.
        await using (var jobs = await Jobs.Start())
        {
            await Built.Run(jobs, Built.Prepared(Built.Report()));
            Fixed.Later(jobs);
            await HeldThenHanded(jobs, Job.Review);
        }

        // A fix, once its review asked for changes; the project read on its minute so its default
        // branch stays watched.
        await using (var jobs = await Jobs.Start())
        {
            await Fixed.BuiltAndReviewed(jobs);
            Fixed.Later(jobs);
            Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
            await HeldThenHanded(jobs, Job.Fix);
        }

        // A conflict, once the feature's cards landed and main moved on in the files they changed.
        await using (var jobs = await Ending.Feature())
        {
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            await Reviewing.ReadAgain(jobs);
            await HeldThenHanded(jobs, Job.Conflict);
        }

        // A whole feature's review, once its pull request is open.
        await using (var jobs = await Ending.Feature())
        {
            await Reviewing.Published(jobs);
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            await HeldThenHanded(jobs, Job.FeatureReview);
        }

        // A whole feature's fix, once its review asked for changes.
        await using (var jobs = await Ending.Feature())
        {
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            await HeldThenHanded(jobs, Job.FeatureFix);
        }

        // A reading of a card not read yet.
        await using (var jobs = await Jobs.Start(read: false))
        {
            await HeldThenHanded(jobs, Job.Read);
        }

        // The check of a setup version that has none that passed.
        await using (var jobs = await Jobs.Start(checkedRecipe: false))
        {
            await HeldThenHanded(jobs, Job.RecipeCheck);
        }

        // A setup run, for a worker built from no recipe.
        await using (var jobs = await Setup.SetupRunTests.Unset())
        {
            await HeldThenHanded(jobs, Job.Setup, Setup.SetupRunTests.SetupWorker);
        }
    }

    [Fact]
    public async Task ARunUnderWayFinishes()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        var job = await jobs.Job();
        var card = (int)job["card"]!["number"]!;

        await Pause(jobs, true);

        // Its result is recorded as always, and its card moves on, to its pull request.
        var finished = await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), Jobs.Result(Jobs.FirstWorker, (string)job["branch"]!));
        Assert.Equal(StatusCodes.Status204NoContent, finished.Response.StatusCode);
        Assert.Equal("succeeded", Assert.Single(await jobs.Runs()).Result);
        Assert.Equal("checking", (await jobs.Card(card)).State);

        // What follows it waits: the other card's build is not handed out to the worker now free.
        Fixed.Later(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Single(await jobs.Runs());
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));
    }

    [Fact]
    public async Task ContinuingHandsOutAgain()
    {
        await using var jobs = await Jobs.Start();

        await Pause(jobs, true);
        var pausedAt = await ProjectPausedAt(jobs);
        Assert.Equal(jobs.Routes.Time.GetUtcNow().UtcDateTime, pausedAt);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Pausing what is paused changes nothing, its time kept.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(5));
        await Pause(jobs, true);
        Assert.Equal(pausedAt, await ProjectPausedAt(jobs));

        await Pause(jobs, false);
        Assert.Null(await ProjectPausedAt(jobs));
        // Continuing what runs changes nothing.
        await Pause(jobs, false);
        Assert.Null(await ProjectPausedAt(jobs));

        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task EverythingPausedHoldsEveryProject()
    {
        await using var jobs = await Jobs.Start();
        var (fodr, fodrKey) = await SecondProject(jobs);

        // Another user's project, which everything paused of mine never holds.
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");

        Assert.Equal((false, (DateTime?)null), await All(jobs));
        await PauseAll(jobs, true);
        Assert.Equal((true, (DateTime?)jobs.Routes.Time.GetUtcNow().UtcDateTime), await All(jobs));

        // Neither project of mine hands out its build, and each answers everything paused.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(FodrWorker, fodrKey)).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs, fodr, FodrWorker));

        // Bob's is not held by it.
        var bobsSituation = await RunDecision.ReadForPage(jobs.Routes.Door, bob, bobs, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(bobsSituation);
        Assert.IsNotType<WaitReason.AllPaused>((RunDecision.Of(bobsSituation) as Decision.Wait)?.Reason);
        Assert.IsNotType<WaitReason.Paused>((RunDecision.Of(bobsSituation) as Decision.Wait)?.Reason);

        // Pausing everything again keeps its time; continuing it hands out in both.
        var pausedAt = (await All(jobs)).PausedAt;
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(5));
        await PauseAll(jobs, true);
        Assert.Equal(pausedAt, (await All(jobs)).PausedAt);
        await PauseAll(jobs, false);
        Assert.Equal((false, (DateTime?)null), await All(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
        var fodrJob = await jobs.Next(FodrWorker, fodrKey);
        Assert.Equal(StatusCodes.Status200OK, fodrJob.Response.StatusCode);
    }

    [Fact]
    public async Task EverythingIsLayeredOverAProject()
    {
        await using var jobs = await Jobs.Start();

        await Pause(jobs, true);
        await PauseAll(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));

        // Continuing everything leaves the project paused on its own.
        await PauseAll(jobs, false);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));
        Assert.NotNull(await ProjectPausedAt(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task AllPausedComesFirst()
    {
        await using var jobs = await Jobs.Start();

        // Paused and stopped by a commit that landed on main.
        await Pause(jobs, true);
        await Merging.Merged.Stop(jobs);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));

        // With everything paused too.
        await PauseAll(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));
    }

    [Fact]
    public async Task StopNowPausesAndStopsEveryRun()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        await jobs.SayHello(Jobs.SecondWorker, name: "second-worker");
        var first = await jobs.Job(Jobs.FirstWorker);
        var second = await jobs.Job(Jobs.SecondWorker);
        var firstRun = (Guid)first["job_id"]!;
        var secondRun = (Guid)second["job_id"]!;

        // fodr, another project of mine, with a run under way, which stopping this one leaves alone.
        var (fodr, fodrKey) = await SecondProject(jobs);
        var fodrJob = await jobs.Next(FodrWorker, fodrKey);
        Assert.Equal(StatusCodes.Status200OK, fodrJob.Response.StatusCode);
        var fodrRun = (Guid)(await Workers.Workers.Json(fodrJob))["job_id"]!;

        var stopped = await StopNow(jobs, jobs.Project);
        Assert.Equal(StatusCodes.Status200OK, stopped.Response.StatusCode);
        Assert.Equal(2, (int)JsonNode.Parse(await Harness.Body(stopped))!["stopped"]!);

        // The project is paused, and both runs are marked as stopping one marks it.
        var now = jobs.Routes.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(now, await ProjectPausedAt(jobs));
        Assert.Equal(now, await StopRequestedAt(jobs, firstRun));
        Assert.Equal(now, await StopRequestedAt(jobs, secondRun));
        Assert.Null(await StopRequestedAt(jobs, fodrRun));
        Assert.Null(await ProjectPausedAt(jobs, fodr));

        // Each run's worker ends it stopped, and its card lands as stopping one run leaves it.
        var ended = await jobs.Post(WorkerApi.ResultPath(firstRun), StopRunTests.Stopped(Jobs.FirstWorker, (string)first["branch"]!));
        Assert.Equal(StatusCodes.Status204NoContent, ended.Response.StatusCode);
        var card = await jobs.Card((int)first["card"]!["number"]!);
        Assert.Equal(("stopped", DevFlow.Jobs.RunStore.StoppedByYou), (card.State, card.Reason));

        // With none under way it pauses, and says none was stopped.
        await Pause(jobs, false);
        await using var idle = await Jobs.Start();
        var none = await StopNow(idle, idle.Project);
        Assert.Equal(StatusCodes.Status200OK, none.Response.StatusCode);
        Assert.Equal(0, (int)JsonNode.Parse(await Harness.Body(none))!["stopped"]!);
        Assert.NotNull(await ProjectPausedAt(idle));
        Assert.Equal(StatusCodes.Status204NoContent, (await idle.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task RoutesAreTheUsersOwn()
    {
        await using var jobs = await Jobs.Start();
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");

        // Another user's project, and one never made, are none of mine.
        foreach (var project in new[] { bobs, Guid.CreateVersion7() })
        {
            Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.SendJson("PUT", PausedPath(project), Contract.Token, """{"paused": true}""")).Response.StatusCode);
            Assert.Equal(StatusCodes.Status404NotFound, (await StopNow(jobs, project)).Response.StatusCode);
        }

        Assert.Null(await ProjectPausedAt(jobs, bobs));

        // A body without paused is refused, for a project and for everything.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await jobs.Routes.App.SendJson("PUT", PausedPath(jobs.Project), Contract.Token, "{}")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await jobs.Routes.App.SendJson("PUT", AllPath, Contract.Token, "{}")).Response.StatusCode);

        // A change from another origin is refused before it is read, and without a token there is
        // nothing to read.
        foreach (var (method, path) in new[] { ("PUT", PausedPath(jobs.Project)), ("PUT", AllPath), ("POST", StopNowPath(jobs.Project)) })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, (await jobs.Routes.App.Send(method, path, Contract.Token, ("Origin", "https://evil.test"))).Response.StatusCode);
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, (await jobs.Routes.App.Get(AllPath)).Response.StatusCode);
        Assert.Null(await ProjectPausedAt(jobs));
        Assert.Equal((false, (DateTime?)null), await All(jobs));
    }

    private const string AllPath = "/devflow/api/paused";

    private static string PausedPath(Guid project) => $"/devflow/api/projects/{project}/paused";

    private static string StopNowPath(Guid project) => $"/devflow/api/projects/{project}/stop-now";

    /// <summary>The owner pauses the project, or continues it, as its page does.</summary>
    internal static async Task Pause(Jobs jobs, bool paused)
    {
        var response = await jobs.Routes.App.SendJson("PUT", PausedPath(jobs.Project), Contract.Token, new JsonObject { ["paused"] = paused }.ToJsonString());
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    /// <summary>The owner pauses everything, or continues it, as Running now's switch does.</summary>
    internal static async Task PauseAll(Jobs jobs, bool paused)
    {
        var response = await jobs.Routes.App.SendJson("PUT", AllPath, Contract.Token, new JsonObject { ["paused"] = paused }.ToJsonString());
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    private static Task<HttpContext> StopNow(Jobs jobs, Guid project) =>
        jobs.Routes.App.SendJson("POST", StopNowPath(project), Contract.Token, "{}");

    /// <summary>Whether everything is paused, and since when, as the route answers it.</summary>
    private static async Task<(bool Paused, DateTime? PausedAt)> All(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get(AllPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var body = JsonNode.Parse(await Harness.Body(response))!;
        return ((bool)body["paused"]!, body["paused_at"] is { } at ? ((DateTimeOffset)at).UtcDateTime : null);
    }

    /// <summary>
    /// With the job of <paramref name="kind"/> due for <paramref name="worker"/>, the project
    /// paused hands it nothing and answers <see cref="WaitReason.Paused"/>; continued, the job is
    /// handed out.
    /// </summary>
    private static async Task HeldThenHanded(Jobs jobs, string kind, Guid? worker = null)
    {
        var asking = worker ?? Jobs.FirstWorker;
        await Pause(jobs, true);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(asking)).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs, jobs.Project, asking));

        await Pause(jobs, false);
        var handed = await jobs.Next(asking);
        Assert.Equal(StatusCodes.Status200OK, handed.Response.StatusCode);
        Assert.Equal(kind, (string)(await Workers.Workers.Json(handed))["kind"]!);
    }

    /// <summary>The rule's decision for the project as <paramref name="worker"/> finds it, the first
    /// worker in the jobs' project unless given.</summary>
    private static async Task<Decision> Rule(Jobs jobs, Guid? project = null, Guid? worker = null)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.User, project ?? jobs.Project, worker ?? Jobs.FirstWorker, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return RunDecision.Of(situation);
    }

    /// <summary>
    /// fodr, a second project of the user's in the same installation, with one card and
    /// project.json, read once so its card is picked, and a worker of its own said hello with its
    /// own key. The project and that key.
    /// </summary>
    private static async Task<(Guid Project, string Key)> SecondProject(Jobs jobs)
    {
        var world = jobs.Fake.World(Jobs.User);
        var installation = world.Installations[0];
        var repository = new FakeRepository(Fodr, "Adamniels", "fodr", [], [], [],
            [new FakeFile(".devflow/project.json", "main", Encoding.UTF8.GetBytes(Jobs.ProjectFile()))],
            [Jobs.Card(5)]);
        jobs.Fake.Replace(Jobs.User, world with { Installations = [installation with { Repositories = [.. installation.Repositories, repository] }] });
        var project = await jobs.Routes.Project(Jobs.User, Fodr, "fodr");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET waits_for_owner = 'never' WHERE project_id = @project",
                new { project },
                cancellationToken: Ct));
        }

        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(project, Jobs.User));
        var key = await Workers.Workers.NewKey(jobs.Routes, project);
        var hello = new JsonObject { ["worker_id"] = FodrWorker, ["name"] = "fodr-worker", ["recipe_version"] = "1", ["base_image"] = Jobs.Image };
        var said = await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(key));
        Assert.Equal(StatusCodes.Status204NoContent, said.Response.StatusCode);
        return (project, key);
    }

    private static async Task<DateTime?> ProjectPausedAt(Jobs jobs, Guid? project = null)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var at = await admin.QuerySingleAsync<DateTime?>(new CommandDefinition(
            "SELECT paused_at FROM projects WHERE id = @project",
            new { project = project ?? jobs.Project },
            cancellationToken: Ct));
        return at?.ToUniversalTime();
    }

    private static async Task<DateTime?> StopRequestedAt(Jobs jobs, Guid run)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var at = await admin.QuerySingleAsync<DateTime?>(new CommandDefinition(
            "SELECT stop_requested_at FROM runs WHERE id = @run",
            new { run },
            cancellationToken: Ct));
        return at?.ToUniversalTime();
    }
}
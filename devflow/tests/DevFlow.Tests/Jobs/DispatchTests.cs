using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Handing out work: <c>jobs/next</c> answers a worker its project's next Queued card, in the
/// order cards are handed out, moves it to building and records a run, in one transaction as the
/// project's user, and answers nothing when the user has no Claude login or one past its renewal,
/// or the worker needs a rebuild. The job holds what the worker needs and nothing more, and a
/// fresh push key goes only to the worker holding a running job. Against the fake and a real
/// Postgres. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class DispatchTests
{
    private static CancellationToken Ct => Jobs.Ct;

    [Fact]
    public async Task AWorkerGetsItsProjectsNextCard()
    {
        // Three queued cards: a low one written first, a high one, and one with no priority. The
        // high one goes first, whatever its age.
        await using var jobs = await Jobs.Start([
            Jobs.Card(1, labels: ["devflow", "priority: low"]),
            Jobs.Card(2),
            Jobs.Card(3, labels: ["devflow", "priority: high"]),
        ]);

        var job = await jobs.Job();

        Assert.Equal(3, (int)job["card"]!["number"]!);
        var card = await jobs.Card(3);
        Assert.Equal("building", card.State);
        Assert.Null(card.Reason);
        Assert.All((await jobs.Cards()).Where(each => each.Number != 3), each => Assert.Equal("queued", each.State));

        var run = Assert.Single(await jobs.Runs());
        Assert.Equal((Guid)job["job_id"]!, run.Id);
        Assert.Equal(Jobs.FirstWorker, run.WorkerId);
        Assert.Equal(3, run.CardNumber);
        Assert.Equal(Jobs.User, run.UserId);
        Assert.Equal("build", run.Kind);
        Assert.Null(run.FinishedAt);
        Assert.Null(run.Result);

        // The next ask takes the next card, no priority before low.
        Assert.Equal(2, (int)(await jobs.Job())["card"]!["number"]!);
        Assert.Equal(1, (int)(await jobs.Job())["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task TwoWorkersNeverGetTheSameCard()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)]);
        await jobs.SayHello(Jobs.SecondWorker);
        var door = jobs.Routes.Door;
        var now = jobs.Routes.Time.GetUtcNow();

        // The first takes the card and holds its transaction open while the second tries: the
        // second skips the locked row and finds nothing to take. No race is fired; the order is
        // fixed.
        var (first, second) = await door.InUserTransaction(Jobs.User, async transaction =>
        {
            var taken = await JobStore.Claim(transaction, jobs.Project, Jobs.User, Jobs.FirstWorker, "main", Jobs.Check, now, Ct);
            var other = await door.InUserTransaction(Jobs.User, inner => JobStore.Claim(inner, jobs.Project, Jobs.User, Jobs.SecondWorker, "main", Jobs.Check, now, Ct), Ct);
            return (taken, other);
        }, Ct);

        Assert.NotNull(first);
        Assert.Equal(1, first.Number);
        Assert.Null(second);

        // Once the first has committed, there is still nothing: the card is building.
        Assert.Null(await door.InUserTransaction(Jobs.User, transaction => JobStore.Claim(transaction, jobs.Project, Jobs.User, Jobs.SecondWorker, "main", Jobs.Check, now, Ct), Ct));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal(Jobs.FirstWorker, run.WorkerId);
    }

    [Fact]
    public async Task ACardWithARunUnderWayIsNotHandedOutAgain()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2, minutes: 1)]);
        await jobs.SayHello(Jobs.SecondWorker);
        Assert.Equal(1, (int)(await jobs.Job())["card"]!["number"]!);

        // The card is queued again while its run is under way, as a read that ignores it and a
        // later one that picks it again would leave it.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET state = 'queued' WHERE project_id = @project AND number = 1", new { project = jobs.Project }, cancellationToken: Ct));
        }

        // It is passed over, rather than taken a second time against the one run it may have:
        // the next card goes to the next worker, and then there is nothing.
        Assert.Equal(2, (int)(await jobs.Job(Jobs.SecondWorker))["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal(2, (await jobs.Runs()).Count);
    }

    [Fact]
    public async Task AWorkerNeverGetsAnotherProjectsCard()
    {
        // The project has no card; another project of the same user has one.
        await using var jobs = await Jobs.Start([]);
        var other = await jobs.Routes.Project(Jobs.User, repository: 9002, name: "fodr");
        var world = jobs.Fake.World(Jobs.User);
        var both = Jobs.World([Jobs.Card(7)], Jobs.ProjectFile(), 9002, "fodr").Installations[0].Repositories[0];
        jobs.Fake.Replace(Jobs.User, world with
        {
            Installations = [world.Installations[0] with { Repositories = [.. world.Installations[0].Repositories, both] }],
        });
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(other, Jobs.User));
        Assert.Equal("queued", Assert.Single(await jobs.Cards(other)).State);

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", Assert.Single(await jobs.Cards(other)).State);
        Assert.Empty(await jobs.Runs(other));

        // The other project's own worker takes it.
        var otherKey = await Workers.Workers.NewKey(jobs.Routes, other);
        var hello = new JsonObject { ["worker_id"] = Jobs.SecondWorker, ["name"] = "fodr-worker", ["recipe_version"] = "1", ["base_image"] = Jobs.Image };
        await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(otherKey));
        var response = await jobs.Next(Jobs.SecondWorker, otherKey);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(7, (int)(await Workers.Workers.Json(response))["card"]!["number"]!);
    }

    [Fact]
    public async Task AWorkerNeverGetsAnotherUsersCard()
    {
        // Alice's project has no card; Bob's has one, with his own login, link and worker.
        await using var jobs = await Jobs.Start([]);
        var bob = Workers.Workers.Bob;
        var bobs = await jobs.Routes.Project(bob, repository: 9002, name: "bobs");
        await Reading.Reading.Link(jobs.Routes.Door, bob, "octo-bob");
        var card = Jobs.Card(5) with { Author = "octo-bob" };
        jobs.Fake.Replace(bob, Jobs.World([card], Jobs.ProjectFile(), 9002, "bobs"));
        await jobs.Routes.App.Services.GetRequiredService<ClaudeLogins>().Set(bob, "sk-ant-oat01-bob-test-token", Ct);
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(bobs, bob));
        Assert.Equal("queued", Assert.Single(await jobs.Cards(bobs)).State);

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", Assert.Single(await jobs.Cards(bobs)).State);

        // Bob's worker gets it, with Bob's token, never Alice's.
        var bobsKey = await Workers.Workers.KeyOf(jobs.Routes, bob, bobs);
        var hello = new JsonObject { ["worker_id"] = Jobs.SecondWorker, ["name"] = "bobs-worker", ["recipe_version"] = "1", ["base_image"] = Jobs.Image };
        await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(bobsKey));
        var response = await jobs.Next(Jobs.SecondWorker, bobsKey);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var job = await Workers.Workers.Json(response);
        Assert.Equal(5, (int)job["card"]!["number"]!);
        Assert.Equal("sk-ant-oat01-bob-test-token", (string)job["claude_token"]!);
        Assert.Equal(bob, Assert.Single(await jobs.Runs(bobs)).UserId);
    }

    [Fact]
    public async Task NoWorkWithoutAClaudeLoginOrWhenStopped()
    {
        // No login: nothing, and the project's page says why.
        await using var jobs = await Jobs.Start([Jobs.Card(1)], login: false);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Equal("no_claude_login", (string?)(await Workers.Workers.List(jobs.Routes, jobs.Project))["held"]);

        // A login past its renewal: nothing.
        await jobs.Routes.App.Services.GetRequiredService<ClaudeLogins>().Set(Jobs.User, Jobs.ClaudeToken, Ct);
        await jobs.AgeLogin(366);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("claude_login_expired", (string?)(await Workers.Workers.List(jobs.Routes, jobs.Project))["held"]);

        // Due soon is still a login that works.
        await jobs.AgeLogin(340);
        Assert.Null((string?)(await Workers.Workers.List(jobs.Routes, jobs.Project))["held"]);

        // A worker that needs a rebuild: nothing, and the page already says so for that worker.
        await jobs.SayHello(Jobs.SecondWorker, "platform-hosted/devflow-worker:2026.01.01");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Empty(await jobs.Runs());
        var listed = (await Workers.Workers.List(jobs.Routes, jobs.Project))["workers"]!.AsArray();
        Assert.Equal("base_image", (string?)listed.Single(worker => (Guid)worker!["id"]! == Jobs.SecondWorker)!["rebuild"]);

        // A worker on the current image takes it.
        Assert.Equal(1, (int)(await jobs.Job())["card"]!["number"]!);
    }

    [Fact]
    public async Task TheJobHoldsWhatTheWorkerNeedsAndNoMore()
    {
        await using var jobs = await Jobs.Start(
            [Jobs.Card(12, "Show the habits' empty state!", "The list is blank when there are none.\n\n```js\nalert(1)\n```")],
            projectFile: Jobs.ProjectFile(start: "npm ci && npm run dev", url: "http://localhost:5173"));

        var job = await jobs.Job();

        Assert.Equal(
            ["base", "branch", "brief", "card", "check", "claude_token", "committer", "job_id", "kind", "limits", "push_key", "push_key_expires_at", "report_schema", "repository", "start", "to_fix", "to_resolve", "to_review", "url"],
            job.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        // How the worker starts the project for its pictures, from the same project.json as the
        // check.
        Assert.Equal("npm ci && npm run dev", (string)job["start"]!);
        Assert.Equal("http://localhost:5173", (string)job["url"]!);
        Assert.Equal((Guid)job["job_id"]!, Assert.Single(await jobs.Runs()).Id);
        Assert.Equal("build", (string)job["kind"]!);
        // A build reads nothing besides the code and the card, and starts no fix.
        Assert.Null(job["to_review"]);
        Assert.Null(job["to_fix"]);

        var card = job["card"]!.AsObject();
        Assert.Equal(["body", "number", "title"], card.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(12, (int)card["number"]!);
        Assert.Equal("Show the habits' empty state!", (string)card["title"]!);
        Assert.Equal("The list is blank when there are none.\n\n```js\nalert(1)\n```", (string)card["body"]!);

        var repository = job["repository"]!.AsObject();
        Assert.Equal(["clone_address", "default_branch", "name", "owner"], repository.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Adamniels", (string)repository["owner"]!);
        Assert.Equal("Personal-platform", (string)repository["name"]!);
        Assert.Equal("main", (string)repository["default_branch"]!);
        var platform = new DevFlow.CodeHost.Repository(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");
        var clone = Assert.IsType<CodeHostResult<Uri>.Found>(await jobs.Fake.CloneAddress(Jobs.User, platform, Ct)).Value;
        Assert.Equal(clone, new Uri((string)repository["clone_address"]!));

        Assert.Equal("devflow/12-show-the-habits-empty-state", (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        Assert.Equal(Jobs.Check, (string)job["check"]!);
        Assert.Contains("The list is blank when there are none.", (string)job["brief"]!, StringComparison.Ordinal);
        Assert.Equal(DevFlow.Protocol.Report.Schema().ToJsonString(), job["report_schema"]!.ToJsonString());

        // The token is the run's user's.
        Assert.Equal(Jobs.ClaudeToken, (string)job["claude_token"]!);

        // The push key is the fake's, narrowed to Push on the project's repository, for an hour.
        var key = (string)job["push_key"]!;
        Assert.Equal((Reading.Reading.Installation, Reading.Reading.RepositoryId, WritePurpose.Push), jobs.Fake.Issued(Jobs.User, key));
        Assert.Equal(jobs.Routes.Time.GetUtcNow() + FakeCodeHost.KeyLifetime, DateTimeOffset.Parse((string)job["push_key_expires_at"]!, System.Globalization.CultureInfo.InvariantCulture));

        var committer = job["committer"]!.AsObject();
        Assert.Equal(FakeCodeHost.AppLogin, (string)committer["name"]!);
        Assert.Equal(FakeCodeHost.AppEmail, (string)committer["email"]!);

        var limits = job["limits"]!.AsObject();
        Assert.Equal(200, (int)limits["turns"]!);
        Assert.Equal(25m, (decimal)limits["budget_usd"]!);
        Assert.Equal(90, (int)limits["minutes"]!);

        // There was one card, and it is taken.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task AJobIsKeptByNoCache()
    {
        await using var jobs = await Jobs.Start();
        var response = await jobs.Next();
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("no-store", response.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AFreshPushKeyOnlyForTheJobsWorker()
    {
        await using var jobs = await Jobs.Start();
        await jobs.SayHello(Jobs.SecondWorker);
        var job = await jobs.Job();
        var id = (Guid)job["job_id"]!;
        var path = DevFlow.Protocol.WorkerApi.PushKeyPath(id);

        // The worker holding it gets a new one, for the same repository and purpose.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(50));
        var fresh = await jobs.Post(path, new JsonObject { ["worker_id"] = Jobs.FirstWorker });
        Assert.Equal(StatusCodes.Status200OK, fresh.Response.StatusCode);
        Assert.Equal("no-store", fresh.Response.Headers.CacheControl.ToString());
        var answer = await Workers.Workers.Json(fresh);
        var key = (string)answer["push_key"]!;
        Assert.NotEqual((string)job["push_key"]!, key);
        Assert.Equal((Reading.Reading.Installation, Reading.Reading.RepositoryId, WritePurpose.Push), jobs.Fake.Issued(Jobs.User, key));
        Assert.Equal(jobs.Routes.Time.GetUtcNow() + FakeCodeHost.KeyLifetime, DateTimeOffset.Parse((string)answer["expires_at"]!, System.Globalization.CultureInfo.InvariantCulture));

        // Another worker of the same project, a job that is not one, and no worker id.
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(path, new JsonObject { ["worker_id"] = Jobs.SecondWorker })).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(DevFlow.Protocol.WorkerApi.PushKeyPath(Guid.CreateVersion7()), new JsonObject { ["worker_id"] = Jobs.FirstWorker })).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await jobs.Post(path, new JsonObject())).Response.StatusCode);

        // A finished job: nothing more.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE runs SET finished_at = now(), result = 'succeeded' WHERE id = @id", new { id }, cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(path, new JsonObject { ["worker_id"] = Jobs.FirstWorker })).Response.StatusCode);
    }

    [Fact]
    public async Task NothingIsHandedOutWhenTheProjectFileCannotBeRead()
    {
        // No project.json on the default branch: no check, so no work, and the card waits.
        await using var jobs = await Jobs.Start([Jobs.Card(1)]);
        var world = jobs.Fake.World(Jobs.User);
        var repository = world.Installations[0].Repositories[0] with { Files = [] };
        jobs.Fake.Replace(Jobs.User, world with { Installations = [world.Installations[0] with { Repositories = [repository] }] });

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Empty(await jobs.Runs());

        // GitHub away: the same.
        jobs.Fake.Replace(Jobs.User, Jobs.World([Jobs.Card(1)], Jobs.ProjectFile()));
        jobs.Fake.SetUnavailable(Jobs.User, true);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Empty(await jobs.Runs());
    }

    [Theory]
    [InlineData("Show the habits' empty state!", 12, "devflow/12-show-the-habits-empty-state")]
    [InlineData("  --Ändra   SPRÅK--  ", 3, "devflow/3-ndra-spr-k")]
    [InlineData("日本語", 4, "devflow/4")]
    [InlineData("A title that runs on far longer than forty characters ever should", 5, "devflow/5-a-title-that-runs-on-far-longer-than-for")]
    [InlineData("abcdefghij abcdefghij abcdefghij abcdef next", 6, "devflow/6-abcdefghij-abcdefghij-abcdefghij-abcdef")]
    public void TheBranchIsMadeFromTheTitle(string title, int number, string branch) =>
        Assert.Equal(branch, JobStore.Branch(number, title));

    [Fact]
    public async Task ACardPickedBeforeItsBodyWasKeptWaitsForTheNextRead()
    {
        // A card picked before bodies were kept has none until its next read, and is not handed
        // out without one.
        await using var jobs = await Jobs.Start([Jobs.Card(1)]);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("UPDATE cards SET picked_body = NULL", cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal(1, (int)(await jobs.Job())["card"]!["number"]!);
    }
}
using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Dev flow on a test server with one project of the fixture token's user, ready to hand out work:
/// its owner linked, its world staged with <c>.devflow/project.json</c> on the default branch and
/// the cards given, its key made, one worker that has said hello on the image the app expects, a
/// Claude login unless a test says otherwise, and the project read once so its cards are picked.
/// See "Jobs" in devflow/README.md.
/// </summary>
internal sealed class Jobs : IAsyncDisposable
{
    public const string Owner = "octo-owner";
    public const string Check = "npm ci && npm test";
    public const string ClaudeToken = "sk-ant-oat01-alice-test-token";

    /// <summary>The base image the harness's settings name, so a worker on it needs no rebuild.</summary>
    public const string Image = "platform-hosted/devflow-worker:dev";

    public static readonly Guid FirstWorker = Guid.Parse("0190a8f0-0000-7000-8000-00000000f001");
    public static readonly Guid SecondWorker = Guid.Parse("0190a8f0-0000-7000-8000-00000000f002");

    private static readonly DateTimeOffset At = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    private Jobs(Routes routes, Guid project, string key)
    {
        Routes = routes;
        Project = project;
        Key = key;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Guid User => Routes.User;

    public Routes Routes { get; }

    public Guid Project { get; }

    public string Key { get; }

    public FakeCodeHost Fake => Routes.Fake;

    /// <summary>The project.json the world holds unless a test gives another: no start and no url
    /// unless given.</summary>
    public static string ProjectFile(string check = Check, string start = "", string url = "") =>
        new JsonObject { ["version"] = 1, ["check"] = check, ["start"] = start, ["url"] = url }.ToJsonString();

    /// <summary>A card the owner wrote and nobody edited, carrying the pick-up label.</summary>
    public static FakeIssue Card(int number, string? title = null, string? body = null, IReadOnlyList<string>? labels = null, int minutes = 0) => new(
        number,
        title ?? $"Card {number}",
        Owner,
        labels ?? ["devflow"],
        body ?? $"Build the thing of card {number}.",
        new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"),
        At.AddMinutes(minutes == 0 ? number : minutes),
        true,
        [],
        []);

    /// <summary>A world with one repository, <see cref="Reading.Reading.RepositoryId"/> unless
    /// given, holding <paramref name="cards"/> and, unless null, project.json on main.</summary>
    public static FakeWorld World(IReadOnlyList<FakeIssue> cards, string? projectFile, long repository = Reading.Reading.RepositoryId, string name = "Personal-platform") =>
        new([
            new FakeInstallation(Reading.Reading.Installation, "Adamniels",
            [
                new FakeRepository(repository, "Adamniels", name, [], [], [],
                    projectFile is null ? [] : [new FakeFile(".devflow/project.json", "main", Encoding.UTF8.GetBytes(projectFile))],
                    cards),
            ]),
        ]);

    /// <summary>The project ready to hand out <paramref name="cards"/>, one card unless given.
    /// With <paramref name="signIn"/>, the owner's GitHub is linked all the way, through the fake's
    /// sign in as the Projects tab links it, so Dev flow holds a token that merges as him; otherwise
    /// only the link's row is written, which is all reading and handing out need.</summary>
    /// <param name="configure">More, run after the harness's own, such as a service of a test's
    /// own.</param>
    /// <param name="read">Whether every card the harness's reads pick is taken as already read and
    /// ready, <see cref="Routes.CardsRead"/>; false for a test of reading itself, whose cards are
    /// read by a reading job first.</param>
    public static async Task<Jobs> Start(IReadOnlyList<FakeIssue>? cards = null, bool login = true, string? projectFile = null, bool signIn = false, Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null, bool read = true)
    {
        var routes = await Routes.Start(configure);
        routes.CardsRead = read;
        try
        {
            var project = await routes.Project(User);
            routes.Fake.Replace(User, World(cards ?? [Card(1)], projectFile ?? ProjectFile()));
            if (signIn)
            {
                await SignIn(routes);
            }
            else
            {
                await Reading.Reading.Link(routes.Door, User, Owner);
            }

            var jobs = new Jobs(routes, project, await Workers.Workers.NewKey(routes, project));
            await jobs.SayHello(FirstWorker);
            if (login)
            {
                await routes.App.Services.GetRequiredService<ClaudeLogins>().Set(User, ClaudeToken, Ct);
            }

            Assert.Equal(ReadResult.Read, await routes.Read(project, User));
            return jobs;
        }
        catch
        {
            await routes.DisposeAsync();
            throw;
        }
    }

    /// <summary>Links the user's GitHub account all the way, as the Projects tab and the fake's
    /// page do, as the fake's default user, <see cref="Owner"/>.</summary>
    private static async Task SignIn(Routes routes)
    {
        var started = await routes.App.Send("POST", Link.Linking.StartPath, Contract.Token, ("Origin", Link.Linking.Origin));
        Assert.Equal(StatusCodes.Status200OK, started.Response.StatusCode);
        var query = Link.Linking.Query(new Uri((string)JsonNode.Parse(await Harness.Body(started))!["authorize_url"]!));
        var code = routes.Fake.Grant(User, query["code_challenge"], Link.Linking.Redirect);
        var returned = await routes.App.Send("GET", $"{DevFlow.Link.GitHubLink.CallbackPath}?code={code}&state={query["state"]}", Contract.Token);
        Assert.DoesNotContain("failed", returned.Response.Headers.Location.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The worker says hello in the project, on <paramref name="image"/>.</summary>
    public async Task SayHello(Guid worker, string image = Image)
    {
        var hello = new JsonObject { ["worker_id"] = worker, ["name"] = "fodr-worker", ["recipe_version"] = "1", ["base_image"] = image };
        var response = await Workers.Workers.AsWorker(Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(Key));
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    /// <summary>The worker asks for work with the project's key, or with <paramref name="key"/>.</summary>
    public Task<HttpContext> Next(Guid? worker = null, string? key = null) =>
        Workers.Workers.AsWorker(Routes.App, Workers.Workers.NextPath, Workers.Workers.Next(worker ?? FirstWorker), null, Workers.Workers.Bearer(key ?? Key));

    /// <summary>The job the worker is handed, after checking it is one.</summary>
    public async Task<JsonObject> Job(Guid? worker = null)
    {
        var response = await Next(worker);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Workers.Workers.Json(response)).AsObject();
    }

    /// <summary>A POST of <paramref name="body"/> to one of the job's routes, as the worker.</summary>
    public Task<HttpContext> Post(string path, JsonObject body) =>
        Workers.Workers.AsWorker(Routes.App, path, body.ToJsonString(), null, Workers.Workers.Bearer(Key));

    /// <summary>A report in its form that passes, done, with every field.</summary>
    public static JsonObject GoodReport(string outcome = "done", string stoppedReason = "") => new()
    {
        ["outcome"] = outcome,
        ["stopped_reason"] = stoppedReason,
        ["summary"] = "Added the empty state to the habits list.",
        ["tasks"] = new JsonArray(new JsonObject { ["text"] = "Add the empty state", ["done"] = true }),
        ["decisions"] = new JsonArray(new JsonObject { ["chosen"] = "A plain sentence", ["alternative"] = "An illustration", ["why"] = "Nothing else on the page is drawn", ["for_owner"] = false }),
        ["tests"] = new JsonArray(new JsonObject { ["what"] = "list.test.tsx", ["result"] = "passed" }),
        ["pages"] = new JsonArray(new JsonObject { ["path"] = "/habits", ["why"] = "The empty state shows there", ["looked"] = true }),
        ["installed"] = new JsonArray(),
        ["follow_ups"] = new JsonArray("Count the habits in the title"),
        ["replies"] = new JsonArray(),
        ["handover"] = "The empty state is in list.tsx; its copy is in en.json.",
    };

    /// <summary>Claude Code's usage as the worker sends it, the five fields it keeps.</summary>
    public static JsonObject Usage() => new()
    {
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = 41,
            ["cache_creation_input_tokens"] = 18204,
            ["cache_read_input_tokens"] = 512870,
            ["output_tokens"] = 6113,
        },
        ["modelUsage"] = new JsonObject { ["claude-opus-5-5"] = new JsonObject { ["costUSD"] = 1.8342 } },
        ["total_cost_usd"] = 1.8342,
        ["duration_ms"] = 184213,
        ["num_turns"] = 23,
    };

    /// <summary>A result as a worker sends one: succeeded with <paramref name="report"/> and a
    /// passing check unless a test changes it.</summary>
    public static JsonObject Result(Guid worker, string branch, JsonNode? report = null, int? checkExit = 0) => new()
    {
        ["worker_id"] = worker,
        ["outcome"] = "succeeded",
        ["report"] = report ?? GoodReport(),
        ["failure"] = null,
        ["check"] = checkExit is { } exit ? new JsonObject { ["exit_code"] = exit, ["output"] = "Tests: 12 passed" } : null,
        ["branch"] = branch,
        ["head_commit"] = new string('a', 40),
        ["auth_method"] = "oauth_token",
        ["usage"] = Usage(),
        ["duration_ms"] = 200000,
        ["stderr"] = "a warning",
    };

    /// <summary>A failed result, in the card or outside it.</summary>
    public static JsonObject Failure(Guid worker, string branch, bool inCard, string reason) => new()
    {
        ["worker_id"] = worker,
        ["outcome"] = "failed",
        ["report"] = null,
        ["failure"] = new JsonObject { ["reason"] = reason, ["in_card"] = inCard },
        ["check"] = null,
        ["branch"] = branch,
        ["head_commit"] = null,
        ["auth_method"] = null,
        ["usage"] = new JsonObject(),
        ["duration_ms"] = 1000,
        ["stderr"] = "",
    };

    /// <summary>The project's cards, by number, read past row level security.</summary>
    public async Task<List<CardRow>> Cards(Guid? project = null)
    {
        await using var admin = await Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<CardRow>(new CommandDefinition(
            "SELECT number AS Number, state AS State, reason AS Reason, user_id AS UserId FROM cards WHERE project_id = @project ORDER BY number",
            new { project = project ?? Project },
            cancellationToken: Ct))];
    }

    /// <summary>The card with the number.</summary>
    public async Task<CardRow> Card(int number) => Assert.Single(await Cards(), card => card.Number == number);

    /// <summary>The project's runs, oldest first, read past row level security.</summary>
    public async Task<List<RunRow>> Runs(Guid? project = null)
    {
        await using var admin = await Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<RunRow>(new CommandDefinition(
            """
            SELECT id AS Id, user_id AS UserId, card_number AS CardNumber, worker_id AS WorkerId, kind AS Kind, branch AS Branch,
                   base AS Base, started_at AS StartedAt, checking_at AS CheckingAt, finished_at AS FinishedAt, result AS Result,
                   reason AS Reason, report::text AS Report, check_exit_code AS CheckExitCode, check_output AS CheckOutput,
                   head_commit AS HeadCommit, auth_method AS AuthMethod, usage::text AS Usage, input_tokens AS InputTokens,
                   output_tokens AS OutputTokens, cache_creation_input_tokens AS CacheCreationInputTokens,
                   cache_read_input_tokens AS CacheReadInputTokens, cost_usd AS CostUsd, turns AS Turns, duration_ms AS DurationMs,
                   stderr AS Stderr
            FROM runs WHERE project_id = @project ORDER BY started_at, id
            """,
            new { project = project ?? Project },
            cancellationToken: Ct))];
    }

    /// <summary>Moves when the user's Claude login was given to <paramref name="days"/> before
    /// now.</summary>
    public async Task AgeLogin(int days)
    {
        await using var admin = await Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE claude_logins SET given_at = @at WHERE user_id = @user",
            new { at = Routes.Time.GetUtcNow().AddDays(-days), user = User },
            cancellationToken: Ct));
    }

    public ValueTask DisposeAsync() => Routes.DisposeAsync();

    internal sealed class CardRow
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public Guid UserId { get; init; }
    }

    internal sealed class RunRow
    {
        public Guid Id { get; init; }

        public Guid UserId { get; init; }

        public int CardNumber { get; init; }

        public Guid WorkerId { get; init; }

        public string Kind { get; init; } = "";

        public string Branch { get; init; } = "";

        public string Base { get; init; } = "";

        public DateTime StartedAt { get; init; }

        public DateTime? CheckingAt { get; init; }

        public DateTime? FinishedAt { get; init; }

        public string? Result { get; init; }

        public string? Reason { get; init; }

        public string? Report { get; init; }

        public int? CheckExitCode { get; init; }

        public string? CheckOutput { get; init; }

        public string? HeadCommit { get; init; }

        public string? AuthMethod { get; init; }

        public string? Usage { get; init; }

        public long? InputTokens { get; init; }

        public long? OutputTokens { get; init; }

        public long? CacheCreationInputTokens { get; init; }

        public long? CacheReadInputTokens { get; init; }

        public decimal? CostUsd { get; init; }

        public int? Turns { get; init; }

        public long? DurationMs { get; init; }

        public string? Stderr { get; init; }
    }
}
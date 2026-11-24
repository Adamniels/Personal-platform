using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.Projects;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Projects;

/// <summary>
/// The summary on each project of <c>GET /devflow/api/projects</c>: its cards counted by group,
/// its workers' health as the workers list says it, its setup steps while it sets up, and its open
/// feature furthest on; computed from what Dev flow stored, under row level security. Against a
/// real Postgres as devflow_app. See "Connecting a repository" in devflow/README.md, and "Projects"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ProjectSummaryTests
{
    private const string ProjectsPath = "/devflow/api/projects";

    private static CancellationToken Ct => Reading.Reading.Ct;

    private static Guid User => Routes.User;

    [Fact]
    public async Task CountsCardsByGroup()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(User);
        // Every state the cards table allows, once, and queued and done twice more, so a count is
        // never the right answer by being one.
        string[] states =
        [
            "needs_answers", "waiting_for_owner", "stopped", "refused",
            "reading", "building", "checking", "reviewing", "fixing",
            "queued", "held",
            "done",
            "ignored",
            "queued", "done", "done",
        ];
        for (var number = 1; number <= states.Length; number++)
        {
            await Card(routes, project, User, number, states[number - 1]);
        }

        var summary = await Summary(routes, project);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"needs_you": 4, "in_progress": 5, "queued": 3, "done": 3}"""),
            summary["cards"]));
    }

    [Fact]
    public async Task CountsNoCardsAsZero()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(User);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"needs_you": 0, "in_progress": 0, "queued": 0, "done": 0}"""),
            (await Summary(routes, project))["cards"]));
    }

    /// <summary>Every group names only card states, each in exactly one group, and every state the
    /// cards table allows but <c>ignored</c> is in one.</summary>
    [Fact]
    public void EveryCardStateIsInOneGroupButIgnored()
    {
        string[] allowed =
        [
            "queued", "reading", "needs_answers", "building", "checking", "reviewing", "fixing", "waiting_for_owner",
            "done", "stopped", "refused", "held", "ignored",
        ];
        var grouped = ProjectSummaries.Groups.SelectMany(group => group.Value).ToList();

        Assert.Equal(grouped.Count, grouped.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(allowed.Where(state => state != "ignored").Order(StringComparer.Ordinal), grouped.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaysTheWorkersHealth()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(User);

        // No worker, and a default branch never read: held as the workers list holds it.
        await SameAsTheWorkersList(routes, project, count: 0, lastSeenAt: null, held: "unwatched", cannotRun: null);

        // Two workers, the second seen later; the default branch read now, and no Claude login.
        var key = await Workers.Workers.NewKey(routes, project);
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.Workers.SayHello(routes, key, Guid.CreateVersion7(), name: "fodr-worker-1")).Response.StatusCode);
        routes.Time.Advance(TimeSpan.FromMinutes(2));
        var newest = routes.Time.GetUtcNow();
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.Workers.SayHello(routes, key, Guid.CreateVersion7(), name: "fodr-worker-2")).Response.StatusCode);
        await Watched(routes, project);
        await SameAsTheWorkersList(routes, project, count: 2, lastSeenAt: newest, held: "no_claude_login", cannotRun: null);

        // A Claude login given, and nothing else holding it: work is handed out.
        await routes.App.Services.GetRequiredService<ClaudeLogins>().Set(User, Jobs.Jobs.ClaudeToken, Ct);
        await SameAsTheWorkersList(routes, project, count: 2, lastSeenAt: newest, held: null, cannotRun: null);

        // The login past its renewal, given that long ago, since moving the clock a year on would
        // expire the test's token too.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE claude_logins SET given_at = given_at - @lasts WHERE user_id = @user",
                new { lasts = ClaudeLogins.Lasts, user = User },
                cancellationToken: Ct));
        }

        await SameAsTheWorkersList(routes, project, count: 2, lastSeenAt: newest, held: "claude_login_expired", cannotRun: null);

        // Something landed that Dev flow did not put there, and six runs in a row failed outside
        // their cards.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_stops (project_id, user_id, stopped_at, branch, activity_id, activity_type, actor, commit, landed_at)
                VALUES (@project, @user, @now, 'main', 900, 'push', 'octo-stranger', @commit, @now);
                INSERT INTO project_waits (project_id, user_id, failures, reason, wait_until, since)
                VALUES (@project, @user, 6, 'The usage limit was reached', @now, @now);
                """,
                new { project, user = User, now = routes.Time.GetUtcNow(), commit = new string('a', 40) },
                cancellationToken: Ct));
        }

        await SameAsTheWorkersList(routes, project, count: 2, lastSeenAt: newest, held: "stopped",
            cannotRun: "Dev flow cannot run Personal-platform's cards: The usage limit was reached");
    }

    [Fact]
    public async Task SaysSettingUpWithItsSteps()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(User);
        // Its .devflow/ valid on the default branch and its recipe read: three of six, with the
        // values step, done while no setup run listed any.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET devflow_valid = true, recipe_version = @version WHERE id = @project",
                new { project, version = new string('3', 40) },
                cancellationToken: Ct));
        }

        var setup = await routes.App.Get($"{ProjectsPath}/{project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, setup.Response.StatusCode);
        var steps = JsonNode.Parse(await Harness.Body(setup))!;
        Assert.Equal("setting_up", (string)steps["state"]!);
        Assert.Equal(3, steps["steps"]!.AsArray().Count(step => (bool)step!["done"]!));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"done": 3, "total": 6}"""), (await Summary(routes, project))["setting_up"]));

        // A check passed and the settings accepted: registered, and not setting up.
        await Setup.SetupFixture.AllChecked(routes.Database, project, Ct);
        await Setup.SetupFixture.Accepted(routes.Database, project, Ct);
        var registered = JsonNode.Parse(await Harness.Body(await routes.App.Get($"{ProjectsPath}/{project}/setup", Contract.Token)))!;
        Assert.Equal("registered", (string)registered["state"]!);

        var summary = await Summary(routes, project);
        Assert.True(summary.AsObject().ContainsKey("setting_up"));
        Assert.Null(summary["setting_up"]);
    }

    [Fact]
    public async Task NamesTheOpenFeatureFurthestOn()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(User);
        var other = await routes.Project(User, repository: 9002, name: "habit-tracker");

        // 120 has two landed of three, its ignored card not one of them; 130 one of two; 140, merged
        // with three landed, and 150, ignored, are not open.
        var nextCard = 1;
        async Task Feature(Guid on, int number, string state, params string[] cards)
        {
            var feature = await FeatureRow(routes, on, User, number, state);
            for (var position = 1; position <= cards.Length; position++)
            {
                await Card(routes, on, User, nextCard++, cards[position - 1], feature, position);
            }
        }

        await Feature(project, 120, "building", "done", "done", "building", "ignored");
        await Feature(project, 130, "queued", "done", "queued");
        await Feature(project, 140, "merged", "done", "done", "done");
        await Feature(project, 150, "ignored", "ignored", "ignored", "ignored", "ignored");
        // A card on its own, done, belongs to no feature.
        await Card(routes, project, User, nextCard++, "done");
        // The other project has only a closed feature.
        await Feature(other, 160, "closed", "done");

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"number": 120, "title": "Feature 120", "landed": 2, "total": 3}"""),
            (await Summary(routes, project))["feature"]));
        var none = await Summary(routes, other);
        Assert.True(none.AsObject().ContainsKey("feature"));
        Assert.Null(none["feature"]);
    }

    /// <summary>Each project says when it was last read, its last read that succeeded, as the
    /// waiting list's freshness says it; a read that failed since leaves it as it was, and a
    /// project never read says null.</summary>
    [Fact]
    public async Task SaysWhenEachWasLastRead()
    {
        await using var routes = await Routes.Start();
        var read = await routes.Project(User);
        var never = await routes.Project(User, repository: 9002, name: "habit-tracker");
        var readAt = routes.Time.GetUtcNow() - TimeSpan.FromMinutes(2);
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_reads (project_id, user_id, last_attempt_at, last_success_at, last_error)
                VALUES (@read, @user, @now, @readAt, 'GitHub answered 502')
                """,
                new { read, user = User, now = routes.Time.GetUtcNow(), readAt },
                cancellationToken: Ct));
        }

        var listed = await List(routes);
        JsonNode Of(Guid project) => listed.Single(p => Guid.Parse((string)p!["id"]!) == project)!;
        Assert.Equal($"{readAt:yyyy-MM-ddTHH:mm:ss.FFFFFFF}+00:00", (string?)Of(read)["last_read_at"]);
        Assert.True(Of(never).AsObject().ContainsKey("last_read_at"));
        Assert.Null(Of(never)["last_read_at"]);
    }

    [Fact]
    public async Task ReadsOnlyTheCallersProjects()
    {
        await using var routes = await Routes.Start();
        var bob = Routes.Bob;
        // The same repository, a project each.
        var mine = await routes.Project(User);
        var bobs = await routes.Project(bob);
        await Card(routes, mine, User, 1, "waiting_for_owner");
        var myFeature = await FeatureRow(routes, mine, User, 120, "building");
        await Card(routes, mine, User, 2, "done", myFeature, 1);
        for (var number = 1; number <= 5; number++)
        {
            await Card(routes, bobs, bob, number, "waiting_for_owner");
        }

        var bobsFeature = await FeatureRow(routes, bobs, bob, 120, "building");
        for (var position = 1; position <= 4; position++)
        {
            await Card(routes, bobs, bob, 10 + position, "done", bobsFeature, position);
        }

        var listed = await List(routes);
        var only = Assert.Single(listed);
        Assert.Equal(mine, Guid.Parse((string)only!["id"]!));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"needs_you": 1, "in_progress": 0, "queued": 0, "done": 1}"""),
            only["summary"]!["cards"]));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"number": 120, "title": "Feature 120", "landed": 1, "total": 1}"""),
            only["summary"]!["feature"]));

        // Bob's own list, read as him, counts only his.
        var store = routes.App.Services.GetRequiredService<ProjectStore>();
        var his = Assert.Single(await store.List(bob, Ct));
        Assert.Equal(bobs, his.Id);
        Assert.Equal(new CardCounts(5, 0, 0, 4), his.Summary.Cards);
        Assert.Equal(new FeatureProgress(120, "Feature 120", 4, 4), his.Summary.Feature);
    }

    /// <summary>The project's summary and its workers list agree, and say what is expected.</summary>
    private static async Task SameAsTheWorkersList(Routes routes, Guid project, int count, DateTimeOffset? lastSeenAt, string? held, string? cannotRun)
    {
        var list = await Workers.Workers.List(routes, project);
        Assert.Equal(held, (string?)list["held"]);
        Assert.Equal(cannotRun, (string?)list["cannot_run"]);

        var workers = (await Summary(routes, project))["workers"]!;
        Assert.True(JsonNode.DeepEquals(
            new JsonObject
            {
                ["count"] = count,
                ["last_seen_at"] = lastSeenAt is { } at ? $"{at:yyyy-MM-ddTHH:mm:ss.FFFFFFF}+00:00" : null,
                ["held"] = held,
                ["cannot_run"] = cannotRun,
            },
            workers), workers.ToJsonString());
    }

    /// <summary>The project's default branch taken as read now, as the watch leaves it.</summary>
    private static async Task Watched(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM branch_watches WHERE project_id = @project AND feature_id IS NULL;
            INSERT INTO branch_watches (project_id, user_id, branch, began_at, last_read_at, last_attempt_at)
            VALUES (@project, @user, 'main', @now, @now, @now);
            """,
            new { project, user = User, now = routes.Time.GetUtcNow() },
            cancellationToken: Ct));
    }

    private static async Task<JsonArray> List(Routes routes)
    {
        var response = await routes.App.Get(ProjectsPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!["projects"]!.AsArray();
    }

    private static async Task<JsonNode> Summary(Routes routes, Guid project)
    {
        var listed = (await List(routes)).Single(p => Guid.Parse((string)p!["id"]!) == project)!;
        return listed["summary"] ?? throw new InvalidOperationException($"no summary on {listed.ToJsonString()}");
    }

    /// <summary>A card of the project in <paramref name="state"/>, past row level security, on its
    /// own or at <paramref name="position"/> in <paramref name="feature"/>.</summary>
    private static async Task Card(Routes routes, Guid project, Guid user, int number, string state, Guid? feature = null, int? position = null)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at, feature_id, position,
                               picked_body_sha256, picked_title, picked_body)
            VALUES (gen_random_uuid(), @project, @user, @number, 'Card ' || @number, 'https://github.com/Adamniels/Personal-platform/issues/' || @number,
                    @state, 'none', now(), now(), now(), @feature, @position, sha256('body'::bytea), 'Card ' || @number, 'body')
            """,
            new { project, user, number, state, feature, position },
            cancellationToken: Ct));
    }

    /// <summary>A feature of the project in <paramref name="state"/>, past row level security, as a
    /// read that picked it leaves it.</summary>
    private static async Task<Guid> FeatureRow(Routes routes, Guid project, Guid user, int number, string state)
    {
        var id = Guid.CreateVersion7();
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO features (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at,
                                  picked_body_sha256, picked_title, picked_body)
            VALUES (@id, @project, @user, @number, 'Feature ' || @number, 'https://github.com/Adamniels/Personal-platform/issues/' || @number,
                    @state, 'none', now(), now(), now(), sha256('body'::bytea), 'Feature ' || @number, 'body')
            """,
            new { id, project, user, number, state },
            cancellationToken: Ct));
        return id;
    }
}
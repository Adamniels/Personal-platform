using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// The settings of "sometimes": <c>waits_for_owner</c> takes <c>never</c>, <c>sometimes</c> and
/// <c>always</c>; on <c>sometimes</c> a card's pull request waits for the owner when a ticked
/// condition holds, <c>wait_when_marked</c>, <c>wait_when_paths</c> and <c>wait_when_decision</c>;
/// <c>review_label</c> marks a card for his review; and <c>wait_paths</c> lists the paths he wants
/// to see. Through <c>PUT /devflow/api/projects/{id}/settings</c>, against a real Postgres. See
/// "Settings" in devflow/README.md.
/// </summary>
public sealed class SometimesSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static JsonObject Settings(
        string waits = "sometimes",
        string review = "devflow-review",
        bool marked = true,
        bool paths = true,
        bool decision = true,
        JsonArray? patterns = null,
        string pickup = "devflow",
        string hold = "devflow-hold",
        string attention = "devflow-attention") => new()
        {
            ["pickup_label"] = pickup,
            ["hold_label"] = hold,
            ["attention_label"] = attention,
            ["review_label"] = review,
            ["waits_for_owner"] = waits,
            ["wait_when_marked"] = marked,
            ["wait_when_paths"] = paths,
            ["wait_when_decision"] = decision,
            ["wait_paths"] = patterns ?? [],
            ["trusted_logins"] = new JsonArray(),
            ["model_reading"] = "opus",
            ["model_building"] = "opus",
            ["model_reviewing"] = "opus",
            ["run_minutes"] = 90,
            ["run_turns"] = 200,
            ["fix_rounds_card"] = 3,
            ["fix_rounds_feature"] = 3,
            ["questions"] = true,
        };

    private static JsonArray Patterns(params string[] patterns) => new([.. patterns.Select(pattern => (JsonNode)pattern)]);

    private static Task<HttpContext> Put(Routes routes, Guid project, JsonObject settings) =>
        routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());

    private static async Task<JsonObject> Get(Routes routes, Guid project)
    {
        var answer = await routes.App.Get(PathOf(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    /// <summary>The project's row, read as the superuser past row level security, its patterns one
    /// a line.</summary>
    private static async Task<(string Waits, string Review, bool Marked, bool Paths, bool Decision, string Patterns)> Row(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var row = await admin.QuerySingleAsync(new CommandDefinition(
            """
            SELECT waits_for_owner, review_label, wait_when_marked, wait_when_paths, wait_when_decision, wait_paths
            FROM project_settings WHERE project_id = @project
            """,
            new { project },
            cancellationToken: Ct));
        return ((string)row.waits_for_owner, (string)row.review_label, (bool)row.wait_when_marked, (bool)row.wait_when_paths, (bool)row.wait_when_decision, string.Join('\n', (string[])row.wait_paths));
    }

    private static async Task Refused(Routes routes, Guid project, JsonObject body, string field)
    {
        var answer = await Put(routes, project, body);
        Assert.True(answer.Response.StatusCode == StatusCodes.Status422UnprocessableEntity, $"{body.ToJsonString()} is refused");
        var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
        Assert.True(problem["errors"]!.AsObject().ContainsKey(field), $"{body.ToJsonString()} names {field}");
    }

    [Fact]
    public async Task TheValuesAndTheirRules()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // A new project's: the review label devflow-review, the three conditions ticked, no paths.
        Assert.Equal(("never", "devflow-review", true, true, true, ""), await Row(routes, project));
        Assert.Equal(Settings(waits: "never").ToJsonString(), (await Get(routes, project)).ToJsonString());

        // Each of the three values is taken, and answered as saved.
        foreach (var waits in new[] { "sometimes", "always", "never" })
        {
            var body = Settings(waits: waits, patterns: Patterns("src/auth/**", "*.sql", ".github/"));
            var saved = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
            Assert.Equal(body.ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
            Assert.Equal(body.ToJsonString(), (await Get(routes, project)).ToJsonString());
        }

        // The conditions are read only on sometimes, so any of them may be off on the others.
        var conditions = Settings(waits: "sometimes", review: "Look at me", marked: false, paths: true, decision: false, patterns: Patterns("infra/hosted/compose.yaml"));
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, conditions)).Response.StatusCode);
        Assert.Equal(("sometimes", "Look at me", false, true, false, "infra/hosted/compose.yaml"), await Row(routes, project));
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(waits: "always", marked: false, paths: false, decision: false))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(waits: "never", marked: false, paths: false, decision: false))).Response.StatusCode);
        var kept = await Row(routes, project);

        // Sometimes with nothing ticked waits for nothing, and is refused.
        await Refused(routes, project, Settings(marked: false, paths: false, decision: false), "waits_for_owner");
        await Refused(routes, project, Settings(waits: "Sometimes"), "waits_for_owner");
        await Refused(routes, project, Settings(waits: "maybe"), "waits_for_owner");

        // The review label is validated as the other three, and differs from them whatever the case.
        await Refused(routes, project, Settings(review: ""), "review_label");
        await Refused(routes, project, Settings(review: "  "), "review_label");
        await Refused(routes, project, Settings(review: " review"), "review_label");
        await Refused(routes, project, Settings(review: new string('r', 51)), "review_label");
        await Refused(routes, project, Settings(review: "review, please"), "review_label");
        await Refused(routes, project, Settings(review: "DEVFLOW"), "review_label");
        await Refused(routes, project, Settings(review: "Devflow-Hold"), "review_label");
        await Refused(routes, project, Settings(review: "devflow-ATTENTION"), "review_label");
        // Named on the later field, as a duplicate of the other three is.
        await Refused(routes, project, Settings(hold: "devflow-review"), "review_label");

        // Each path pattern is validated, and the list is at most a hundred.
        foreach (var wrong in new[] { "", "  ", " src/**", "!src/**", "/src/**", "src\\auth", "../secrets/**", "src/./auth", new string('p', 201) })
        {
            await Refused(routes, project, Settings(patterns: Patterns("docs/**", wrong)), "wait_paths");
        }

        await Refused(routes, project, Settings(patterns: new JsonArray("docs/**", null)), "wait_paths");
        await Refused(routes, project, Settings(patterns: Patterns([.. Enumerable.Range(0, 101).Select(index => FormattableString.Invariant($"docs/{index}/**"))])), "wait_paths");

        // Every field is required.
        foreach (var field in new[] { "review_label", "wait_when_marked", "wait_when_paths", "wait_when_decision", "wait_paths" })
        {
            var missing = Settings();
            missing.Remove(field);
            await Refused(routes, project, missing, field);
        }

        // Nothing refused changed anything.
        Assert.Equal(kept, await Row(routes, project));

        // A hundred patterns of 200 characters are taken, and a pattern given twice is kept once.
        var most = Enumerable.Range(0, 100).Select(index => FormattableString.Invariant($"{index:D3}/") + new string('p', 196)).ToArray();
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(patterns: Patterns(most)))).Response.StatusCode);
        Assert.Equal(string.Join('\n', most), (await Row(routes, project)).Patterns);
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(patterns: Patterns("*.sql", "docs/**", "*.sql")))).Response.StatusCode);
        Assert.Equal("*.sql\ndocs/**", (await Row(routes, project)).Patterns);
        Assert.Equal(Patterns("*.sql", "docs/**").ToJsonString(), (await Get(routes, project))["wait_paths"]!.ToJsonString());
    }

    /// <summary>The table refuses what the route refuses, so nothing but the route can store a
    /// sometimes that waits for nothing, a value it does not know, or a review label that is
    /// another's.</summary>
    [Fact]
    public async Task TheTableKeepsTheRules()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        await using var admin = await routes.Database.OpenAdmin(Ct);
        foreach (var change in new[]
        {
            "waits_for_owner = 'sometimes', wait_when_marked = false, wait_when_paths = false, wait_when_decision = false",
            "waits_for_owner = 'maybe'",
            "review_label = 'DevFlow'",
            "review_label = ''",
            "wait_paths = ARRAY['a', NULL]",
        })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(
                $"UPDATE project_settings SET {change} WHERE project_id = @project", new { project }, cancellationToken: Ct)));
        }
    }
}
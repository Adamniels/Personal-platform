using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's trusted accounts, the setting <c>trusted_logins</c>: the GitHub logins whose cards,
/// edits and comments are instructions. The owner's linked login is always on the list without
/// being stored, so a relinked account is trusted under its new login at once; the others are
/// stored, added and removed through <c>PUT /devflow/api/projects/{id}/settings</c>. Against a real
/// Postgres. See "Settings" in devflow/README.md.
/// </summary>
public sealed class TrustedAccountsTests
{
    private const string Owner = "octo-owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static JsonObject Settings(params string[] trusted) => new()
    {
        ["pickup_label"] = "devflow",
        ["hold_label"] = "devflow-hold",
        ["attention_label"] = "devflow-attention",
        ["review_label"] = "devflow-review",
        ["waits_for_owner"] = "never",
        ["wait_when_marked"] = true,
        ["wait_when_paths"] = true,
        ["wait_when_decision"] = true,
        ["wait_paths"] = new JsonArray(),
        ["trusted_logins"] = new JsonArray([.. trusted.Select(login => (JsonNode)login)]),
        ["model_reading"] = "opus",
        ["model_building"] = "opus",
        ["model_reviewing"] = "opus",
        ["run_minutes"] = 90,
        ["run_turns"] = 200,
        ["fix_rounds_card"] = 3,
        ["fix_rounds_feature"] = 3,
        ["questions"] = true,
    };

    /// <summary>The list as the route answers it: each login with whether it is the caller's.</summary>
    private static string Answered(params (string Login, bool Mine)[] trusted) =>
        new JsonArray([.. trusted.Select(entry => (JsonNode)new JsonObject { ["login"] = entry.Login, ["mine"] = entry.Mine })]).ToJsonString();

    private static JsonObject Without(JsonObject settings, string field)
    {
        settings.Remove(field);
        return settings;
    }

    private static Task<HttpContext> Put(Routes routes, Guid project, JsonObject settings) =>
        routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());

    private static async Task<string> Listed(Routes routes, Guid project)
    {
        var answer = await routes.App.Get(PathOf(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!["trusted_logins"]!.ToJsonString();
    }

    /// <summary>The logins stored for the project, read as the superuser past row level
    /// security.</summary>
    private static async Task<string[]> Stored(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<string[]>(new CommandDefinition(
            "SELECT trusted_logins FROM project_settings WHERE project_id = @project", new { project }, cancellationToken: Ct));
    }

    [Fact]
    public async Task ANewProjectTrustsOnlyItsUser()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // Without a link nobody is known to be the owner, and nothing is trusted.
        Assert.Equal(Answered(), await Listed(routes, project));

        await Reading.Reading.Link(routes.Door, Routes.User, Owner);

        // His linked login, first and marked as his, and nothing stored for it.
        Assert.Equal(Answered((Owner, true)), await Listed(routes, project));
        Assert.Empty(await Stored(routes, project));

        // Relinked as another account: the new login is trusted at once, and the old one is not.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("UPDATE github_accounts SET login = 'octo-renamed'", cancellationToken: Ct));
        }

        Assert.Equal(Answered(("octo-renamed", true)), await Listed(routes, project));
    }

    [Fact]
    public async Task TheOwnerAddsAndRemovesLogins()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        await Reading.Reading.Link(routes.Door, Routes.User, Owner);

        // Added: duplicates whatever their case are one, the first spelling kept, and his own
        // login, in any case, is never stored.
        var added = await Put(routes, project, Settings("Octo-Owner", "octo-friend", "OCTO-FRIEND", "octo-pal"));
        Assert.Equal(StatusCodes.Status200OK, added.Response.StatusCode);
        var expected = Answered((Owner, true), ("octo-friend", false), ("octo-pal", false));
        Assert.Equal(expected, JsonNode.Parse(await Harness.Body(added))!["trusted_logins"]!.ToJsonString());
        Assert.Equal(expected, await Listed(routes, project));
        Assert.Equal(["octo-friend", "octo-pal"], await Stored(routes, project));

        // Removed.
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(Owner, "octo-pal"))).Response.StatusCode);
        Assert.Equal(Answered((Owner, true), ("octo-pal", false)), await Listed(routes, project));

        // Each refused with 422 naming the field, and nothing changed: his own login left out, a
        // login that is not GitHub's shape, and the list missing.
        var tooLong = new string('a', 40);
        var refused = new JsonObject[]
        {
            Settings("octo-pal"),
            Settings(),
            Settings(Owner, "-octo"),
            Settings(Owner, "octo-"),
            Settings(Owner, "octo--pal"),
            Settings(Owner, ""),
            Settings(Owner, "octo pal"),
            Settings(Owner, "octo_pal"),
            Settings(Owner, tooLong),
            Without(Settings(Owner), "trusted_logins"),
        };
        foreach (var body in refused)
        {
            var answer = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
            var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
            Assert.True(problem["errors"]!.AsObject().ContainsKey("trusted_logins"), $"{body.ToJsonString()} names trusted_logins");
        }

        Assert.Equal(["octo-pal"], await Stored(routes, project));

        // Thirty nine characters is the most GitHub takes, and is taken.
        var longest = new string('a', 39);
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(Owner, longest, "a", "a1-b2-c3"))).Response.StatusCode);
        Assert.Equal([longest, "a", "a1-b2-c3"], await Stored(routes, project));
    }
}
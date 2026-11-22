using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Reading;
using DevFlow.Tests.Setup;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Values;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A project's values: what its tests or its start need, which the owner gives on its page,
/// sealed in the vault in <c>project_values</c>, never answered by a route, listed by the values
/// step against what the setup run asked for, and carried by every job that runs the project's
/// code. Against real Postgres and the fake code host. See "Setting up a project" and "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed partial class ProjectValuesTests
{
    /// <summary>A throwaway value, never a real one.</summary>
    internal const string Value = "sk_test_throwaway-4f9a1c7e2b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ValuesPath(Guid project) => $"/devflow/api/projects/{project}/values";

    internal static Task<HttpContext> Give(Harness app, Guid project, string name, string? value) =>
        app.SendJson("PUT", $"{ValuesPath(project)}/{name}", Contract.Token, new JsonObject { ["value"] = value }.ToJsonString());

    private static Task<HttpContext> Remove(Harness app, Guid project, string name) =>
        app.Send("DELETE", $"{ValuesPath(project)}/{name}", Contract.Token, ("Origin", Link.Linking.Origin));

    private static async Task<JsonObject> Listed(Harness app, Guid project)
    {
        var response = await app.Get(ValuesPath(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    private static async Task<bool> StepDone(Harness app, Guid project)
    {
        var response = await app.Get($"/devflow/api/projects/{project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var steps = JsonNode.Parse(await Harness.Body(response))!["steps"]!.AsArray();
        Assert.Equal(["setup", "recipe", "settings", "values", "worker", "check"], steps.Select(step => (string)step!["step"]!));
        return (bool)steps.Single(step => (string)step!["step"]! == "values")!["done"]!;
    }

    [Fact]
    public async Task ValuesAreSealedAndNeverAnswered()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var bobs = await routes.Project(Routes.Bob, repository: Reading.Reading.RepositoryId + 1, name: "bobs");
        var given = routes.Time.GetUtcNow();

        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, "STRIPE_TEST_KEY", Value)).Response.StatusCode);

        // The row holds the project, its user, the name, the sealed value and when, and the value
        // as written is nowhere in the database.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            var columns = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'project_values' ORDER BY column_name",
                cancellationToken: Ct));
            Assert.Equal(["ciphertext", "key_id", "name", "nonce", "project_id", "set_at", "user_id"], columns);

            var row = await admin.QuerySingleAsync<(Guid Project, Guid User, string Name, string KeyId, byte[] Nonce, byte[] Ciphertext, DateTime SetAt)>(new CommandDefinition(
                "SELECT project_id, user_id, name, key_id, nonce, ciphertext, set_at FROM project_values", cancellationToken: Ct));
            Assert.Equal((project, Routes.User, "STRIPE_TEST_KEY"), (row.Project, row.User, row.Name));
            Assert.Equal(given, new DateTimeOffset(row.SetAt, TimeSpan.Zero), TimeSpan.FromMilliseconds(1));

            // Sealed for the project's user: the vault opens it as theirs, for this project and
            // name, and not as anyone else's or for another project or name.
            var vault = routes.App.Services.GetRequiredService<Vault>();
            var sealedValue = new SealedValue(row.KeyId, row.Nonce, row.Ciphertext);
            Assert.Equal(Value, Encoding.UTF8.GetString(vault.OpenProjectValue(Routes.User, project, "STRIPE_TEST_KEY", sealedValue)));
            Assert.Throws<VaultException>(() => vault.OpenProjectValue(Routes.Bob, project, "STRIPE_TEST_KEY", sealedValue));
            Assert.Throws<VaultException>(() => vault.OpenProjectValue(Routes.User, bobs, "STRIPE_TEST_KEY", sealedValue));
            Assert.Throws<VaultException>(() => vault.OpenProjectValue(Routes.User, project, "OTHER_KEY", sealedValue));

            var tables = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
                cancellationToken: Ct));
            foreach (var table in tables)
            {
                var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                    $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @value) > 0 OR strpos(t::text, @hex) > 0",
                    new { value = Value, hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Value)) },
                    cancellationToken: Ct));
                Assert.True(found == 0, $"{table} holds the value");
            }
        }

        // The route answers the name and when it was set, never the value.
        var listed = await Listed(routes.App, project);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse($$"""
                {"values": [{"name": "STRIPE_TEST_KEY", "why": null, "asked": false, "set_at": "{{given:yyyy-MM-ddTHH:mm:ss.FFFFFFF}}+00:00", "problem": null}], "done": true}
                """),
            listed), listed.ToJsonString());

        // Given again, it replaces the first, set at the new time.
        routes.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, "STRIPE_TEST_KEY", Value + "-second")).Response.StatusCode);
        var again = Assert.Single((await Listed(routes.App, project))["values"]!.AsArray());
        Assert.Equal($"{routes.Time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.FFFFFFF}+00:00", (string)again!["set_at"]!);

        // Nothing the page or the core reads holds it.
        foreach (var path in new[] { ValuesPath(project), $"/devflow/api/projects/{project}/setup", "/devflow/api/projects", "/devflow/api/waiting", "/devflow/api/display", "/devflow/api/attention" })
        {
            var body = await Harness.Body(await routes.App.Get(path, Contract.Token));
            Assert.DoesNotContain(Value, body, StringComparison.Ordinal);
        }

        // Another user's project is not there: not listed, given or removed.
        Assert.Equal(StatusCodes.Status404NotFound, (await routes.App.Get(ValuesPath(bobs), Contract.Token)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Give(routes.App, bobs, "STRIPE_TEST_KEY", Value)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Remove(routes.App, bobs, "STRIPE_TEST_KEY")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await routes.App.Get(ValuesPath(Guid.NewGuid()), Contract.Token)).Response.StatusCode);

        // Removed, it is gone, and removing it again is 404.
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes.App, project, "STRIPE_TEST_KEY")).Response.StatusCode);
        Assert.Empty((await Listed(routes.App, project))["values"]!.AsArray());
        Assert.Equal(StatusCodes.Status404NotFound, (await Remove(routes.App, project, "STRIPE_TEST_KEY")).Response.StatusCode);

        // A value goes with its project.
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, "STRIPE_TEST_KEY", Value)).Response.StatusCode);
        Assert.True(await routes.App.Services.GetRequiredService<DevFlow.Projects.ProjectStore>().Disconnect(Routes.User, project, Ct));
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM project_values", cancellationToken: Ct)));
        }
    }

    [Theory]
    [InlineData("lower_case", "name")]
    [InlineData("1STARTS_WITH_DIGIT", "name")]
    [InlineData("HAS-HYPHEN", "name")]
    [InlineData("DEVFLOW_URL", "name")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", "name")]
    [InlineData("ANTHROPIC_API_KEY", "name")]
    [InlineData("GIT_ASKPASS", "name")]
    [InlineData("PLAYWRIGHT_BROWSERS_PATH", "name")]
    [InlineData("PATH", "name")]
    [InlineData("HOME", "name")]
    [InlineData("LANG", "name")]
    [InlineData("USER", "name")]
    [InlineData("SHELL", "name")]
    [InlineData("TERM", "name")]
    [InlineData("TOO_LONG", "name", 101)]
    [InlineData("SHORT_VALUE", "value", 0, "seven77")]
    [InlineData("LONG_VALUE", "value", 0, null, 4097)]
    [InlineData("LINE_BREAK", "value", 0, "first-line\nsecond-line")]
    [InlineData("RETURN", "value", 0, "first-line\rsecond-line")]
    [InlineData("MISSING", "value", 0, null)]
    public async Task NamesAndValuesAreValidated(string name, string field, int nameLength = 0, string? value = Value, int valueLength = 0)
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var named = nameLength > 0 ? new string('N', nameLength) : name;
        var given = valueLength > 0 ? new string('v', valueLength) : value;

        var response = await Give(routes.App, project, named, given);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
        var problem = JsonNode.Parse(await Harness.Body(response))!;
        Assert.Equal([field], problem["errors"]!.AsObject().Select(error => error.Key));
        Assert.Empty((await Listed(routes.App, project))["values"]!.AsArray());
    }

    [Fact]
    public async Task TheLimitsAreAllowed()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // The shortest and longest value, the longest name, and a name beginning with an underscore.
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, "EIGHT", "eight888")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, new string('N', 100), new string('v', 4096))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(routes.App, project, "_PRIVATE_KEY_2", "with spaces and = signs")).Response.StatusCode);
        Assert.Equal(3, (await Listed(routes.App, project))["values"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheStepNeedsWhatTheSetupListed()
    {
        // A project with no setup run lists nothing, so its values step is done.
        await using (var jobs = await Jobs.Start([Jobs.Card(1)]))
        {
            Assert.True(await StepDone(jobs.Routes.App, jobs.Project));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"values": [], "done": true}"""), await Listed(jobs.Routes.App, jobs.Project)));
        }

        // A setup run that succeeded listed two variables, one whose name cannot be given.
        await using (var jobs = await SetupRunTests.Unset())
        {
            var report = SetupRunTests.Report();
            report["variables"] = new JsonArray(
                new JsonObject { ["name"] = "STRIPE_TEST_KEY", ["why"] = "The payment tests call Stripe's test mode." },
                new JsonObject { ["name"] = "PATH", ["why"] = "A name the values do not take." });
            var job = await SetupRunTests.Next(jobs);
            SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "devflow/setup", SetupRunTests.GoodRecipe), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
            var head = await SetupRunTests.Push(jobs);
            Assert.Equal(StatusCodes.Status204NoContent, (await SetupRunTests.Send(jobs, job, SetupRunTests.Result(head, report))).Response.StatusCode);

            // The step lists them with why, and waits for the one that can be given.
            Assert.False(await StepDone(jobs.Routes.App, jobs.Project));
            var listed = await Listed(jobs.Routes.App, jobs.Project);
            Assert.False((bool)listed["done"]!);
            var values = listed["values"]!.AsArray();
            Assert.Equal(["STRIPE_TEST_KEY", "PATH"], values.Select(value => (string)value!["name"]!));
            Assert.Equal("The payment tests call Stripe's test mode.", (string)values[0]!["why"]!);
            Assert.True((bool)values[0]!["asked"]!);
            Assert.Null(values[0]!["set_at"]);
            Assert.Null(values[0]!["problem"]);
            Assert.StartsWith("is reserved", (string)values[1]!["problem"]!, StringComparison.Ordinal);

            // A value it did not list does not do the step, and is listed after.
            Assert.Equal(StatusCodes.Status204NoContent, (await Give(jobs.Routes.App, jobs.Project, "EXTRA_VALUE", Value)).Response.StatusCode);
            Assert.False(await StepDone(jobs.Routes.App, jobs.Project));
            Assert.Equal(["STRIPE_TEST_KEY", "PATH", "EXTRA_VALUE"], (await Listed(jobs.Routes.App, jobs.Project))["values"]!.AsArray().Select(value => (string)value!["name"]!));
            Assert.False((bool)(await Listed(jobs.Routes.App, jobs.Project))["values"]![2]!["asked"]!);

            // Given what it listed, the step is done; removed, it waits again.
            Assert.Equal(StatusCodes.Status204NoContent, (await Give(jobs.Routes.App, jobs.Project, "STRIPE_TEST_KEY", Value)).Response.StatusCode);
            Assert.True(await StepDone(jobs.Routes.App, jobs.Project));
            Assert.True((bool)(await Listed(jobs.Routes.App, jobs.Project))["done"]!);
            Assert.Equal(StatusCodes.Status204NoContent, (await Remove(jobs.Routes.App, jobs.Project, "STRIPE_TEST_KEY")).Response.StatusCode);
            Assert.False(await StepDone(jobs.Routes.App, jobs.Project));
        }
    }
}
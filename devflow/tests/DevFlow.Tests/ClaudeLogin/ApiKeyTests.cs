using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using PlatformData.Testing;

namespace DevFlow.Tests.ClaudeLogin;

/// <summary>
/// The Anthropic API key: given once on the Projects tab, sealed in the vault under
/// <c>anthropic-api-key</c>, never answered by a route, and opened only for one of its user's runs
/// on a project that runs on it. See "The API key" in devflow/README.md.
/// </summary>
public sealed class ApiKeyTests
{
    private const string KeyPath = "/devflow/api/api-key";

    /// <summary>The shape of an API key, never a real one.</summary>
    private const string Key = "sk-ant-api03-Zm9yLXRlc3RzLW9ubHktbm90LWEtcmVhbC1rZXk_AAAAAAAAAAAAAAAAAAAAAAAA";

    private const string BobsKey = "sk-ant-api03-Ym9icy10ZXN0LWtleS1uZXZlci1yZWFsLWVpdGhlcg_BBBBBBBBBBBBBBBBBBBBB";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiKeys Keys(Routes routes) => routes.App.Services.GetRequiredService<ApiKeys>();

    private static Task<HttpContext> Save(Routes routes, string key) =>
        routes.App.SendJson("PUT", KeyPath, Contract.Token, new JsonObject { ["key"] = key }.ToJsonString());

    private static Task<HttpContext> Remove(Routes routes) =>
        routes.App.Send("DELETE", KeyPath, Contract.Token, ("Origin", Linking.Origin));

    private static async Task<JsonObject> Status(Routes routes)
    {
        var response = await routes.App.Get(KeyPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    /// <summary>Every row of the key's table and of the vault, read as the superuser, past row level
    /// security.</summary>
    private static async Task<(List<(Guid User, string Purpose)> Keys, List<(Guid User, string Purpose)> Secrets)> Rows(Routes routes)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var keys = await admin.QueryAsync<(Guid, string)>(new CommandDefinition(
            "SELECT user_id, secret_purpose FROM api_keys ORDER BY user_id", cancellationToken: Ct));
        var secrets = await admin.QueryAsync<(Guid, string)>(new CommandDefinition(
            "SELECT user_id, purpose FROM secrets ORDER BY user_id, purpose", cancellationToken: Ct));
        return ([.. keys], [.. secrets]);
    }

    [Fact]
    public async Task GivenReadAndRemoved()
    {
        await using var routes = await Routes.Start();
        var given = routes.Time.GetUtcNow();

        var saved = await Save(routes, $"  {Key}\n");

        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        var answers = new List<string> { await Harness.Body(saved) };

        // The status says whether one is set and when it was given, and nothing else.
        var status = await Status(routes);
        Assert.Equal(["given_at", "set"], status.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.True((bool)status["set"]!);
        Assert.Equal($"{given:yyyy-MM-ddTHH:mm:ss}+00:00", (string)status["given_at"]!);
        answers.Add(status.ToJsonString());

        // The table holds the user, the vault's id of the sealed key and when, and nothing else; the
        // vault holds it under anthropic-api-key, without the space around the paste.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            var columns = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'api_keys' ORDER BY column_name",
                cancellationToken: Ct));
            Assert.Equal(["given_at", "secret_purpose", "user_id"], columns);

            // Nothing anywhere in the database holds the key as written.
            var tables = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
                cancellationToken: Ct));
            foreach (var table in tables)
            {
                var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                    $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @secret) > 0 OR strpos(t::text, @hex) > 0",
                    new { secret = Key[10..], hex = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(Key[10..])) },
                    cancellationToken: Ct));
                Assert.True(found == 0, $"{table} holds the key");
            }
        }

        var (keys, secrets) = await Rows(routes);
        Assert.Equal([(Routes.User, ApiKeys.Purpose)], keys);
        Assert.Equal([(Routes.User, "anthropic-api-key")], secrets);
        Assert.Equal(Key, (await Keys(routes).ForRun(Routes.User, Ct))!.Value);

        // No route answers it: everything the page and the core read.
        foreach (var path in new[] { "/devflow/api/status", "/devflow/api/waiting", "/devflow/api/display", "/devflow/api/attention", "/devflow/api/projects", "/devflow/api/claude-login" })
        {
            answers.Add(await Harness.Body(await routes.App.Get(path, Contract.Token)));
        }

        Assert.All(answers, answer => Assert.DoesNotContain(Key[10..], answer, StringComparison.Ordinal));

        // Removing it forgets it and its sealed key, and removing what is not there is the same.
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
        (keys, secrets) = await Rows(routes);
        Assert.Empty(keys);
        Assert.Empty(secrets);
        Assert.Null(await Keys(routes).ForRun(Routes.User, Ct));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"set": false, "given_at": null}"""), await Status(routes)));
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);

        // Replacing one keeps only the new one, given now.
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Key)).Response.StatusCode);
        routes.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, BobsKey)).Response.StatusCode);
        Assert.Single((await Rows(routes)).Keys);
        Assert.Equal(BobsKey, (await Keys(routes).ForRun(Routes.User, Ct))!.Value);
        Assert.Equal($"{routes.Time.GetUtcNow():yyyy-MM-ddTHH:mm:ss}+00:00", (string)(await Status(routes))["given_at"]!);

        // The Claude login is apart: giving the key gives no login.
        Assert.Equal("none", (string)JsonNode.Parse(await Harness.Body(await routes.App.Get("/devflow/api/claude-login", Contract.Token)))!["state"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-ant-api")]
    // A setup token is the Claude login's, never the key.
    [InlineData("sk-ant-oat01-a-setup-token-is-not-an-api-key")]
    [InlineData("hello")]
    [InlineData("SK-ANT-API03-shouting")]
    [InlineData("sk-ant-api03-has a space")]
    [InlineData("sk-ant-api03-two\nlines")]
    [InlineData("sk-ant-api03-tab\there")]
    public async Task OnlyAnApiKeyIsTaken(string text)
    {
        await using var routes = await Routes.Start();

        var refused = await Save(routes, text);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        var problem = JsonNode.Parse(await Harness.Body(refused))!;
        Assert.Equal(["key"], problem["errors"]!.AsObject().Select(error => error.Key));
        // What was sent is not repeated back.
        if (text.Trim().Length > ApiKeys.Prefix.Length)
        {
            Assert.DoesNotContain(text.Trim(), problem.ToJsonString(), StringComparison.Ordinal);
        }

        var (keys, secrets) = await Rows(routes);
        Assert.Empty(keys);
        Assert.Empty(secrets);
    }

    [Fact]
    public async Task OnlyAnApiKeyIsTakenAtMost512Characters()
    {
        await using var routes = await Routes.Start();
        var longest = ApiKeys.Prefix + new string('x', ApiKeys.MaxLength - ApiKeys.Prefix.Length);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Save(routes, longest + "x")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await routes.App.SendJson("PUT", KeyPath, Contract.Token, "{}")).Response.StatusCode);
        Assert.Empty((await Rows(routes)).Keys);

        // The longest taken, once the space around a paste is dropped.
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, $" {longest} ")).Response.StatusCode);
        Assert.Equal(longest, (await Keys(routes).ForRun(Routes.User, Ct))!.Value);
    }

    [Fact]
    public async Task ItIsTheUsersOwn()
    {
        await using var routes = await Routes.Start();
        var keys = Keys(routes);
        await keys.Set(Routes.Bob, BobsKey, Ct);
        var carol = Guid.Parse("0190a8f0-0000-7000-8000-0000000ca201");

        // The caller sees none, and removing or giving theirs leaves Bob's as it was.
        Assert.False((bool)(await Status(routes))["set"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Key)).Response.StatusCode);
        Assert.Equal(BobsKey, (await keys.ForRun(Routes.Bob, Ct))!.Value);
        Assert.Equal(Key, (await keys.ForRun(Routes.User, Ct))!.Value);
        Assert.Null(await keys.ForRun(carol, Ct));

        // Another user's transaction does not see the caller's row: row level security.
        var seen = await routes.Door.InUserTransaction(Routes.Bob, transaction => transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
            "SELECT user_id FROM api_keys", transaction: transaction, cancellationToken: Ct)), Ct);
        Assert.Equal([Routes.Bob], seen);

        // The caller's sealed key copied to another user, past row level security, does not open
        // for that user's run: it is bound to the user it was sealed for.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at)
                SELECT @carol, purpose, key_id, nonce, ciphertext, updated_at FROM secrets WHERE user_id = @user AND purpose = 'anthropic-api-key';
                INSERT INTO api_keys (user_id, secret_purpose, given_at)
                SELECT @carol, secret_purpose, given_at FROM api_keys WHERE user_id = @user;
                """,
                new { carol, user = Routes.User },
                cancellationToken: Ct));
        }

        await Assert.ThrowsAsync<VaultException>(() => keys.ForRun(carol, Ct));

        // The key leaves Dev flow through this one method: nothing else public answers one.
        var handing = typeof(ApiKeys).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => method.ReturnType == typeof(Task<ApiKey?>) || method.ReturnType == typeof(Task<ApiKey>) || method.ReturnType == typeof(ApiKey))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}");
        Assert.Equal(["ApiKeys.ForRun"], handing);

        // And a key is never printed by accident, in a log line or an exception.
        var opened = (await keys.ForRun(Routes.User, Ct))!;
        Assert.Equal("ApiKey { sealed }", opened.ToString());
    }

    [Fact]
    public async Task RemovingItWhileUsedIsRefused()
    {
        await using var routes = await Routes.Start();
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Key)).Response.StatusCode);
        var platform = await routes.Project(Routes.User);
        var fodr = await routes.Project(Routes.User, repository: 7101, name: "fodr");
        // Bob's project on his own key is no project of the caller's.
        await Keys(routes).Set(Routes.Bob, BobsKey, Ct);
        var bobs = await routes.Project(Routes.Bob, repository: 7102, name: "bobs");
        await RunOn(routes, bobs, "api_key");

        await RunOn(routes, platform, "api_key");
        await RunOn(routes, fodr, "api_key");
        var refused = await Remove(routes);

        // Refused while any project of the caller's runs on it, naming them, and nothing is removed.
        Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
        Assert.Equal("These projects run on it: fodr, Personal-platform", (string)JsonNode.Parse(await Harness.Body(refused))!["detail"]!);
        Assert.Equal(Key, (await Keys(routes).ForRun(Routes.User, Ct))!.Value);

        await RunOn(routes, fodr, "subscription");
        refused = await Remove(routes);
        Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
        Assert.Equal("These projects run on it: Personal-platform", (string)JsonNode.Parse(await Harness.Body(refused))!["detail"]!);
        Assert.True((bool)(await Status(routes))["set"]!);

        // Once none does, it is removed; Bob's stays.
        await RunOn(routes, platform, "subscription");
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
        Assert.Null(await Keys(routes).ForRun(Routes.User, Ct));
        Assert.Equal(BobsKey, (await Keys(routes).ForRun(Routes.Bob, Ct))!.Value);
    }

    /// <summary>Sets what <paramref name="project"/> runs on, as the superuser, past the route.</summary>
    private static async Task RunOn(Routes routes, Guid project, string runsOn)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET runs_on = @runsOn, api_daily_usd = CASE WHEN @runsOn = 'subscription' THEN NULL ELSE coalesce(api_daily_usd, 20) END WHERE project_id = @project",
            new { runsOn, project },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task TheRoutesAreTheUsersAndFromThePlatformsOrigin()
    {
        await using var routes = await Routes.Start();

        var elsewhere = await routes.App.Send("DELETE", KeyPath, Contract.Token, ("Origin", "https://evil.test"));
        var anonymous = await routes.App.Get(KeyPath, null);
        var anonymousPut = await routes.App.SendJson("PUT", KeyPath, null, new JsonObject { ["key"] = Key }.ToJsonString());

        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymousPut.Response.StatusCode);
        Assert.Empty((await Rows(routes)).Keys);
    }
}
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
/// The Claude login: the token from <c>claude setup-token</c>, given once on the Projects tab,
/// sealed in the vault under <c>claude-login</c>, never answered by a route, opened only for one of
/// its user's runs, and due for renewal a year after it was given, with a warning from a month
/// before. See "Claude login" in devflow/README.md.
/// </summary>
public sealed class ClaudeLoginTests
{
    private const string LoginPath = "/devflow/api/claude-login";

    /// <summary>The shape of a setup token, never a real one.</summary>
    private const string Token = "sk-ant-oat01-Zm9yLXRlc3RzLW9ubHktbm90LWEtcmVhbC10b2tlbg_AAAAAA";

    private const string Replacement = "sk-ant-oat01-YW5vdGhlci10ZXN0LXRva2VuLW5ldmVyLXJlYWw_BBBBBB";

    private const string BobsToken = "sk-ant-oat01-Ym9icy10ZXN0LXRva2VuLW5ldmVyLXJlYWwtZWl0aGVy_CCC";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaudeLogins Logins(Routes routes) => routes.App.Services.GetRequiredService<ClaudeLogins>();

    private static Task<HttpContext> Save(Routes routes, string token) =>
        routes.App.SendJson("PUT", LoginPath, Contract.Token, new JsonObject { ["token"] = token }.ToJsonString());

    private static Task<HttpContext> Remove(Routes routes) =>
        routes.App.Send("DELETE", LoginPath, Contract.Token, ("Origin", Linking.Origin));

    private static async Task<JsonObject> Status(Routes routes)
    {
        var response = await routes.App.Get(LoginPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    /// <summary>Every row of both tables, read as the superuser, past row level security.</summary>
    private static async Task<(List<(Guid User, string Purpose, DateTime Given)> Logins, List<(Guid User, string Purpose)> Secrets)> Rows(Routes routes)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var logins = await admin.QueryAsync<(Guid, string, DateTime)>(new CommandDefinition(
            "SELECT user_id, secret_purpose, given_at FROM claude_logins ORDER BY user_id", cancellationToken: Ct));
        var secrets = await admin.QueryAsync<(Guid, string)>(new CommandDefinition(
            "SELECT user_id, purpose FROM secrets ORDER BY user_id, purpose", cancellationToken: Ct));
        return ([.. logins], [.. secrets]);
    }

    /// <summary>The user's sealed token as the vault stores it.</summary>
    private static async Task<byte[]> Sealed(Routes routes)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<byte[]>(new CommandDefinition(
            "SELECT ciphertext FROM secrets WHERE user_id = @user AND purpose = 'claude-login'", new { user = Routes.User }, cancellationToken: Ct));
    }

    [Fact]
    public async Task TheTokenIsSealedAndNeverAnswered()
    {
        await using var routes = await Routes.Start();
        var given = routes.Time.GetUtcNow();

        var saved = await Save(routes, Token);

        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        var answers = new List<string> { await Harness.Body(saved) };

        // The status says whether one is set, when it was given and when it is due, and nothing else.
        var status = await Status(routes);
        Assert.Equal(["given_at", "renew_by", "set", "state"], status.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.True((bool)status["set"]!);
        Assert.Equal($"{given:yyyy-MM-ddTHH:mm:ss}+00:00", (string)status["given_at"]!);
        Assert.Equal($"{given.AddDays(365):yyyy-MM-ddTHH:mm:ss}+00:00", (string)status["renew_by"]!);
        Assert.Equal("ok", (string)status["state"]!);
        answers.Add(status.ToJsonString());

        // The table holds the user, the vault's id of the sealed token and when, and nothing else;
        // the vault holds it under claude-login.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            var columns = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'claude_logins' ORDER BY column_name",
                cancellationToken: Ct));
            Assert.Equal(["given_at", "secret_purpose", "user_id"], columns);

            // Nothing anywhere in the database holds the token as written.
            var tables = await admin.QueryAsync<string>(new CommandDefinition(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
                cancellationToken: Ct));
            foreach (var table in tables)
            {
                var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                    $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @secret) > 0 OR strpos(t::text, @hex) > 0",
                    new { secret = Token[10..], hex = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(Token[10..])) },
                    cancellationToken: Ct));
                Assert.True(found == 0, $"{table} holds the token");
            }
        }

        var (logins, secrets) = await Rows(routes);
        Assert.Equal([(Routes.User, ClaudeLogins.Purpose)], logins.Select(row => (row.User, row.Purpose)));
        Assert.Equal([(Routes.User, "claude-login")], secrets);

        // No route answers it: everything the page and the core read.
        foreach (var path in new[] { "/devflow/api/status", "/devflow/api/waiting", "/devflow/api/display", "/devflow/api/attention", "/devflow/api/projects" })
        {
            answers.Add(await Harness.Body(await routes.App.Get(path, Contract.Token)));
        }

        foreach (var answer in answers)
        {
            Assert.DoesNotContain(Token[10..], answer, StringComparison.Ordinal);
        }

        // Before anything is set, the status says so.
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"set": false, "given_at": null, "renew_by": null, "state": "none"}"""),
            await Status(routes)));
    }

    [Fact]
    public async Task ReplacingItKeepsOnlyTheNewOne()
    {
        await using var routes = await Routes.Start();
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Token)).Response.StatusCode);
        var before = await Sealed(routes);
        routes.Time.Advance(TimeSpan.FromMinutes(5));
        var replaced = routes.Time.GetUtcNow();

        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Replacement)).Response.StatusCode);

        // One login, the new one, sealed afresh and given now, so its year starts again. The clock
        // moved minutes, since the fixture's token lasts no longer.
        var (logins, secrets) = await Rows(routes);
        Assert.Single(logins);
        Assert.Equal(replaced.UtcDateTime, logins[0].Given, TimeSpan.FromMilliseconds(1));
        Assert.Equal([(Routes.User, "claude-login")], secrets);
        Assert.NotEqual(before, await Sealed(routes));
        Assert.Equal($"{replaced.AddDays(365):yyyy-MM-ddTHH:mm:ss}+00:00", (string)(await Status(routes))["renew_by"]!);
    }

    [Fact]
    public async Task RemovingItLeavesNoTrace()
    {
        await using var routes = await Routes.Start();
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Token)).Response.StatusCode);

        var removed = await Remove(routes);

        Assert.Equal(StatusCodes.Status204NoContent, removed.Response.StatusCode);
        var (logins, secrets) = await Rows(routes);
        Assert.Empty(logins);
        Assert.Empty(secrets);
        Assert.Equal("none", (string)(await Status(routes))["state"]!);
        // Removing what is not there is the same answer.
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-ant-oat")]
    [InlineData("sk-ant-api03-an-api-key-is-not-a-setup-token")]
    [InlineData("hello")]
    [InlineData("SK-ANT-OAT01-shouting")]
    [InlineData("sk-ant-oat01-has a space")]
    [InlineData("sk-ant-oat01-two\nlines")]
    public async Task AnythingThatIsNotASetupTokenIsRefused(string text)
    {
        await using var routes = await Routes.Start();

        var refused = await Save(routes, text);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        var problem = JsonNode.Parse(await Harness.Body(refused))!;
        Assert.NotNull(problem["errors"]?["token"]);
        // What was sent is not repeated back.
        if (text.Trim().Length > ClaudeLogins.Prefix.Length)
        {
            Assert.DoesNotContain(text.Trim(), problem.ToJsonString(), StringComparison.Ordinal);
        }

        var (logins, secrets) = await Rows(routes);
        Assert.Empty(logins);
        Assert.Empty(secrets);
    }

    [Fact]
    public async Task AMissingTokenIsRefused()
    {
        await using var routes = await Routes.Start();

        var refused = await routes.App.SendJson("PUT", LoginPath, Contract.Token, "{}");

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Empty((await Rows(routes)).Logins);
    }

    [Fact]
    public async Task TheWarningComesAMonthBefore()
    {
        await using var routes = await Routes.Start();
        var given = routes.Time.GetUtcNow();
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Token)).Response.StatusCode);
        var logins = Logins(routes);

        // Through the route, with the login aged: the fixture's token lasts minutes, so the clock
        // cannot move a year while the route is asked.
        Assert.Equal("ok", await RouteStateAged(routes, TimeSpan.FromDays(334) - TimeSpan.FromSeconds(1)));
        Assert.Equal("due_soon", await RouteStateAged(routes, TimeSpan.FromDays(334)));
        Assert.Equal("expired", await RouteStateAged(routes, TimeSpan.FromDays(365)));

        // By the clock, which the route reads the same way: the store's status on a fake clock
        // moved on from when the login was given.
        await Age(routes, TimeSpan.Zero);
        async Task<ClaudeLoginState> StateAt(TimeSpan after)
        {
            routes.Time.SetUtcNow(given + after);
            return (await logins.Status(Routes.User, Ct)).State;
        }

        Assert.Equal(ClaudeLoginState.Ok, await StateAt(TimeSpan.FromDays(334) - TimeSpan.FromSeconds(1)));
        Assert.Equal(ClaudeLoginState.DueSoon, await StateAt(TimeSpan.FromDays(334)));
        Assert.Equal(ClaudeLoginState.DueSoon, await StateAt(TimeSpan.FromDays(365) - TimeSpan.FromSeconds(1)));
        Assert.Equal(ClaudeLoginState.Expired, await StateAt(TimeSpan.FromDays(365)));
        Assert.Equal(ClaudeLoginState.Expired, await StateAt(TimeSpan.FromDays(800)));
    }

    /// <summary>The route's state with the user's login given <paramref name="age"/> before the
    /// clock's now, set as the superuser.</summary>
    private static async Task<string> RouteStateAged(Routes routes, TimeSpan age)
    {
        await Age(routes, age);
        return (string)(await Status(routes))["state"]!;
    }

    /// <summary>Moves when the user's login was given to <paramref name="age"/> before now, as
    /// the fake's claude-login-age does.</summary>
    private static async Task Age(Routes routes, TimeSpan age)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE claude_logins SET given_at = @at WHERE user_id = @user",
            new { at = routes.Time.GetUtcNow() - age, user = Routes.User },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task AnotherUserNeverSeesIt()
    {
        await using var routes = await Routes.Start();
        var logins = Logins(routes);
        await logins.Set(Routes.Bob, BobsToken, Ct);

        // The caller sees none, and removing or replacing theirs leaves Bob's as it was.
        Assert.Equal("none", (string)(await Status(routes))["state"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await Remove(routes)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Save(routes, Token)).Response.StatusCode);

        var (rows, secrets) = await Rows(routes);
        Assert.Equal(new[] { Routes.Bob, Routes.User }.Order(), rows.Select(row => row.User).Order());
        Assert.Equal(2, secrets.Count);
        Assert.True((await logins.Status(Routes.Bob, Ct)).Set);
        Assert.DoesNotContain(BobsToken[10..], (await Status(routes)).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRoutesAreTheUsersAndFromThePlatformsOrigin()
    {
        await using var routes = await Routes.Start();

        var elsewhere = await routes.App.Send("DELETE", LoginPath, Contract.Token, ("Origin", "https://evil.test"));
        var anonymous = await routes.App.Get(LoginPath, null);
        var anonymousPut = await routes.App.SendJson("PUT", LoginPath, null, new JsonObject { ["token"] = Token }.ToJsonString());

        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymousPut.Response.StatusCode);
        Assert.Empty((await Rows(routes)).Logins);
    }
}
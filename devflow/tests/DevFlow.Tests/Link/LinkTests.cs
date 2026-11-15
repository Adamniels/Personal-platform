using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Settings;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevFlow.Tests.Link;

/// <summary>
/// Linking GitHub: starting a link, the return from the code host, and unlinking, through Dev
/// flow's routes against the fake code host and a real Postgres. A state is single use, bound to
/// the logged in user and ten minutes old at most, and any refusal stores nothing. See "Linking
/// GitHub" in devflow/README.md.
/// </summary>
public sealed class LinkTests
{
    private static CancellationToken Ct => Linking.Ct;

    [Fact]
    public async Task StartingGivesTheAuthorizeAddressWithStateAndChallenge()
    {
        await using var linking = await Linking.Start();

        var response = await linking.PostStart();

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await Harness.Body(response))!.AsObject();
        Assert.Equal(["authorize_url"], body.Select(pair => pair.Key));
        var address = new Uri((string)body["authorize_url"]!);
        Assert.Equal("https://platform.test/devflow/fake-github/authorize", address.GetLeftPart(UriPartial.Path));
        var query = Linking.Query(address);
        Assert.Equal(["client_id", "code_challenge", "code_challenge_method", "redirect_uri", "state"], query.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(FakeCodeHost.ClientId, query["client_id"]);
        // The redirect address follows the public origin the copy is served at.
        Assert.Equal("https://platform.test/devflow/github/callback", query["redirect_uri"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["state"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        // Stored: the state's SHA-256, never the state, with the verifier and the time.
        var signIn = Assert.Single(await linking.SignIns());
        Assert.Equal(Linking.Sha256(query["state"]), signIn.StateHash);
        Assert.Equal(linking.Time.GetUtcNow(), GitHubLink.Utc(signIn.CreatedAt));
        Assert.DoesNotContain(query["state"], await linking.Everything(), StringComparison.Ordinal);

        // Two starts are two sign ins, each with its own state.
        var again = await linking.Started();
        Assert.NotEqual(query["state"], again["state"]);
        Assert.Equal(2, (await linking.SignIns()).Count);
    }

    [Fact]
    public async Task StartingFollowsThePublicOrigin()
    {
        await using var linking = await Linking.Start(settings: Harness.Defaults with { PublicOrigin = "https://platform.example" });

        var response = await linking.App.Send("POST", Linking.StartPath, Contract.Token, ("Origin", "https://platform.example"));

        var url = new Uri((string)System.Text.Json.Nodes.JsonNode.Parse(await Harness.Body(response))!["authorize_url"]!);
        Assert.Equal("https://platform.example/devflow/github/callback", Linking.Query(url)["redirect_uri"]);
    }

    [Fact]
    public async Task TheReturnStoresTheAccountAndItsTokensSealed()
    {
        await using var linking = await Linking.Start();
        var started = await linking.Started();
        var now = linking.Time.GetUtcNow();

        var returned = await linking.Return($"code={linking.Grant(started)}&state={started["state"]}");

        Assert.Equal(StatusCodes.Status303SeeOther, returned.Response.StatusCode);
        Assert.Equal("/devflow/projects", returned.Response.Headers.Location);
        Assert.Equal("no-store", returned.Response.Headers.CacheControl);

        var account = await linking.Account();
        Assert.NotNull(account);
        Assert.Equal(new Linking.AccountRow("octo-owner", FakeUser.Default.Id, "linked", account.AccessExpiresAt, account.RefreshExpiresAt, account.LinkedAt), account);
        Assert.Equal(now + FakeCodeHost.AccessLifetime, GitHubLink.Utc(account.AccessExpiresAt));
        Assert.Equal(now + FakeCodeHost.RefreshLifetime, GitHubLink.Utc(account.RefreshExpiresAt));
        Assert.Equal(now, GitHubLink.Utc(account.LinkedAt));

        // Both tokens are in the vault, and the access token is the one the fake takes.
        Assert.Equal([GitHubLink.AccessPurpose, GitHubLink.RefreshPurpose], await linking.Secrets());
        var access = await linking.Secret(GitHubLink.AccessPurpose);
        var refresh = await linking.Secret(GitHubLink.RefreshPurpose);
        Assert.NotNull(access);
        Assert.NotNull(refresh);
        Assert.IsType<SignInResult<CodeHostAccount>.Granted>(await linking.Fake.WhoIs(Linking.User, access, Ct));

        // Neither is in any table in plain form, as text or as the hex Postgres shows bytes in.
        var everything = await linking.Everything();
        foreach (var token in new[] { access, refresh })
        {
            Assert.DoesNotContain(token, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(token)), everything, StringComparison.OrdinalIgnoreCase);
        }

        // The state is spent.
        Assert.Empty(await linking.SignIns());
    }

    [Fact]
    public async Task TheWorldsUserIsWhomTheLinkActsAs()
    {
        await using var linking = await Linking.Start();
        linking.Fake.Replace(Linking.User, FakeWorld.Empty with { User = new FakeUser("someone-else", 42) });

        await linking.LinkAll();

        var account = await linking.Account();
        Assert.Equal(("someone-else", 42L), (account!.Login, account.GitHubUserId));
    }

    [Fact]
    public async Task AStateFromAnotherUserIsRefused()
    {
        await using var linking = await Linking.Start();
        // Bob started a link: his state and verifier, stored as him.
        const string state = "bobs-state-bobs-state-bobs-state-bobs-state";
        var verifier = Pkce.Random();
        await linking.Door.InUserTransaction(Linking.Bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO github_sign_ins (user_id, state_hash, verifier, created_at) VALUES (@bob, @hash, @verifier, @now)",
            new { bob = Linking.Bob, hash = Linking.Sha256(state), verifier, now = linking.Time.GetUtcNow() },
            transaction,
            cancellationToken: Ct)), Ct);
        var code = linking.Fake.Grant(Linking.User, Pkce.Challenge(verifier), Linking.Redirect);

        // The user comes back with Bob's state.
        var returned = await linking.Return($"code={code}&state={state}");

        Assert.Equal("/devflow/projects?link=failed", returned.Response.Headers.Location);
        await linking.NothingStored();
        Assert.Null(await linking.Account(Linking.Bob));
        // Bob's own is untouched, for him to come back with.
        Assert.Single(await linking.SignIns(Linking.Bob));
    }

    [Fact]
    public async Task AStateUsedTwiceIsRefused()
    {
        await using var linking = await Linking.Start();

        // Spent by a return that did not link, then used again with a good code.
        var started = await linking.Started();
        Assert.Equal("/devflow/projects?link=failed", (await linking.Return($"error=access_denied&state={started["state"]}")).Response.Headers.Location);
        var returned = await linking.Return($"code={linking.Grant(started)}&state={started["state"]}");

        Assert.Equal("/devflow/projects?link=failed", returned.Response.Headers.Location);
        await linking.NothingStored();

        // And spent by a return that linked: the account is not touched again.
        var second = await linking.Started();
        Assert.Equal("/devflow/projects", (await linking.Return($"code={linking.Grant(second)}&state={second["state"]}")).Response.Headers.Location);
        var access = await linking.Secret(GitHubLink.AccessPurpose);
        Assert.Equal("/devflow/projects?link=failed", (await linking.Return($"code={linking.Grant(second)}&state={second["state"]}")).Response.Headers.Location);
        Assert.Equal(access, await linking.Secret(GitHubLink.AccessPurpose));
    }

    [Fact]
    public async Task AStateOlderThanTenMinutesIsRefused()
    {
        await using var linking = await Linking.Start();
        var started = await linking.Started();
        linking.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var returned = await linking.Return($"code={linking.Grant(started)}&state={started["state"]}");

        Assert.Equal("/devflow/projects?link=failed", returned.Response.Headers.Location);
        await linking.NothingStored();
        Assert.Empty(await linking.SignIns());

        // Ten minutes to the second is still in time.
        var inTime = await linking.Started();
        linking.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("/devflow/projects", (await linking.Return($"code={linking.Grant(inTime)}&state={inTime["state"]}")).Response.Headers.Location);
    }

    [Fact]
    public async Task AReturnWithoutALoginGoesToRefresh()
    {
        await using var linking = await Linking.Start();
        var started = await linking.Started();
        var query = $"code={linking.Grant(started)}&state={started["state"]}";

        var returned = await linking.App.Get($"{GitHubLink.CallbackPath}?{query}");

        Assert.Equal(StatusCodes.Status303SeeOther, returned.Response.StatusCode);
        Assert.Equal(
            $"/auth/refresh?return_to={Uri.EscapeDataString($"{GitHubLink.CallbackPath}?{query}")}",
            returned.Response.Headers.Location);
        await linking.NothingStored();
        // Nothing was spent, so the browser coming back logged in still links.
        Assert.Single(await linking.SignIns());
        Assert.Equal("/devflow/projects", (await linking.Return(query)).Response.Headers.Location);
    }

    [Fact]
    public async Task TheVerifierSentIsTheOneTheChallengeWasMadeFrom()
    {
        ScriptedSignIn? scripted = null;
        await using var linking = await Linking.Start(signIn: fake => scripted = new ScriptedSignIn(fake));

        Assert.Equal("/devflow/projects", await linking.LinkAll());

        var challenge = Assert.Single(scripted!.Challenges);
        var verifier = Assert.Single(scripted.Verifiers);
        // 32 random bytes in base64url, and the challenge is S256 of it, computed here as RFC 7636
        // says rather than by the code under test.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
        Assert.Equal(32, Base64Url.DecodeFromChars(verifier).Length);
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            challenge);

        // And a code is not spent with any other verifier.
        var started = await linking.Started();
        var code = linking.Grant(started);
        Assert.IsType<SignInResult<UserTokenPair>.Refused>(await linking.Fake.Exchange(Linking.User, code, Pkce.Random(), Linking.Redirect, Ct));
    }

    [Fact]
    public async Task ARefusalFromTheHostShowsOnThePageAndStoresNothing()
    {
        await using var linking = await Linking.Start();
        var started = await linking.Started();

        var returned = await linking.Return($"error=access_denied&error_description=The+user+has+denied+your+application+access.&state={started["state"]}");

        Assert.Equal(StatusCodes.Status303SeeOther, returned.Response.StatusCode);
        Assert.Equal("/devflow/projects?link=failed", returned.Response.Headers.Location);
        await linking.NothingStored();
        Assert.Empty(await linking.SignIns());

        // A code the host does not take, and a return with no code or no state, are the same.
        var wrong = await linking.Started();
        Assert.Equal("/devflow/projects?link=failed", (await linking.Return($"code=not-a-code&state={wrong["state"]}")).Response.Headers.Location);
        var noCode = await linking.Started();
        Assert.Equal("/devflow/projects?link=failed", (await linking.Return($"state={noCode["state"]}")).Response.Headers.Location);
        Assert.Equal("/devflow/projects?link=failed", (await linking.Return($"code={linking.Grant(noCode)}")).Response.Headers.Location);
        await linking.NothingStored();
    }

    [Fact]
    public async Task AHostThatIsDownStoresNothing()
    {
        await using var linking = await Linking.Start();
        var started = await linking.Started();
        var code = linking.Grant(started);
        linking.Fake.SetUnavailable(Linking.User, unavailable: true);

        var returned = await linking.Return($"code={code}&state={started["state"]}");

        Assert.Equal("/devflow/projects?link=failed", returned.Response.Headers.Location);
        await linking.NothingStored();
    }

    [Fact]
    public async Task UnlinkingRemovesTheAccountAndItsTokens()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        await linking.Started();
        Assert.NotNull(await linking.Account());

        // A change, so it needs the platform's origin.
        Assert.Equal(StatusCodes.Status403Forbidden, (await linking.App.Send("DELETE", Linking.StartPath, Contract.Token)).Response.StatusCode);
        Assert.NotNull(await linking.Account());

        var unlinked = await linking.App.Send("DELETE", Linking.StartPath, Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status204NoContent, unlinked.Response.StatusCode);
        await linking.NothingStored();
        Assert.Empty(await linking.SignIns());
        // Unlinking with nothing linked is the same answer.
        Assert.Equal(StatusCodes.Status204NoContent, (await linking.App.Send("DELETE", Linking.StartPath, Contract.Token, ("Origin", Linking.Origin))).Response.StatusCode);
    }

    [Fact]
    public async Task StartingAndUnlinkingNeedATokenAndTheOrigin()
    {
        await using var linking = await Linking.Start();

        foreach (var method in new[] { "POST", "DELETE" })
        {
            var anonymous = await linking.App.Send(method, Linking.StartPath, token: null, ("Origin", Linking.Origin));
            Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
            Assert.Equal("", await Harness.Body(anonymous));
            Assert.Equal(StatusCodes.Status403Forbidden, (await linking.App.Send(method, Linking.StartPath, Contract.Token, ("Origin", "https://evil.test"))).Response.StatusCode);
        }

        Assert.Empty(await linking.SignIns());
    }

    [Fact]
    public async Task AStartRemovesTheUsersOwnStaleSignIns()
    {
        await using var linking = await Linking.Start();
        await linking.Started();
        linking.Time.Advance(TimeSpan.FromMinutes(11));

        await linking.Started();

        Assert.Single(await linking.SignIns());
    }

    [Fact]
    public async Task TheFakesSignInPageAsksAndSendsTheBrowserBack()
    {
        await using var linking = await Linking.Start();
        linking.Fake.Replace(Linking.User, FakeWorld.Empty with { User = new FakeUser("<b>octo</b>", 7) });
        var response = await linking.PostStart();
        var address = new Uri((string)System.Text.Json.Nodes.JsonNode.Parse(await Harness.Body(response))!["authorize_url"]!);
        var started = Linking.Query(address);

        var page = await linking.App.Get(address.PathAndQuery, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, page.Response.StatusCode);
        Assert.StartsWith("text/html", page.Response.ContentType, StringComparison.Ordinal);
        var html = await Harness.Body(page);
        Assert.Contains("<h1>Fake GitHub</h1>", html, StringComparison.Ordinal);
        // The world's user, encoded as text.
        Assert.Contains("@&lt;b&gt;octo&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains(">Authorize</button>", html, StringComparison.Ordinal);
        Assert.Contains(">Deny</button>", html, StringComparison.Ordinal);

        // Authorize: back to the redirect address with a code and the state.
        var authorized = await Form(linking, started, "authorize");
        Assert.Equal(StatusCodes.Status303SeeOther, authorized.Response.StatusCode);
        var back = new Uri(authorized.Response.Headers.Location!);
        Assert.Equal(Linking.Redirect.AbsoluteUri, back.GetLeftPart(UriPartial.Path));
        Assert.Equal(started["state"], Linking.Query(back)["state"]);
        Assert.Equal("/devflow/projects", (await linking.Return(back.Query.TrimStart('?'))).Response.Headers.Location);

        // Deny: back with access_denied and the state.
        var denied = await Form(linking, await linking.Started(), "deny");
        var deniedBack = Linking.Query(new Uri(denied.Response.Headers.Location!));
        Assert.Equal("access_denied", deniedBack["error"]);
        Assert.False(deniedBack.ContainsKey("code"));
    }

    [Theory]
    [InlineData("redirect_uri", "https://evil.test/devflow/github/callback")]
    [InlineData("client_id", "someone-else")]
    [InlineData("code_challenge_method", "plain")]
    [InlineData("state", "")]
    public async Task TheFakesSignInPageSendsTheBrowserOnlyToDevFlow(string field, string value)
    {
        await using var linking = await Linking.Start();
        var started = new Dictionary<string, string>(await linking.Started()) { [field] = value };
        var query = string.Join('&', started.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        var page = await linking.App.Get($"{FakeCodeHost.AuthorizePath}?{query}", Contract.Token);
        Assert.Equal(StatusCodes.Status400BadRequest, page.Response.StatusCode);

        if (field is "redirect_uri" or "state")
        {
            var posted = await Form(linking, started, "authorize");
            Assert.Equal(StatusCodes.Status400BadRequest, posted.Response.StatusCode);
            Assert.Null(posted.Response.Headers.Location.FirstOrDefault());
        }
    }

    [Fact]
    public async Task TheFakesSignInPageIsNotInGitHubMode()
    {
        await using var linking = await Linking.Start(settings: Harness.Defaults with { CodeHost = CodeHostMode.GitHub });
        var started = await linking.Started();

        var page = await linking.App.Get($"{FakeCodeHost.AuthorizePath}?state={started["state"]}", Contract.Token);
        var posted = await Form(linking, started, "authorize");
        var expire = await linking.App.Send("POST", "/devflow/api/fake/expire-token", Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status404NotFound, page.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, posted.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, expire.Response.StatusCode);
    }

    [Fact]
    public async Task NothingPrintsACodeAVerifierAStateOrAToken()
    {
        var logs = new GitHub.CapturingLogs();
        ScriptedSignIn? scripted = null;
        await using var linking = await Linking.Start(
            signIn: fake => scripted = new ScriptedSignIn(fake),
            configure: builder => builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

        var response = await linking.PostStart();
        var started = Linking.Query(new Uri((string)System.Text.Json.Nodes.JsonNode.Parse(await Harness.Body(response))!["authorize_url"]!));
        await linking.App.Get($"{FakeCodeHost.AuthorizePath}?{string.Join('&', started.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))}", Contract.Token);
        var authorized = await Form(linking, started, "authorize");
        var back = new Uri(authorized.Response.Headers.Location!);
        await linking.Return(back.Query.TrimStart('?'));
        var code = Linking.Query(back)["code"];
        // A renewal, and the address that ends a token early on the way.
        await linking.App.Send("POST", "/devflow/api/fake/expire-token", Contract.Token, ("Origin", Linking.Origin));
        var renewed = Assert.IsType<UserToken.Usable>(await linking.Tokens.For(Linking.User, Ct));
        var refresh = await linking.Secret(GitHubLink.RefreshPurpose);

        var printed = string.Join('\n', logs.Lines) + renewed;
        Assert.NotEmpty(logs.Lines);
        foreach (var secret in new[] { code, started["state"], started["code_challenge"], Assert.Single(scripted!.Verifiers), renewed.AccessToken, refresh! })
        {
            Assert.DoesNotContain(secret, printed, StringComparison.Ordinal);
        }
    }

    /// <summary>The fake's form, posted from the platform's origin as the browser posts it.</summary>
    private static Task<HttpContext> Form(Linking linking, IReadOnlyDictionary<string, string> started, string decision) =>
        linking.App.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = FakeCodeHost.AuthorizePath;
            context.Request.Headers.Origin = Linking.Origin;
            context.Request.Headers.Cookie = $"__Host-access={Contract.Token}";
            context.Request.ContentType = "application/x-www-form-urlencoded";
            var fields = new Dictionary<string, string>
            {
                ["redirect_uri"] = started["redirect_uri"],
                ["state"] = started["state"],
                ["code_challenge"] = started["code_challenge"],
                ["decision"] = decision,
            };
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('&', fields.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))));
        }, Ct);
}
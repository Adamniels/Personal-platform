using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Link;

/// <summary>
/// <see cref="UserTokens.For"/>, the one way to a token that acts as the user: fresh tokens are used
/// as they are, one near its end is renewed under the account row's lock, a refused renewal ends
/// the link, and a host that is down changes nothing. On the fake code host, whose refresh tokens
/// are single use, and a clock the tests move. See "Acting as the owner" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class TokenTests
{
    private static CancellationToken Ct => Linking.Ct;

    [Fact]
    public async Task AFreshTokenIsUsedAsItIs()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        var access = await linking.Secret(GitHubLink.AccessPurpose);
        var before = await linking.Account();

        // Five minutes and a second left is still fresh.
        linking.Time.Advance(FakeCodeHost.AccessLifetime - UserTokens.Margin - TimeSpan.FromSeconds(1));
        var token = Assert.IsType<UserToken.Usable>(await linking.Tokens.For(Linking.User, Ct));

        Assert.Equal(access, token.AccessToken);
        Assert.Equal(0, linking.Fake.Renewals(Linking.User));
        Assert.Equal(before, await linking.Account());
        // Nothing of the token in what it prints.
        Assert.DoesNotContain(access!, token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkRevokedAtTheFakeNeedsLinkingAgain()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();

        // As revoking the app on GitHub: the next use renews, and the renewal is refused.
        var revoked = await linking.App.Send("POST", "/devflow/api/fake/revoke-token", Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status204NoContent, revoked.Response.StatusCode);
        Assert.IsType<UserToken.NeedsRelink>(await linking.Tokens.For(Linking.User, Ct));
        Assert.Equal("needs_relink", (await linking.Account())!.State);
        Assert.Empty(await linking.Secrets());
    }

    [Fact]
    public async Task ATokenNearItsEndIsRenewedAndBothNewTokensStored()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        var oldAccess = await linking.Secret(GitHubLink.AccessPurpose);
        var oldRefresh = await linking.Secret(GitHubLink.RefreshPurpose);

        // Five minutes left is too few.
        linking.Time.Advance(FakeCodeHost.AccessLifetime - UserTokens.Margin);
        var now = linking.Time.GetUtcNow();
        var token = Assert.IsType<UserToken.Usable>(await linking.Tokens.For(Linking.User, Ct));

        Assert.NotEqual(oldAccess, token.AccessToken);
        Assert.Equal(1, linking.Fake.Renewals(Linking.User));
        Assert.Equal(token.AccessToken, await linking.Secret(GitHubLink.AccessPurpose));
        var newRefresh = await linking.Secret(GitHubLink.RefreshPurpose);
        Assert.NotEqual(oldRefresh, newRefresh);
        var account = await linking.Account();
        Assert.Equal("linked", account!.State);
        Assert.Equal(now + FakeCodeHost.AccessLifetime, GitHubLink.Utc(account.AccessExpiresAt));
        Assert.Equal(now + FakeCodeHost.RefreshLifetime, GitHubLink.Utc(account.RefreshExpiresAt));

        // The new pair is the one that works now, and the next call uses it as it is.
        Assert.IsType<SignInResult<CodeHostAccount>.Granted>(await linking.Fake.WhoIs(Linking.User, token.AccessToken, Ct));
        Assert.IsType<SignInResult<CodeHostAccount>.Refused>(await linking.Fake.WhoIs(Linking.User, oldAccess!, Ct));
        Assert.Equal(token, await linking.Tokens.For(Linking.User, Ct));
        Assert.Equal(1, linking.Fake.Renewals(Linking.User));
    }

    [Fact]
    public async Task TwoRequestsAtOnceSpendOneRefreshToken()
    {
        ScriptedSignIn? scripted = null;
        await using var linking = await Linking.Start(signIn: fake => scripted = new ScriptedSignIn(fake));
        await linking.LinkAll();
        linking.Time.Advance(FakeCodeHost.AccessLifetime);
        scripted!.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first holds the row's lock while the host renews.
        var first = linking.Tokens.For(Linking.User, Ct);
        await scripted.RenewStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // The second waits on that lock, which Postgres shows, before the renewal is let go.
        var second = linking.Tokens.For(Linking.User, Ct);
        await WaitForALockWait(linking);
        scripted.Hold.SetResult();

        var tokens = await Task.WhenAll(first, second);

        var usable = Assert.IsType<UserToken.Usable>(tokens[0]);
        Assert.Equal(usable, tokens[1]);
        Assert.Equal(1, scripted.Renewals);
        Assert.Equal(1, linking.Fake.Renewals(Linking.User));
        Assert.Equal(usable.AccessToken, await linking.Secret(GitHubLink.AccessPurpose));
    }

    [Fact]
    public async Task ARefusedRenewalMarksTheLinkAndRemovesTheTokens()
    {
        ScriptedSignIn? scripted = null;
        await using var linking = await Linking.Start(signIn: fake => scripted = new ScriptedSignIn(fake));
        await linking.LinkAll();
        linking.Time.Advance(FakeCodeHost.AccessLifetime);
        scripted!.RenewAnswer = new SignInResult<UserTokenPair>.Refused();

        Assert.IsType<UserToken.NeedsRelink>(await linking.Tokens.For(Linking.User, Ct));

        Assert.Equal("needs_relink", (await linking.Account())!.State);
        Assert.Empty(await linking.Secrets());
        // From then on it says so without asking the host.
        Assert.IsType<UserToken.NeedsRelink>(await linking.Tokens.For(Linking.User, Ct));
        Assert.Equal(1, scripted.Renewals);

        // A refresh token the fake itself no longer takes is the same: spent elsewhere.
        await using var spent = await Linking.Start();
        await spent.LinkAll();
        var refresh = await spent.Secret(GitHubLink.RefreshPurpose);
        Assert.IsType<SignInResult<UserTokenPair>.Granted>(await spent.Fake.Renew(Linking.User, refresh!, Ct));
        spent.Time.Advance(FakeCodeHost.AccessLifetime);
        Assert.IsType<UserToken.NeedsRelink>(await spent.Tokens.For(Linking.User, Ct));
        Assert.Equal("needs_relink", (await spent.Account())!.State);
        Assert.Empty(await spent.Secrets());
    }

    [Fact]
    public async Task ARefreshTokenPastItsEndIsNotSent()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        linking.Time.Advance(FakeCodeHost.RefreshLifetime);

        Assert.IsType<UserToken.NeedsRelink>(await linking.Tokens.For(Linking.User, Ct));

        Assert.Equal(0, linking.Fake.Renewals(Linking.User));
        Assert.Equal("needs_relink", (await linking.Account())!.State);
        Assert.Empty(await linking.Secrets());
    }

    [Fact]
    public async Task AHostThatIsDownChangesNothing()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        var account = await linking.Account();
        var access = await linking.Secret(GitHubLink.AccessPurpose);
        var refresh = await linking.Secret(GitHubLink.RefreshPurpose);
        linking.Time.Advance(FakeCodeHost.AccessLifetime);
        linking.Fake.SetUnavailable(Linking.User, unavailable: true);

        var token = await linking.Tokens.For(Linking.User, Ct);

        // Unavailable is not a refusal: the link and both tokens stay as they were.
        Assert.Equal(new UserToken.Unavailable(RetryAt: null), token);
        Assert.Equal(account, await linking.Account());
        Assert.Equal(access, await linking.Secret(GitHubLink.AccessPurpose));
        Assert.Equal(refresh, await linking.Secret(GitHubLink.RefreshPurpose));

        // And once it answers again, the same refresh token renews.
        linking.Fake.SetUnavailable(Linking.User, unavailable: false);
        Assert.IsType<UserToken.Usable>(await linking.Tokens.For(Linking.User, Ct));
    }

    [Fact]
    public async Task WithoutALinkThereIsNoToken()
    {
        await using var linking = await Linking.Start();

        Assert.IsType<UserToken.NotLinked>(await linking.Tokens.For(Linking.User, Ct));

        await linking.LinkAll();
        await linking.GitHub.Unlink(Linking.User, Ct);
        Assert.IsType<UserToken.NotLinked>(await linking.Tokens.For(Linking.User, Ct));
    }

    [Fact]
    public async Task TheFakeEndsATokenEarlyForATest()
    {
        await using var linking = await Linking.Start();
        await linking.LinkAll();
        var access = await linking.Secret(GitHubLink.AccessPurpose);

        var ended = await linking.App.Send("POST", "/devflow/api/fake/expire-token", Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status204NoContent, ended.Response.StatusCode);
        Assert.IsType<SignInResult<CodeHostAccount>.Refused>(await linking.Fake.WhoIs(Linking.User, access!, Ct));
        Assert.Equal(linking.Time.GetUtcNow(), GitHubLink.Utc((await linking.Account())!.AccessExpiresAt));
        var renewed = Assert.IsType<UserToken.Usable>(await linking.Tokens.For(Linking.User, Ct));
        Assert.NotEqual(access, renewed.AccessToken);
        Assert.Equal(1, linking.Fake.Renewals(Linking.User));
    }

    /// <summary>Waits until a query in the test's database waits on a lock, at most ten seconds.</summary>
    private static async Task WaitForALockWait(Linking linking)
    {
        await using var admin = await linking.Database.OpenAdmin(Ct);
        for (var tries = 0; tries < 200; tries++)
        {
            var waiting = await admin.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", cancellationToken: Ct));
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("the second request never waited on the account's lock");
    }
}
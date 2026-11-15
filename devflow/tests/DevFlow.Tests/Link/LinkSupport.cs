using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Link;

/// <summary>
/// Dev flow over a migrated database of its own, with the fake code host on the harness's clock,
/// for the tests of linking GitHub and of a usable token. The user is the fixture token's.
/// </summary>
internal sealed class Linking : IAsyncDisposable
{
    public const string StartPath = "/devflow/api/github/link";
    public const string Origin = "https://platform.test";
    public static readonly Uri Redirect = new("https://platform.test/devflow/github/callback");

    private Linking(TestDatabase database, Harness app, FakeCodeHost fake)
    {
        Database = database;
        App = app;
        Fake = fake;
    }

    public static Guid User { get; } = Guid.Parse(Contract.Sub);

    public static Guid Bob { get; } = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestDatabase Database { get; }

    public Harness App { get; }

    public FakeCodeHost Fake { get; }

    public FakeTimeProvider Time => App.Time;

    public GitHubLink GitHub => App.Services.GetRequiredService<GitHubLink>();

    public Vault Vault => App.Services.GetRequiredService<Vault>();

    public UserDb Door => App.Services.GetRequiredService<UserDb>();

    /// <param name="signIn">Made from the fake, to stand between Dev flow and it.</param>
    public static async Task<Linking> Start(
        Func<FakeCodeHost, ICodeHostSignIn>? signIn = null,
        DevFlowSettings? settings = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var database = await Databases.DevFlow.Migrated(Ct);
        var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        var time = Harness.Clock();
        var fake = new FakeCodeHost(time);
        var app = await Harness.Start(
            settings: settings,
            database: door,
            codeHost: fake,
            signIn: signIn?.Invoke(fake),
            time: time,
            configure: builder =>
            {
                // As itself too, for its addresses, as the fake mode registers it.
                builder.Services.AddSingleton(fake);
                configure?.Invoke(builder);
            });
        return new Linking(database, app, fake);
    }

    /// <summary>Starts a link as the page does, a POST from the platform's origin.</summary>
    public Task<HttpContext> PostStart(string? token = null) =>
        App.Send("POST", StartPath, token ?? Contract.Token, ("Origin", Origin));

    /// <summary>Starts a link and reads the authorize address's query.</summary>
    public async Task<IReadOnlyDictionary<string, string>> Started()
    {
        var response = await PostStart();
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var url = (string)JsonNode.Parse(await Harness.Body(response))!["authorize_url"]!;
        return Query(new Uri(url));
    }

    /// <summary>The return from the code host, as the browser makes it.</summary>
    public Task<HttpContext> Return(string query, string? token = null) =>
        App.Send("GET", $"{GitHubLink.CallbackPath}?{query}", token ?? Contract.Token);

    /// <summary>A code from the fake for a link started, as pressing Authorize gives one.</summary>
    public string Grant(IReadOnlyDictionary<string, string> started) =>
        Fake.Grant(User, started["code_challenge"], Redirect);

    /// <summary>Links the user all the way, and answers where the return sent the browser.</summary>
    public async Task<string?> LinkAll()
    {
        var started = await Started();
        var returned = await Return($"code={Grant(started)}&state={started["state"]}");
        return returned.Response.Headers.Location;
    }

    public static IReadOnlyDictionary<string, string> Query(Uri address) =>
        new GitHub.Received(HttpMethod.Get, address, new Dictionary<string, string>(), null).Query;

    public static byte[] Sha256(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    /// <summary>The user's account row, read around row level security, or null.</summary>
    public async Task<AccountRow?> Account(Guid? user = null)
    {
        await using var admin = await Database.OpenAdmin(Ct);
        return await admin.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            """
            SELECT login AS Login, github_user_id AS GitHubUserId, state AS State, access_expires_at AS AccessExpiresAt,
                   refresh_expires_at AS RefreshExpiresAt, linked_at AS LinkedAt
            FROM github_accounts WHERE user_id = @user
            """,
            new { user = user ?? User }, cancellationToken: Ct));
    }

    /// <summary>The user's sign ins, read around row level security.</summary>
    public async Task<List<SignInRow>> SignIns(Guid? user = null)
    {
        await using var admin = await Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<SignInRow>(new CommandDefinition(
            "SELECT state_hash AS StateHash, verifier AS Verifier, created_at AS CreatedAt FROM github_sign_ins WHERE user_id = @user",
            new { user = user ?? User }, cancellationToken: Ct))];
    }

    /// <summary>The vault's purposes the user has a secret for.</summary>
    public async Task<List<string>> Secrets(Guid? user = null)
    {
        await using var admin = await Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT purpose FROM secrets WHERE user_id = @user ORDER BY purpose", new { user = user ?? User }, cancellationToken: Ct))];
    }

    /// <summary>A secret of the user's, opened through the door as them.</summary>
    public async Task<string?> Secret(string purpose) =>
        await Door.InUserTransaction(User, transaction => Vault.Get(transaction, User, purpose, Ct), Ct) is { } bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    /// <summary>Every row of every table in public, as text, so a test can look for a value in
    /// any of them.</summary>
    public async Task<string> Everything()
    {
        await using var admin = await Database.OpenAdmin(Ct);
        var tables = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", cancellationToken: Ct));
        var all = new StringBuilder();
        foreach (var table in tables)
        {
            all.Append(await admin.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                $"SELECT string_agg(t::text, ' ') FROM \"{table}\" t", cancellationToken: Ct))).Append('\n');
        }

        return all.ToString();
    }

    /// <summary>Asserts nothing of the link was stored for the user: no account, no token.</summary>
    public async Task NothingStored()
    {
        Assert.Null(await Account());
        Assert.Empty(await Secrets());
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Database.DisposeAsync();
    }

    internal sealed record AccountRow(string Login, long GitHubUserId, string State, DateTime AccessExpiresAt, DateTime RefreshExpiresAt, DateTime LinkedAt);

    internal sealed record SignInRow(byte[] StateHash, string Verifier, DateTime CreatedAt);
}

/// <summary>
/// Stands between Dev flow and the fake: records the challenges and verifiers it is given, can
/// answer a renewal of its own, and can hold a renewal until a test lets it go.
/// </summary>
internal sealed class ScriptedSignIn(FakeCodeHost fake) : ICodeHostSignIn
{
    private int _renewals;

    public List<string> Challenges { get; } = [];

    public List<string> Verifiers { get; } = [];

    /// <summary>What a renewal answers instead of asking the fake.</summary>
    public SignInResult<UserTokenPair>? RenewAnswer { get; set; }

    /// <summary>When set, a renewal says it has started, then waits for it.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public TaskCompletionSource RenewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Renewals => Volatile.Read(ref _renewals);

    public Uri AuthorizeAddress(Guid userId, string state, string challenge, Uri redirect)
    {
        Challenges.Add(challenge);
        return fake.AuthorizeAddress(userId, state, challenge, redirect);
    }

    public Task<SignInResult<UserTokenPair>> Exchange(Guid userId, string code, string verifier, Uri redirect, CancellationToken cancellationToken)
    {
        Verifiers.Add(verifier);
        return fake.Exchange(userId, code, verifier, redirect, cancellationToken);
    }

    public async Task<SignInResult<UserTokenPair>> Renew(Guid userId, string refreshToken, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _renewals);
        RenewStarted.TrySetResult();
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return RenewAnswer ?? await fake.Renew(userId, refreshToken, cancellationToken);
    }

    public Task<SignInResult<CodeHostAccount>> WhoIs(Guid userId, string accessToken, CancellationToken cancellationToken) =>
        fake.WhoIs(userId, accessToken, cancellationToken);
}
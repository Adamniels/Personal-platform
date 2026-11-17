using System.Buffers.Text;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Tests.GitHub;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DevFlow.Tests.Workers;

/// <summary>
/// A project's worker key: made on the project's page, answered once, stored only as its SHA-256,
/// and replaced, never listed, by making a new one. See "Workers" in devflow/README.md and the
/// worker key's entry in "Security decisions and accepted risks" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class WorkerKeyTests
{
    private static CancellationToken Ct => Workers.Ct;

    [GeneratedRegex("^dfw_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyShape();

    [Fact]
    public async Task AKeyIsShownOnceAndOnlyItsHashIsStored()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var now = routes.Time.GetUtcNow();

        var response = await Workers.MakeKey(routes, project);

        // The key alone, never kept by a cache on the way.
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("no-store", response.Response.Headers.CacheControl.ToString());
        var answer = (await Workers.Json(response)).AsObject();
        Assert.Equal(["key"], answer.Select(pair => pair.Key));
        var key = (string)answer["key"]!;
        // dfw_ and the base64url of 32 random bytes.
        Assert.Matches(KeyShape(), key);
        var random = Base64Url.DecodeFromChars(key.AsSpan(4));
        Assert.Equal(32, random.Length);

        // The table holds the hash, the project, its user and when, and nothing else.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var columns = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'worker_keys' ORDER BY column_name",
            cancellationToken: Ct));
        Assert.Equal(["created_at", "key_hash", "project_id", "user_id"], columns);
        var row = await admin.QuerySingleAsync<(Guid Project, Guid User, byte[] Hash, DateTime Made)>(new CommandDefinition(
            "SELECT project_id, user_id, key_hash, created_at FROM worker_keys", cancellationToken: Ct));
        Assert.Equal(project, row.Project);
        Assert.Equal(Workers.User, row.User);
        Assert.Equal(Workers.Hash(key), row.Hash);
        Assert.Equal(now.UtcDateTime, row.Made, TimeSpan.FromMilliseconds(1));

        // Nothing anywhere in the database gives the key back, as text or as its bytes.
        var tables = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
            cancellationToken: Ct));
        var hex = Convert.ToHexStringLower(random);
        foreach (var table in tables)
        {
            var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @text) > 0 OR strpos(t::text, @hex) > 0",
                new { text = key[4..], hex },
                cancellationToken: Ct));
            Assert.True(found == 0, $"{table} holds the key");
        }

        // A second key is another key.
        Assert.NotEqual(key, await Workers.NewKey(routes, project));
    }

    [Fact]
    public async Task AnotherUsersProjectIs404()
    {
        await using var routes = await Routes.Start();
        var bobs = await routes.Project(Workers.Bob);

        var response = await Workers.MakeKey(routes, bobs);

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        // No key was made for it.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM worker_keys", cancellationToken: Ct)));
        // As for a project nobody has.
        Assert.Equal(StatusCodes.Status404NotFound, (await Workers.MakeKey(routes, Guid.NewGuid())).Response.StatusCode);
    }

    [Fact]
    public async Task MakingAKeyIsTheUsersAndFromThePlatformsOrigin()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);

        // A change from another origin, and one with no token.
        var elsewhere = await routes.App.Send("POST", Workers.KeyPath(project), Contract.Token, ("Origin", "https://evil.test"));
        var anonymous = await routes.App.Send("POST", Workers.KeyPath(project), null, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        await using var admin = await routes.Database.OpenAdmin(Ct);
        Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM worker_keys", cancellationToken: Ct)));
    }

    [Fact]
    public async Task TheKeyIsNeverLogged()
    {
        var logs = new CapturingLogs();
        await using var routes = await Routes.Start(builder => builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var project = await routes.Project(Workers.User);
        var worker = Guid.NewGuid();

        var key = await Workers.NewKey(routes, project);
        await Workers.SayHello(routes, key, worker);
        await Workers.AskForWork(routes, key, worker);
        // Refused ones too: a replaced key, and one a character off.
        await Workers.NewKey(routes, project);
        await Workers.SayHello(routes, key, worker);
        await Workers.SayHello(routes, key[..^1] + (key[^1] == 'A' ? 'B' : 'A'), worker);

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(key[4..^1], StringComparison.Ordinal));
    }
}
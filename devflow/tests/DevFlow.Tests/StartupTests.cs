using DevFlow.Settings;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>
/// What Dev flow checks before it serves: its database, opened at startup, so a database it cannot
/// reach stops it there rather than at its first query. See "The door" in devflow/README.md.
/// </summary>
public sealed class StartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnreachableDatabaseStopsStartup()
    {
        // Nothing listens on port 1, so the connection is refused at once.
        var environment = new Dictionary<string, string>
        {
            ["DEVFLOW_APP_DATABASE_URL"] = "postgres://devflow_app:secret-a1b2c3@127.0.0.1:1/devflow",
        };
        await using var stderr = new StringWriter { NewLine = "\n" };

        var code = await Commands.Run([], name => environment.GetValueOrDefault(name), TextWriter.Null, stderr, Ct);

        Assert.Equal(1, code);
        Assert.StartsWith("devflow: DEVFLOW_APP_DATABASE_URL: could not connect to the database as devflow_app", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-a1b2c3", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongPasswordStopsStartup()
    {
        // devflow_app's real address, on the database devflow.sh made, with a password it is not.
        var app = DatabaseUrl.Parse(Environment.GetEnvironmentVariable(Databases.DevFlow.AppVariable)
            ?? throw new InvalidOperationException($"{Databases.DevFlow.AppVariable} is not set, see tests/DevFlow.Tests/Harness/TestDatabase.cs"));
        app.Password = "not-the-password-a1b2c3";

        var refused = await Assert.ThrowsAsync<StartupException>(() => UserDb.Open(DevFlowSettings.Service, app, DevFlowSettings.AppDatabaseUrl, Ct));

        Assert.StartsWith("DEVFLOW_APP_DATABASE_URL: could not connect to the database as devflow_app", refused.Message, StringComparison.Ordinal);
        Assert.Contains("password authentication failed", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password-a1b2c3", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItServesOrMigratesAndRefusesAnythingElse()
    {
        await using var stderr = new StringWriter { NewLine = "\n" };

        var code = await Commands.Run(["serve"], _ => null, TextWriter.Null, stderr, Ct);

        Assert.Equal(2, code);
        Assert.Equal("devflow: usage: devflow [migrate]\n", stderr.ToString());
    }
}
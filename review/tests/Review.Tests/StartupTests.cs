using Review.Database;
using Review.Settings;

namespace Review.Tests;

/// <summary>
/// What review checks before it serves: its database, opened at startup, so a database it cannot
/// reach stops it there rather than at its first query. See "The door" in review/README.md.
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
            ["REVIEW_APP_DATABASE_URL"] = "postgres://review_app:secret-a1b2c3@127.0.0.1:1/review",
        };
        await using var stderr = new StringWriter { NewLine = "\n" };

        var code = await Commands.Run([], name => environment.GetValueOrDefault(name), TextWriter.Null, stderr, Ct);

        Assert.Equal(1, code);
        Assert.StartsWith("review: REVIEW_APP_DATABASE_URL: could not connect to the database as review_app", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-a1b2c3", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongPasswordStopsStartup()
    {
        // review_app's real address, on the database review.sh made, with a password it is not.
        var app = DatabaseUrl.Parse(Environment.GetEnvironmentVariable(TestDatabase.AppVariable)
            ?? throw new InvalidOperationException($"{TestDatabase.AppVariable} is not set, see tests/Review.Tests/Harness/TestDatabase.cs"));
        app.Password = "not-the-password-a1b2c3";

        var refused = await Assert.ThrowsAsync<StartupException>(() => ReviewDb.Open(app, Ct));

        Assert.StartsWith("REVIEW_APP_DATABASE_URL: could not connect to the database as review_app", refused.Message, StringComparison.Ordinal);
        Assert.Contains("password authentication failed", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password-a1b2c3", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItServesOrMigratesAndRefusesAnythingElse()
    {
        await using var stderr = new StringWriter { NewLine = "\n" };

        var code = await Commands.Run(["serve"], _ => null, TextWriter.Null, stderr, Ct);

        Assert.Equal(2, code);
        Assert.Equal("review: usage: review [migrate]\n", stderr.ToString());
    }
}
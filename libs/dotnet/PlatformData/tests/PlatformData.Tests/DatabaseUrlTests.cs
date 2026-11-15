namespace PlatformData.Tests;

/// <summary>
/// <see cref="DatabaseUrl.Parse"/>, the platform's <c>postgres://</c> form turned into Npgsql's
/// settings. The value holds a password, so a refusal never repeats any of it.
/// </summary>
public sealed class DatabaseUrlTests
{
    private static readonly (string Url, string Host, int Port, string User, string Password, string Database)[] Parsed =
    [
        ("postgres://review_app:secret-a1b2@127.0.0.1:5432/review", "127.0.0.1", 5432, "review_app", "secret-a1b2", "review"),
        ("postgresql://review_app:secret-a1b2@db.internal/review", "db.internal", 5432, "review_app", "secret-a1b2", "review"),
        ("postgres://review_app:p%40ss%3Aw%2Fd@[::1]:6543/review", "::1", 6543, "review_app", "p@ss:w/d", "review"),
        ("postgres://dev_flow_app:secret-a1b2@localhost:5433/dev%20flow", "localhost", 5433, "dev_flow_app", "secret-a1b2", "dev flow"),
    ];

    private static readonly string[] Refused =
    [
        "review_app:secret-a1b2@127.0.0.1:5432/review",
        "http://review_app:secret-a1b2@127.0.0.1:5432/review",
        "POSTGRES://review_app:secret-a1b2@127.0.0.1:5432/review",
        "postgres://review_app@127.0.0.1:5432/review",
        "postgres://:secret-a1b2@127.0.0.1:5432/review",
        "postgres://review_app:@127.0.0.1:5432/review",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432/",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432/review/more",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432/review?sslmode=disable",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432/review#here",
        "postgres://review_app:secret-a1b2@127.0.0.1:0/review",
        "postgres://review_app:secret-a1b2@127.0.0.1:99999/review",
        " postgres://review_app:secret-a1b2@127.0.0.1:5432/review",
        "postgres://review_app:secret a1b2@127.0.0.1:5432/review",
        "postgres://review_app:secret-a1b2@127.0.0.1:5432/review\n",
        "",
    ];

    [Fact]
    public void AUrlParsesIntoABuilderOrIsRefusedWithoutBeingPrinted()
    {
        foreach (var (url, host, port, user, password, database) in Parsed)
        {
            var settings = DatabaseUrl.Parse(url);
            Assert.Equal((host, port, user, password, database), (settings.Host, settings.Port, settings.Username, settings.Password, settings.Database));
        }

        foreach (var url in Refused)
        {
            var refused = Assert.Throws<FormatException>(() => DatabaseUrl.Parse(url));
            Assert.Equal("is not a URL like postgres://user:password@host:5432/database", refused.Message);
            Assert.DoesNotContain("secret", refused.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("review_app", refused.ToString(), StringComparison.Ordinal);
        }
    }
}
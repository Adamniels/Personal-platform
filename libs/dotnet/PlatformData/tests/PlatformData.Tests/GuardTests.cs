namespace PlatformData.Tests;

/// <summary>
/// The strings a service's migrations and tests build their policies from, which must be exactly
/// what the door sets. That a policy built from them isolates users, against a real Postgres, is
/// tested in each service, review's <c>IsolationTests</c> first.
/// </summary>
public sealed class GuardTests
{
    [Fact]
    public void TheGuardNamesTheServicesSetting()
    {
        // Review's own strings before the door moved here, written out, so review's policies, made
        // by migrations already applied, still match what the door sets.
        Assert.Equal("nullif(current_setting('review.user_id', true), '')::uuid", UserDb.CurrentUserSqlFor("review"));
        Assert.Equal("user_id = nullif(current_setting('review.user_id', true), '')::uuid", UserDb.GuardFor("review"));

        Assert.Equal("user_id = nullif(current_setting('dev_flow.user_id', true), '')::uuid", UserDb.GuardFor("dev_flow"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Review")]
    [InlineData("1review")]
    [InlineData("_review")]
    [InlineData("dev-flow")]
    [InlineData("review.user_id")]
    [InlineData("review', true) OR true OR current_setting('x")]
    [InlineData("review ")]
    [InlineData("revïew")]
    public void ANameThatIsNotAPlainIdentifierIsRefused(string service)
    {
        // The name goes into SQL, so anything but ^[a-z][a-z0-9_]*$ is refused wherever it is
        // taken, the door's construction included, before it reaches a statement.
        Assert.Throws<ArgumentException>(() => UserDb.GuardFor(service));
        Assert.Throws<ArgumentException>(() => UserDb.CurrentUserSqlFor(service));
        Assert.Throws<ArgumentException>(() => UserDb.Unopened(service, new Npgsql.NpgsqlConnectionStringBuilder("Host=127.0.0.1;Username=x;Password=y;Database=z")));
        Assert.Throws<ArgumentException>(() => Migrator.LockKey(service));
    }
}
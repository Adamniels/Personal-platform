namespace PlatformData.Tests;

/// <summary>
/// How <see cref="Migrator.Parse"/> turns a service's migration files into the order they run in,
/// with no database. Running them, against a real Postgres, is tested in each service that uses the
/// library, review's <c>MigrateTests</c> first, see "Running its tests" in this library's README.
/// </summary>
public sealed class MigrationFilesTests
{
    [Fact]
    public void FilesParseIntoOrderedMigrations()
    {
        // Out of order, and with a gap, so neither the order given nor a count decides it.
        var migrations = Migrator.Parse(
        [
            ("0010_third.sql", "SELECT 3;"),
            ("0001_create_log.sql", "SELECT 1;"),
            ("0002_second_one.sql", "SELECT 2;"),
        ]);

        Assert.Equal(
            [(1, "create_log", "SELECT 1;"), (2, "second_one", "SELECT 2;"), (10, "third", "SELECT 3;")],
            migrations.Select(m => (m.Version, m.Name, m.Sql)));
        Assert.Equal(["0001_create_log.sql", "0002_second_one.sql", "0010_third.sql"], migrations.Select(m => m.File));
        Assert.Empty(Migrator.Parse([]));
    }

    [Theory]
    [InlineData("1_short.sql")]
    [InlineData("00001_long.sql")]
    [InlineData("0004_Capital.sql")]
    [InlineData("0004-hyphen.sql")]
    [InlineData("0004_trailing_.sql")]
    [InlineData("0004__double.sql")]
    [InlineData("0004_x.SQL")]
    [InlineData("0004_.sql")]
    [InlineData("0004_x.sql.bak")]
    [InlineData("0004_x.sql\n")]
    public void ANameThatCannotBeOrderedIsRefused(string name)
    {
        var refused = Assert.Throws<MigrationException>(() => Migrator.Parse([("0001_first.sql", "SELECT 1;"), (name, "SELECT 1;")]));

        Assert.StartsWith($"{name}: not a migration name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFilesWithOneVersionAreRefused()
    {
        var refused = Assert.Throws<MigrationException>(() => Migrator.Parse(
        [
            ("0003_third.sql", "SELECT 1;"),
            ("0001_first.sql", "SELECT 1;"),
            ("0003_twin.sql", "SELECT 1;"),
        ]));

        Assert.Equal("0003_twin.sql: version 3 is also 0003_third.sql", refused.Message);
    }
}
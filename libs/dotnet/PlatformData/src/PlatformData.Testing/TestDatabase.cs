using Npgsql;

namespace PlatformData.Testing;

/// <summary>
/// One test's database, from <see cref="TestPostgres.Empty"/> or <see cref="TestPostgres.Migrated"/>,
/// dropped when it is disposed. It opens connections as the superuser, as the owner and as the app
/// role, outside the door, as only a test may.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly TestPostgres _postgres;

    internal TestDatabase(TestPostgres postgres, string name)
    {
        _postgres = postgres;
        Name = name;
    }

    public string Name { get; }

    /// <summary>The app role's settings, on this database, for opening the door to it.</summary>
    public NpgsqlConnectionStringBuilder App() => _postgres.App(Name);

    /// <summary>A superuser connection to this database.</summary>
    public Task<NpgsqlConnection> OpenAdmin(CancellationToken cancellationToken) =>
        TestPostgres.Open(_postgres.Admin(Name), cancellationToken);

    /// <summary>A superuser connection to this database acting as the owner, as <c>migrate</c>
    /// runs, so what it creates is the owner's and the owner's default privileges apply.</summary>
    public async Task<NpgsqlConnection> OpenOwner(CancellationToken cancellationToken)
    {
        var connection = await OpenAdmin(cancellationToken);
        await Execute(connection, $"SET ROLE {_postgres.OwnerRole}", cancellationToken);
        return connection;
    }

    /// <summary>A connection as the app role itself, outside the door, as only a test may open.</summary>
    public Task<NpgsqlConnection> OpenApp(CancellationToken cancellationToken) => TestPostgres.Open(App(), cancellationToken);

    /// <summary>The app role, as <see cref="TestPostgres.AppRole"/>.</summary>
    public string AppRole => _postgres.AppRole;

    /// <summary>The service's name, as <see cref="TestPostgres.Service"/>.</summary>
    public string Service => _postgres.Service;

    public async ValueTask DisposeAsync() => await _postgres.Drop(Name);

    /// <summary>One statement, or several, with no parameters.</summary>
    public static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A URL like the ones a service reads, for <paramref name="settings"/>.</summary>
    public static string Url(NpgsqlConnectionStringBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"postgres://{Uri.EscapeDataString(settings.Username!)}:{Uri.EscapeDataString(settings.Password!)}@{settings.Host}:{settings.Port}/{Uri.EscapeDataString(settings.Database!)}");
    }
}
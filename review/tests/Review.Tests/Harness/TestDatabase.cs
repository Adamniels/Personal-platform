using System.Globalization;
using System.Text.RegularExpressions;

using Npgsql;

using Review.Database.Migrations;
using Review.Settings;

namespace Review.Tests;

/// <summary>
/// A database of its own for a test that needs one, on the real Postgres: compose locally, the
/// service container in CI. Review's counterpart of <c>Brain/tests/harness.py</c> and
/// <c>Core/tests/common/mod.rs</c>. A test that needs no database does not take one.
///
/// Each is made by the Postgres superuser, owned by review_owner and given the database
/// privileges <c>infra/postgres/initdb/review.sh</c> gives <c>review</c>, and dropped when the
/// test ends. A run that dies before dropping one leaves it for a later run, which sweeps what is
/// older than ten minutes.
///
/// Two connection strings, both from the environment, both required: unset is a failure, never a
/// skip, so a run that lost one cannot pass by testing nothing.
/// <list type="bullet">
/// <item><c>REVIEW_TEST_ADMIN_DATABASE_URL</c>, the superuser, for making and dropping databases
/// and for acting as review_owner, whose password tests do not hold. Only tests hold it.</item>
/// <item><c>REVIEW_APP_DATABASE_URL</c>, review_app's own, the one review serves with, pointed at
/// each test's database, so a test checks what the running service is.</item>
/// </list>
/// From review/:
/// <code>
/// REVIEW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
/// REVIEW_APP_DATABASE_URL="postgres://review_app:$(grep '^REVIEW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/review" \
/// dotnet test
/// </code>
/// </summary>
internal sealed partial class TestDatabase : IAsyncDisposable
{
    public const string AdminVariable = "REVIEW_TEST_ADMIN_DATABASE_URL";
    public const string AppVariable = "REVIEW_APP_DATABASE_URL";

    // Older than this, a leftover database belongs to a run that has finished, here or elsewhere.
    private static readonly TimeSpan SweepAfter = TimeSpan.FromMinutes(10);
    private static readonly long StartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly Lazy<Task> Swept = new(Sweep);
    private static readonly Lazy<Task<string>> Template = new(CreateTemplate);
    private static int _counter;

    private TestDatabase(string name) => Name = name;

    public string Name { get; }

    /// <summary>A database set up as review.sh sets up <c>review</c>, with no migration applied,
    /// for testing <c>migrate</c> itself.</summary>
    public static async Task<TestDatabase> Empty(CancellationToken cancellationToken)
    {
        await Swept.Value;
        var name = UniqueName("empty");
        await Create(name, template: null, cancellationToken);
        return new TestDatabase(name);
    }

    /// <summary>This test's own copy of the template, which is migrated once per run, as the
    /// owner, from the files the assembly embeds, so every migration in review/migrations/ has
    /// run on it.</summary>
    public static async Task<TestDatabase> Migrated(CancellationToken cancellationToken)
    {
        var template = await Template.Value;
        var name = UniqueName("test");
        await Create(name, template, cancellationToken);
        return new TestDatabase(name);
    }

    /// <summary>Drops the template, if this run made one. <see cref="Templates"/> calls it once
    /// every test has run.</summary>
    internal static async Task DropTemplate()
    {
        if (Template.IsValueCreated && Template.Value.IsCompletedSuccessfully)
        {
            await Drop(await Template.Value);
        }
    }

    /// <summary>The superuser's settings, on <paramref name="database"/>. Unpooled, so a closed
    /// connection is gone, since a template cannot be copied while anyone is connected to it.</summary>
    public static NpgsqlConnectionStringBuilder Admin(string database = "postgres")
    {
        var settings = DatabaseUrl.Parse(Required(AdminVariable));
        settings.Database = database;
        settings.Pooling = false;
        return settings;
    }

    /// <summary>review_app's settings, on this database.</summary>
    public NpgsqlConnectionStringBuilder App()
    {
        var settings = DatabaseUrl.Parse(Required(AppVariable));
        settings.Database = Name;
        return settings;
    }

    /// <summary>A URL like the ones review reads, for <paramref name="settings"/>.</summary>
    public static string Url(NpgsqlConnectionStringBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"postgres://{Uri.EscapeDataString(settings.Username!)}:{Uri.EscapeDataString(settings.Password!)}@{settings.Host}:{settings.Port}/{Uri.EscapeDataString(settings.Database!)}");
    }

    /// <summary>A superuser connection to this database.</summary>
    public Task<NpgsqlConnection> OpenAdmin(CancellationToken cancellationToken) => Open(Admin(Name), cancellationToken);

    /// <summary>A superuser connection to this database acting as review_owner, as
    /// <c>migrate</c> runs, so what it creates is the owner's and the owner's default privileges
    /// apply.</summary>
    public async Task<NpgsqlConnection> OpenOwner(CancellationToken cancellationToken)
    {
        var connection = await Open(Admin(Name), cancellationToken);
        await Execute(connection, "SET ROLE review_owner", cancellationToken);
        return connection;
    }

    /// <summary>A connection as review_app itself, outside the door, as only a test may open.</summary>
    public Task<NpgsqlConnection> OpenApp(CancellationToken cancellationToken) => Open(App(), cancellationToken);

    public async ValueTask DisposeAsync() => await Drop(Name);

    /// <summary>One statement, or several, with no parameters.</summary>
    public static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpgsqlConnection> Open(NpgsqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"{variable} is not set, see tests/Review.Tests/Harness/TestDatabase.cs")
            : value;
    }

    /// <summary>review_&lt;kind&gt;_&lt;ms at run start&gt;_&lt;pid&gt;_&lt;counter&gt;: unique within and
    /// across runs, and safe as an identifier.</summary>
    internal static string UniqueName(string kind) => string.Create(
        CultureInfo.InvariantCulture,
        $"review_{kind}_{StartedMs}_{Environment.ProcessId}_{Interlocked.Increment(ref _counter)}");

    /// <summary>Creates <paramref name="name"/>, owned by review_owner, empty or a copy of
    /// <paramref name="template"/>, with the database privileges review.sh gives <c>review</c>.</summary>
    internal static async Task Create(string name, string? template, CancellationToken cancellationToken)
    {
        await using var admin = await Open(Admin(), cancellationToken);
        var copy = template is null ? "" : $" TEMPLATE \"{template}\"";
        await Execute(admin, $"CREATE DATABASE \"{name}\"{copy} OWNER review_owner", cancellationToken);
        // Database privileges belong to the database, not its contents, so a copy does not
        // inherit the template's. The same two lines as review.sh.
        await Execute(admin, $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC", cancellationToken);
        await Execute(admin, $"GRANT CONNECT ON DATABASE \"{name}\" TO review_app", cancellationToken);
    }

    private static async Task<string> CreateTemplate()
    {
        await Swept.Value;
        var name = UniqueName("tpl");
        await Create(name, template: null, CancellationToken.None);
        // Closed before the first copy, since a template cannot be copied while anyone is
        // connected to it, and unpooled, so closing it ends its session.
        await using (var owner = await new TestDatabase(name).OpenOwner(CancellationToken.None))
        {
            await Migrator.Run(owner, Migrator.Embedded(), CancellationToken.None);
        }

        return name;
    }

    internal static async Task Drop(string name)
    {
        await using var admin = await Open(Admin(), CancellationToken.None);
        await Execute(admin, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", CancellationToken.None);
    }

    /// <summary>Drops what earlier runs left, but only what was stamped long enough before this
    /// run started, so a run going at the same time keeps its own.</summary>
    private static async Task Sweep()
    {
        var names = new List<string>();
        await using (var admin = await Open(Admin(), CancellationToken.None))
        {
            await using var command = new NpgsqlCommand(
                @"SELECT datname::text FROM pg_database WHERE datname ~ '^review_(test|tpl|empty)_\d+_'", admin);
            await using var rows = await command.ExecuteReaderAsync();
            while (await rows.ReadAsync())
            {
                names.Add(rows.GetString(0));
            }
        }

        foreach (var name in names)
        {
            var stamp = Stamp().Match(name);
            if (stamp.Success
                && long.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) + (long)SweepAfter.TotalMilliseconds < StartedMs)
            {
                await Drop(name);
            }
        }
    }

    [GeneratedRegex(@"^review_[a-z]+_([0-9]+)_", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();
}

/// <summary>
/// Drops the run's template once every test has run. An assembly fixture, created for every run,
/// that does nothing until then, so a run of tests that need no database needs no database.
/// </summary>
internal sealed class Templates : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await TestDatabase.DropTemplate();
}
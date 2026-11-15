using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Npgsql;

namespace PlatformData.Testing;

/// <summary>
/// The real Postgres a service's tests make their databases on, compose locally and the service
/// container in CI, the C# counterpart of <c>Brain/tests/harness.py</c> and
/// <c>Core/tests/common/mod.rs</c>. A service's tests hold one, for the run, and a test that needs
/// a database takes one from it with <see cref="Empty"/> or <see cref="Migrated"/>; a test that
/// needs none does not take one. See "Running its tests" in libs/dotnet/PlatformData/README.md.
///
/// Each database is made by the Postgres superuser, owned by <c>&lt;service&gt;_owner</c> and given
/// the database privileges the service's script in <c>infra/postgres/initdb/</c> gives its own:
/// connect for <c>&lt;service&gt;_app</c>, nothing for anyone else. It is dropped when the test ends.
/// A run that dies before dropping one leaves it for a later run, which sweeps what is older than
/// ten minutes.
///
/// Two connection strings, both from the environment, both required: unset is a failure, never a
/// skip, so a run that lost one cannot pass by testing nothing.
/// <list type="bullet">
/// <item><see cref="AdminVariable"/>, <c>&lt;SERVICE&gt;_TEST_ADMIN_DATABASE_URL</c>, the superuser,
/// for making and dropping databases and for acting as the owner, whose password tests do not
/// hold. Only tests hold it; <c>.github/workflows/dotnet.yml</c> sets it on its test step.</item>
/// <item><see cref="AppVariable"/>, the app role's own, the one the service serves with, pointed at
/// each test's database, so a test checks what the running service is.</item>
/// </list>
/// </summary>
public sealed partial class TestPostgres
{
    // Older than this, a leftover database belongs to a run that has finished, here or elsewhere.
    private static readonly TimeSpan SweepAfter = TimeSpan.FromMinutes(10);
    private static readonly long StartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static int _counter;

    private readonly Assembly _migrations;
    private readonly Lazy<Task> _swept;
    private readonly Lazy<Task<string>> _template;

    /// <param name="service">The service's name, such as <c>review</c>: its roles are
    /// <c>&lt;service&gt;_owner</c> and <c>&lt;service&gt;_app</c>, and its test databases are named
    /// after it.</param>
    /// <param name="appVariable">The variable holding the app role's URL, the one the service
    /// serves with, such as <c>REVIEW_APP_DATABASE_URL</c>.</param>
    /// <param name="migrations">The service's assembly, whose embedded migrations every
    /// <see cref="Migrated"/> database has run, see <see cref="Migrator.Embedded"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    public TestPostgres(string service, string appVariable, Assembly migrations)
    {
        ArgumentNullException.ThrowIfNull(appVariable);
        ArgumentNullException.ThrowIfNull(migrations);
        Service = ServiceName.Check(service, nameof(service));
        AdminVariable = $"{service.ToUpperInvariant()}_TEST_ADMIN_DATABASE_URL";
        AppVariable = appVariable;
        _migrations = migrations;
        _swept = new(Sweep);
        _template = new(CreateTemplate);
    }

    /// <summary>The service's name.</summary>
    public string Service { get; }

    /// <summary>The superuser's URL's variable, <c>&lt;SERVICE&gt;_TEST_ADMIN_DATABASE_URL</c>.</summary>
    public string AdminVariable { get; }

    /// <summary>The app role's URL's variable.</summary>
    public string AppVariable { get; }

    /// <summary>The role that owns the service's tables and runs its migrations.</summary>
    public string OwnerRole => $"{Service}_owner";

    /// <summary>The role the service serves as.</summary>
    public string AppRole => $"{Service}_app";

    /// <summary>A database set up as the service's own is, with no migration applied, for testing
    /// <c>migrate</c> itself.</summary>
    public async Task<TestDatabase> Empty(CancellationToken cancellationToken)
    {
        await _swept.Value;
        var name = UniqueName("empty");
        await Create(name, template: null, cancellationToken);
        return new TestDatabase(this, name);
    }

    /// <summary>This test's own copy of the template, which is migrated once per run, as the
    /// owner, from the files the service's assembly embeds, so every one of its migrations has run
    /// on it.</summary>
    public async Task<TestDatabase> Migrated(CancellationToken cancellationToken)
    {
        var template = await _template.Value;
        var name = UniqueName("test");
        await Create(name, template, cancellationToken);
        return new TestDatabase(this, name);
    }

    /// <summary>Drops the template, if this run made one. The service's tests call it once every
    /// test has run, from an assembly fixture.</summary>
    public async Task DropTemplate()
    {
        if (_template.IsValueCreated && _template.Value.IsCompletedSuccessfully)
        {
            await Drop(await _template.Value);
        }
    }

    /// <summary>The superuser's settings, on <paramref name="database"/>. Unpooled, so a closed
    /// connection is gone, since a template cannot be copied while anyone is connected to it.</summary>
    public NpgsqlConnectionStringBuilder Admin(string database = "postgres")
    {
        var settings = DatabaseUrl.Parse(Required(AdminVariable));
        settings.Database = database;
        settings.Pooling = false;
        return settings;
    }

    /// <summary>The app role's settings, on <paramref name="database"/>.</summary>
    public NpgsqlConnectionStringBuilder App(string database)
    {
        var settings = DatabaseUrl.Parse(Required(AppVariable));
        settings.Database = database;
        return settings;
    }

    internal static async Task<NpgsqlConnection> Open(NpgsqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal async Task Drop(string name)
    {
        await using var admin = await Open(Admin(), CancellationToken.None);
        await TestDatabase.Execute(admin, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", CancellationToken.None);
    }

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"{variable} is not set, see \"Running its tests\" in libs/dotnet/PlatformData/README.md")
            : value;
    }

    /// <summary>&lt;service&gt;_&lt;kind&gt;_&lt;ms at run start&gt;_&lt;pid&gt;_&lt;counter&gt;: unique
    /// within and across runs, and safe as an identifier, since the service's name is.</summary>
    private string UniqueName(string kind) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Service}_{kind}_{StartedMs}_{Environment.ProcessId}_{Interlocked.Increment(ref _counter)}");

    /// <summary>Creates <paramref name="name"/>, owned by the owner, empty or a copy of
    /// <paramref name="template"/>, with the database privileges the service's own has.</summary>
    private async Task Create(string name, string? template, CancellationToken cancellationToken)
    {
        await using var admin = await Open(Admin(), cancellationToken);
        var copy = template is null ? "" : $" TEMPLATE \"{template}\"";
        await TestDatabase.Execute(admin, $"CREATE DATABASE \"{name}\"{copy} OWNER {OwnerRole}", cancellationToken);
        // Database privileges belong to the database, not its contents, so a copy does not
        // inherit the template's. The same two lines as each service's script in
        // infra/postgres/initdb/.
        await TestDatabase.Execute(admin, $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC", cancellationToken);
        await TestDatabase.Execute(admin, $"GRANT CONNECT ON DATABASE \"{name}\" TO {AppRole}", cancellationToken);
    }

    private async Task<string> CreateTemplate()
    {
        await _swept.Value;
        var name = UniqueName("tpl");
        await Create(name, template: null, CancellationToken.None);
        // Closed before the first copy, since a template cannot be copied while anyone is
        // connected to it, and unpooled, so closing it ends its session.
        await using (var owner = await new TestDatabase(this, name).OpenOwner(CancellationToken.None))
        {
            await Migrator.Run(owner, Service, Migrator.Embedded(_migrations), CancellationToken.None);
        }

        return name;
    }

    /// <summary>Drops what earlier runs left, but only what was stamped long enough before this
    /// run started, so a run going at the same time keeps its own.</summary>
    private async Task Sweep()
    {
        var names = new List<string>();
        await using (var admin = await Open(Admin(), CancellationToken.None))
        {
            await using var command = new NpgsqlCommand("SELECT datname::text FROM pg_database WHERE datname ~ @pattern", admin);
            command.Parameters.AddWithValue("pattern", $"^{Service}_(test|tpl|empty)_[0-9]+_");
            await using var rows = await command.ExecuteReaderAsync();
            while (await rows.ReadAsync())
            {
                names.Add(rows.GetString(0));
            }
        }

        foreach (var name in names)
        {
            var stamp = Stamp().Match(name[(Service.Length + 1)..]);
            if (stamp.Success
                && long.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) + (long)SweepAfter.TotalMilliseconds < StartedMs)
            {
                await Drop(name);
            }
        }
    }

    // What follows the service's name and its underscore.
    [GeneratedRegex(@"^(?:test|tpl|empty)_([0-9]+)_", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();
}
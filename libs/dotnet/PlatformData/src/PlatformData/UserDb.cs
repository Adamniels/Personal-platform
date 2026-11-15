using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;

using Dapper;

using Npgsql;

namespace PlatformData;

/// <summary>
/// The one door to a service's database. See "What it holds" in libs/dotnet/PlatformData/README.md.
///
/// Every query a service makes runs inside <see cref="InUserTransaction"/>, which opens a
/// transaction on a connection as the service's app role and sets <c>&lt;service&gt;.user_id</c>
/// for that transaction alone. The setting is what every scoped table's one policy,
/// <see cref="GuardFor"/>, reads. It is transaction scoped, <c>set_config(..., true)</c>, the
/// <c>SET LOCAL</c> "Multi user, and how isolation is enforced" in
/// <c>High-level/docs/platform-architecture.md</c> requires: a plain <c>SET</c> would survive on the
/// pooled connection into whatever borrows it next.
///
/// Every connection a service opens is opened here and nowhere in the service's own assembly, the
/// owner's one for <c>migrate</c> included; each service's tests check it with
/// <c>IsolationChecks.ConnectionHolders</c> from <c>PlatformData.Testing</c>.
/// </summary>
public sealed class UserDb : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly string _setting;

    private UserDb(string service, NpgsqlDataSource source)
    {
        _setting = Setting(service);
        _source = source;
    }

    /// <summary>
    /// The user a policy compares <c>user_id</c> with, for <paramref name="service"/>. With no user
    /// set it is null, so the comparison is never true and a query outside the door returns
    /// nothing. The setting is missing on a connection that never had it, hence the <c>true</c>,
    /// and reverts to '' after a transaction that set it, hence the <c>nullif</c>; either way a bare
    /// cast would raise instead of returning nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    public static string CurrentUserSqlFor(string service) =>
        $"nullif(current_setting('{Setting(service)}', true), '')::uuid";

    /// <summary>Every scoped table's one policy for <paramref name="service"/>, for both
    /// <c>USING</c> and <c>WITH CHECK</c>: <c>CREATE POLICY ... USING (guard) WITH CHECK
    /// (guard)</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    public static string GuardFor(string service) => "user_id = " + CurrentUserSqlFor(service);

    /// <summary>
    /// The door for <paramref name="service"/>, over a pool of connections as its app role, held
    /// for the service's lifetime. It opens one connection before it returns, so a database it
    /// cannot reach, or a wrong password, stops the service at startup rather than at its first
    /// query.
    /// </summary>
    /// <param name="variable">The setting <paramref name="app"/> came from, such as
    /// <c>REVIEW_APP_DATABASE_URL</c>, which a refusal names.</param>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    /// <exception cref="StartupException">It could not connect, naming <paramref name="variable"/>
    /// and saying why, never with the password.</exception>
    public static async Task<UserDb> Open(string service, NpgsqlConnectionStringBuilder app, string variable, CancellationToken cancellationToken)
    {
        ServiceName.Check(service, nameof(service));
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(variable);
        var source = NpgsqlDataSource.Create(app.ConnectionString);
        try
        {
            await using var first = await source.OpenConnectionAsync(cancellationToken);
        }
        catch (Exception refused) when (IsUnreachable(refused))
        {
            await source.DisposeAsync();
            throw new StartupException($"{variable}: {CouldNotConnect(app, refused)}");
        }

        return new UserDb(service, source);
    }

    /// <summary>The door without the check <see cref="Open"/> makes, for a service's tests that
    /// build the app and take no database. A service itself always opens it with
    /// <see cref="Open"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    public static UserDb Unopened(string service, NpgsqlConnectionStringBuilder app)
    {
        ServiceName.Check(service, nameof(service));
        ArgumentNullException.ThrowIfNull(app);
        return new UserDb(service, NpgsqlDataSource.Create(app.ConnectionString));
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction on behalf of <paramref name="userId"/>,
    /// committed when it returns and rolled back if it throws. The transaction, and its
    /// connection, are lent for the work only: keeping either past it is keeping it outside the
    /// door.
    /// </summary>
    /// <param name="userId">The verified token's <c>sub</c>. Only the core mints a user id.</param>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is the empty id, which
    /// names nobody.</exception>
    public async Task<T> InUserTransaction<T>(Guid userId, Func<NpgsqlTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("the empty id names no user", nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config(@setting, @id, true)",
            new { setting = _setting, id = userId.ToString("D", CultureInfo.InvariantCulture) },
            transaction,
            cancellationToken: cancellationToken));
        var result = await work(transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>
    /// A connection as the owner, for <c>migrate</c> alone. Typed as a plain
    /// <see cref="DbConnection"/>, since the migrator needs nothing of Npgsql's own.
    /// </summary>
    /// <param name="variable">The setting <paramref name="owner"/> came from, such as
    /// <c>REVIEW_OWNER_DATABASE_URL</c>, which a refusal names.</param>
    /// <exception cref="StartupException">It could not connect, naming <paramref name="variable"/>
    /// and saying why, never with the password.</exception>
    public static async Task<DbConnection> ConnectAsOwner(NpgsqlConnectionStringBuilder owner, string variable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(variable);
        var connection = new NpgsqlConnection(owner.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception refused) when (IsUnreachable(refused))
        {
            await connection.DisposeAsync();
            throw new StartupException($"{variable}: {CouldNotConnect(owner, refused)}");
        }
    }

    /// <summary>The setting the door sets and every policy reads, <c>&lt;service&gt;.user_id</c>.</summary>
    private static string Setting(string service) => $"{ServiceName.Check(service, nameof(service))}.user_id";

    /// <summary>Npgsql's reason names the host, the role and what failed, never the password,
    /// and the connection string itself is never part of it.</summary>
    private static string CouldNotConnect(NpgsqlConnectionStringBuilder settings, Exception refused) =>
        $"could not connect to the database as {settings.Username}: {refused.Message}"
        + (refused.InnerException is { } inner ? $" ({inner.Message})" : "");

    private static bool IsUnreachable(Exception error) =>
        error is NpgsqlException or SocketException or TimeoutException;
}
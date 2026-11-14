using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;

using Dapper;

using Npgsql;

using Review.Settings;

namespace Review.Database;

/// <summary>
/// The one door to review's database. See "The door" in review/README.md.
///
/// Every query review makes runs inside <see cref="InUserTransaction"/>, which opens a
/// transaction on a connection as review_app and sets <c>review.user_id</c> for that transaction
/// alone. The setting is what every scoped table's one policy, <see cref="Guard"/>, reads. It is
/// transaction scoped, <c>set_config(..., true)</c>, the <c>SET LOCAL</c> "Multi user, and how
/// isolation is enforced" in <c>High-level/docs/platform-architecture.md</c> requires: a plain
/// <c>SET</c> would survive on the pooled connection into whatever borrows it next.
///
/// Every connection review opens is opened here and nowhere else in the assembly, the owner's
/// one for <c>migrate</c> included; <c>IsolationTests.OnlyTheDoorOpensConnections</c> checks it.
/// </summary>
public sealed class ReviewDb : IAsyncDisposable
{
    /// <summary>
    /// The user a policy compares <c>user_id</c> with. With no user set it is null, so the
    /// comparison is never true and a query outside the door returns nothing. The setting is
    /// missing on a connection that never had it, hence the <c>true</c>, and reverts to '' after a
    /// transaction that set it, hence the <c>nullif</c>; either way a bare cast would raise instead
    /// of returning nothing.
    /// </summary>
    public const string CurrentUserSql = "nullif(current_setting('review.user_id', true), '')::uuid";

    /// <summary>Every scoped table's one policy, for both <c>USING</c> and <c>WITH CHECK</c>:
    /// <c>CREATE POLICY ... USING (Guard) WITH CHECK (Guard)</c>.</summary>
    public const string Guard = "user_id = " + CurrentUserSql;

    private readonly NpgsqlDataSource _source;

    private ReviewDb(NpgsqlDataSource source) => _source = source;

    /// <summary>
    /// The door, over a pool of connections as review_app, held for the service's lifetime. It
    /// opens one connection before it returns, so a database it cannot reach, or a wrong password,
    /// stops review at startup rather than at its first query.
    /// </summary>
    /// <exception cref="StartupException">It could not connect, saying why, never with the
    /// password.</exception>
    public static async Task<ReviewDb> Open(NpgsqlConnectionStringBuilder app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        var source = NpgsqlDataSource.Create(app.ConnectionString);
        try
        {
            await using var first = await source.OpenConnectionAsync(cancellationToken);
        }
        catch (Exception refused) when (IsUnreachable(refused))
        {
            await source.DisposeAsync();
            throw new StartupException($"{ReviewSettings.AppDatabaseUrl}: {CouldNotConnect(app, refused)}");
        }

        return new ReviewDb(source);
    }

    /// <summary>The door without the check <see cref="Open"/> makes, for tests that build the app
    /// and take no database.</summary>
    internal static ReviewDb Unopened(NpgsqlConnectionStringBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new ReviewDb(NpgsqlDataSource.Create(app.ConnectionString));
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
            "SELECT set_config('review.user_id', @id, true)",
            new { id = userId.ToString("D", CultureInfo.InvariantCulture) },
            transaction,
            cancellationToken: cancellationToken));
        var result = await work(transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>
    /// A connection as the owner, for <c>migrate</c> alone, from
    /// <c>REVIEW_OWNER_DATABASE_URL</c>. Typed as a plain <see cref="DbConnection"/>, since the
    /// migrator needs nothing of Npgsql's own.
    /// </summary>
    /// <exception cref="StartupException">It could not connect, saying why, never with the
    /// password.</exception>
    public static async Task<DbConnection> ConnectAsOwner(NpgsqlConnectionStringBuilder owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var connection = new NpgsqlConnection(owner.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception refused) when (IsUnreachable(refused))
        {
            await connection.DisposeAsync();
            throw new StartupException($"{ReviewSettings.OwnerDatabaseUrl}: {CouldNotConnect(owner, refused)}");
        }
    }

    /// <summary>Npgsql's reason names the host, the role and what failed, never the password,
    /// and the connection string itself is never part of it.</summary>
    private static string CouldNotConnect(NpgsqlConnectionStringBuilder settings, Exception refused) =>
        $"could not connect to the database as {settings.Username}: {refused.Message}"
        + (refused.InnerException is { } inner ? $" ({inner.Message})" : "");

    private static bool IsUnreachable(Exception error) =>
        error is NpgsqlException or SocketException or TimeoutException;
}
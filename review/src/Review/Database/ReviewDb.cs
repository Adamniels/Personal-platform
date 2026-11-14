using System.Data.Common;
using System.Net.Sockets;

using Npgsql;

using Review.Settings;

namespace Review.Database;

/// <summary>
/// Every connection review opens is opened here, and nowhere else in the assembly; a test checks
/// it. See "The door" in review/README.md.
/// </summary>
public static class ReviewDb
{
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
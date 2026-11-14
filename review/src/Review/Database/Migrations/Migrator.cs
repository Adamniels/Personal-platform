using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Dapper;

namespace Review.Database.Migrations;

/// <summary>
/// Applies review's plain SQL migrations as the owner role, forward only, the brain's
/// <c>Brain/src/brain/migrate.py</c> written again in C#. See "Migrating" in review/README.md.
///
/// A file not yet in the ledger, <c>schema_migrations</c>, is applied in its own transaction
/// together with its ledger row, so it either applies whole and is recorded or leaves nothing. A
/// failing file stops the run, and no later file runs. A session advisory lock keeps two runs
/// from applying the same file at once.
///
/// The files are checked against the ledger before anything is applied: an applied file renamed
/// or removed, or a new file numbered below one already applied, is refused rather than guessed
/// at, since forward only means the files are the history.
/// </summary>
public static partial class Migrator
{
    /// <summary>The session advisory lock every run takes: the first 8 bytes of SHA-256 of
    /// <c>review.migrate</c>, read as a big endian signed 64 bit integer.</summary>
    public static readonly long LockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData("review.migrate"u8));

    // Where Review.csproj embeds review/migrations/*.sql.
    private const string ResourcePrefix = "migrations/";

    private const string Ledger = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version integer PRIMARY KEY,
            name text NOT NULL,
            applied_at timestamptz NOT NULL
        )
        """;

    [GeneratedRegex("^([0-9]{4})_([a-z0-9]+(?:_[a-z0-9]+)*)\\.sql\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    /// <summary>
    /// The migrations <paramref name="files"/> hold, in order. A name that is not
    /// <c>NNNN_snake_description.sql</c>, or two files with one version, is an error, since the
    /// order would be a guess.
    /// </summary>
    /// <exception cref="MigrationException">A file it cannot order.</exception>
    public static IReadOnlyList<Migration> Parse(IEnumerable<(string FileName, string Sql)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var found = new SortedDictionary<int, Migration>();
        foreach (var (file, sql) in files.OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            var match = FileName().Match(file);
            if (!match.Success)
            {
                throw new MigrationException($"{file}: not a migration name, expected NNNN_snake_description.sql");
            }

            var version = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (found.TryGetValue(version, out var other))
            {
                throw new MigrationException(string.Create(CultureInfo.InvariantCulture, $"{file}: version {version} is also {other.File}"));
            }

            found[version] = new Migration(version, match.Groups[2].Value, sql);
        }

        return [.. found.Values];
    }

    /// <summary>review/migrations/*.sql, as this assembly embeds them.</summary>
    /// <exception cref="MigrationException">A file it cannot order.</exception>
    public static IReadOnlyList<Migration> Embedded()
    {
        var assembly = typeof(Migrator).Assembly;
        return Parse(assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return (name[ResourcePrefix.Length..], reader.ReadToEnd());
            }));
    }

    /// <summary>
    /// Applies what <paramref name="migrations"/> holds and the ledger lacks, in order, and
    /// returns what it applied. <paramref name="owner"/> is open, as the owner, outside a
    /// transaction, and is left open.
    /// </summary>
    /// <exception cref="MigrationException">The files and the ledger disagree, and nothing was
    /// applied; or a file failed, and nothing from it or after it was.</exception>
    public static async Task<IReadOnlyList<Migration>> Run(DbConnection owner, IReadOnlyList<Migration> migrations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(migrations);

        // Session scoped, so it is held across the transactions below, and freed when the
        // connection closes even if this process dies. It waits for a run already going.
        await owner.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = LockKey }, cancellationToken: cancellationToken));
        try
        {
            await owner.ExecuteAsync(new CommandDefinition(Ledger, cancellationToken: cancellationToken));
            var applied = (await owner.QueryAsync<(int Version, string Name)>(new CommandDefinition(
                    "SELECT version, name FROM schema_migrations", cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Version, row => row.Name);
            var pending = Check(migrations, applied);
            foreach (var migration in pending)
            {
                await Apply(owner, migration, cancellationToken);
            }

            return pending;
        }
        finally
        {
            // A connection that broke has lost the lock with its session already.
            if (owner.State == ConnectionState.Open)
            {
                await owner.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@key)", new { key = LockKey }, cancellationToken: CancellationToken.None));
            }
        }
    }

    /// <summary>What is still to apply, once the files agree with the ledger.</summary>
    private static List<Migration> Check(IReadOnlyList<Migration> migrations, Dictionary<int, string> applied)
    {
        var byVersion = migrations.ToDictionary(m => m.Version);
        foreach (var (version, name) in applied.OrderBy(row => row.Key))
        {
            var recorded = Migration.FileName(version, name);
            if (!byVersion.TryGetValue(version, out var migration))
            {
                throw new MigrationException($"{recorded} was applied and is no longer here");
            }

            if (!string.Equals(migration.Name, name, StringComparison.Ordinal))
            {
                throw new MigrationException($"{migration.File} was applied as {recorded}, an applied file is never renamed");
            }
        }

        var newest = applied.Count == 0 ? -1 : applied.Keys.Max();
        var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
        var late = pending.Where(m => m.Version < newest).Select(m => m.File).ToList();
        if (late.Count > 0)
        {
            throw new MigrationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{string.Join(", ", late)}: numbered below {newest:D4}, which is applied, so it would run out of order"));
        }

        return pending;
    }

    private static async Task Apply(DbConnection owner, Migration migration, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await owner.BeginTransactionAsync(cancellationToken);
            // No parameters, so the file may hold several statements, all in this transaction.
            await owner.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: transaction, cancellationToken: cancellationToken));
            await owner.ExecuteAsync(new CommandDefinition(
                "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@Version, @Name, now())",
                new { migration.Version, migration.Name },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbException failed)
        {
            // Disposing the transaction without committing it rolled it back.
            throw new MigrationException($"{migration.File} failed and was rolled back: {failed.Message}", failed);
        }
    }
}
using System.Reflection;

namespace PlatformData;

/// <summary>
/// A service's <c>migrate</c> command: applies its migrations as the owner, from the one variable
/// that holds the owner's URL, the only variable it reads. It prints one line per file applied, or
/// that there was nothing to do. It never runs at startup. See "What it holds" in
/// libs/dotnet/PlatformData/README.md.
/// </summary>
public static class MigrateCommand
{
    /// <param name="service">The service's name, which prefixes every line it prints to
    /// <paramref name="stderr"/> and gives the lock's key, see <see cref="Migrator.LockKey"/>.</param>
    /// <param name="ownerVariable">The variable holding the owner's URL, such as
    /// <c>REVIEW_OWNER_DATABASE_URL</c>.</param>
    /// <param name="embedding">The service's assembly, which embeds its migrations/*.sql, see
    /// <see cref="Migrator.Embedded"/>.</param>
    /// <param name="read">Gives a variable's value, or null when it is unset.</param>
    /// <param name="migrations">What to apply; null for the files <paramref name="embedding"/>
    /// embeds.</param>
    /// <returns>The exit code: 0 when every file is applied, 1 when the run could not go on.</returns>
    /// <exception cref="ArgumentException"><paramref name="service"/> is not a service's name.</exception>
    public static async Task<int> Run(
        string service,
        string ownerVariable,
        Assembly embedding,
        Func<string, string?> read,
        TextWriter stdout,
        TextWriter stderr,
        IReadOnlyList<Migration>? migrations,
        CancellationToken cancellationToken)
    {
        ServiceName.Check(service, nameof(service));
        ArgumentNullException.ThrowIfNull(ownerVariable);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var url = read(ownerVariable);
        if (string.IsNullOrEmpty(url))
        {
            await stderr.WriteLineAsync($"{service}: {ownerVariable} is not set, see {service}/README.md");
            return 1;
        }

        try
        {
            var owner = DatabaseUrl.Parse(url);
            var files = migrations ?? Migrator.Embedded(embedding);
            await using var connection = await UserDb.ConnectAsOwner(owner, ownerVariable, cancellationToken);
            var applied = await Migrator.Run(connection, service, files, cancellationToken);
            if (applied.Count == 0)
            {
                await stdout.WriteLineAsync("nothing to do, every migration is applied");
            }

            foreach (var migration in applied)
            {
                await stdout.WriteLineAsync(FormattableString.Invariant($"applied {migration.Version} {migration.Name}"));
            }

            return 0;
        }
        catch (FormatException malformed)
        {
            await stderr.WriteLineAsync($"{service}: {ownerVariable}: {malformed.Message}");
            return 1;
        }
        catch (StartupException refused)
        {
            await stderr.WriteLineAsync($"{service}: {refused.Message}");
            return 1;
        }
        catch (MigrationException failed)
        {
            await stderr.WriteLineAsync($"{service}: migration failed: {failed.Message}");
            return 1;
        }
    }
}
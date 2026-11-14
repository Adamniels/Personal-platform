using Review.Settings;

namespace Review.Database.Migrations;

/// <summary>
/// <c>review migrate</c>: applies review/migrations/ as the owner, from
/// <c>REVIEW_OWNER_DATABASE_URL</c>, the one variable it reads. It prints one line per file
/// applied, or that there was nothing to do. It never runs at startup. See "Migrating" in
/// review/README.md.
/// </summary>
internal static class MigrateCommand
{
    /// <param name="read">Gives a variable's value, or null when it is unset.</param>
    /// <param name="migrations">What to apply; null for the files this assembly embeds.</param>
    /// <returns>The exit code: 0 when every file is applied, 1 when the run could not go on.</returns>
    public static async Task<int> Run(
        Func<string, string?> read,
        TextWriter stdout,
        TextWriter stderr,
        IReadOnlyList<Migration>? migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var url = read(ReviewSettings.OwnerDatabaseUrl);
        if (string.IsNullOrEmpty(url))
        {
            await stderr.WriteLineAsync($"review: {ReviewSettings.OwnerDatabaseUrl} is not set, see review/README.md");
            return 1;
        }

        try
        {
            var owner = DatabaseUrl.Parse(url);
            var files = migrations ?? Migrator.Embedded();
            await using var connection = await ReviewDb.ConnectAsOwner(owner, cancellationToken);
            var applied = await Migrator.Run(connection, files, cancellationToken);
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
            await stderr.WriteLineAsync($"review: {ReviewSettings.OwnerDatabaseUrl}: {malformed.Message}");
            return 1;
        }
        catch (StartupException refused)
        {
            await stderr.WriteLineAsync($"review: {refused.Message}");
            return 1;
        }
        catch (MigrationException failed)
        {
            await stderr.WriteLineAsync($"review: migration failed: {failed.Message}");
            return 1;
        }
    }
}
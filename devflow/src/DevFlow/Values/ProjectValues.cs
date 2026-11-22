using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Setup;

using Npgsql;

using PlatformData;

namespace DevFlow.Values;

/// <summary>One name of a project's values step, as <c>GET /devflow/api/projects/{id}/values</c>
/// answers it: never a value.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="Why">What needs it, as the setup run said; null for a value it did not ask for.</param>
/// <param name="Asked">Whether the project's newest setup run that succeeded listed it.</param>
/// <param name="SetAt">When the owner last gave it; null while it has no value.</param>
/// <param name="Problem">Why a listed name cannot be given, it not being a name the values take;
/// null otherwise. Such a name does not hold the step back.</param>
public sealed record ProjectValueName(string Name, string? Why, bool Asked, DateTimeOffset? SetAt, string? Problem);

/// <summary>What <c>GET /devflow/api/projects/{id}/values</c> answers.</summary>
/// <param name="Values">The names the setup run listed, in its order, then every other name given,
/// by name.</param>
/// <param name="Done">Whether the values step is done: every name listed that can be given has a
/// value. True when none is listed.</param>
public sealed record ProjectValueList(IReadOnlyList<ProjectValueName> Values, bool Done);

/// <summary>What removing a value comes to.</summary>
public enum ValueRemoval
{
    Removed,

    /// <summary>The project has no value by that name.</summary>
    NotSet,

    /// <summary>The caller has no such project.</summary>
    NoProject,
}

/// <summary>
/// A project's values: what its tests or its start need, which the owner gives on its page, each
/// sealed by the vault in <c>project_values</c> and never answered by any route. Every job that runs
/// the project's code carries them, <see cref="Carries"/>, opened in the transaction that hands it
/// out; and Dev flow scrubs every one from what a worker sends and from what it posts, with
/// <see cref="ValueScrub"/>. Every operation runs through the door as the project's user. See
/// "Setting up a project" and "Jobs" in devflow/README.md.
/// </summary>
public sealed partial class ProjectValues(UserDb database, Vault vault)
{
    /// <summary>The longest name.</summary>
    public const int MostName = 100;

    /// <summary>The shortest value: a shorter one could not be scrubbed without scrubbing ordinary
    /// words.</summary>
    public const int LeastValue = 8;

    /// <summary>The longest value.</summary>
    public const int MostValue = 4096;

    /// <summary>The beginnings no name may have: the worker's and Dev flow's own, Claude Code's and
    /// the API's, git's and Playwright's, which a value would otherwise override or join.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["DEVFLOW_", "CLAUDE_", "ANTHROPIC_", "GIT_", "PLAYWRIGHT_"];

    /// <summary>The names the worker sets itself, or the shell does.</summary>
    public static readonly IReadOnlyList<string> ReservedNames = ["PATH", "HOME", "LANG", "USER", "SHELL", "TERM"];

    /// <summary>Whether a job of <paramref name="kind"/> carries the project's values: every job
    /// that runs the project's code, a build, a fix, a conflict, a feature's fix, a setup and a
    /// check. A reading and a review, of a card, a feature or a setup, run none of it.</summary>
    public static bool Carries(string kind) =>
        kind is Job.Build or Job.Fix or Job.Conflict or Job.FeatureFix or Job.Setup or Job.RecipeCheck;

    /// <summary>Why <paramref name="name"/> is not a value's name, or null when it is.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "is missing";
        }

        if (name.Length > MostName)
        {
            return Said($"has {name.Length} characters; it may have at most {MostName}");
        }

        if (!Name().IsMatch(name))
        {
            return "must be capital letters, digits and underscores, not beginning with a digit";
        }

        if (ReservedPrefixes.FirstOrDefault(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) is { } reserved)
        {
            return $"is reserved: a name beginning with {reserved} belongs to the worker, Claude Code, git or Playwright";
        }

        return ReservedNames.Contains(name, StringComparer.Ordinal)
            ? "is reserved: the worker sets it itself"
            : null;
    }

    /// <summary>Why <paramref name="value"/> is not a value, or null when it is.</summary>
    public static string? ValueProblem(string? value)
    {
        if (value is null)
        {
            return "is missing";
        }

        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            return "may not hold a line break";
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            return "may not hold a NUL character";
        }

        if (value.Length < LeastValue)
        {
            return Said($"has {value.Length} characters; it needs at least {LeastValue}, since a shorter value cannot be scrubbed without scrubbing ordinary words, so choose a longer throwaway value");
        }

        return value.Length > MostValue
            ? Said($"has {value.Length:N0} characters; it may have at most {MostValue:N0}")
            : null;
    }

    /// <summary>
    /// Seals <paramref name="value"/> as the project's value named <paramref name="name"/>,
    /// replacing any before it, set at <paramref name="now"/>. False when the user has no such
    /// project. Both must have passed <see cref="NameProblem"/> and <see cref="ValueProblem"/>.
    /// </summary>
    public async Task<bool> Set(Guid userId, Guid projectId, string name, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (NameProblem(name) is not null || ValueProblem(value) is not null)
        {
            throw new ArgumentException("is not a value's name and value", nameof(name));
        }

        var sealedValue = vault.SealProjectValue(userId, projectId, name, Encoding.UTF8.GetBytes(value));
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_values (project_id, user_id, name, key_id, nonce, ciphertext, set_at)
            SELECT p.id, p.user_id, @name, @keyId, @nonce, @ciphertext, @now FROM projects p WHERE p.id = @projectId
            ON CONFLICT (project_id, name) DO UPDATE
            SET key_id = excluded.key_id, nonce = excluded.nonce, ciphertext = excluded.ciphertext, set_at = excluded.set_at
            """,
            new { projectId, name, keyId = sealedValue.KeyId, nonce = sealedValue.Nonce, ciphertext = sealedValue.Ciphertext, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) == 1;
    }

    /// <summary>Removes the project's value named <paramref name="name"/>.</summary>
    public async Task<ValueRemoval> Remove(Guid userId, Guid projectId, string name, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Exists(transaction, projectId, cancellationToken))
            {
                return ValueRemoval.NoProject;
            }

            var removed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM project_values WHERE project_id = @projectId AND name = @name",
                new { projectId, name },
                transaction,
                cancellationToken: cancellationToken));
            return removed == 1 ? ValueRemoval.Removed : ValueRemoval.NotSet;
        }, cancellationToken);

    /// <summary>The project's values step, never a value; null when the user has no such
    /// project.</summary>
    public async Task<ProjectValueList?> List(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
            await Exists(transaction, projectId, cancellationToken)
                ? await Listed(transaction, projectId, cancellationToken)
                : null,
            cancellationToken);

    /// <summary>
    /// The project's values step, inside the door's <paramref name="transaction"/>: the variables
    /// its newest setup run that succeeded listed, in its order, then every other name given; and
    /// whether every listed name that can be given has a value.
    /// </summary>
    internal static async Task<ProjectValueList> Listed(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var given = (await transaction.Connection!.QueryAsync<(string Name, DateTime SetAt)>(new CommandDefinition(
                "SELECT name, set_at FROM project_values WHERE project_id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken)))
            .ToDictionary(row => row.Name, row => Link.GitHubLink.Utc(row.SetAt), StringComparer.Ordinal);
        var report = await SetupRunStore.NewestReport(transaction, projectId, cancellationToken);
        var names = new List<ProjectValueName>();
        foreach (var variable in report?.Variables ?? [])
        {
            if (names.Any(name => name.Name == variable.Name))
            {
                continue;
            }

            names.Add(new ProjectValueName(variable.Name, variable.Why, true, given.TryGetValue(variable.Name, out var at) ? at : null, NameProblem(variable.Name)));
        }

        names.AddRange(given
            .Where(value => names.All(name => name.Name != value.Key))
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new ProjectValueName(value.Key, null, false, value.Value, null)));
        return new ProjectValueList(names, names.All(name => !name.Asked || name.Problem is not null || name.SetAt is not null));
    }

    /// <summary>
    /// Every value of the project, opened, by name, inside the door's <paramref name="transaction"/>
    /// as <paramref name="userId"/>: what a job that runs its code carries, called only when one is
    /// handed out, so a value leaves Dev flow only inside a job's answer to its own project's worker.
    /// </summary>
    /// <exception cref="VaultException">A value does not open as this user's project's.</exception>
    internal async Task<IReadOnlyList<JobValue>> Open(NpgsqlTransaction transaction, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var rows = await transaction.Connection!.QueryAsync<SealedRow>(new CommandDefinition(
            "SELECT name AS Name, key_id AS KeyId, nonce AS Nonce, ciphertext AS Ciphertext FROM project_values WHERE project_id = @projectId ORDER BY name",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new JobValue(
            row.Name,
            Encoding.UTF8.GetString(vault.OpenProjectValue(userId, projectId, row.Name, new SealedValue(row.KeyId, row.Nonce, row.Ciphertext)))))];
    }

    /// <summary>The scrub of every value of the project, for what one of its workers sends; it
    /// knows none when the user has no such project.</summary>
    public async Task<ValueScrub> ScrubFor(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        new(await database.InUserTransaction(userId, transaction => Open(transaction, userId, projectId, cancellationToken), cancellationToken));

    /// <summary>The scrub of every value of each of the user's projects on
    /// <paramref name="repository"/>, by installation and id, for what Dev flow posts there.</summary>
    public async Task<ValueScrub> ScrubFor(Guid userId, CodeHost.Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return new(await database.InUserTransaction(userId, async transaction =>
        {
            var projects = await transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM projects WHERE installation_id = @installation AND repository_id = @id",
                new { installation = repository.Installation, id = repository.Id },
                transaction,
                cancellationToken: cancellationToken));
            var values = new List<JobValue>();
            foreach (var project in projects)
            {
                values.AddRange(await Open(transaction, userId, project, cancellationToken));
            }

            return values;
        }, cancellationToken));
    }

    private static Task<bool> Exists(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM projects WHERE id = @projectId)",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));

    private static string Said(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>The table's rule, ending at <c>\z</c>, since <c>$</c> would let a trailing newline
    /// through.</summary>
    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    /// <summary>A value's row as stored.</summary>
    private sealed record SealedRow(string Name, string KeyId, byte[] Nonce, byte[] Ciphertext);
}
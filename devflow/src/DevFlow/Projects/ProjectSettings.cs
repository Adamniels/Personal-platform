using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Projects;

/// <summary>
/// A project's settings, as the API answers them and picking cards reads them. Only the settings
/// something reads; the design's others arrive with the parts that read them. See "Cards" in
/// devflow/README.md and in devflow/docs/devflow-architecture.md.
/// </summary>
/// <param name="PickupLabel">A card is an open issue carrying it.</param>
/// <param name="HoldLabel">A card carrying it is held.</param>
/// <param name="AttentionLabel">What a later part marks a card or an issue that needs the owner
/// with.</param>
/// <param name="WaitsForOwner"><c>never</c> or <c>always</c>: whether a card's pull request waits
/// for the owner, read by a later part.</param>
/// <param name="TrustedLogins">The trusted accounts, the owner's linked login first, marked as his,
/// then the others; see <see cref="TrustedAccounts"/>.</param>
public sealed record ProjectSettings(string PickupLabel, string HoldLabel, string AttentionLabel, string WaitsForOwner, IReadOnlyList<TrustedLogin> TrustedLogins)
{
    /// <summary>The most characters a label has, the most GitHub takes.</summary>
    public const int LongestLabel = 50;

    /// <summary>The values <see cref="WaitsForOwner"/> takes.</summary>
    public static readonly IReadOnlyList<string> WaitsValues = ["never", "always"];

    /// <summary>A new project's, as the table's defaults make them.</summary>
    public static ProjectSettings Defaults { get; } = new("devflow", "devflow-hold", "devflow-attention", "never", []);
}

/// <summary>What <c>PUT /devflow/api/projects/{id}/settings</c> takes: all five, every time, the
/// trusted accounts as plain logins.</summary>
public sealed record SettingsRequest(string? PickupLabel, string? HoldLabel, string? AttentionLabel, string? WaitsForOwner, IReadOnlyList<string?>? TrustedLogins)
{
    /// <summary>
    /// The settings, with the trusted accounts to store, or what is wrong with each field, by its
    /// name on the wire. A label is written, at most <see cref="ProjectSettings.LongestLabel"/>
    /// characters, with no space at either end and no comma, since GitHub's list of issues by label
    /// takes a comma separated list; and the three are different names, whatever their case, as
    /// GitHub's label names are. <c>waits_for_owner</c> is <c>never</c> or <c>always</c>, exactly.
    /// <c>trusted_logins</c> is a list of GitHub logins, each of GitHub's shape, that holds
    /// <paramref name="mine"/>, the owner's linked login, whatever its case, when he has one; a
    /// login given twice is kept once.
    /// </summary>
    public (ProjectSettings? Settings, TrustedAccounts? Trusted, Dictionary<string, string[]> Errors) Check(string? mine)
    {
        var errors = new Dictionary<string, string[]>();
        var labels = new (string Field, string? Value)[] { ("pickup_label", PickupLabel), ("hold_label", HoldLabel), ("attention_label", AttentionLabel) };
        var seen = new List<string>();
        foreach (var (field, value) in labels)
        {
            if (Label(value) is { } wrong)
            {
                errors[field] = [wrong];
            }
            else if (seen.Contains(value!, StringComparer.OrdinalIgnoreCase))
            {
                errors[field] = ["must be a different label from the other two"];
            }
            else
            {
                seen.Add(value!);
            }
        }

        if (WaitsForOwner is null || !ProjectSettings.WaitsValues.Contains(WaitsForOwner, StringComparer.Ordinal))
        {
            errors["waits_for_owner"] = ["must be never or always"];
        }

        if (Logins(TrustedLogins, mine) is { } wrongLogins)
        {
            errors["trusted_logins"] = [wrongLogins];
        }

        if (errors.Count > 0)
        {
            return (null, null, errors);
        }

        var trusted = TrustedAccounts.Of(mine, TrustedLogins!.Select(login => login!));
        return (new ProjectSettings(PickupLabel!, HoldLabel!, AttentionLabel!, WaitsForOwner!, trusted.Listed), trusted, errors);
    }

    /// <summary>Why a list holding a login that is not GitHub's shape is refused.</summary>
    public static readonly string NotALogin =
        $"must hold GitHub logins only, each 1 to {TrustedAccounts.LongestLogin} letters, digits and single hyphens, with no hyphen at either end";

    /// <summary>What is wrong with the trusted accounts, or null when nothing is: a login that is
    /// not GitHub's shape, else the owner's own left out.</summary>
    private static string? Logins(IReadOnlyList<string?>? logins, string? mine)
    {
        if (logins is null)
        {
            return "is missing";
        }

        if (logins.Any(login => login is null || !TrustedAccounts.IsLogin(login)))
        {
            return NotALogin;
        }

        return mine is not null && !logins.Contains(mine, StringComparer.OrdinalIgnoreCase)
            ? $"must hold your own login, {mine}, which cannot be removed"
            : null;
    }

    /// <summary>What is wrong with a label, or null when nothing is.</summary>
    private static string? Label(string? value) => value switch
    {
        null => "is missing",
        _ when string.IsNullOrWhiteSpace(value) => "must be a label's name, not empty",
        { Length: > ProjectSettings.LongestLabel } => $"must be at most {ProjectSettings.LongestLabel} characters, as GitHub's labels are",
        _ when value.Trim().Length != value.Length => "must not start or end with a space",
        _ when value.Contains(',', StringComparison.Ordinal) => "must not hold a comma",
        _ when value.Any(char.IsControl) => "must not hold a control character",
        _ => null,
    };
}

/// <summary>A project's settings in the database, through the door as the project's user.</summary>
public sealed class ProjectSettingsStore(UserDb database)
{
    /// <summary>The project's settings, or null when the user has no such project.</summary>
    public async Task<ProjectSettings?> Get(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => Read(transaction, projectId, cancellationToken), cancellationToken);

    /// <summary>The project's settings inside <paramref name="transaction"/>, the trusted accounts
    /// with the owner's linked login as it is now, whatever the link's state; or null when the user
    /// has no such project.</summary>
    public static async Task<ProjectSettings?> Read(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT s.pickup_label AS PickupLabel, s.hold_label AS HoldLabel, s.attention_label AS AttentionLabel,
                   s.waits_for_owner AS WaitsForOwner, s.trusted_logins AS TrustedLogins, a.login AS Mine
            FROM project_settings s
            LEFT JOIN github_accounts a ON a.user_id = s.user_id
            WHERE s.project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new ProjectSettings(row.PickupLabel, row.HoldLabel, row.AttentionLabel, row.WaitsForOwner, TrustedAccounts.Of(row.Mine, row.TrustedLogins).Listed);
    }

    /// <summary>The owner's linked login, whatever the link's state, or null without one.</summary>
    public async Task<string?> Mine(Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT login FROM github_accounts WHERE user_id = @userId",
            new { userId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>Replaces the project's settings with <paramref name="settings"/>, and its trusted
    /// accounts with <paramref name="trusted"/>'s others, never the owner's own.</summary>
    /// <returns>Whether the user has such a project.</returns>
    public async Task<bool> Save(Guid userId, Guid projectId, ProjectSettings settings, TrustedAccounts trusted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(trusted);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE project_settings
            SET pickup_label = @PickupLabel, hold_label = @HoldLabel, attention_label = @AttentionLabel, waits_for_owner = @WaitsForOwner,
                trusted_logins = @trustedLogins
            WHERE project_id = @projectId
            """,
            new { projectId, settings.PickupLabel, settings.HoldLabel, settings.AttentionLabel, settings.WaitsForOwner, trustedLogins = trusted.Others.ToArray() },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
    }

    /// <summary>A row as read; with init properties, since Dapper maps an array column only to a
    /// property, as <see cref="Cards.CardStore"/>'s rows do.</summary>
    private sealed class Row
    {
        public string PickupLabel { get; init; } = "";
        public string HoldLabel { get; init; } = "";
        public string AttentionLabel { get; init; } = "";
        public string WaitsForOwner { get; init; } = "";
        public string[] TrustedLogins { get; init; } = [];
        public string? Mine { get; init; }
    }
}
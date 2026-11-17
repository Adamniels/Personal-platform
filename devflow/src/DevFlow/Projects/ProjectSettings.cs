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
public sealed record ProjectSettings(string PickupLabel, string HoldLabel, string AttentionLabel, string WaitsForOwner)
{
    /// <summary>The most characters a label has, the most GitHub takes.</summary>
    public const int LongestLabel = 50;

    /// <summary>The values <see cref="WaitsForOwner"/> takes.</summary>
    public static readonly IReadOnlyList<string> WaitsValues = ["never", "always"];

    /// <summary>A new project's, as the table's defaults make them.</summary>
    public static ProjectSettings Defaults { get; } = new("devflow", "devflow-hold", "devflow-attention", "never");
}

/// <summary>What <c>PUT /devflow/api/projects/{id}/settings</c> takes: all four, every time.</summary>
public sealed record SettingsRequest(string? PickupLabel, string? HoldLabel, string? AttentionLabel, string? WaitsForOwner)
{
    /// <summary>
    /// The settings, or what is wrong with each field, by its name on the wire. A label is
    /// written, at most <see cref="ProjectSettings.LongestLabel"/> characters, with no space at
    /// either end and no comma, since GitHub's list of issues by label takes a comma separated
    /// list; and the three are different names, whatever their case, as GitHub's label names
    /// are. <c>waits_for_owner</c> is <c>never</c> or <c>always</c>, exactly.
    /// </summary>
    public (ProjectSettings? Settings, Dictionary<string, string[]> Errors) Check()
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

        return errors.Count > 0
            ? (null, errors)
            : (new ProjectSettings(PickupLabel!, HoldLabel!, AttentionLabel!, WaitsForOwner!), errors);
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

    /// <summary>The project's settings inside <paramref name="transaction"/>, or null when the
    /// user has no such project.</summary>
    public static Task<ProjectSettings?> Read(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        transaction.Connection!.QuerySingleOrDefaultAsync<ProjectSettings?>(new CommandDefinition(
            """
            SELECT pickup_label AS PickupLabel, hold_label AS HoldLabel, attention_label AS AttentionLabel, waits_for_owner AS WaitsForOwner
            FROM project_settings WHERE project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>Replaces the project's settings with <paramref name="settings"/>.</summary>
    /// <returns>Whether the user has such a project.</returns>
    public async Task<bool> Save(Guid userId, Guid projectId, ProjectSettings settings, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE project_settings
            SET pickup_label = @PickupLabel, hold_label = @HoldLabel, attention_label = @AttentionLabel, waits_for_owner = @WaitsForOwner
            WHERE project_id = @projectId
            """,
            new { projectId, settings.PickupLabel, settings.HoldLabel, settings.AttentionLabel, settings.WaitsForOwner },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
}
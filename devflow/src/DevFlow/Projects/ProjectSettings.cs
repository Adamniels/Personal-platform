using Dapper;

using DevFlow.Merging;

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
/// <param name="AttentionLabel">What a card or an issue that needs the owner is marked
/// with.</param>
/// <param name="ReviewLabel">What marks a card, or a feature's parent and so every card of it, for
/// the owner's review, read on <c>sometimes</c>.</param>
/// <param name="WaitsForOwner"><c>never</c>, <c>sometimes</c> or <c>always</c>: whether a card's
/// pull request waits for the owner, as the merge check reads it.</param>
/// <param name="WaitWhenMarked">On <c>sometimes</c>, whether a card marked with the review label
/// waits.</param>
/// <param name="WaitWhenPaths">On <c>sometimes</c>, whether a change touching a path on
/// <paramref name="WaitPaths"/> waits.</param>
/// <param name="WaitWhenDecision">On <c>sometimes</c>, whether a card whose agent marked a decision
/// for the owner waits.</param>
/// <param name="WaitPaths">The paths the owner wants to see, glob patterns as
/// <see cref="PathPatterns"/> reads them, in the order he gave them.</param>
/// <param name="TrustedLogins">The trusted accounts, the owner's linked login first, marked as his,
/// then the others; see <see cref="TrustedAccounts"/>.</param>
/// <param name="ModelReading">The model the reading job runs on, one of
/// <see cref="RunSettings.Models"/>.</param>
/// <param name="ModelBuilding">The model a build, a fix, a conflict and a feature's fix run
/// on.</param>
/// <param name="ModelReviewing">The model a card's review and a feature's run on.</param>
/// <param name="RunMinutes">The longest a run may take, in minutes.</param>
/// <param name="RunTurns">The most turns a run may take.</param>
/// <param name="FixRoundsCard">The fix rounds a card's reviews may start.</param>
/// <param name="FixRoundsFeature">The fix rounds a whole feature's reviews may start.</param>
public sealed record ProjectSettings(
    string PickupLabel,
    string HoldLabel,
    string AttentionLabel,
    string ReviewLabel,
    string WaitsForOwner,
    bool WaitWhenMarked,
    bool WaitWhenPaths,
    bool WaitWhenDecision,
    IReadOnlyList<string> WaitPaths,
    IReadOnlyList<TrustedLogin> TrustedLogins,
    string ModelReading,
    string ModelBuilding,
    string ModelReviewing,
    int RunMinutes,
    int RunTurns,
    int FixRoundsCard,
    int FixRoundsFeature)
{
    /// <summary>The most characters a label has, the most GitHub takes.</summary>
    public const int LongestLabel = 50;

    /// <summary>The values <see cref="WaitsForOwner"/> takes.</summary>
    public static readonly IReadOnlyList<string> WaitsValues = ["never", "sometimes", "always"];

    /// <summary>A new project's, as the table's defaults make them.</summary>
    public static ProjectSettings Defaults { get; } = new(
        "devflow", "devflow-hold", "devflow-attention", "devflow-review", "never", true, true, true, [], [],
        RunSettings.Defaults.ModelReading, RunSettings.Defaults.ModelBuilding, RunSettings.Defaults.ModelReviewing,
        RunSettings.Defaults.Minutes, RunSettings.Defaults.Turns, RunSettings.Defaults.FixRoundsCard, RunSettings.Defaults.FixRoundsFeature);
}

/// <summary>What <c>PUT /devflow/api/projects/{id}/settings</c> takes: all seventeen, every time,
/// the trusted accounts as plain logins.</summary>
public sealed record SettingsRequest(
    string? PickupLabel,
    string? HoldLabel,
    string? AttentionLabel,
    string? ReviewLabel,
    string? WaitsForOwner,
    bool? WaitWhenMarked,
    bool? WaitWhenPaths,
    bool? WaitWhenDecision,
    IReadOnlyList<string?>? WaitPaths,
    IReadOnlyList<string?>? TrustedLogins,
    string? ModelReading,
    string? ModelBuilding,
    string? ModelReviewing,
    int? RunMinutes,
    int? RunTurns,
    int? FixRoundsCard,
    int? FixRoundsFeature)
{
    /// <summary>
    /// The settings, with the trusted accounts to store, or what is wrong with each field, by its
    /// name on the wire. A label is written, at most <see cref="ProjectSettings.LongestLabel"/>
    /// characters, with no space at either end and no comma, since GitHub's list of issues by label
    /// takes a comma separated list; and the four are different names, whatever their case, as
    /// GitHub's label names are. <c>waits_for_owner</c> is <c>never</c>, <c>sometimes</c> or
    /// <c>always</c>, exactly, and <c>sometimes</c> has at least one of its three conditions
    /// ticked. <c>wait_paths</c> is at most <see cref="PathPatterns.Most"/> patterns, each as
    /// <see cref="PathPatterns.Problem"/> has it; a pattern given twice is kept once.
    /// <c>trusted_logins</c> is a list of GitHub logins, each of GitHub's shape, that holds
    /// <paramref name="mine"/>, the owner's linked login, whatever its case, when he has one; a
    /// login given twice is kept once. Each model is one of <see cref="RunSettings.Models"/>, exactly;
    /// <c>run_minutes</c>, <c>run_turns</c>, <c>fix_rounds_card</c> and <c>fix_rounds_feature</c> are
    /// within <see cref="RunSettings"/>'s ranges.
    /// </summary>
    public (ProjectSettings? Settings, TrustedAccounts? Trusted, Dictionary<string, string[]> Errors) Check(string? mine)
    {
        var errors = new Dictionary<string, string[]>();
        var labels = new (string Field, string? Value)[] { ("pickup_label", PickupLabel), ("hold_label", HoldLabel), ("attention_label", AttentionLabel), ("review_label", ReviewLabel) };
        var seen = new List<string>();
        foreach (var (field, value) in labels)
        {
            if (Label(value) is { } wrong)
            {
                errors[field] = [wrong];
            }
            else if (seen.Contains(value!, StringComparer.OrdinalIgnoreCase))
            {
                errors[field] = ["must be a different label from the other three"];
            }
            else
            {
                seen.Add(value!);
            }
        }

        var conditions = new (string Field, bool? Value)[] { ("wait_when_marked", WaitWhenMarked), ("wait_when_paths", WaitWhenPaths), ("wait_when_decision", WaitWhenDecision) };
        foreach (var (field, _) in conditions.Where(condition => condition.Value is null))
        {
            errors[field] = ["is missing"];
        }

        if (WaitsForOwner is null || !ProjectSettings.WaitsValues.Contains(WaitsForOwner, StringComparer.Ordinal))
        {
            errors["waits_for_owner"] = ["must be never, sometimes or always"];
        }
        else if (WaitsForOwner == "sometimes" && conditions.All(condition => condition.Value == false))
        {
            errors["waits_for_owner"] = ["must have at least one condition ticked on sometimes, which otherwise waits for nothing"];
        }

        if (Paths(WaitPaths) is { } wrongPaths)
        {
            errors["wait_paths"] = [wrongPaths];
        }

        if (Logins(TrustedLogins, mine) is { } wrongLogins)
        {
            errors["trusted_logins"] = [wrongLogins];
        }

        foreach (var (field, value) in new (string, string?)[] { ("model_reading", ModelReading), ("model_building", ModelBuilding), ("model_reviewing", ModelReviewing) })
        {
            if (value is null || !RunSettings.Models.Contains(value, StringComparer.Ordinal))
            {
                errors[field] = [$"must be {string.Join(", ", RunSettings.Models.SkipLast(1))} or {RunSettings.Models[^1]}"];
            }
        }

        foreach (var (field, value, range) in new (string, int?, RunSettings.Range)[]
        {
            ("run_minutes", RunMinutes, RunSettings.MinutesRange),
            ("run_turns", RunTurns, RunSettings.TurnsRange),
            ("fix_rounds_card", FixRoundsCard, RunSettings.RoundsRange),
            ("fix_rounds_feature", FixRoundsFeature, RunSettings.RoundsRange),
        })
        {
            if (value is not { } number || !range.Holds(number))
            {
                errors[field] = [value is null ? "is missing" : range.Problem];
            }
        }

        if (errors.Count > 0)
        {
            return (null, null, errors);
        }

        var trusted = TrustedAccounts.Of(mine, TrustedLogins!.Select(login => login!));
        var paths = WaitPaths!.Select(pattern => pattern!).Distinct(StringComparer.Ordinal).ToList();
        return (
            new ProjectSettings(
                PickupLabel!, HoldLabel!, AttentionLabel!, ReviewLabel!, WaitsForOwner!, WaitWhenMarked!.Value, WaitWhenPaths!.Value, WaitWhenDecision!.Value, paths, trusted.Listed,
                ModelReading!, ModelBuilding!, ModelReviewing!, RunMinutes!.Value, RunTurns!.Value, FixRoundsCard!.Value, FixRoundsFeature!.Value),
            trusted,
            errors);
    }

    /// <summary>What is wrong with the list of paths, or null when nothing is: missing, longer than
    /// <see cref="PathPatterns.Most"/>, or the first pattern that breaks a rule.</summary>
    private static string? Paths(IReadOnlyList<string?>? patterns)
    {
        if (patterns is null)
        {
            return "is missing";
        }

        if (patterns.Count > PathPatterns.Most)
        {
            return $"must hold at most {PathPatterns.Most} patterns";
        }

        return patterns.Select(PathPatterns.Problem).FirstOrDefault(problem => problem is not null);
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
                   s.review_label AS ReviewLabel, s.waits_for_owner AS WaitsForOwner, s.wait_when_marked AS WaitWhenMarked,
                   s.wait_when_paths AS WaitWhenPaths, s.wait_when_decision AS WaitWhenDecision, s.wait_paths AS WaitPaths,
                   s.trusted_logins AS TrustedLogins, a.login AS Mine, s.model_reading AS ModelReading,
                   s.model_building AS ModelBuilding, s.model_reviewing AS ModelReviewing, s.run_minutes AS RunMinutes,
                   s.run_turns AS RunTurns, s.fix_rounds_card AS FixRoundsCard, s.fix_rounds_feature AS FixRoundsFeature
            FROM project_settings s
            LEFT JOIN github_accounts a ON a.user_id = s.user_id
            WHERE s.project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new ProjectSettings(
                row.PickupLabel, row.HoldLabel, row.AttentionLabel, row.ReviewLabel, row.WaitsForOwner, row.WaitWhenMarked, row.WaitWhenPaths,
                row.WaitWhenDecision, row.WaitPaths, TrustedAccounts.Of(row.Mine, row.TrustedLogins).Listed, row.ModelReading, row.ModelBuilding,
                row.ModelReviewing, row.RunMinutes, row.RunTurns, row.FixRoundsCard, row.FixRoundsFeature);
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
            SET pickup_label = @PickupLabel, hold_label = @HoldLabel, attention_label = @AttentionLabel, review_label = @ReviewLabel,
                waits_for_owner = @WaitsForOwner, wait_when_marked = @WaitWhenMarked, wait_when_paths = @WaitWhenPaths,
                wait_when_decision = @WaitWhenDecision, wait_paths = @waitPaths, trusted_logins = @trustedLogins,
                model_reading = @ModelReading, model_building = @ModelBuilding, model_reviewing = @ModelReviewing,
                run_minutes = @RunMinutes, run_turns = @RunTurns, fix_rounds_card = @FixRoundsCard, fix_rounds_feature = @FixRoundsFeature
            WHERE project_id = @projectId
            """,
            new
            {
                projectId,
                settings.PickupLabel,
                settings.HoldLabel,
                settings.AttentionLabel,
                settings.ReviewLabel,
                settings.WaitsForOwner,
                settings.WaitWhenMarked,
                settings.WaitWhenPaths,
                settings.WaitWhenDecision,
                waitPaths = settings.WaitPaths.ToArray(),
                trustedLogins = trusted.Others.ToArray(),
                settings.ModelReading,
                settings.ModelBuilding,
                settings.ModelReviewing,
                settings.RunMinutes,
                settings.RunTurns,
                settings.FixRoundsCard,
                settings.FixRoundsFeature,
            },
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
        public string ReviewLabel { get; init; } = "";
        public string WaitsForOwner { get; init; } = "";
        public bool WaitWhenMarked { get; init; }
        public bool WaitWhenPaths { get; init; }
        public bool WaitWhenDecision { get; init; }
        public string[] WaitPaths { get; init; } = [];
        public string[] TrustedLogins { get; init; } = [];
        public string? Mine { get; init; }
        public string ModelReading { get; init; } = "";
        public string ModelBuilding { get; init; } = "";
        public string ModelReviewing { get; init; } = "";
        public int RunMinutes { get; init; }
        public int RunTurns { get; init; }
        public int FixRoundsCard { get; init; }
        public int FixRoundsFeature { get; init; }
    }
}
namespace DevFlow.Projects;

/// <summary>A project as <c>GET /devflow/api/projects</c> answers it: the project, and how it is
/// doing.</summary>
/// <param name="Id">The project's own id.</param>
/// <param name="InstallationId">The installation of the app it is reached through.</param>
/// <param name="RepositoryId">The repository's id on the code host.</param>
/// <param name="Owner">Its owner, as the code host named it when it was connected.</param>
/// <param name="Name">Its name, as the code host named it when it was connected.</param>
/// <param name="CreatedAt">When it was connected.</param>
/// <param name="Summary">How it is doing, computed as it is listed.</param>
public sealed record ListedProject(Guid Id, long InstallationId, long RepositoryId, string Owner, string Name, DateTimeOffset CreatedAt, ProjectSummary Summary);

/// <summary>How a project is doing, for its card on the Projects tab, computed from what Dev flow
/// stored and never stored itself. See "Projects" in devflow/docs/devflow-architecture.md.</summary>
/// <param name="Cards">Its cards, counted by group, <see cref="ProjectSummaries.Groups"/>.</param>
/// <param name="Workers">Its workers' health.</param>
/// <param name="SettingUp">Its setup steps, while its state is
/// <see cref="Setup.SetupStore.SettingUp"/>; null once it is registered.</param>
/// <param name="Feature">Its open feature with the most cards landed; null with none open.</param>
public sealed record ProjectSummary(CardCounts Cards, WorkersHealth Workers, SetupProgress? SettingUp, FeatureProgress? Feature);

/// <summary>A project's cards counted by group, <see cref="ProjectSummaries.Groups"/>; an ignored
/// card is in none.</summary>
public sealed record CardCounts(int NeedsYou, int InProgress, int Queued, int Done);

/// <summary>A project's workers' health.</summary>
/// <param name="Count">How many workers have said hello with its key.</param>
/// <param name="LastSeenAt">When the one seen last was seen, null with none.</param>
/// <param name="Held">Why it hands out no work, the same value the workers list answers:
/// <c>stopped</c>, <c>unwatched</c>, <c>no_claude_login</c> or <c>claude_login_expired</c>; null
/// when work is handed out.</param>
/// <param name="CannotRun">What the workers list says once six runs in a row failed outside their
/// cards, null otherwise.</param>
public sealed record WorkersHealth(int Count, DateTimeOffset? LastSeenAt, string? Held, string? CannotRun);

/// <summary>How far a project setting up is: its steps done, of <see cref="Total"/>.</summary>
public sealed record SetupProgress(int Done, int Total);

/// <summary>A project's open feature: its parent's number and title as last read, its cards landed,
/// in <c>done</c>, and its cards, an ignored one not counted.</summary>
public sealed record FeatureProgress(int Number, string Title, int Landed, int Total);

/// <summary>The groups a project's summary counts its cards in, written once here.</summary>
public static class ProjectSummaries
{
    /// <summary>A card's turn is the owner's.</summary>
    public const string NeedsYou = "needs_you";

    /// <summary>A card is being read, built, checked, reviewed or fixed.</summary>
    public const string InProgress = "in_progress";

    /// <summary>A card waits for its turn.</summary>
    public const string Queued = "queued";

    /// <summary>A card has landed.</summary>
    public const string Done = "done";

    /// <summary>
    /// Each group's card states, over the thirteen the <c>cards.state</c> check allows; every
    /// state but <c>ignored</c> is in exactly one. The feature states <c>STATES</c> in
    /// <c>web/src/cards.tsx</c> also names are not card states and are in none.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        [NeedsYou] = ["needs_answers", "waiting_for_owner", "stopped", "refused"],
        [InProgress] = ["reading", "building", "checking", "reviewing", "fixing"],
        [Queued] = ["queued", "held"],
        [Done] = ["done"],
    };

    /// <summary>Every state some group counts, which a feature's total counts too.</summary>
    internal static string[] Counted => [.. Groups.Values.SelectMany(states => states)];
}
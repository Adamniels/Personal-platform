using System.Text.Json;
using System.Text.Json.Serialization;

using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Runner;
using DevFlow.Stops;

using PlatformData;

namespace DevFlow.Reading;

/// <summary>Whose turn an item is, as the API says it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WaitingKind>))]
public enum WaitingKind
{
    /// <summary>Approved and handed to the owner, to read and merge or answer.</summary>
    [JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>The runner gave up on it: it must not be merged as it is.</summary>
    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>A pull request that is the owner's turn, as last read: the stored row, with its
/// project's id and name. Every text is plain text exactly as read, which a page shows as text.</summary>
/// <param name="ProjectName">The repository's name, without its owner, as the project's.</param>
/// <param name="Screenshots">The screenshots its body shows of its own, paired for the page, which
/// it reads through <c>GET /devflow/api/waiting/{id}/screenshots/{name}</c>.</param>
/// <param name="ReadAt">When it was last read as it is.</param>
public sealed record WaitingItem(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    int Number,
    WaitingKind Kind,
    bool SentBack,
    string Title,
    Uri WebUrl,
    int? SliceNumber,
    string HeadCommit,
    bool CanMerge,
    string Summary,
    string Reason,
    IReadOnlyList<OwnerDecision> Decisions,
    int OtherDecisions,
    IReadOnlyList<string> ReviewerView,
    IReadOnlyList<ScreenshotGroup> Screenshots,
    DateTimeOffset ReadAt);

/// <summary>Where an item's screenshots are read from: its project's repository, its pull
/// request's number, and the names its body showed, the only ones that may be read.</summary>
public sealed record ScreenshotSource(Repository Repository, int Number, IReadOnlyList<string> Names);

/// <summary>How old a project's picture is.</summary>
/// <param name="LastSuccessAt">When a read of it last succeeded, null when none has.</param>
/// <param name="Stale">Whether that is more than <see cref="WaitingStore.StaleAfter"/> ago, or never.</param>
/// <param name="Failing">Whether its last read failed.</param>
public sealed record ProjectFreshness(Guid Id, string Owner, string Name, DateTimeOffset? LastSuccessAt, bool Stale, bool Failing);

/// <summary>What kind of thing of Dev flow's own waits for the owner.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OwnItemKind>))]
public enum OwnItemKind
{
    /// <summary>Something landed on a project's default branch that Dev flow did not put there,
    /// and the project is stopped until he looks and resumes it.</summary>
    [JsonStringEnumMemberName("project_stopped")]
    ProjectStopped,
}

/// <summary>Something of Dev flow's own that waits for the owner, read from its own tables rather
/// than a pull request: in this part only a stopped project. Every text the code host gave, the
/// actor among them, is shown as text.</summary>
/// <param name="ProjectName">The repository's name, as connected.</param>
/// <param name="Owner">The repository's owner, as connected.</param>
/// <param name="Branch">The default branch something landed on.</param>
/// <param name="ActivityType">What landed: <c>push</c>, <c>force_push</c>,
/// <c>branch_creation</c>, <c>branch_deletion</c>, <c>pull_request_merge</c> or
/// <c>merge_queue_merge</c>.</param>
/// <param name="Actor">Who did it, as the code host named them, or null when it named nobody.</param>
/// <param name="Commit">The full id of the commit the branch was at after it, which resuming
/// names.</param>
/// <param name="CommitUrl">Where the commit is on the code host.</param>
/// <param name="LandedAt">When it landed.</param>
/// <param name="StoppedAt">When the project stopped.</param>
public sealed record OwnItem(
    OwnItemKind Kind,
    Guid ProjectId,
    string ProjectName,
    string Owner,
    string Branch,
    string ActivityType,
    string? Actor,
    string Commit,
    Uri CommitUrl,
    DateTimeOffset LandedAt,
    DateTimeOffset StoppedAt);

/// <summary>The check Dev flow ran on a card's commit, as its run recorded it.</summary>
/// <param name="Command">The check, from the project's <c>.devflow/project.json</c>; null for a
/// run handed out before runs kept it.</param>
/// <param name="ExitCode">How it ended, 0 for every card that waits.</param>
/// <param name="Commit">The commit it ran on, the pull request's head as built.</param>
public sealed record CardCheck(string? Command, int ExitCode, string Commit);

/// <summary>The reviewer's verdict on a card's head commit, as Dev flow kept it.</summary>
/// <param name="Verdict"><c>approve</c> or <c>changes</c>.</param>
/// <param name="Summary">What the reviewer found, as text.</param>
/// <param name="Blocking">How many of its findings block the card.</param>
/// <param name="Commit">The commit it is for, the card's head.</param>
public sealed record CardItemReview(string Verdict, string Summary, int Blocking, string Commit);

/// <summary>A card of Dev flow's own whose pull request waits for the owner, read from its own
/// tables beside the runner's items: the card, its pull request, what its report says, and the
/// check Dev flow ran. Every text came from the code host or the agent, and is shown as text.</summary>
/// <param name="ProjectName">The repository's name, as connected.</param>
/// <param name="Owner">The repository's owner, as connected.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">The card's title, as it was picked.</param>
/// <param name="WebUrl">Where the card's issue is on the code host.</param>
/// <param name="PullRequestNumber">Its pull request's number.</param>
/// <param name="PullRequestUrl">Where its pull request is on the code host.</param>
/// <param name="HeadCommit">The commit its run built and checked, which merging names.</param>
/// <param name="Base">The branch its pull request merges into.</param>
/// <param name="Summary">The report's summary.</param>
/// <param name="Decisions">The decisions the agent marked for the owner, each with what was
/// chosen, the alternative and why, null when it wrote none.</param>
/// <param name="OtherDecisions">How many it made that it did not mark for him.</param>
/// <param name="Check">The check Dev flow ran.</param>
/// <param name="CanMerge">Whether Merge is offered: false while the project is stopped.</param>
/// <param name="ChangedAt">When the card came to wait.</param>
/// <param name="Review">The reviewer's verdict on its head commit, null when it has none.</param>
/// <param name="Screenshots">The pictures the worker took of its head commit, null when its build
/// tried none.</param>
public sealed record CardItem(
    Guid ProjectId,
    string ProjectName,
    string Owner,
    int Number,
    string Title,
    Uri WebUrl,
    int PullRequestNumber,
    Uri PullRequestUrl,
    string HeadCommit,
    string Base,
    string Summary,
    IReadOnlyList<OwnerDecision> Decisions,
    int OtherDecisions,
    CardCheck Check,
    bool CanMerge,
    DateTimeOffset ChangedAt,
    CardItemReview? Review,
    CardScreenshots? Screenshots);

/// <summary>A card of Dev flow's own that stopped, shown under "Stopped" until the owner's comment
/// runs it again. Its title and its reason came from the code host, a worker or an agent, and are
/// shown as text. See "Stops" in devflow/README.md.</summary>
/// <param name="ProjectName">The repository's name, as connected.</param>
/// <param name="Owner">The repository's owner, as connected.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">The card's title, as it was picked.</param>
/// <param name="WebUrl">Where the card's issue is on the code host, where a comment runs it
/// again.</param>
/// <param name="Reason">Why it stopped.</param>
/// <param name="StoppedAt">When it stopped.</param>
/// <param name="PullRequestUrl">Where its pull request is, null when it has none.</param>
public sealed record StoppedCard(Guid ProjectId, string ProjectName, string Owner, int Number, string Title, Uri WebUrl, string Reason, DateTimeOffset StoppedAt, Uri? PullRequestUrl);

/// <summary>A project whose runs keep failing outside their cards, so it cannot run them, shown
/// until a run succeeds. See "Stops" in devflow/README.md.</summary>
/// <param name="ProjectName">The repository's name, as connected.</param>
/// <param name="Owner">The repository's owner, as connected.</param>
/// <param name="Message">"Dev flow cannot run &lt;project&gt;'s cards: &lt;the last reason&gt;".</param>
/// <param name="Reason">The last failure's reason, from a worker or Dev flow, shown as text.</param>
/// <param name="Failures">How many runs in a row failed outside their cards.</param>
/// <param name="Since">When the first of them failed.</param>
/// <param name="WaitUntil">Until when it hands out no job.</param>
public sealed record FailingProject(Guid ProjectId, string ProjectName, string Owner, string Message, string Reason, int Failures, DateTimeOffset Since, DateTimeOffset WaitUntil);

/// <summary>What <c>GET /devflow/api/waiting</c> answers.</summary>
/// <param name="Count">The items not sent back, Dev flow's own items and cards, and one more when
/// the link needs renewing: what waits for the owner to act.</param>
/// <param name="NeedsRelink">Whether GitHub stopped taking the owner's link.</param>
/// <param name="OwnItems">Dev flow's own items, read from its own tables beside the runner's, by
/// project.</param>
/// <param name="Cards">Dev flow's own cards whose pull requests wait for the owner, by project and
/// number.</param>
/// <param name="StoppedCards">Dev flow's own cards that stopped, by project and number.</param>
/// <param name="FailingProjects">The projects whose runs have failed outside their cards six times
/// in a row, by project.</param>
public sealed record WaitingList(int Count, bool NeedsRelink, IReadOnlyList<WaitingItem> Items, IReadOnlyList<ProjectFreshness> Projects, IReadOnlyList<OwnItem> OwnItems, IReadOnlyList<CardItem> Cards, IReadOnlyList<StoppedCard> StoppedCards, IReadOnlyList<FailingProject> FailingProjects);

/// <summary>
/// What waits for a user, read from the snapshot <see cref="ProjectReader"/> stores and never from
/// the code host, through the door as the user. See "What waits" in devflow/README.md.
/// </summary>
public sealed class WaitingStore(UserDb database, TimeProvider time)
{
    /// <summary>How long ago a project's last success may be before its picture is stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The user's items, by project and number, their projects' freshness, by owner and
    /// name, and whether their link needs renewing, read in one transaction.</summary>
    public async Task<WaitingList> For(Guid userId, CancellationToken cancellationToken)
    {
        var (items, projects, relink, stops, cardRows, stoppedRows, failingRows) = await database.InUserTransaction(userId, async transaction =>
        {
            var items = await transaction.Connection!.QueryAsync<ItemRow>(new CommandDefinition(
                """
                SELECT w.id AS Id, w.project_id AS ProjectId, p.name AS ProjectName, w.number AS Number, w.kind AS Kind,
                       w.sent_back AS SentBack, w.title AS Title, w.web_url AS WebUrl, w.slice_number AS SliceNumber,
                       w.head_commit AS HeadCommit, w.can_merge AS CanMerge, w.summary AS Summary, w.reason AS Reason,
                       w.decisions::text AS Decisions, w.other_decisions AS OtherDecisions,
                       w.reviewer_view::text AS ReviewerView, w.screenshots::text AS Screenshots, w.read_at AS ReadAt
                FROM waiting_items w JOIN projects p ON p.id = w.project_id
                ORDER BY lower(p.owner), lower(p.name), p.id, w.number
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            var projects = await transaction.Connection!.QueryAsync<ProjectRow>(new CommandDefinition(
                """
                SELECT p.id AS Id, p.owner AS Owner, p.name AS Name, r.last_success_at AS LastSuccessAt,
                       r.last_error IS NOT NULL AS Failing
                FROM projects p LEFT JOIN project_reads r ON r.project_id = p.id
                ORDER BY lower(p.owner), lower(p.name), p.id
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            var relink = await transaction.Connection!.QuerySingleOrDefaultAsync<bool>(new CommandDefinition(
                "SELECT state = 'needs_relink' FROM github_accounts",
                transaction: transaction,
                cancellationToken: cancellationToken));
            var stops = await transaction.Connection!.QueryAsync<StopRow>(new CommandDefinition(
                """
                SELECT s.project_id AS ProjectId, p.name AS Name, p.owner AS Owner, s.branch AS Branch, s.activity_type AS ActivityType,
                       s.actor AS Actor, s.commit AS Commit, s.landed_at AS LandedAt, s.stopped_at AS StoppedAt
                FROM project_stops s JOIN projects p ON p.id = s.project_id
                ORDER BY lower(p.owner), lower(p.name), p.id
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            var cards = await transaction.Connection!.QueryAsync<CardRow>(new CommandDefinition(
                """
                SELECT c.project_id AS ProjectId, p.name AS ProjectName, p.owner AS Owner, c.number AS Number,
                       coalesce(c.picked_title, c.title) AS Title, c.web_url AS WebUrl, c.pull_request_number AS PullRequestNumber,
                       c.pull_request_url AS PullRequestUrl, r.head_commit AS HeadCommit, r.base AS Base, r.report::text AS Report,
                       r.check_command AS CheckCommand, r.check_exit_code AS CheckExitCode,
                       NOT EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = c.project_id) AS CanMerge,
                       c.changed_at AS ChangedAt, v.verdict AS Verdict, v.summary AS VerdictSummary,
                       (SELECT count(*) FROM jsonb_array_elements(v.findings) f WHERE f->>'blocking' = 'true')::int AS Blocking,
                       r.screenshots::text AS Screenshots, r.screenshots_note AS ScreenshotsNote
                FROM cards c
                JOIN projects p ON p.id = c.project_id
                JOIN LATERAL (SELECT * FROM runs WHERE runs.project_id = c.project_id AND runs.card_number = c.number
                              AND runs.kind IN ('build', 'fix') AND runs.result = 'succeeded' ORDER BY started_at DESC, id DESC LIMIT 1) r ON true
                LEFT JOIN LATERAL (SELECT * FROM reviews WHERE reviews.project_id = c.project_id AND reviews.card_number = c.number
                                   AND reviews.reviewed_commit = r.head_commit
                                   ORDER BY reviewed_at DESC, run_id DESC LIMIT 1) v ON true
                WHERE c.state = 'waiting_for_owner' AND c.pull_request_number IS NOT NULL AND r.head_commit IS NOT NULL
                ORDER BY lower(p.owner), lower(p.name), p.id, c.number
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            var failing = await transaction.Connection!.QueryAsync<FailingRow>(new CommandDefinition(
                """
                SELECT w.project_id AS ProjectId, p.name AS ProjectName, p.owner AS Owner, w.reason AS Reason, w.failures AS Failures,
                       w.since AS Since, w.wait_until AS WaitUntil
                FROM project_waits w JOIN projects p ON p.id = w.project_id
                WHERE w.failures >= @shown
                ORDER BY lower(p.owner), lower(p.name), p.id
                """,
                new { shown = ProjectWaits.Shown },
                transaction,
                cancellationToken: cancellationToken));
            var stopped = await transaction.Connection!.QueryAsync<StoppedRow>(new CommandDefinition(
                """
                SELECT c.project_id AS ProjectId, p.name AS ProjectName, p.owner AS Owner, c.number AS Number,
                       coalesce(c.picked_title, c.title) AS Title, c.web_url AS WebUrl, coalesce(c.reason, '') AS Reason,
                       coalesce(c.stopped_at, c.changed_at) AS StoppedAt, c.pull_request_url AS PullRequestUrl
                FROM cards c JOIN projects p ON p.id = c.project_id
                WHERE c.state = 'stopped'
                ORDER BY lower(p.owner), lower(p.name), p.id, c.number
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));
            return (items.ToList(), projects.ToList(), relink, stops.ToList(), cards.ToList(), stopped.ToList(), failing.ToList());
        }, cancellationToken);

        var now = time.GetUtcNow();
        var waiting = items.Select(Item).ToList();
        var own = stops.Select(stop => new OwnItem(
            OwnItemKind.ProjectStopped,
            stop.ProjectId,
            stop.Name,
            stop.Owner,
            stop.Branch,
            stop.ActivityType,
            stop.Actor,
            stop.Commit,
            CommitUrl(stop.Owner, stop.Name, stop.Commit),
            GitHubLink.Utc(stop.LandedAt),
            GitHubLink.Utc(stop.StoppedAt))).ToList();
        var cards = cardRows.Select(Card).ToList();
        var failingProjects = failingRows.Select(row => new FailingProject(
            row.ProjectId,
            row.ProjectName,
            row.Owner,
            ProjectWaits.CannotRun(row.ProjectName, row.Reason),
            row.Reason,
            row.Failures,
            GitHubLink.Utc(row.Since),
            GitHubLink.Utc(row.WaitUntil))).ToList();
        var stoppedCards = stoppedRows.Select(row => new StoppedCard(
            row.ProjectId,
            row.ProjectName,
            row.Owner,
            row.Number,
            row.Title,
            new Uri(row.WebUrl),
            row.Reason,
            GitHubLink.Utc(row.StoppedAt),
            row.PullRequestUrl is { } address ? new Uri(address) : null)).ToList();
        return new WaitingList(
            waiting.Count(item => !item.SentBack) + own.Count + cards.Count + stoppedCards.Count + failingProjects.Count + (relink ? 1 : 0),
            relink,
            waiting,
            [.. projects.Select(project =>
            {
                DateTimeOffset? success = project.LastSuccessAt is { } at ? GitHubLink.Utc(at) : null;
                return new ProjectFreshness(project.Id, project.Owner, project.Name, success, success is null || now - success > StaleAfter, project.Failing);
            })],
            own,
            cards,
            stoppedCards,
            failingProjects);
    }

    /// <summary>A waiting card as the list answers it, from its row and its run's report, read into
    /// its form; a report that does not read, which a waiting card's run never has, says
    /// nothing.</summary>
    private static CardItem Card(CardRow row)
    {
        using var document = JsonDocument.Parse(row.Report ?? "null");
        var report = Protocol.Report.TryRead(document.RootElement, out var read, out _) ? read : null;
        var decisions = report?.Decisions ?? [];
        return new CardItem(
            row.ProjectId,
            row.ProjectName,
            row.Owner,
            row.Number,
            row.Title,
            new Uri(row.WebUrl),
            row.PullRequestNumber,
            new Uri(row.PullRequestUrl),
            row.HeadCommit,
            row.Base,
            report?.Summary.Trim() ?? "",
            [.. decisions.Where(decision => decision.ForOwner).Select(decision => new OwnerDecision(decision.Chosen.Trim(), Said(decision.Alternative), Said(decision.Why)))],
            decisions.Count(decision => !decision.ForOwner),
            new CardCheck(row.CheckCommand, row.CheckExitCode ?? 0, row.HeadCommit),
            row.CanMerge,
            GitHubLink.Utc(row.ChangedAt),
            row.Verdict is { } verdict ? new CardItemReview(verdict, row.VerdictSummary?.Trim() ?? "", row.Blocking, row.HeadCommit) : null,
            CardScreenshots.Of(row.HeadCommit, row.Screenshots, row.ScreenshotsNote));
    }

    /// <summary>What the agent wrote, trimmed, or null when it wrote nothing.</summary>
    private static string? Said(string text) => text.Trim() is { Length: > 0 } said ? said : null;

    /// <summary>The commit's page on GitHub, the one code host a project can be on, from the
    /// repository's owner and name as connected, each escaped; the commit is a full id, checked
    /// when it was read and by the table.</summary>
    private static Uri CommitUrl(string owner, string name, string commit) =>
        new($"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commit/{commit}");

    /// <summary>Whether the user has the project.</summary>
    public async Task<bool> Has(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM projects WHERE id = @projectId)",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>What serving one of the caller's item's screenshots needs: its project's repository,
    /// by the installation and id it was connected with, its number and the names its body showed;
    /// null when the caller has no such item.</summary>
    public async Task<ScreenshotSource?> ScreenshotsOf(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ScreenshotRow>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                   w.number AS Number, w.screenshots::text AS Screenshots
            FROM waiting_items w JOIN projects p ON p.id = w.project_id
            WHERE w.id = @itemId
            """,
            new { itemId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is null
            ? null
            : new ScreenshotSource(
                new Repository(row.Installation, row.RepositoryId, row.Owner, row.Name),
                row.Number,
                JsonSerializer.Deserialize<List<string>>(row.Screenshots, Json) ?? []);
    }

    private static WaitingItem Item(ItemRow row) => new(
        row.Id,
        row.ProjectId,
        row.ProjectName,
        row.Number,
        row.Kind == "waiting" ? WaitingKind.Waiting : WaitingKind.Stopped,
        row.SentBack,
        row.Title,
        new Uri(row.WebUrl),
        row.SliceNumber,
        row.HeadCommit,
        row.CanMerge,
        row.Summary,
        row.Reason,
        JsonSerializer.Deserialize<List<OwnerDecision>>(row.Decisions, Json) ?? [],
        row.OtherDecisions,
        JsonSerializer.Deserialize<List<string>>(row.ReviewerView, Json) ?? [],
        Screenshots.Group(JsonSerializer.Deserialize<List<string>>(row.Screenshots, Json) ?? []),
        GitHubLink.Utc(row.ReadAt));

    private sealed record ItemRow(
        Guid Id,
        Guid ProjectId,
        string ProjectName,
        int Number,
        string Kind,
        bool SentBack,
        string Title,
        string WebUrl,
        int? SliceNumber,
        string HeadCommit,
        bool CanMerge,
        string Summary,
        string Reason,
        string Decisions,
        int OtherDecisions,
        string ReviewerView,
        string Screenshots,
        DateTime ReadAt);

    private sealed record ScreenshotRow(long Installation, long RepositoryId, string Owner, string Name, int Number, string Screenshots);

    private sealed record ProjectRow(Guid Id, string Owner, string Name, DateTime? LastSuccessAt, bool Failing);

    private sealed record CardRow(
        Guid ProjectId,
        string ProjectName,
        string Owner,
        int Number,
        string Title,
        string WebUrl,
        int PullRequestNumber,
        string PullRequestUrl,
        string HeadCommit,
        string Base,
        string? Report,
        string? CheckCommand,
        int? CheckExitCode,
        bool CanMerge,
        DateTime ChangedAt,
        string? Verdict,
        string? VerdictSummary,
        int Blocking,
        string? Screenshots,
        string? ScreenshotsNote);

    private sealed record StoppedRow(Guid ProjectId, string ProjectName, string Owner, int Number, string Title, string WebUrl, string Reason, DateTime StoppedAt, string? PullRequestUrl);

    private sealed record FailingRow(Guid ProjectId, string ProjectName, string Owner, string Reason, int Failures, DateTime Since, DateTime WaitUntil);

    private sealed record StopRow(Guid ProjectId, string Name, string Owner, string Branch, string ActivityType, string? Actor, string Commit, DateTime LandedAt, DateTime StoppedAt);
}
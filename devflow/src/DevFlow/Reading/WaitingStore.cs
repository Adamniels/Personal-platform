using System.Text.Json;
using System.Text.Json.Serialization;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Runner;

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
    DateTimeOffset ReadAt);

/// <summary>Where an item's screenshots are read from: its project's repository, its pull
/// request's number, and the names its body showed, the only ones that may be read.</summary>
public sealed record ScreenshotSource(Repository Repository, int Number, IReadOnlyList<string> Names);

/// <summary>How old a project's picture is.</summary>
/// <param name="LastSuccessAt">When a read of it last succeeded, null when none has.</param>
/// <param name="Stale">Whether that is more than <see cref="WaitingStore.StaleAfter"/> ago, or never.</param>
/// <param name="Failing">Whether its last read failed.</param>
public sealed record ProjectFreshness(Guid Id, string Owner, string Name, DateTimeOffset? LastSuccessAt, bool Stale, bool Failing);

/// <summary>What <c>GET /devflow/api/waiting</c> answers.</summary>
/// <param name="Count">The items not sent back, and one more when the link needs renewing: what
/// waits for the owner to act.</param>
/// <param name="NeedsRelink">Whether GitHub stopped taking the owner's link.</param>
public sealed record WaitingList(int Count, bool NeedsRelink, IReadOnlyList<WaitingItem> Items, IReadOnlyList<ProjectFreshness> Projects);

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
        var (items, projects, relink) = await database.InUserTransaction(userId, async transaction =>
        {
            var items = await transaction.Connection!.QueryAsync<ItemRow>(new CommandDefinition(
                """
                SELECT w.id AS Id, w.project_id AS ProjectId, p.name AS ProjectName, w.number AS Number, w.kind AS Kind,
                       w.sent_back AS SentBack, w.title AS Title, w.web_url AS WebUrl, w.slice_number AS SliceNumber,
                       w.head_commit AS HeadCommit, w.can_merge AS CanMerge, w.summary AS Summary, w.reason AS Reason,
                       w.decisions::text AS Decisions, w.other_decisions AS OtherDecisions,
                       w.reviewer_view::text AS ReviewerView, w.read_at AS ReadAt
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
            return (items.ToList(), projects.ToList(), relink);
        }, cancellationToken);

        var now = time.GetUtcNow();
        var waiting = items.Select(Item).ToList();
        return new WaitingList(
            waiting.Count(item => !item.SentBack) + (relink ? 1 : 0),
            relink,
            waiting,
            [.. projects.Select(project =>
            {
                DateTimeOffset? success = project.LastSuccessAt is { } at ? GitHubLink.Utc(at) : null;
                return new ProjectFreshness(project.Id, project.Owner, project.Name, success, success is null || now - success > StaleAfter, project.Failing);
            })]);
    }

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
        DateTime ReadAt);

    private sealed record ScreenshotRow(long Installation, long RepositoryId, string Owner, string Name, int Number, string Screenshots);

    private sealed record ProjectRow(Guid Id, string Owner, string Name, DateTime? LastSuccessAt, bool Failing);
}
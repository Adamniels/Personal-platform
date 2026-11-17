using System.Text.Json;

using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Projects;
using DevFlow.Runner;
using DevFlow.Workers;

using Npgsql;

using PlatformData;

namespace DevFlow.Reading;

/// <summary>How a read of one project ended.</summary>
public enum ReadResult
{
    /// <summary>The code host answered, and the project's items are what it answered.</summary>
    Read,

    /// <summary>It did not, or something threw: the items are as they were, and the failure is
    /// recorded.</summary>
    Failed,

    /// <summary>The user has no such project, disconnected since it was asked for or never
    /// theirs: nothing was read or stored.</summary>
    NoSuchProject,
}

/// <summary>
/// Reads one project from the code host into the snapshot the pages read: its open pull requests in
/// one list, then, only for those by the project's <c>bot_login</c> that carry an owner label, the
/// comments, the head's statuses and check runs, and the comments of the slice the body closes;
/// each through <see cref="RunnerReader.Read"/>; then its cards, through
/// <see cref="CardPicker.Pick"/>; and the result in place of the project's stored items, and
/// merged into its cards, in one transaction through the door as the user, so the two always
/// describe the same read. A read that fails leaves the items as
/// they were and records why, as a fixed phrase. The reads are as the app, through
/// <see cref="ICodeHost"/>, so they work whether or not the user's own link does. See "Reading and
/// the snapshot" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class ProjectReader(UserDb database, ICodeHost host, TimeProvider time, ILogger<ProjectReader> log)
{
    /// <summary>Why a read failed, as stored and shown: the code host did not answer.</summary>
    public const string HostUnavailable = "GitHub did not answer";

    /// <summary>The code host does not show the repository, removed or no longer reached by the
    /// app.</summary>
    public const string RepositoryNotFound = "GitHub does not show this repository";

    /// <summary>Anything else, a bug among it; never the exception's own text, which may carry
    /// the code host's.</summary>
    public const string ReadFailed = "Reading failed";

    /// <summary>The runner's labels that make a pull request the owner's turn, the only ones read
    /// past the list. See "Reading this repository's runner" in
    /// devflow/docs/devflow-architecture.md.</summary>
    private static readonly string[] OwnerLabels = ["owner-merge", "owner-decision", "owner-blocked"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Reads the project as <paramref name="userId"/> and stores what waits. Never throws for what
    /// the code host answers or for a bug in reading: either is recorded as the project's failure.
    /// Only a database that cannot be reached, or cancellation, escapes.
    /// </summary>
    public async Task<ReadResult> Read(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        if (await Find(projectId, userId, cancellationToken) is not { } target)
        {
            return ReadResult.NoSuchProject;
        }

        string? failure;
        IReadOnlyList<RunnerItem> items = [];
        IReadOnlyList<PickedCard> cards = [];
        try
        {
            (items, cards, failure) = await Fetch(target, userId, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogReadFailed(log, projectId, error);
            failure = ReadFailed;
        }

        var now = Now();
        if (failure is null)
        {
            try
            {
                if (!await Store(projectId, userId, items, cards, now, cancellationToken))
                {
                    return ReadResult.NoSuchProject;
                }

                await ReadRecipe(target, projectId, userId, cancellationToken);
                return ReadResult.Read;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogStoreFailed(log, projectId, error);
                failure = ReadFailed;
            }
        }

        await RecordFailure(projectId, userId, failure, now, cancellationToken);
        return ReadResult.Failed;
    }

    /// <summary>The time, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>What reading the project needs, through the door as the user: its repository, the
    /// bot whose pull requests are read, the owner's login from his link, whatever its state, or
    /// null when he has none, and the labels its cards are picked by. Null when the user has no
    /// such project.</summary>
    private async Task<ProjectTarget?> Find(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectTarget>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                   p.bot_login AS Bot, a.login AS OwnerLogin,
                   s.pickup_label AS PickupLabel, s.hold_label AS HoldLabel, s.attention_label AS AttentionLabel,
                   s.waits_for_owner AS WaitsForOwner
            FROM projects p
            JOIN project_settings s ON s.project_id = p.id
            LEFT JOIN github_accounts a ON a.user_id = p.user_id
            WHERE p.id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The project's items and cards as the code host answers now, or why it could not be
    /// read.</summary>
    private async Task<(IReadOnlyList<RunnerItem> Items, IReadOnlyList<PickedCard> Cards, string? Failure)> Fetch(ProjectTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var (items, failure) = await FetchItems(target, userId, cancellationToken);
        if (failure is not null)
        {
            return ([], [], failure);
        }

        var settings = new ProjectSettings(target.PickupLabel, target.HoldLabel, target.AttentionLabel, target.WaitsForOwner);
        var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
        return await CardPicker.Pick(host, userId, repository, settings, target.OwnerLogin, cancellationToken) switch
        {
            CodeHostResult<IReadOnlyList<PickedCard>>.Found { Value: var cards } => (items, cards, null),
            CodeHostResult<IReadOnlyList<PickedCard>>.NotFound => ([], [], RepositoryNotFound),
            CodeHostResult<IReadOnlyList<PickedCard>>.Unavailable => ([], [], HostUnavailable),
            _ => throw new InvalidOperationException("a code host result that is not one of the three"),
        };
    }

    /// <summary>The project's waiting items as the code host answers now, or why they could not be
    /// read.</summary>
    private async Task<(IReadOnlyList<RunnerItem> Items, string? Failure)> FetchItems(ProjectTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
        IReadOnlyList<PullRequest> open;
        switch (await host.OpenPullRequests(userId, repository, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var found }:
                open = found;
                break;
            case CodeHostResult<IReadOnlyList<PullRequest>>.NotFound:
                return ([], RepositoryNotFound);
            case CodeHostResult<IReadOnlyList<PullRequest>>.Unavailable:
                return ([], HostUnavailable);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        var items = new List<RunnerItem>();
        foreach (var pull in open.Where(pull => IsTheOwnersTurn(pull, target.Bot)).OrderBy(pull => pull.Number))
        {
            // What is not found is read as nothing: a conversation, a commit's checks, or a slice
            // that is not there. Only a host that does not answer fails the read.
            var comments = Listed(await host.Comments(userId, repository, pull.Number, cancellationToken));
            var statuses = Listed(await host.Statuses(userId, repository, pull.HeadCommit, cancellationToken));
            var checkRuns = Listed(await host.CheckRuns(userId, repository, pull.HeadCommit, cancellationToken));
            var sliceComments = RunnerReader.Slice(pull.Body ?? "") is { } slice
                ? Listed(await host.Comments(userId, repository, slice, cancellationToken))
                : [];
            if (comments is null || statuses is null || checkRuns is null || sliceComments is null)
            {
                return ([], HostUnavailable);
            }

            if (RunnerReader.Read(new RunnerInput(repository, pull, comments, statuses, checkRuns, sliceComments, target.Bot, target.OwnerLogin)) is { } item)
            {
                items.Add(item);
            }
        }

        return (items, null);
    }

    /// <summary>By the bot and carrying an owner label: the only pull requests that cost more than
    /// the list.</summary>
    private static bool IsTheOwnersTurn(PullRequest pull, string bot) =>
        string.Equals(pull.Author, bot, StringComparison.Ordinal)
        && (pull.Labels ?? []).Any(label => OwnerLabels.Contains(label, StringComparer.Ordinal));

    /// <summary>The list found, empty when not found, and null when the host did not answer.</summary>
    private static IReadOnlyList<T>? Listed<T>(CodeHostResult<IReadOnlyList<T>> result) => result switch
    {
        CodeHostResult<IReadOnlyList<T>>.Found { Value: var value } => value,
        CodeHostResult<IReadOnlyList<T>>.NotFound => [],
        CodeHostResult<IReadOnlyList<T>>.Unavailable => null,
        _ => throw new InvalidOperationException("a code host result that is not one of the three"),
    };

    /// <summary>
    /// The project's items replaced by <paramref name="items"/>, its cards merged with
    /// <paramref name="cards"/> by <see cref="CardStore.Store"/>, and the read recorded as a
    /// success, in one transaction through the door: an item still there is updated in place and
    /// keeps its id, one gone is deleted. The project's row is locked for share first, so a disconnect waits
    /// for the transaction, and a project disconnected before it stores nothing.
    /// </summary>
    /// <returns>Whether the project was still there.</returns>
    private async Task<bool> Store(Guid projectId, Guid userId, IReadOnlyList<RunnerItem> items, IReadOnlyList<PickedCard> cards, DateTimeOffset now, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
        {
            if (await transaction.Connection!.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM projects WHERE id = @projectId FOR SHARE", new { projectId }, transaction, cancellationToken: cancellationToken)) is null)
            {
                return false;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM waiting_items WHERE project_id = @projectId AND NOT (number = ANY (@numbers))",
                new { projectId, numbers = items.Select(item => item.Number).ToArray() },
                transaction,
                cancellationToken: cancellationToken));
            foreach (var item in items)
            {
                await Upsert(transaction, projectId, userId, item, now, cancellationToken);
            }

            await CardStore.Store(transaction, projectId, userId, cards, now, cancellationToken);

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_reads (project_id, user_id, last_attempt_at, last_success_at, last_error)
                VALUES (@projectId, @userId, @now, @now, NULL)
                ON CONFLICT (project_id) DO UPDATE
                SET last_attempt_at = EXCLUDED.last_attempt_at, last_success_at = EXCLUDED.last_success_at, last_error = NULL
                """,
                new { projectId, userId, now },
                transaction,
                cancellationToken: cancellationToken));
            return true;
        }, cancellationToken);

    private static Task<int> Upsert(NpgsqlTransaction transaction, Guid projectId, Guid userId, RunnerItem item, DateTimeOffset now, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO waiting_items (id, user_id, project_id, number, kind, sent_back, title, web_url, slice_number, head_commit,
                                       can_merge, summary, reason, decisions, other_decisions, reviewer_view, screenshots, read_at)
            VALUES (@id, @userId, @projectId, @number, @kind, @sentBack, @title, @webUrl, @slice, @headCommit,
                    @canMerge, @summary, @reason, @decisions::jsonb, @otherDecisions, @reviewerView::jsonb, @screenshots::jsonb, @now)
            ON CONFLICT (project_id, number) DO UPDATE
            SET kind = EXCLUDED.kind, sent_back = EXCLUDED.sent_back, title = EXCLUDED.title, web_url = EXCLUDED.web_url,
                slice_number = EXCLUDED.slice_number, head_commit = EXCLUDED.head_commit, can_merge = EXCLUDED.can_merge,
                summary = EXCLUDED.summary, reason = EXCLUDED.reason, decisions = EXCLUDED.decisions,
                other_decisions = EXCLUDED.other_decisions, reviewer_view = EXCLUDED.reviewer_view,
                screenshots = EXCLUDED.screenshots, read_at = EXCLUDED.read_at
            """,
            new
            {
                id = Guid.CreateVersion7(now),
                userId,
                projectId,
                number = item.Number,
                kind = item.Kind == RunnerItemKind.Waiting ? "waiting" : "stopped",
                sentBack = item.SentBack,
                title = item.Title,
                webUrl = item.Url.ToString(),
                slice = item.Slice,
                headCommit = item.HeadCommit,
                canMerge = item.CanMerge,
                summary = item.Summary,
                reason = item.Reason,
                decisions = JsonSerializer.Serialize(item.OwnerDecisions, Json),
                otherDecisions = item.OtherDecisions,
                reviewerView = JsonSerializer.Serialize(item.ReviewerView, Json),
                screenshots = JsonSerializer.Serialize(item.Screenshots, Json),
                now,
            },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>The read recorded as failed with <paramref name="failure"/>, its items and its last
    /// success left as they were; nothing when the project is gone.</summary>
    private Task<int> RecordFailure(Guid projectId, Guid userId, string failure, DateTimeOffset now, CancellationToken cancellationToken) =>
        database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_reads (project_id, user_id, last_attempt_at, last_success_at, last_error)
            SELECT id, user_id, @now, NULL, @failure FROM projects WHERE id = @projectId
            ON CONFLICT (project_id) DO UPDATE SET last_attempt_at = EXCLUDED.last_attempt_at, last_error = EXCLUDED.last_error
            """,
            new { projectId, failure, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading project {Project} failed; its items stay as they were")]
    private static partial void LogReadFailed(ILogger logger, Guid project, Exception failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing what project {Project} read failed; its items stay as they were")]
    private static partial void LogStoreFailed(ILogger logger, Guid project, Exception failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the recipe of project {Project} failed; its version stays as it was")]
    private static partial void LogRecipeFailed(ILogger logger, Guid project, Exception failed);

    /// <summary>
    /// Records the version of the project's recipe as its default branch has it now, so a worker
    /// built from an older one is told apart; null when the branch has none. When the code host
    /// does not answer, or reading fails, the version stays as it was, and the project's own read
    /// is not failed for it: the recipe is the workers' business, not the waiting list's.
    /// </summary>
    private async Task ReadRecipe(ProjectTarget target, Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
            string? version;
            switch (await host.DefaultBranchFile(userId, repository, Recipes.RecipeEndpoints.RecipePath, cancellationToken))
            {
                case CodeHostResult<FileAtCommit>.Found { Value: var file }:
                    version = file.Commit;
                    break;
                case CodeHostResult<FileAtCommit>.NotFound:
                    version = null;
                    break;
                default:
                    return;
            }

            await WorkerStore.RecipeRead(database, userId, projectId, version, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRecipeFailed(log, projectId, error);
        }
    }

    private sealed record ProjectTarget(
        long Installation,
        long RepositoryId,
        string Owner,
        string Name,
        string Bot,
        string? OwnerLogin,
        string PickupLabel,
        string HoldLabel,
        string AttentionLabel,
        string WaitsForOwner);
}
using System.Text.Json;

using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Features;
using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Projects;
using DevFlow.Runner;
using DevFlow.Watching;
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
/// <see cref="ICodeHost"/>, so they work whether or not the user's own link does. Before all of it,
/// the project's default branch is watched, see <see cref="BranchWatch"/>. See "Reading and
/// the snapshot" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class ProjectReader(UserDb database, ICodeHost host, TimeProvider time, ILogger<ProjectReader> log)
{
    /// <summary>The project's default branch, watched first in every read, whatever the rest of the
    /// read does.</summary>
    private readonly BranchWatch _watch = new(database, host, time, log);

    /// <summary>Why a read failed, as stored and shown: the code host did not answer.</summary>
    public const string HostUnavailable = "GitHub did not answer";

    /// <summary>The code host does not show the repository, removed or no longer reached by the
    /// app.</summary>
    public const string RepositoryNotFound = "GitHub does not show this repository";

    /// <summary>Anything else, a bug among it; never the exception's own text, which may carry
    /// the code host's.</summary>
    public const string ReadFailed = "Reading failed";

    /// <summary>What a read that did not get as far as picking has picked.</summary>
    private static readonly PickedProject NothingPicked = new([], []);

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

        // First, and on its own: what landed on the default branch is judged whether or not the
        // rest of the project can be read, and a watch that fails never fails the read.
        await _watch.Watch(projectId, userId, new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name), target.OwnerLogin, cancellationToken);

        // Runs that never returned are found on the project's minute too, whether or not the code
        // host answers, since finding them asks it nothing.
        try
        {
            await RunStore.SweepLost(database, userId, projectId, Now(), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSweepFailed(log, projectId, error);
        }

        string? failure;
        IReadOnlyList<RunnerItem> items = [];
        var cards = NothingPicked;
        IReadOnlyList<FollowedPull> followed = [];
        IReadOnlyList<FollowedFeature> features = [];
        IReadOnlyList<TakenComments> commented = [];
        IReadOnlyList<TakenComments> featureCommented = [];
        IReadOnlyList<OpenDecisions> decisions = [];
        try
        {
            (items, cards, followed, features, commented, featureCommented, failure) = await Fetch(projectId, target, userId, cancellationToken);
            if (failure is null)
            {
                decisions = await ReadDecisions(projectId, target, userId, cancellationToken);
            }
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
                if (!await Store(projectId, userId, items, cards, followed, features, commented, featureCommented, decisions, now, cancellationToken))
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
    /// null when he has none, the labels its cards are picked by and its other trusted accounts.
    /// Null when the user has no such project.</summary>
    private async Task<ProjectTarget?> Find(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectTarget>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                   p.bot_login AS Bot, a.login AS OwnerLogin,
                   s.pickup_label AS PickupLabel, s.hold_label AS HoldLabel, s.attention_label AS AttentionLabel,
                   s.waits_for_owner AS WaitsForOwner, s.trusted_logins AS TrustedLogins
            FROM projects p
            JOIN project_settings s ON s.project_id = p.id
            LEFT JOIN github_accounts a ON a.user_id = p.user_id
            WHERE p.id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The project's items, its cards, where its waiting cards' pull requests are and its
    /// features' that left the open ones, and the owner's new comments on its cards' and its
    /// features' open pull requests, as the code host answers now, or why it could not be
    /// read.</summary>
    private async Task<(IReadOnlyList<RunnerItem> Items, PickedProject Cards, IReadOnlyList<FollowedPull> Followed, IReadOnlyList<FollowedFeature> Features, IReadOnlyList<TakenComments> Commented, IReadOnlyList<TakenComments> FeatureCommented, string? Failure)> Fetch(Guid projectId, ProjectTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var (items, open, failure) = await FetchItems(target, userId, cancellationToken);
        if (failure is not null)
        {
            return ([], NothingPicked, [], [], [], [], failure);
        }

        var trusted = target.Trusted;
        var settings = new ProjectSettings(target.PickupLabel, target.HoldLabel, target.AttentionLabel, target.WaitsForOwner, trusted.Listed);
        var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
        PickedProject cards;
        switch (await CardPicker.Pick(host, userId, repository, settings, trusted, cancellationToken))
        {
            case CodeHostResult<PickedProject>.Found { Value: var found }:
                cards = found;
                break;
            case CodeHostResult<PickedProject>.NotFound:
                return ([], NothingPicked, [], [], [], [], RepositoryNotFound);
            case CodeHostResult<PickedProject>.Unavailable:
                return ([], NothingPicked, [], [], [], [], HostUnavailable);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        var (followed, followFailure) = await Follow(projectId, userId, repository, open, cancellationToken);
        if (followFailure is not null)
        {
            return ([], NothingPicked, [], [], [], [], followFailure);
        }

        var (features, followFeatureFailure) = await FollowFeatures(projectId, userId, repository, open, cancellationToken);
        if (followFeatureFailure is not null)
        {
            return ([], NothingPicked, [], [], [], [], followFeatureFailure);
        }

        var (commented, commentFailure) = await ReadComments(projectId, userId, repository, open, target.OwnerLogin, cancellationToken);
        if (commentFailure is not null)
        {
            return ([], NothingPicked, [], [], [], [], commentFailure);
        }

        var (featureCommented, featureFailure) = await ReadFeatureComments(projectId, userId, repository, open, target.OwnerLogin, cancellationToken);
        return featureFailure is null ? (items, cards, followed, features, commented, featureCommented, null) : ([], NothingPicked, [], [], [], [], featureFailure);
    }

    /// <summary>
    /// Where the pull request of each feature not merged or closed yet is now, as
    /// <see cref="Follow"/> does for cards: one still among the open pull requests costs nothing
    /// more, and only one that has left the list is read on its own, to tell merged from closed.
    /// One not found is left as it is. See "Merging a feature" in devflow/README.md.
    /// </summary>
    private async Task<(IReadOnlyList<FollowedFeature> Followed, string? Failure)> FollowFeatures(Guid projectId, Guid userId, Repository repository, IReadOnlyList<PullRequest> open, CancellationToken cancellationToken)
    {
        var stillOpen = open.Select(pull => pull.Number).ToHashSet();
        var followed = new List<FollowedFeature>();
        foreach (var (feature, number) in (await FeaturePulls.Followed(database, userId, projectId, cancellationToken)).Where(pair => !stillOpen.Contains(pair.PullRequest)))
        {
            switch (await host.PullRequest(userId, repository, number, cancellationToken))
            {
                case CodeHostResult<PullRequest>.Found { Value: { State: not PullRequestState.Open } pull }:
                    followed.Add(new FollowedFeature(feature, number, pull.State == PullRequestState.Merged));
                    break;
                case CodeHostResult<PullRequest>.Found or CodeHostResult<PullRequest>.NotFound:
                    break;
                case CodeHostResult<PullRequest>.Unavailable:
                    return ([], HostUnavailable);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        return (followed, null);
    }

    /// <summary>
    /// The owner's new comments on each feature's pull request that is still open, for the features
    /// whose pull request is read for them, by <see cref="FeatureComments"/>'s rule; nothing without
    /// his linked login. Every other comment is passed over unread. See "Fixing a feature" in
    /// devflow/README.md.
    /// </summary>
    private async Task<(IReadOnlyList<TakenComments> Commented, string? Failure)> ReadFeatureComments(Guid projectId, Guid userId, Repository repository, IReadOnlyList<PullRequest> open, string? owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return ([], null);
        }

        var stillOpen = open.Select(pull => pull.Number).ToHashSet();
        var commented = new List<TakenComments>();
        foreach (var feature in await FeatureComments.Features(database, userId, projectId, cancellationToken))
        {
            if (!stillOpen.Contains(feature.PullRequest))
            {
                continue;
            }

            if (Listed(await host.Comments(userId, repository, feature.PullRequest, cancellationToken)) is not { } listed)
            {
                return ([], HostUnavailable);
            }

            if (FeatureComments.New(feature, listed, owner) is { } taken)
            {
                commented.Add(taken);
            }
        }

        return (commented, null);
    }

    /// <summary>
    /// The owner's new comments on each card's pull request that is still open, for the cards whose
    /// pull request is read for them, by <see cref="OwnerComments"/>'s rule; nothing without his
    /// linked login. Every other comment is passed over unread. See "Fixing" in devflow/README.md.
    /// </summary>
    private async Task<(IReadOnlyList<TakenComments> Commented, string? Failure)> ReadComments(Guid projectId, Guid userId, Repository repository, IReadOnlyList<PullRequest> open, string? owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(owner))
        {
            return ([], null);
        }

        var stillOpen = open.Select(pull => pull.Number).ToHashSet();
        var commented = new List<TakenComments>();
        foreach (var card in await OwnerComments.Cards(database, userId, projectId, cancellationToken))
        {
            var landed = card.LandedAt is not null;
            int? openPull = !landed && card.PullRequest is { } pull && stillOpen.Contains(pull) ? pull : null;
            var onIssue = card.StoppedAt is not null || landed;
            if (openPull is null && !onIssue)
            {
                continue;
            }

            // A stopped card runs again on his comment on its issue or its open pull request, and a
            // card landed in an open feature's branch is sent back on its issue, open or closed; any
            // other is sent back on its open pull request alone.
            var comments = new List<Comment>();
            foreach (var number in (int?[])[openPull, onIssue ? card.Number : null])
            {
                if (number is not { } conversation)
                {
                    continue;
                }

                if (Listed(await host.Comments(userId, repository, conversation, cancellationToken)) is not { } listed)
                {
                    return ([], HostUnavailable);
                }

                comments.AddRange(listed);
            }

            if (OwnerComments.New(card, comments, owner, openPull) is { } taken)
            {
                commented.Add(taken);
            }
        }

        return (commented, null);
    }

    /// <summary>
    /// Where the pull request of each card that waits for the owner or is under review is now: one
    /// still among the open pull requests just listed costs nothing more, and only one that has left the list is
    /// read on its own, to tell merged from closed. One not found is left as it is. See "The pull
    /// request Dev flow opens" in devflow/README.md.
    /// </summary>
    private async Task<(IReadOnlyList<FollowedPull> Followed, string? Failure)> Follow(Guid projectId, Guid userId, Repository repository, IReadOnlyList<PullRequest> open, CancellationToken cancellationToken)
    {
        var waiting = await CardStore.FollowedPulls(database, userId, projectId, cancellationToken);
        var stillOpen = open.Select(pull => pull.Number).ToHashSet();
        var followed = new List<FollowedPull>();
        foreach (var (card, number) in waiting.Where(pair => !stillOpen.Contains(pair.PullRequest)))
        {
            switch (await host.PullRequest(userId, repository, number, cancellationToken))
            {
                case CodeHostResult<PullRequest>.Found { Value: { State: not PullRequestState.Open } pull }:
                    followed.Add(new FollowedPull(card, number, pull.State == PullRequestState.Merged));
                    break;
                case CodeHostResult<PullRequest>.Found or CodeHostResult<PullRequest>.NotFound:
                    break;
                case CodeHostResult<PullRequest>.Unavailable:
                    return ([], HostUnavailable);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        return (followed, null);
    }

    /// <summary>The project's waiting items, and every open pull request they were read from, as
    /// the code host answers now, or why they could not be read.</summary>
    private async Task<(IReadOnlyList<RunnerItem> Items, IReadOnlyList<PullRequest> Open, string? Failure)> FetchItems(ProjectTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
        IReadOnlyList<PullRequest> open;
        switch (await host.OpenPullRequests(userId, repository, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var found }:
                open = found;
                break;
            case CodeHostResult<IReadOnlyList<PullRequest>>.NotFound:
                return ([], [], RepositoryNotFound);
            case CodeHostResult<IReadOnlyList<PullRequest>>.Unavailable:
                return ([], [], HostUnavailable);
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
                return ([], [], HostUnavailable);
            }

            if (RunnerReader.Read(new RunnerInput(repository, pull, comments, statuses, checkRuns, sliceComments, target.Bot, target.OwnerLogin)) is { } item)
            {
                items.Add(item);
            }
        }

        return (items, open, null);
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
    /// <paramref name="cards"/>, features among them, by <see cref="CardStore.Store"/>, each waiting card whose pull
    /// request merged or closed moved on by <see cref="CardStore.Follow"/>, the owner's new comments
    /// taken into their cards by <see cref="OwnerComments.Take"/> and into their features by
    /// <see cref="FeatureComments.Take"/>, each feature whose pull request left the open ones moved
    /// on by <see cref="FeaturePulls.Follow"/>, each feature moved to its end, or
    /// back from it, by <see cref="FeatureEnd.Advance"/>, and the read recorded as a success, in one
    /// transaction through the door: an item still there is updated in place and
    /// keeps its id, one gone is deleted. The project's row is locked for share first, so a disconnect waits
    /// for the transaction, and a project disconnected before it stores nothing.
    /// </summary>
    /// <returns>Whether the project was still there.</returns>
    private async Task<bool> Store(Guid projectId, Guid userId, IReadOnlyList<RunnerItem> items, PickedProject cards, IReadOnlyList<FollowedPull> followed, IReadOnlyList<FollowedFeature> features, IReadOnlyList<TakenComments> commented, IReadOnlyList<TakenComments> featureCommented, IReadOnlyList<OpenDecisions> decisions, DateTimeOffset now, CancellationToken cancellationToken) =>
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
            foreach (var pull in followed)
            {
                await CardStore.Follow(transaction, projectId, pull, now, cancellationToken);
            }

            foreach (var comments in commented)
            {
                await OwnerComments.Take(transaction, projectId, comments, now, cancellationToken);
            }

            foreach (var comments in featureCommented)
            {
                await FeatureComments.Take(transaction, projectId, comments, now, cancellationToken);
            }

            // A feature whose pull request merged is merged, before its end is decided, which
            // leaves it alone from then on.
            foreach (var pull in features)
            {
                await FeaturePulls.Follow(transaction, projectId, pull, now, cancellationToken);
            }

            // A feature whose cards are now all done moves to its end, and one at its end whose
            // default branch moved is brought in again, from what was just stored.
            await FeatureEnd.Advance(transaction, projectId, now, cancellationToken);

            foreach (var (label, open) in decisions)
            {
                await DecisionStore.Close(transaction, projectId, label, open, now, cancellationToken);
            }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finding the lost runs of project {Project} failed; they are looked for again on its next read")]
    private static partial void LogSweepFailed(ILogger logger, Guid project, Exception failed);

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

    /// <summary>
    /// For each attention label the project's open decision issues were opened with, the numbers
    /// the code host lists open with it, read as the app; none when no decision issue is open, so a
    /// read costs nothing more. A label the code host did not answer for is left out, so its issues
    /// stay listed until a read that does: whether an issue was closed is not worth failing the rest
    /// of the read for.
    /// </summary>
    private async Task<IReadOnlyList<OpenDecisions>> ReadDecisions(Guid projectId, ProjectTarget target, Guid userId, CancellationToken cancellationToken)
    {
        var repository = new Repository(target.Installation, target.RepositoryId, target.Owner, target.Name);
        var read = new List<OpenDecisions>();
        foreach (var label in await DecisionStore.OpenLabels(database, userId, projectId, cancellationToken))
        {
            if (await host.LabelledIssues(userId, repository, label, cancellationToken) is CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var open })
            {
                read.Add(new OpenDecisions(label, [.. open.Where(issue => !issue.IsPullRequest).Select(issue => issue.Number)]));
            }
        }

        return read;
    }

    /// <summary>The issues the code host lists open with an attention label.</summary>
    private sealed record OpenDecisions(string Label, IReadOnlyList<int> Open);

    /// <summary>A project as reading needs it, set by property, since Dapper maps an array column
    /// only to a property.</summary>
    private sealed class ProjectTarget
    {
        public long Installation { get; init; }

        public long RepositoryId { get; init; }

        public string Owner { get; init; } = "";

        public string Name { get; init; } = "";

        public string Bot { get; init; } = "";

        public string? OwnerLogin { get; init; }

        public string PickupLabel { get; init; } = "";

        public string HoldLabel { get; init; } = "";

        public string AttentionLabel { get; init; } = "";

        public string WaitsForOwner { get; init; } = "";

        public string[] TrustedLogins { get; init; } = [];

        /// <summary>The project's trusted accounts, the owner's linked login among them.</summary>
        public TrustedAccounts Trusted => TrustedAccounts.Of(OwnerLogin, TrustedLogins);
    }
}
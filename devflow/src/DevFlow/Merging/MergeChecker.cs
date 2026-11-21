using Dapper;

using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Projects;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Merging;

/// <summary>
/// The merge check's caller: for each card of a project that waits for the owner and is due for the
/// check, it reads what <see cref="MergeCheck.Decide"/> decides on, fresh, Dev flow's own records
/// as the project's user and the code host as the app, calls it, and merges through
/// <see cref="AppMerges"/> or records why not. Run right after a review approves a card, through
/// <see cref="MergeRequests"/>, and on each project's minute. Nothing is held while the code host
/// answers the reads; the merge itself is made under the project's lock, which re-reads what
/// could have changed since. A card whose reason may clear by itself stays due and is read again on
/// the next minute; any other is the owner's. See "The merge check" in devflow/README.md.
/// </summary>
public sealed partial class MergeChecker(UserDb database, ICodeHost host, AppMerges merges, TimeProvider time, ILogger<MergeChecker> log)
{
    /// <summary>
    /// Checks every card of the project that is due, as the project's user,
    /// <paramref name="userId"/>, one after another. Never throws for what the code host answers or
    /// for one card's failure, which is logged and tried again on the next call; only cancellation
    /// and a database that cannot be reached escape.
    /// </summary>
    public async Task CheckPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (project, due) = await Due(projectId, userId, cancellationToken);
        if (project is null || due.Count == 0)
        {
            return;
        }

        var repository = new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name);
        var hold = WatchStore.Read(project.Hold);
        var setting = project.Setting;
        // The files are read only when a ticked condition needs them.
        var readsFiles = setting is { WaitsForOwner: "sometimes", Conditions: { WhenPaths: true, Paths.Count: > 0 } };
        // Read once for the project's cards, and only when anything is read at all.
        CodeHostResult<IReadOnlyList<Issue>>? labelled = null;
        foreach (var card in due)
        {
            try
            {
                MergeFacts facts;
                if (hold != ProjectHold.None)
                {
                    // A project that holds merges nothing, so nothing is asked of the code host.
                    facts = Facts(project, card, hold, Unread<PullRequest>(), Unread<IReadOnlyList<CommitStatus>>(), Unread<IReadOnlyList<CheckRun>>(), Unread<IReadOnlyList<Issue>>(), Unread<IReadOnlyList<IssueEdit>>(), Unread<IReadOnlyList<LabelEvent>>(), Unread<IReadOnlyList<SubIssue>>(), Unread<IReadOnlyList<IssueEdit>>(), Unread<IReadOnlyList<LabelEvent>>(), Unread<IReadOnlyList<ChangedFile>>());
                }
                else
                {
                    var pull = await host.PullRequest(userId, repository, card.PullRequest, cancellationToken);
                    var head = pull is CodeHostResult<PullRequest>.Found { Value: var found } ? found.HeadCommit : null;
                    var statuses = head is null ? Unread<IReadOnlyList<CommitStatus>>() : await host.Statuses(userId, repository, head, cancellationToken);
                    var checkRuns = head is null ? Unread<IReadOnlyList<CheckRun>>() : await host.CheckRuns(userId, repository, head, cancellationToken);
                    labelled ??= await host.LabelledIssues(userId, repository, project.PickupLabel, cancellationToken);
                    var edits = await host.Edits(userId, repository, card.Number, cancellationToken);
                    var labelEvents = await host.LabelEvents(userId, repository, card.Number, cancellationToken);
                    // A card of a feature is read as its parent lists it, and the parent as a card.
                    var subIssues = card.FeatureNumber is { } feature ? await host.SubIssues(userId, repository, feature, cancellationToken) : Unread<IReadOnlyList<SubIssue>>();
                    var featureEdits = card.FeatureNumber is { } parent ? await host.Edits(userId, repository, parent, cancellationToken) : Unread<IReadOnlyList<IssueEdit>>();
                    var featureLabelEvents = card.FeatureNumber is { } labelledParent ? await host.LabelEvents(userId, repository, labelledParent, cancellationToken) : Unread<IReadOnlyList<LabelEvent>>();
                    var files = readsFiles && head is not null ? await host.PullRequestFiles(userId, repository, card.PullRequest, cancellationToken) : Unread<IReadOnlyList<ChangedFile>>();
                    facts = Facts(project, card, hold, pull, statuses, checkRuns, labelled, edits, labelEvents, subIssues, featureEdits, featureLabelEvents, files);
                }

                switch (MergeCheck.Decide(facts))
                {
                    case MergeDecision.Wait { Reason: var reason, TryAgain: var again }:
                        await Record(projectId, userId, card, reason, again, cancellationToken);
                        break;
                    case MergeDecision.Merge { Commit: var commit }:
                        await Merge(projectId, userId, card, commit, setting, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("a merge decision that is not one of the two");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, card.Number, error);
            }
        }
    }

    /// <summary>Merges the card as the app, while the project's setting is still
    /// <paramref name="setting"/>, the one the check decided on, and records what came of it when it
    /// did not merge.</summary>
    private async Task Merge(Guid projectId, Guid userId, DueCard card, string commit, WaitSetting setting, CancellationToken cancellationToken)
    {
        var (reason, again) = await merges.MergeCard(userId, projectId, card.Number, card.PullRequest, commit, setting, cancellationToken) switch
        {
            AppMergeOutcome.Done { Result: MergeResult.Merged { Commit: var merged } } => Merged(projectId, card, merged),
            AppMergeOutcome.Done { Result: MergeResult.HeadMoved } => (MergeCheck.HeadMoved, true),
            AppMergeOutcome.Done { Result: MergeResult.NotMergeable } => (MergeCheck.NotMergeable, false),
            AppMergeOutcome.Done { Result: MergeResult.Refused } => (MergeCheck.MergeRefused, false),
            AppMergeOutcome.Done { Result: MergeResult.Unavailable } => (MergeCheck.MergeUnanswered, true),
            AppMergeOutcome.ProjectStopped => (MergeCheck.Stopped, false),
            AppMergeOutcome.ProjectUnwatched => (MergeCheck.Unwatched, true),
            AppMergeOutcome.FeatureUnwatched => (MergeCheck.FeatureUnwatched, true),
            AppMergeOutcome.NoKey => (MergeCheck.NoKey, true),
            AppMergeOutcome.CardMoved or AppMergeOutcome.NoSuchProject => ((string?)null, false),
            _ => throw new InvalidOperationException("a merge outcome that is not one of the known"),
        };
        if (reason is not null)
        {
            await Record(projectId, userId, card, reason, again, cancellationToken);
        }
    }

    /// <summary>A merge made: the card is already done, in the merge's transaction.</summary>
    private (string?, bool) Merged(Guid projectId, DueCard card, string? commit)
    {
        if (commit is null)
        {
            LogMergedUnrecorded(log, projectId, card.Number, card.PullRequest);
        }
        else
        {
            LogMerged(log, projectId, card.Number, card.PullRequest, commit);
        }

        return (null, false);
    }

    /// <summary>
    /// The card's reason, and whether it stays due, only while it is still due with the same pull
    /// request, so a card that moved on meanwhile is left as it moved; <c>changed_at</c> moves only
    /// when the reason does, so the list keeps its order while CI runs.
    /// </summary>
    private async Task Record(Guid projectId, Guid userId, DueCard card, string reason, bool again, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET reason = @reason, merge_check_due = @again,
                             changed_at = CASE WHEN reason IS DISTINCT FROM @reason THEN @now ELSE changed_at END
            WHERE project_id = @projectId AND number = @number AND state = 'waiting_for_owner' AND merge_check_due
              AND pull_request_number = @pull
            """,
            new { projectId, number = card.Number, pull = card.PullRequest, reason, again, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (!again)
        {
            LogWaits(log, projectId, card.Number, reason);
        }
    }

    private MergeFacts Facts(
        ProjectRow project,
        DueCard card,
        ProjectHold hold,
        CodeHostResult<PullRequest> pull,
        CodeHostResult<IReadOnlyList<CommitStatus>> statuses,
        CodeHostResult<IReadOnlyList<CheckRun>> checkRuns,
        CodeHostResult<IReadOnlyList<Issue>> labelled,
        CodeHostResult<IReadOnlyList<IssueEdit>> edits,
        CodeHostResult<IReadOnlyList<LabelEvent>> labelEvents,
        CodeHostResult<IReadOnlyList<SubIssue>> subIssues,
        CodeHostResult<IReadOnlyList<IssueEdit>> featureEdits,
        CodeHostResult<IReadOnlyList<LabelEvent>> featureLabelEvents,
        CodeHostResult<IReadOnlyList<ChangedFile>> files) => new(
        time.GetUtcNow(),
        hold,
        project.WaitsForOwner,
        project.PickupLabel,
        project.HoldLabel,
        TrustedAccounts.Of(project.OwnerLogin, project.TrustedLogins),
        card.Number,
        new CardRecord(card.BodySha256, card.Title, card.NewestEdit),
        card.HeadCommit is { } commit && card.PushedAt is { } pushed && card.Base is { } ran ? new CheckedHead(commit, card.CheckExitCode, Utc(pushed), ran) : null,
        card.Verdict is { } verdict && card.ReviewedCommit is { } reviewed ? new LatestReview(verdict, reviewed) : null,
        pull,
        statuses,
        checkRuns,
        labelled,
        edits,
        labelEvents,
        project.Setting.Conditions,
        card.DecisionForOwner,
        files,
        card.FeatureNumber is { } feature
            ? new FeatureFacts(
                feature,
                card.FeatureUnwatched,
                card.FeatureBodySha256 is { } sha && card.FeatureTitle is { } title ? new CardRecord(sha, title, card.FeatureNewestEdit) : null,
                project.Owner,
                project.Name,
                subIssues,
                featureEdits,
                featureLabelEvents)
            : null);

    /// <summary>A read that was not made, which the check takes as one that could not be.</summary>
    private static CodeHostResult<T> Unread<T>() => new CodeHostResult<T>.Unavailable(RetryAt: null);

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    /// <summary>The project as the check reads it, with whether it holds, and its cards due for the
    /// check, each with the record of what was picked, what its newest build or fix that succeeded
    /// left and the base it ran on, its latest review, and for a card of a feature the parent's
    /// number and record and whether the feature's branch was read lately; read in one transaction
    /// as the user. Null for a project the user does not have.</summary>
    private async Task<(ProjectRow? Project, IReadOnlyList<DueCard> Due)> Due(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(ProjectRow?, IReadOnlyList<DueCard>)>(userId, async transaction =>
        {
            var due = (await transaction.Connection!.QueryAsync<DueCard>(new CommandDefinition(
                $"""
                SELECT cards.number AS Number, cards.pull_request_number AS PullRequest, cards.picked_body_sha256 AS BodySha256,
                       cards.picked_title AS Title, cards.picked_edit AS NewestEdit,
                       h.head_commit AS HeadCommit, h.check_exit_code AS CheckExitCode, h.finished_at AS PushedAt, h.base AS Base,
                       v.verdict AS Verdict, v.reviewed_commit AS ReviewedCommit,
                       f.number AS FeatureNumber, f.picked_body_sha256 AS FeatureBodySha256, f.picked_title AS FeatureTitle,
                       f.picked_edit AS FeatureNewestEdit, NOT {WatchStore.FeatureWatchedSql} AS FeatureUnwatched,
                       EXISTS (SELECT 1 {DecisionStore.MarkedFrom("cards.project_id", "cards.number")}) AS DecisionForOwner
                FROM cards
                LEFT JOIN features f ON f.id = cards.feature_id
                LEFT JOIN LATERAL (SELECT head_commit, check_exit_code, finished_at, base FROM runs
                                   WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number
                                     AND runs.kind IN ('build', 'fix') AND runs.result = 'succeeded'
                                   ORDER BY started_at DESC, id DESC LIMIT 1) h ON true
                LEFT JOIN LATERAL (SELECT verdict, reviewed_commit FROM reviews
                                   WHERE reviews.project_id = cards.project_id AND reviews.card_number = cards.number
                                   ORDER BY reviewed_at DESC, run_id DESC LIMIT 1) v ON true
                WHERE cards.project_id = @projectId AND cards.state = 'waiting_for_owner' AND cards.merge_check_due
                  AND cards.pull_request_number IS NOT NULL AND cards.picked_body_sha256 IS NOT NULL
                ORDER BY cards.number
                """,
                new { projectId, watchedSince = WatchStore.WatchedSince(time.GetUtcNow()) },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            if (due.Count == 0)
            {
                return (null, []);
            }

            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                $"""
                SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                       a.login AS OwnerLogin, s.pickup_label AS PickupLabel, s.hold_label AS HoldLabel,
                       s.waits_for_owner AS WaitsForOwner, s.trusted_logins AS TrustedLogins, {WatchStore.HoldSql} AS Hold,
                       s.review_label AS ReviewLabel, s.wait_when_marked AS WaitWhenMarked, s.wait_when_paths AS WaitWhenPaths,
                       s.wait_when_decision AS WaitWhenDecision, s.wait_paths AS WaitPaths
                FROM projects p
                JOIN project_settings s ON s.project_id = p.id
                LEFT JOIN github_accounts a ON a.user_id = p.user_id
                WHERE p.id = @projectId
                """,
                new { projectId, watchedSince = time.GetUtcNow() - BranchWatch.UnwatchedAfter },
                transaction,
                cancellationToken: cancellationToken));
            return (project, due);
        }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Merged pull request {Pull} of card {Card} of project {Project} as the app, merge commit {Commit}")]
    private static partial void LogMerged(ILogger logger, Guid project, int card, int pull, string commit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Merged pull request {Pull} of card {Card} of project {Project} as the app, and the code host named no merge commit; the watch stops the project when it finds the merge")]
    private static partial void LogMergedUnrecorded(ILogger logger, Guid project, int card, int pull);

    [LoggerMessage(Level = LogLevel.Information, Message = "Card {Card} of project {Project} is not merged and waits for its owner: {Reason}")]
    private static partial void LogWaits(ILogger logger, Guid project, int card, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The merge check of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    /// <summary>The project as the check reads it, set by property, since Dapper maps an array
    /// column only to a property.</summary>
    private sealed class ProjectRow
    {
        public long Installation { get; init; }

        public long RepositoryId { get; init; }

        public string Owner { get; init; } = "";

        public string Name { get; init; } = "";

        public string? OwnerLogin { get; init; }

        public string PickupLabel { get; init; } = "";

        public string HoldLabel { get; init; } = "";

        public string WaitsForOwner { get; init; } = "";

        public string[] TrustedLogins { get; init; } = [];

        public string? Hold { get; init; }

        public string ReviewLabel { get; init; } = "";

        public bool WaitWhenMarked { get; init; }

        public bool WaitWhenPaths { get; init; }

        public bool WaitWhenDecision { get; init; }

        public string[] WaitPaths { get; init; } = [];

        /// <summary>The setting as the check decides on it, and as the merge reads it again.</summary>
        public WaitSetting Setting => new(WaitsForOwner, new WaitConditions(WaitWhenMarked, WaitWhenPaths, WaitWhenDecision, ReviewLabel, WaitPaths));
    }

    /// <summary>A card due for the check, set by property, as Dapper reads <c>bytea</c>.</summary>
    private sealed class DueCard
    {
        public int Number { get; init; }

        public int PullRequest { get; init; }

        public byte[] BodySha256 { get; init; } = [];

        public string Title { get; init; } = "";

        public string? NewestEdit { get; init; }

        public string? HeadCommit { get; init; }

        public int? CheckExitCode { get; init; }

        public DateTime? PushedAt { get; init; }

        public string? Verdict { get; init; }

        public string? ReviewedCommit { get; init; }

        public string? Base { get; init; }

        public int? FeatureNumber { get; init; }

        public byte[]? FeatureBodySha256 { get; init; }

        public string? FeatureTitle { get; init; }

        public string? FeatureNewestEdit { get; init; }

        public bool FeatureUnwatched { get; init; }

        public bool DecisionForOwner { get; init; }
    }
}
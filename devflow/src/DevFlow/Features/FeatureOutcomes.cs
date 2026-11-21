using System.Globalization;

using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Features;

/// <summary>
/// Tells a feature's cards how the feature ended, as the app with a <see cref="WritePurpose.Post"/>
/// key, once a read found its pull request merged or closed, see <see cref="FeaturePulls"/>. Merged,
/// the parent was closed by its <c>Closes</c> line, and each card still open, a direct sub-issue of
/// the parent in this repository as GitHub lists them now, is closed with
/// <see cref="MergedComment"/>: one the read has stopped keeping since the parent left the list is
/// found that way too. Closed without merging, each card Dev flow kept for the feature whose issue is
/// closed, as landing in the feature's branch closed it, is opened again with
/// <see cref="ClosedComment"/>; a sub-issue closed that Dev flow never built is left alone. Before
/// commenting, the app's own comments on the issue are looked at for the same words, so an answer
/// that was lost never posts it twice, and the feature is recorded told only once every card is,
/// so what the code host did not answer for is tried again on the next minute. Nothing is held
/// while the code host answers. It writes, so it lives outside <c>Reading/</c>, which can never
/// reach a write key. See "Merging a feature" in devflow/README.md.
/// </summary>
public sealed partial class FeatureOutcomes(UserDb database, ICodeHost host, TimeProvider time, ILogger<FeatureOutcomes> log)
{
    /// <summary>What the app says on a card still open when its feature merged.</summary>
    public static string MergedComment(int feature, int pullRequest, string defaultBranch) =>
        string.Create(CultureInfo.InvariantCulture, $"Its feature, #{feature}, was merged into `{defaultBranch}` in #{pullRequest}, so this card is closed.");

    /// <summary>What the app says on each card it opens again when its feature's pull request was
    /// closed without merging.</summary>
    public static string ClosedComment(int pullRequest) =>
        string.Create(CultureInfo.InvariantCulture, $"The feature's pull request #{pullRequest} was closed without merging, so this card is open again.");

    /// <summary>
    /// Tells the cards of every feature of the project that merged or closed and is not told yet,
    /// as the project's user, <paramref name="userId"/>. Never throws for what the code host
    /// answers or for one feature's failure, which is logged and tried again on the next call; only
    /// cancellation and a database that cannot be reached escape.
    /// </summary>
    public async Task TellPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, pending) = await Pending(projectId, userId, cancellationToken);
        if (repository is null || pending.Count == 0)
        {
            return;
        }

        if (await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var app })
        {
            LogNotNow(log, projectId, "who the app is");
            return;
        }

        AppWriteToken? key = null;
        foreach (var feature in pending)
        {
            try
            {
                IReadOnlyList<SubIssue> subIssues;
                switch (await host.SubIssues(userId, repository, feature.Number, cancellationToken))
                {
                    case CodeHostResult<IReadOnlyList<SubIssue>>.Found { Value: var found }:
                        subIssues = found;
                        break;
                    case CodeHostResult<IReadOnlyList<SubIssue>>.NotFound:
                        // The parent is gone, and its cards are nobody's to tell.
                        await Told(userId, feature, cancellationToken);
                        continue;
                    default:
                        LogNotNow(log, projectId, "the feature's sub-issues");
                        continue;
                }

                var merged = feature.State == "merged";
                var due = subIssues
                    .Where(sub => !sub.IsPullRequest && IsHere(repository, sub))
                    .Where(sub => merged ? sub.Open : !sub.Open && feature.Cards.Contains(sub.Number))
                    .Select(sub => sub.Number)
                    .Distinct()
                    .Order()
                    .ToList();
                if (due.Count > 0)
                {
                    key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                    if (key is null)
                    {
                        LogNotNow(log, projectId, "a key to post with");
                        return;
                    }
                }

                var text = merged ? MergedComment(feature.Number, feature.PullRequest, feature.DefaultBranch) : ClosedComment(feature.PullRequest);
                var all = true;
                foreach (var card in due)
                {
                    all &= await Tell(projectId, userId, repository, app.Name, key!, card, text, open: !merged, cancellationToken);
                }

                if (all)
                {
                    await Told(userId, feature, cancellationToken);
                    LogTold(log, projectId, feature.Number, feature.State, due.Count);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, feature.Number, error);
            }
        }
    }

    /// <summary>The comment once, then the issue opened or closed; whether both are on it.</summary>
    private async Task<bool> Tell(Guid projectId, Guid userId, Repository repository, string app, AppWriteToken key, int card, string text, bool open, CancellationToken cancellationToken)
    {
        switch (await host.Comments(userId, repository, card, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments } when comments.Any(comment =>
                string.Equals(comment.Author, app, StringComparison.OrdinalIgnoreCase) && string.Equals(comment.Body, text, StringComparison.Ordinal)):
                break;
            case CodeHostResult<IReadOnlyList<Comment>>.Unavailable:
                LogNotNow(log, projectId, "the card's comments");
                return false;
            default:
                // None of the app's says it, or the issue has no conversation yet.
                if (!Written(await host.CommentOnIssue(userId, key, repository, card, text, cancellationToken), projectId, card, "the comment"))
                {
                    return false;
                }

                break;
        }

        return Written(await host.SetIssueState(userId, key, repository, card, open, cancellationToken), projectId, card, open ? "opening the issue" : "closing the issue");
    }

    /// <summary>Whether the write was made; anything else is logged, and tried again on a later
    /// minute.</summary>
    private bool Written<T>(WriteResult<T> result, Guid projectId, int card, string what)
    {
        switch (result)
        {
            case WriteResult<T>.Written:
                return true;
            case WriteResult<T>.Refused { Status: var status }:
                LogRefused(log, projectId, card, what, status);
                return false;
            case WriteResult<T>.Unavailable:
                LogNotNow(log, projectId, what);
                return false;
            default:
                throw new InvalidOperationException("a write result that is not one of the three");
        }
    }

    /// <summary>Records the feature's cards told, while it is still as it was read.</summary>
    private async Task Told(Guid userId, Due feature, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE features SET outcome_told_at = @now WHERE id = @id AND state = @state AND outcome_told_at IS NULL",
            new { id = feature.Id, state = feature.State, now = now.AddTicks(-(now.Ticks % 10)) },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The project's repository and its features merged or closed with their cards not
    /// told yet, each with its pull request, the default branch as its watch names it, and the
    /// numbers of the cards Dev flow keeps for it, read as the user. No repository for a project
    /// the user does not have, or when nothing is due.</summary>
    private async Task<(Repository? Repository, IReadOnlyList<Due> Pending)> Pending(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(Repository?, IReadOnlyList<Due>)>(userId, async transaction =>
        {
            var rows = (await transaction.Connection!.QueryAsync<DueRow>(new CommandDefinition(
                """
                SELECT f.id AS Id, f.number AS Number, f.state AS State, f.pull_request_number AS PullRequest, b.branch AS DefaultBranch,
                       coalesce((SELECT array_agg(c.number ORDER BY c.number) FROM cards c WHERE c.project_id = f.project_id AND c.feature_id = f.id), '{}') AS Cards
                FROM features f
                JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id IS NULL
                WHERE f.project_id = @projectId AND f.state IN ('merged', 'closed') AND f.outcome_told_at IS NULL AND f.pull_request_number IS NOT NULL
                ORDER BY f.number
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            if (rows.Count == 0)
            {
                return (null, []);
            }

            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            return (project is null ? null : new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name),
                [.. rows.Select(row => new Due(row.Id, row.Number, row.State, row.PullRequest, row.DefaultBranch, row.Cards.ToHashSet()))]);
        }, cancellationToken);

    private static bool IsHere(Repository repository, SubIssue sub) =>
        string.Equals(sub.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(sub.Name, repository.Name, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Told {Count} cards of feature #{Feature} of project {Project} that it is {State}")]
    private static partial void LogTold(ILogger logger, Guid project, int feature, string state, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its features' cards are told on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused {What} for card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, string what, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telling the cards of feature #{Feature} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int feature, Exception failed);

    /// <summary>A feature whose cards are to be told, as read.</summary>
    private sealed record Due(Guid Id, int Number, string State, int PullRequest, string DefaultBranch, IReadOnlySet<int> Cards);

    /// <summary>A feature as read, set by property: Dapper reads <c>int[]</c> as an array no
    /// constructor parameter matches.</summary>
    private sealed class DueRow
    {
        public Guid Id { get; init; }

        public int Number { get; init; }

        public string State { get; init; } = "";

        public int PullRequest { get; init; }

        public string DefaultBranch { get; init; } = "";

        public int[] Cards { get; init; } = [];
    }

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);
}
using Dapper;

using DevFlow.CodeHost;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Features;

/// <summary>
/// The end of each feature whose cards are all done, as the app, on the project's minute: first
/// <see cref="FeatureEnd.Advance"/>, so an end due since the last read starts now; then, for each
/// feature <c>bringing_in</c>, the default branch brought into its branch with a merge commit, as
/// the app with a <see cref="WritePurpose.Merge"/> key, never a rebase and never a force push,
/// and then the feature's pull request to the default branch, see <see cref="Publish"/>.
/// Bringing in is asked only while the project is not stopped, its default branch's watch and the
/// feature's branch's are current, and the feature still waits for it, all read under the
/// project's lock, which is held while the code host answers: the merge commit it made is written
/// to <c>merges_made</c> with the feature's branch before the lock is let go, so the watch, which
/// takes the same lock before it judges, never sees the merge unrecorded. Should the record fail
/// after the code host merged, or the host not say the commit, the watch finds an unrecorded push by
/// the app and stops the project, erring towards asking the owner, as <see cref="Merging.AppMerges"/>
/// does. A conflict has the feature <c>resolving</c>, for an agent's job, and a refusal leaves it
/// waiting for the owner. It writes, so it lives
/// outside <c>Reading/</c>, which can never reach a write key. See "The end of a feature" in
/// devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class FeatureEnds(UserDb database, ICodeHost host, TimeProvider time, ILogger<FeatureEnds> log)
{
    /// <summary>What the merge commit that brings the default branch in says.</summary>
    public static string Message(string defaultBranch, string branch) => $"Bring {defaultBranch} into {branch}";

    /// <summary>
    /// Ends every feature of the project that is due, as the project's user,
    /// <paramref name="userId"/>. Never throws for what the code host answers or for one feature's
    /// failure, which is logged and tried again on the next call; only cancellation and a database
    /// that cannot be reached escape.
    /// </summary>
    public async Task EndPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, pending) = await Pending(projectId, userId, cancellationToken);
        if (repository is null)
        {
            return;
        }

        foreach (var feature in pending)
        {
            try
            {
                if (await BringIn(projectId, userId, repository, feature, cancellationToken))
                {
                    await Publish(projectId, userId, repository, feature, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, feature.Number, error);
            }
        }
    }

    /// <summary>
    /// Brings the default branch into the feature's branch, under the project's lock, and answers
    /// whether the feature holds it now: brought in, or nothing to bring in. Already brought in at
    /// the default branch's commit, as a minute whose next step failed left it, nothing is asked.
    /// </summary>
    private async Task<bool> BringIn(Guid projectId, Guid userId, Repository repository, Due feature, CancellationToken cancellationToken)
    {
        if (feature.BroughtIn is not null && feature.BroughtIn == feature.DefaultHead)
        {
            return true;
        }

        return await database.InUserTransaction(userId, async transaction =>
        {
            if (!await BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return false;
            }

            var now = Now();
            var hold = WatchStore.Read(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                $"SELECT {WatchStore.HoldSql} FROM projects p WHERE p.id = @projectId",
                new { projectId, watchedSince = WatchStore.WatchedSince(now) },
                transaction,
                cancellationToken: cancellationToken)));
            if (hold != ProjectHold.None)
            {
                LogHeld(log, projectId, feature.Number, hold);
                return false;
            }

            // Read again under the lock: the feature still waits for this, and where the default
            // branch stands now, as its watch named it.
            var current = await transaction.Connection!.QuerySingleOrDefaultAsync<Current>(new CommandDefinition(
                """
                SELECT f.branch AS Branch, b.branch AS DefaultBranch, b.last_commit AS DefaultHead
                FROM features f JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id IS NULL
                WHERE f.id = @id AND f.state = 'bringing_in' AND f.branch IS NOT NULL
                FOR UPDATE OF f
                """,
                new { id = feature.Id },
                transaction,
                cancellationToken: cancellationToken));
            if (current is null)
            {
                return false;
            }

            if (await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                    $"SELECT {WatchStore.FeatureUnwatchedSql}",
                    new { projectId, into = current.Branch, watchedSince = WatchStore.WatchedSince(now) },
                    transaction,
                    cancellationToken: cancellationToken)))
            {
                LogHeld(log, projectId, feature.Number, ProjectHold.Unwatched);
                return false;
            }

            if (await host.ForWrite(userId, repository, WritePurpose.Merge, cancellationToken) is not CodeHostResult<AppWriteToken>.Found { Value: var key })
            {
                LogNotNow(log, projectId, "a key to merge with");
                return false;
            }

            var result = await host.BringIn(userId, key, repository, current.Branch, current.DefaultBranch, Message(current.DefaultBranch, current.Branch), cancellationToken);
            switch (result)
            {
                case BringInResult.Merged { Commit: var commit, BroughtIn: var brought, Parent: var parent }:
                    if (commit is not null)
                    {
                        // With the feature branch's commit it merged into, its first parent, so a
                        // review of that commit carries over to the merge, which only brought the
                        // default branch in; see "Reviewing a feature" in devflow/README.md.
                        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO merges_made (project_id, user_id, branch, commit, number, parent_commit, merged_at)
                            VALUES (@projectId, @userId, @branch, @commit, NULL, @parent, @now)
                            ON CONFLICT (project_id, commit) DO NOTHING
                            """,
                            new { projectId, userId, branch = current.Branch, commit, parent, now },
                            transaction,
                            cancellationToken: cancellationToken));
                    }

                    // The commit it brought in, as the answer named it, or the default branch's as
                    // its watch named it, which the merge holds.
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE features SET brought_in_commit = coalesce(@brought, @head, brought_in_commit), bring_in_failed_at = NULL WHERE id = @id",
                        new { id = feature.Id, brought, head = current.DefaultHead },
                        transaction,
                        cancellationToken: cancellationToken));
                    LogBrought(log, projectId, feature.Number, current.DefaultBranch);
                    return true;
                case BringInResult.NothingToBringIn:
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE features SET brought_in_commit = coalesce(@head, brought_in_commit), bring_in_failed_at = NULL WHERE id = @id",
                        new { id = feature.Id, head = current.DefaultHead },
                        transaction,
                        cancellationToken: cancellationToken));
                    return true;
                case BringInResult.Conflict:
                    // An agent resolves it, in a job of its own handed out next, see JobStore.ClaimConflict;
                    // a run that cannot leaves the feature waiting for the owner.
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE features SET state = 'resolving', reason = @reason, changed_at = @now WHERE id = @id AND state = 'bringing_in'",
                        new { id = feature.Id, now, reason = FeatureEnd.Conflict(current.DefaultBranch) },
                        transaction,
                        cancellationToken: cancellationToken));
                    LogConflict(log, projectId, feature.Number, current.DefaultBranch);
                    return false;
                case BringInResult.Refused { Status: var status }:
                    await Waits(transaction, feature.Id, current.DefaultHead, now, FeatureEnd.Refused(current.DefaultBranch, status), cancellationToken);
                    LogRefused(log, projectId, feature.Number, status);
                    return false;
                case BringInResult.Unavailable:
                    // Whether it was made is not known: tried again on the next minute, which
                    // answers nothing to bring in if it was, and the watch judges what it finds.
                    LogNotNow(log, projectId, "bringing the default branch in");
                    return false;
                default:
                    throw new InvalidOperationException("a bring in result that is not one of the five");
            }
        }, cancellationToken);
    }

    /// <summary>The feature waits for the owner with <paramref name="reason"/>, and bringing in is
    /// not tried again until the default branch moves past the commit it failed at; a refusal's.</summary>
    private static Task<int> Waits(Npgsql.NpgsqlTransaction transaction, Guid id, string? head, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'waiting_for_owner', reason = @reason, bring_in_failed_at = @head, changed_at = @now
            WHERE id = @id AND state = 'bringing_in'
            """,
            new { id, head, now, reason },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// The feature's pull request, once the default branch is in: the app's own open one from the
    /// feature's branch into the default branch is reused, and otherwise, while the feature has
    /// none recorded, one is opened, not a draft, as the app with a
    /// <see cref="WritePurpose.Post"/> key, titled with the parent's title as picked; its body,
    /// <see cref="FeatureText.Body"/> over the feature's cards, is brought in step when it differs.
    /// Then it is recorded and the feature is <c>reviewing</c>, only while it still waits for its end,
    /// for <see cref="FeatureReadiness"/> to decide, once its branch is read, whether its head needs
    /// a review of the whole feature. Nothing is held while the code host answers; a minute
    /// it did not answer for leaves the feature bringing in, already brought in, to be published on
    /// the next. A pull request recorded that is no longer open is never opened again here.
    /// </summary>
    private async Task Publish(Guid projectId, Guid userId, Repository repository, Due feature, CancellationToken cancellationToken)
    {
        if (await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var app }
            || await host.OpenPullRequests(userId, repository, cancellationToken) is not CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var open })
        {
            LogNotNow(log, projectId, "the feature's pull request");
            return;
        }

        var body = FeatureText.Body(feature.Number, await Cards(userId, feature, cancellationToken));
        PullRequest? Theirs(IReadOnlyList<PullRequest> listed) => listed
            .Where(pull => pull.State == PullRequestState.Open
                && (feature.PullRequestNumber is { } recorded
                    ? pull.Number == recorded
                    : string.Equals(pull.HeadBranch, feature.Branch, StringComparison.Ordinal)
                      && string.Equals(pull.BaseBranch, feature.DefaultBranch, StringComparison.Ordinal)
                      && string.Equals(pull.Author, app.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pull => pull.Number)
            .FirstOrDefault();

        AppWriteToken? key = null;
        async Task<AppWriteToken?> Key() =>
            key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;

        var pull = Theirs(open);
        if (pull is null && feature.PullRequestNumber is null)
        {
            if (await Key() is not { } post)
            {
                LogNotNow(log, projectId, "a key to post with");
                return;
            }

            switch (await host.OpenPullRequest(userId, post, repository, feature.Branch, feature.DefaultBranch, feature.Title, body, cancellationToken))
            {
                case WriteResult<PullRequest>.Written { Value: var opened }:
                    pull = opened;
                    LogOpened(log, projectId, feature.Number, opened.Number);
                    break;
                case WriteResult<PullRequest>.Refused { Status: var status }:
                    // GitHub refuses a second one from the same branch into the same base, so one may
                    // have been opened meanwhile, by an opening whose answer was lost.
                    pull = await host.OpenPullRequests(userId, repository, cancellationToken) is CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var again } ? Theirs(again) : null;
                    if (pull is null)
                    {
                        LogOpenRefused(log, projectId, feature.Number, status);
                        return;
                    }

                    break;
                case WriteResult<PullRequest>.Unavailable:
                    LogNotNow(log, projectId, "opening the feature's pull request");
                    return;
                default:
                    throw new InvalidOperationException("a write result that is not one of the three");
            }
        }

        if (pull is not null && !string.Equals(pull.Body, body, StringComparison.Ordinal))
        {
            if (await Key() is not { } post
                || await host.EditPullRequestBody(userId, post, repository, pull.Number, body, cancellationToken) is not WriteResult<PullRequest>.Written)
            {
                LogNotNow(log, projectId, "the feature's pull request's body");
                return;
            }
        }

        var now = Now();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'reviewing', reason = NULL, review_commit = NULL, changed_at = @now,
                                pull_request_number = coalesce(@number, pull_request_number), pull_request_url = coalesce(@url, pull_request_url)
            WHERE id = @id AND state = 'bringing_in'
            """,
            new { id = feature.Id, now, number = pull?.Number, url = pull?.Url.ToString() },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The feature's cards in its order, each with its title as picked and the pull
    /// request it landed in.</summary>
    private async Task<IReadOnlyList<FeatureText.Card>> Cards(Guid userId, Due feature, CancellationToken cancellationToken) =>
        [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<FeatureText.Card>(new CommandDefinition(
            """
            SELECT number AS Number, coalesce(picked_title, title) AS Title, pull_request_number AS PullRequest
            FROM cards WHERE feature_id = @id ORDER BY position
            """,
            new { id = feature.Id },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];

    /// <summary>
    /// The project's repository and its features whose end is due, after moving each as
    /// <see cref="FeatureEnd.Advance"/> says, in one transaction as the user. No repository for a
    /// project the user does not have; nothing is due before the default branch's watch has named
    /// it.
    /// </summary>
    private async Task<(Repository? Repository, IReadOnlyList<Due> Pending)> Pending(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(Repository?, IReadOnlyList<Due>)>(userId, async transaction =>
        {
            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project is null)
            {
                return (null, []);
            }

            await FeatureEnd.Advance(transaction, projectId, Now(), cancellationToken);
            var pending = (await transaction.Connection!.QueryAsync<Due>(new CommandDefinition(
                """
                SELECT f.id AS Id, f.number AS Number, f.branch AS Branch, coalesce(f.picked_title, f.title) AS Title,
                       f.brought_in_commit AS BroughtIn, f.pull_request_number AS PullRequestNumber,
                       b.branch AS DefaultBranch, b.last_commit AS DefaultHead
                FROM features f JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id IS NULL
                WHERE f.project_id = @projectId AND f.state = 'bringing_in' AND f.branch IS NOT NULL
                ORDER BY f.number
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            return (new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name), pending);
        }, cancellationToken);

    /// <summary>The time, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Brought {Default} into the branch of feature #{Feature} of project {Project}")]
    private static partial void LogBrought(ILogger logger, Guid project, int feature, string @default);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened pull request {Pull} for feature #{Feature} of project {Project}")]
    private static partial void LogOpened(ILogger logger, Guid project, int feature, int pull);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused to open the pull request of feature #{Feature} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogOpenRefused(ILogger logger, Guid project, int feature, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bringing the default branch into feature #{Feature} of project {Project} waits: {Hold}")]
    private static partial void LogHeld(ILogger logger, Guid project, int feature, ProjectHold hold);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Default} conflicts with the branch of feature #{Feature} of project {Project}; an agent resolves it")]
    private static partial void LogConflict(ILogger logger, Guid project, int feature, string @default);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused to bring the default branch into feature #{Feature} of project {Project}, with {Status}; it waits for its owner")]
    private static partial void LogRefused(ILogger logger, Guid project, int feature, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its features' ends are tried again on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ending feature #{Feature} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int feature, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);

    /// <summary>A feature whose end is due, as read before its lock.</summary>
    private sealed record Due(Guid Id, int Number, string Branch, string Title, string? BroughtIn, int? PullRequestNumber, string DefaultBranch, string? DefaultHead);

    /// <summary>A feature bringing in, read under the lock, with the default branch.</summary>
    private sealed record Current(string Branch, string DefaultBranch, string? DefaultHead);
}
using Dapper;

using DevFlow.CodeHost;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Merging;

/// <summary>What merging as the app answers. A closed set.</summary>
public abstract record AppMergeOutcome
{
    private AppMergeOutcome()
    {
    }

    /// <summary>The code host was asked, and answered <paramref name="Result"/>.</summary>
    public sealed record Done(MergeResult Result) : AppMergeOutcome;

    /// <summary>The project is stopped, so nothing was asked.</summary>
    public sealed record ProjectStopped : AppMergeOutcome;

    /// <summary>The user has no such project.</summary>
    public sealed record NoSuchProject : AppMergeOutcome;

    /// <summary>No key to merge with: the code host does not reach the repository, or did not
    /// answer, with when to try again if it said.</summary>
    public sealed record NoKey(bool NotFound, DateTimeOffset? RetryAt) : AppMergeOutcome;
}

/// <summary>
/// Dev flow's own merges, as the app, and the record of each that lets the watch tell them from
/// anything else landing on the default branch. A merge is made only while the project is not
/// stopped, and its merge commit is written to <c>merges_made</c> in the same transaction, which
/// holds the lock the watch takes before it judges, so the watch never judges the merge before it is
/// recorded. Should the record fail after the code host merged, or the host not say the commit, the
/// watch finds an unrecorded merge by the app and stops the project: a failure here errs towards
/// asking the owner. Nothing uses it yet but its tests; the later slices merge a card's pull
/// request through it. See "Watching the default branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class AppMerges(UserDb database, ICodeHost host, TimeProvider time)
{
    /// <summary>
    /// Merges the pull request with the number in the user's project, while its head is
    /// <paramref name="commit"/>, as the app with a key for merging alone, unless the project is
    /// stopped; and records the merge commit when the host gives one.
    /// </summary>
    public async Task<AppMergeOutcome> Merge(Guid userId, Guid projectId, int number, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return await database.InUserTransaction<AppMergeOutcome>(userId, async transaction =>
        {
            if (!await BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return new AppMergeOutcome.NoSuchProject();
            }

            var project = await transaction.Connection!.QuerySingleAsync<ProjectRow>(new CommandDefinition(
                """
                SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                       EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = p.id) AS Stopped
                FROM projects p WHERE p.id = @projectId
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project.Stopped)
            {
                return new AppMergeOutcome.ProjectStopped();
            }

            var repository = new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name);
            AppWriteToken key;
            switch (await host.ForWrite(userId, repository, WritePurpose.Merge, cancellationToken))
            {
                case CodeHostResult<AppWriteToken>.Found { Value: var found }:
                    key = found;
                    break;
                case CodeHostResult<AppWriteToken>.NotFound:
                    return new AppMergeOutcome.NoKey(NotFound: true, RetryAt: null);
                case CodeHostResult<AppWriteToken>.Unavailable { RetryAt: var retryAt }:
                    return new AppMergeOutcome.NoKey(NotFound: false, retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            var result = await host.MergeAsApp(userId, key, repository, number, commit, cancellationToken);
            if (result is MergeResult.Merged { Commit: { } merged })
            {
                var now = time.GetUtcNow();
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO merges_made (project_id, user_id, commit, number, merged_at)
                    VALUES (@projectId, @userId, @merged, @number, @now)
                    ON CONFLICT (project_id, commit) DO NOTHING
                    """,
                    new { projectId, userId, merged, number, now = now.AddTicks(-(now.Ticks % 10)) },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            return new AppMergeOutcome.Done(result);
        }, cancellationToken);
    }

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name, bool Stopped);
}
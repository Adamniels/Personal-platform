using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Projects;
using DevFlow.Protocol;

using PlatformData;

namespace DevFlow.Setup;

/// <summary>
/// A project's setup, on the opening loop's minute and after each fake worker run: its pull request
/// opened as the app, or found when the app opened it already, and its body written again from a
/// newer run; then followed, merged making the setup done and closed stopping it; and the owner's
/// comments on it taken, which run a setup that waits for him or stopped again. Never merged by
/// Dev flow: the merge check reads only cards. See "Setting up a project" in devflow/README.md.
/// </summary>
public sealed partial class SetupLoop(UserDb database, ICodeHost host, TimeProvider time, ILogger<SetupLoop> log)
{
    /// <summary>Everything due for the project's setup now. The code host not answering leaves it
    /// for the next minute.</summary>
    public async Task TendPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        if (await Project(projectId, userId, cancellationToken) is not { } project)
        {
            return;
        }

        var repository = new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name);
        await Open(projectId, userId, repository, cancellationToken);
        await Follow(projectId, userId, repository, project.Trusted, cancellationToken);
    }

    private async Task Open(Guid projectId, Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        var pending = await SetupRunStore.Pending(database, userId, projectId, cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        if (await host.DefaultBranch(userId, repository, cancellationToken) is not CodeHostResult<string>.Found { Value: var defaultBranch }
            || await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var app }
            || await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is not CodeHostResult<AppWriteToken>.Found { Value: var key })
        {
            LogNotNow(log, projectId);
            return;
        }

        foreach (var setup in pending)
        {
            using var document = JsonDocument.Parse(setup.Report);
            if (!SetupReport.TryRead(document.RootElement, out var report, out _))
            {
                continue;
            }

            var body = SetupText.Body(report, setup.Number, setup.RunId, setup.HeadCommit, setup.State == "stopped" ? setup.Reason : null);
            if (setup.PullRequestNumber is { } number)
            {
                if (await host.EditPullRequestBody(userId, key, repository, number, body, cancellationToken) is WriteResult<PullRequest>.Written)
                {
                    await SetupRunStore.Rewritten(database, userId, projectId, setup.Number, setup.RunId, cancellationToken);
                }

                continue;
            }

            var opened = await Existing(userId, repository, setup.Branch, defaultBranch, app.Name, cancellationToken);
            if (opened is null && await host.OpenPullRequest(userId, key, repository, setup.Branch, defaultBranch, SetupRunStore.Title, body, cancellationToken) is WriteResult<PullRequest>.Written { Value: var made })
            {
                opened = made;
            }

            // GitHub refuses a second pull request from the same branch, so one may have been opened
            // meanwhile, by an opening whose answer was lost.
            opened ??= await Existing(userId, repository, setup.Branch, defaultBranch, app.Name, cancellationToken);
            if (opened is null)
            {
                LogNotOpened(log, projectId, setup.Number);
                continue;
            }

            await SetupRunStore.Opened(database, userId, projectId, setup.Number, opened.Number, opened.Url, setup.RunId, Now(), cancellationToken);
            LogOpened(log, projectId, setup.Number, opened.Number);
        }
    }

    private async Task<PullRequest?> Existing(Guid userId, Repository repository, string branch, string defaultBranch, string app, CancellationToken cancellationToken) =>
        await host.OpenPullRequests(userId, repository, cancellationToken) is CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var open }
            ? open.Where(pull => pull.State == PullRequestState.Open
                    && string.Equals(pull.HeadBranch, branch, StringComparison.Ordinal)
                    && string.Equals(pull.BaseBranch, defaultBranch, StringComparison.Ordinal)
                    && string.Equals(pull.Author, app, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pull => pull.Number)
                .FirstOrDefault()
            : null;

    private async Task Follow(Guid projectId, Guid userId, Repository repository, TrustedAccounts trusted, CancellationToken cancellationToken)
    {
        foreach (var setup in await SetupRunStore.Followed(database, userId, projectId, cancellationToken))
        {
            if (await host.PullRequest(userId, repository, setup.PullRequestNumber, cancellationToken) is not CodeHostResult<PullRequest>.Found { Value: var pull })
            {
                continue;
            }

            if (pull.State == PullRequestState.Merged)
            {
                await SetupRunStore.Ended(database, userId, projectId, setup.Number, merged: true, Now(), cancellationToken);
                continue;
            }

            var closed = pull.State == PullRequestState.Closed;
            if (closed && !(setup.State == "stopped" && setup.Reason == SetupRunStore.Closed))
            {
                await SetupRunStore.Ended(database, userId, projectId, setup.Number, merged: false, Now(), cancellationToken);
                continue;
            }

            if (setup.Running || setup.State is not ("waiting_for_owner" or "stopped")
                || await host.Comments(userId, repository, setup.PullRequestNumber, cancellationToken) is not CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments })
            {
                continue;
            }

            // His, by a trusted account, new since the last taken, and written after the setup came
            // to wait or stopped, as a stopped card's are.
            var since = new DateTimeOffset(DateTime.SpecifyKind(setup.ChangedAt, DateTimeKind.Utc));
            var taken = comments
                .Where(comment => trusted.Trusts(comment.Author))
                .Where(comment => setup.ReadThrough is not { } through || comment.Id > through)
                .Where(comment => comment.CreatedAt > since)
                .DistinctBy(comment => comment.Id)
                .OrderBy(comment => comment.Id)
                .ToList();
            if (taken.Count > 0)
            {
                await SetupRunStore.Commented(database, userId, projectId, setup.Number, [.. taken.Select(comment => comment.Body)], taken[^1].Id, closed, Now(), cancellationToken);
                LogCommented(log, projectId, setup.Number);
            }
        }
    }

    private async Task<ProjectRow?> Project(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                   a.login AS OwnerLogin, s.trusted_logins AS TrustedLogins
            FROM projects p
            JOIN project_settings s ON s.project_id = p.id
            LEFT JOIN github_accounts a ON a.user_id = p.user_id
            WHERE p.id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>The project as the loop needs it, set by property, since Dapper maps an array
    /// column only to a property.</summary>
    private sealed class ProjectRow
    {
        public long Installation { get; init; }

        public long RepositoryId { get; init; }

        public string Owner { get; init; } = "";

        public string Name { get; init; } = "";

        public string? OwnerLogin { get; init; }

        public string[] TrustedLogins { get; init; } = [];

        public TrustedAccounts Trusted => TrustedAccounts.Of(OwnerLogin, TrustedLogins);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened pull request #{PullRequest} for setup {Setup} of project {ProjectId}")]
    private static partial void LogOpened(ILogger logger, Guid projectId, int setup, int pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pull request of setup {Setup} of project {ProjectId} was not opened; it is tried again on the next minute")]
    private static partial void LogNotOpened(ILogger logger, Guid projectId, int setup);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer for the setup of project {ProjectId}; it is tried again on the next minute")]
    private static partial void LogNotNow(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Took the owner's comments on setup {Setup} of project {ProjectId}; it runs again")]
    private static partial void LogCommented(ILogger logger, Guid projectId, int setup);
}
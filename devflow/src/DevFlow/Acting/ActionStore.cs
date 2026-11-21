using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Acting;

/// <summary>A waiting item the caller asked to act on, as stored, with what acting needs: its
/// project's repository, by the installation and id it was connected with.</summary>
/// <param name="Kind"><c>waiting</c> or <c>stopped</c>, as stored.</param>
public sealed record ActionTarget(
    Guid ProjectId,
    long Installation,
    long RepositoryId,
    string Owner,
    string Name,
    int Number,
    string Kind,
    bool SentBack,
    bool CanMerge,
    string HeadCommit)
{
    /// <summary>The repository, as the code host is asked for it.</summary>
    public Repository Repository => new(Installation, RepositoryId, Owner, Name);
}

/// <summary>A card of Dev flow's own the caller asked to merge or send back, as stored, with what
/// acting needs.</summary>
/// <param name="State">The card's state; only <c>waiting_for_owner</c> is merged, and it or
/// <c>stopped</c> sent back.</param>
/// <param name="Reason">Why it is in its state, null when there is nothing to say.</param>
/// <param name="PullRequestNumber">Its pull request's number, null before one was opened.</param>
/// <param name="HeadCommit">The commit its newest build or fix that succeeded checked, null
/// without one.</param>
/// <param name="Stopped">Whether the project is stopped, by what landed on its default branch.</param>
public sealed record CardTarget(
    Guid ProjectId,
    long Installation,
    long RepositoryId,
    string Owner,
    string Name,
    int Number,
    string State,
    string? Reason,
    int? PullRequestNumber,
    string? HeadCommit,
    bool Stopped)
{
    /// <summary>The repository, as the code host is asked for it.</summary>
    public Repository Repository => new(Installation, RepositoryId, Owner, Name);
}

/// <summary>A feature the caller asked to merge, as stored, with what merging it needs.</summary>
/// <param name="Title">The parent's title as last read.</param>
/// <param name="State">The feature's state; only <c>ready</c> and <c>waiting_for_owner</c> are
/// merged.</param>
/// <param name="PullRequestNumber">Its pull request to the default branch, null before one is
/// opened.</param>
/// <param name="PullRequestUrl">Where it is, beside its number.</param>
/// <param name="Head">The feature branch's head as its watch last named it, null before it named
/// one.</param>
/// <param name="CiPassed">The head CI last passed on, as the minute read it, null when none.</param>
/// <param name="Into">The default branch, as its watch names it, null before it is watched.</param>
/// <param name="Stopped">Whether the project is stopped.</param>
public sealed record FeatureTarget(
    Guid ProjectId,
    long Installation,
    long RepositoryId,
    string Owner,
    string Name,
    int Number,
    string Title,
    string State,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? Head,
    string? CiPassed,
    string? Into,
    bool Stopped)
{
    /// <summary>The repository, as the code host is asked for it.</summary>
    public Repository Repository => new(Installation, RepositoryId, Owner, Name);
}

/// <summary>
/// The items the page acts on, read and changed through the door as the caller, so another user's
/// item is not found rather than refused. See "Acting from the page" in devflow/README.md.
/// </summary>
public sealed class ActionStore(UserDb database)
{
    /// <summary>The caller's item with the id, or null when they have none.</summary>
    public async Task<ActionTarget?> Find(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ActionTarget>(new CommandDefinition(
            """
            SELECT p.id AS ProjectId, p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner,
                   p.name AS Name, w.number AS Number, w.kind AS Kind, w.sent_back AS SentBack, w.can_merge AS CanMerge,
                   w.head_commit AS HeadCommit
            FROM waiting_items w JOIN projects p ON p.id = w.project_id
            WHERE w.id = @itemId
            """,
            new { itemId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The caller's card with the number in their project, or null when they have no
    /// such project or card.</summary>
    public async Task<CardTarget?> FindCard(Guid userId, Guid projectId, int number, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<CardTarget>(new CommandDefinition(
            """
            SELECT p.id AS ProjectId, p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner,
                   p.name AS Name, c.number AS Number, c.state AS State, c.reason AS Reason, c.pull_request_number AS PullRequestNumber,
                   (SELECT r.head_commit FROM runs r WHERE r.project_id = c.project_id AND r.card_number = c.number
                      AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                    ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS HeadCommit,
                   EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = p.id) AS Stopped
            FROM cards c JOIN projects p ON p.id = c.project_id
            WHERE p.id = @projectId AND c.number = @number
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The caller's feature with the number in their project, or null when they have no
    /// such project or feature.</summary>
    public async Task<FeatureTarget?> FindFeature(Guid userId, Guid projectId, int number, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<FeatureTarget>(new CommandDefinition(
            """
            SELECT p.id AS ProjectId, p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner,
                   p.name AS Name, f.number AS Number, f.title AS Title, f.state AS State,
                   f.pull_request_number AS PullRequestNumber, f.pull_request_url AS PullRequestUrl,
                   h.last_commit AS Head, f.ci_passed_commit AS CiPassed, d.branch AS Into,
                   EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = p.id) AS Stopped
            FROM features f
            JOIN projects p ON p.id = f.project_id
            LEFT JOIN branch_watches h ON h.project_id = f.project_id AND h.feature_id = f.id
            LEFT JOIN branch_watches d ON d.project_id = f.project_id AND d.feature_id IS NULL
            WHERE p.id = @projectId AND f.number = @number
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>Marks the caller's item sent back at once, before the project is read again, so
    /// the list says so even when that read fails. The next read sets it from the comments, the
    /// owner's among them.</summary>
    public Task MarkSentBack(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE waiting_items SET sent_back = true WHERE id = @itemId",
            new { itemId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
}
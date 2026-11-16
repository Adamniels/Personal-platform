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
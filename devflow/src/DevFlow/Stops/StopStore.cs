using Dapper;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>A card whose stop waits to be marked on its issue, or whose attention label waits to
/// be taken off since it runs again.</summary>
/// <param name="Number">The card's issue number.</param>
/// <param name="Stopped">Whether it is stopped, so its stop is marked; otherwise its label is
/// taken off.</param>
/// <param name="StoppedAt">When it stopped, which the comment names, so a copy already posted is
/// found; null for a card that runs again.</param>
/// <param name="Reason">Why it stopped.</param>
/// <param name="Labelled">Whether the app's attention label is on its issue.</param>
public sealed record PendingMark(int Number, bool Stopped, DateTimeOffset? StoppedAt, string? Reason, bool Labelled);

/// <summary>A run of a card as its stop's comment lists it.</summary>
/// <param name="Kind"><c>build</c>, <c>review</c> or <c>fix</c>.</param>
/// <param name="Result">How it ended, null while under way.</param>
/// <param name="Reason">Why, as stored; null for a success.</param>
public sealed record TriedRun(string Kind, string? Result, string? Reason);

/// <summary>
/// What marking a stop reads and writes, through the door as the project's user: the cards whose
/// stop or whose label waits, the runs a stop's comment lists, and the marks once made. See "Stops"
/// in devflow/README.md.
/// </summary>
public static class StopStore
{
    /// <summary>The most runs a stop's comment lists, the newest.</summary>
    public const int MostRuns = 10;

    /// <summary>The project's cards whose stop waits to be marked, and those running again whose
    /// attention label waits to be taken off, by number. A refused card keeps its label, which
    /// <see cref="Readings.RefusalMarker"/> put on, until it is queued again.</summary>
    public static async Task<IReadOnlyList<PendingMark>> Pending(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT number AS Number, state = 'stopped' AS Stopped, stopped_at AS StoppedAt, reason AS Reason, attention_labelled AS Labelled
            FROM cards
            WHERE project_id = @projectId
              AND ((state = 'stopped' AND stop_marked_at IS NULL AND stopped_at IS NOT NULL) OR (state NOT IN ('stopped', 'refused') AND attention_labelled))
            ORDER BY number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new PendingMark(row.Number, row.Stopped, row.StoppedAt is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null, row.Reason, row.Labelled))];
    }

    /// <summary>The card's runs, how many there are and the newest <see cref="MostRuns"/> of
    /// them, oldest first.</summary>
    public static async Task<(int Count, IReadOnlyList<TriedRun> Newest)> Runs(UserDb database, Guid userId, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var count = await transaction.Connection!.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM runs WHERE project_id = @projectId AND card_number = @number",
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            var newest = await transaction.Connection!.QueryAsync<TriedRun>(new CommandDefinition(
                """
                SELECT Kind, Result, Reason FROM (
                    SELECT kind AS Kind, result AS Result, reason AS Reason, started_at, id FROM runs
                    WHERE project_id = @projectId AND card_number = @number
                    ORDER BY started_at DESC, id DESC LIMIT @most) newest
                ORDER BY started_at, id
                """,
                new { projectId, number, most = MostRuns },
                transaction,
                cancellationToken: cancellationToken));
            return (count, (IReadOnlyList<TriedRun>)[.. newest]);
        }, cancellationToken);
    }

    /// <summary>Records whether the app's attention label is on the card's issue now.</summary>
    public static Task Labelled(UserDb database, Guid userId, Guid projectId, int number, bool labelled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET attention_labelled = @labelled WHERE project_id = @projectId AND number = @number",
            new { projectId, number, labelled },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>Records the stop of <paramref name="stoppedAt"/> marked, only while it is still
    /// the card's stop, so a card that ran again and stopped since is marked again.</summary>
    public static Task Marked(UserDb database, Guid userId, Guid projectId, int number, DateTimeOffset stoppedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET stop_marked_at = @now
            WHERE project_id = @projectId AND number = @number AND state = 'stopped' AND stopped_at = @stoppedAt
            """,
            new { projectId, number, stoppedAt, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    private sealed record Row(int Number, bool Stopped, DateTime? StoppedAt, string? Reason, bool Labelled);
}
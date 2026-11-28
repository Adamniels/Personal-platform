using Dapper;

using Npgsql;

namespace DevFlow.Stops;

/// <summary>
/// Claude's usage limit, which is the user's, since the subscription is: a run of any project of
/// theirs on the subscription that fails outside its card with a reset Claude Code named, believed
/// by <see cref="ProjectWaits.Believed"/>, records the user's subscription limited until then, the
/// later of two kept; until that reset every project of the user on the subscription runs no
/// model, and one that continues on the API key carries the key. Nothing clears it: once its reset
/// has passed it holds nothing, so the first job after it carries the subscription again. See
/// "Stops" and "The API key" in devflow/README.md, and why it is the user's, "Stops" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class SubscriptionLimits
{
    /// <summary>
    /// The subscription of the user <paramref name="transaction"/> runs as is limited until
    /// <paramref name="resetsAt"/>, recorded at <paramref name="now"/>, inside the transaction that
    /// records the run that found it; a reset earlier than one already recorded leaves that one.
    /// </summary>
    public static Task Record(NpgsqlTransaction transaction, Guid userId, DateTimeOffset resetsAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO subscription_limits (user_id, resets_at, recorded_at)
            VALUES (@userId, @resetsAt, @now)
            ON CONFLICT (user_id) DO UPDATE SET resets_at = EXCLUDED.resets_at, recorded_at = EXCLUDED.recorded_at
            WHERE subscription_limits.resets_at < EXCLUDED.resets_at
            """,
            new { userId, resetsAt, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Until when <paramref name="userId"/>'s subscription is limited at
    /// <paramref name="now"/>, read through the door as them, or null when it is not.</summary>
    public static async Task<DateTimeOffset?> Of(PlatformData.UserDb database, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => UntilIn(transaction, now, cancellationToken), cancellationToken);
    }

    /// <summary>Until when the subscription of the user <paramref name="transaction"/> runs as is
    /// limited at <paramref name="now"/>, or null when it is not: none recorded, or its reset
    /// passed.</summary>
    public static async Task<DateTimeOffset?> UntilIn(NpgsqlTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var until = await transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT resets_at FROM subscription_limits WHERE resets_at > @now",
            new { now },
            transaction,
            cancellationToken: cancellationToken));
        return until is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
    }
}
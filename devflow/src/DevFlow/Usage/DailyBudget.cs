using System.Globalization;

using Dapper;

using DevFlow.Jobs;

using Npgsql;

namespace DevFlow.Usage;

/// <summary>A project whose runs used its daily budget today, and why it waits.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="ProjectName">Its repository's name, as connected.</param>
/// <param name="Owner">Its repository's owner, as connected.</param>
/// <param name="Reached">What it used, of what, and when the hold lifts.</param>
public sealed record BudgetReachedProject(Guid ProjectId, string ProjectName, string Owner, WaitReason.BudgetReached Reached);

/// <summary>
/// A project's daily budget in tokens, off by default: once the tokens of its runs that finished
/// since the latest midnight in the user's zone, by <see cref="Tokens"/>' one definition, reach it,
/// the project starts nothing new until the next midnight there. Worked out from the runs each time
/// it is asked for, and never stored, so it lifts by itself at midnight, and at once when the
/// budget is raised or switched off. A run under way counts once it finishes, so a day overshoots
/// by one run at most. See "Settings" under "Cards" and "Stops" in devflow/README.md, and why it is
/// worked out "Stops" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class DailyBudget
{
    /// <summary>A million tokens, the unit the settings page takes the budget in.</summary>
    public const long Million = 1_000_000;

    /// <summary>The fewest millions a budget may be.</summary>
    public const long LeastMillions = 1;

    /// <summary>The most millions a budget may be.</summary>
    public const long MostMillions = 10_000;

    /// <summary>Why a budget that is not null or a whole number of millions within the range is
    /// refused.</summary>
    public static readonly string Problem = string.Create(CultureInfo.InvariantCulture,
        $"must be null for off, or a whole number of millions of tokens from {LeastMillions * Million} to {MostMillions * Million}");

    /// <summary>Whether <paramref name="budget"/> is one the setting takes: null for off, or a whole
    /// number of millions from <see cref="LeastMillions"/> to <see cref="MostMillions"/>.</summary>
    public static bool Holds(long? budget) =>
        budget is not { } tokens || (tokens % Million == 0 && tokens / Million is >= LeastMillions and <= MostMillions);

    /// <summary>The day <paramref name="now"/> falls on in <paramref name="zone"/>, as the instant
    /// it started and the one it ends, the next midnight, which is not in it; 23 or 25 hours long on
    /// a day the clocks change.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Today(DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = DevFlowTime.DayOf(now, zone);
        return (DevFlowTime.StartOf(day, zone), DevFlowTime.EndOf(day, zone));
    }

    /// <summary>Why a project whose budget is <paramref name="budget"/> and whose runs used
    /// <paramref name="used"/> today waits, until <paramref name="end"/>, today's end, written in
    /// <paramref name="zone"/>'s own offset; null when it has no budget or used less.</summary>
    public static WaitReason.BudgetReached? Reason(long? budget, long used, DateTimeOffset end, TimeZoneInfo zone) =>
        budget is { } tokens && used >= tokens ? new WaitReason.BudgetReached(used, tokens, TimeZoneInfo.ConvertTime(end, zone)) : null;

    /// <summary>
    /// Why the project waits for its budget at <paramref name="now"/>, read inside
    /// <paramref name="transaction"/>, which runs as its user, in their zone; null when it has no
    /// budget, used less of it today, or there is no such project. What <see cref="RunDecision"/>
    /// reads, and handing out a job again under the project's lock.
    /// </summary>
    public static async Task<WaitReason.BudgetReached?> ReachedIn(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await Read(transaction, projectId, now, cancellationToken)).SingleOrDefault()?.Reached;

    /// <summary>Every project of the user <paramref name="transaction"/> runs as whose runs used its
    /// budget today at <paramref name="now"/>, by owner and name, as "Waiting for you" lists
    /// them.</summary>
    public static async Task<IReadOnlyList<BudgetReachedProject>> AllReachedIn(NpgsqlTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken) =>
        await Read(transaction, null, now, cancellationToken);

    private static async Task<IReadOnlyList<BudgetReachedProject>> Read(NpgsqlTransaction transaction, Guid? projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var zone = await DevFlowTime.ZoneOf(transaction, cancellationToken);
        var (start, end) = Today(now, zone);

        // The sum is Tokens' own SQL, over the runs that finished in today's bounds, so a run under
        // way counts only once it finishes, and every kind of run counts.
        var rows = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT p.id AS ProjectId, p.name AS ProjectName, p.owner AS Owner, s.daily_token_budget AS Budget, u.used AS Used
            FROM projects p
            JOIN project_settings s ON s.project_id = p.id
            CROSS JOIN LATERAL (
                SELECT coalesce(sum({Tokens.Sql("r")}), 0)::bigint AS used
                FROM runs r
                WHERE r.project_id = p.id AND r.finished_at >= @start AND r.finished_at < @end
            ) u
            WHERE s.daily_token_budget IS NOT NULL AND u.used >= s.daily_token_budget
              AND (@projectId::uuid IS NULL OR p.id = @projectId)
            ORDER BY lower(p.owner), lower(p.name), p.id
            """,
            new { projectId, start, end },
            transaction,
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new BudgetReachedProject(row.ProjectId, row.ProjectName, row.Owner, Reason(row.Budget, row.Used, end, zone)!))];
    }

    private sealed record Row(Guid ProjectId, string ProjectName, string Owner, long Budget, long Used);
}
using System.Globalization;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Projects;

using Npgsql;

namespace DevFlow.Usage;

/// <summary>A project whose jobs carry the API key now, and what its runs on the key cost today.</summary>
/// <param name="Until">For a project continuing on the key, the subscription's reset, after which
/// its jobs carry the subscription again, in the user's zone's own offset; null for one on the key
/// alone.</param>
/// <param name="Spent">What its runs on the key that finished today cost, in US dollars, by Claude
/// Code's estimate.</param>
/// <param name="Cap">Its daily cap on the key, in whole US dollars.</param>
/// <param name="LiftsAt">When the cap, once reached, lifts by itself: the next midnight in the
/// user's zone, or the subscription's reset when that comes first, in the zone's own offset.</param>
public sealed record OnTheKey(DateTimeOffset? Until, decimal Spent, int Cap, DateTimeOffset LiftsAt)
{
    /// <summary>Why the project waits for its cap, or null while it spent less.</summary>
    public WaitReason.ApiCapReached? Reached => Spent >= Cap ? new WaitReason.ApiCapReached(Spent, Cap, LiftsAt) : null;
}

/// <summary>
/// A project's daily cap on the API key, <c>api_daily_usd</c>: required in whole US dollars from
/// <see cref="Least"/> to <see cref="Most"/> on a project that runs on the key, alone or continuing
/// on it, and null on the subscription. Spend is the <c>cost_usd</c> of the project's runs whose
/// credential was the key and that finished since the latest midnight in the user's zone, read
/// through <see cref="DevFlowTime"/>, so a run under way counts once it finishes, and a day
/// overshoots by one run at most, which the spend per run bounds. Reached while its jobs carry the
/// key, the project starts nothing new on it until midnight, a raise, or, continuing on the key,
/// the subscription's reset, whichever comes first. Worked out each time, never stored, like the
/// daily budget. See "The API key" and "Settings" under "Cards" in devflow/README.md.
/// </summary>
public static class ApiCap
{
    /// <summary>The fewest US dollars a day the cap may be.</summary>
    public const int Least = 1;

    /// <summary>The most US dollars a day the cap may be.</summary>
    public const int Most = 1000;

    /// <summary>What the settings page proposes when a project first runs on the key.</summary>
    public const int Proposed = 20;

    /// <summary>Why a cap that is not a whole number within the range is refused.</summary>
    public static readonly string Problem = string.Create(CultureInfo.InvariantCulture,
        $"must be a whole number of US dollars from {Least} to {Most}");

    /// <summary>Why a project on the key without a cap is refused.</summary>
    public const string Required = "is required when runs_on is not subscription";

    /// <summary>Why a project on the subscription alone with a cap is refused.</summary>
    public const string NotOnSubscription = "must be null when runs_on is subscription";

    /// <summary>What is wrong with <paramref name="cap"/> for a project on
    /// <paramref name="runsOn"/>, or null when nothing is: null on the subscription alone, and on
    /// the key a whole number from <see cref="Least"/> to <see cref="Most"/>.</summary>
    public static string? ProblemOf(string runsOn, decimal? cap) => (runsOn, cap) switch
    {
        (RunsOn.Subscription, null) => null,
        (RunsOn.Subscription, _) => NotOnSubscription,
        (_, null) => Required,
        (_, { } dollars) when dollars != decimal.Truncate(dollars) || dollars is < Least or > Most => Problem,
        _ => null,
    };

    /// <summary>
    /// The project's spend on the key at <paramref name="now"/>, read inside
    /// <paramref name="transaction"/>, which runs as its user, in their zone, while its jobs carry
    /// the key by <see cref="RunsOn.Carried"/>, with the user's subscription limited until
    /// <paramref name="limited"/>, or not when null; null while they carry the subscription, or for
    /// no such project.
    /// </summary>
    public static async Task<OnTheKey?> StateIn(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset? limited, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT runs_on AS RunsOn, api_daily_usd AS Cap FROM project_settings WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (row is not { Cap: { } cap } || RunsOn.Carried(row.RunsOn, limited is not null) != Protocol.JobCredential.ApiKey)
        {
            return null;
        }

        var zone = await DevFlowTime.ZoneOf(transaction, cancellationToken);
        var (start, end) = DailyBudget.Today(now, zone);
        var spent = await transaction.Connection!.ExecuteScalarAsync<decimal>(new CommandDefinition(
            $"""
            SELECT coalesce(sum(r.cost_usd), 0)
            FROM runs r
            WHERE r.project_id = @projectId AND r.credential = '{Protocol.JobCredential.ApiKey}'
              AND r.finished_at >= @start AND r.finished_at < @end
            """,
            new { projectId, start, end },
            transaction,
            cancellationToken: cancellationToken));
        var until = row.RunsOn == RunsOn.SubscriptionThenApiKey ? limited : null;
        var lifts = until is { } reset && reset < end ? reset : end;
        return new OnTheKey(until is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null, spent, cap, TimeZoneInfo.ConvertTime(lifts, zone));
    }

    /// <summary>Why the project waits for its cap at <paramref name="now"/>, inside
    /// <paramref name="transaction"/>, the user's subscription's limit read there too; null while
    /// its jobs carry the subscription or it spent less. What handing out a job reads again under
    /// the project's lock.</summary>
    public static async Task<WaitReason.ApiCapReached?> ReachedIn(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await StateIn(transaction, projectId, await Stops.SubscriptionLimits.UntilIn(transaction, now, cancellationToken), now, cancellationToken))?.Reached;

    private sealed record Row(string RunsOn, int? Cap);
}
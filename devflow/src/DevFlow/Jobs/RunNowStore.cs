using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>What "Run now anyway" is for: a feature, by its parent's number, or a card on its own.</summary>
public enum RunNowOf
{
    /// <summary>A feature, every job of it and of its cards.</summary>
    Feature,

    /// <summary>A card on its own, every job of it.</summary>
    Card,
}

/// <summary>What a feature's or a card's page says of "Run now anyway".</summary>
/// <param name="OutsideSchedule">Whether its project is outside its schedules now.</param>
/// <param name="SomethingToRun">Whether it has something left to run, so an override would hold;
/// false for a card of a feature, whose override is its feature's.</param>
/// <param name="On">Whether an override of it holds now: stored, with something left to run.</param>
public sealed record RunNowState(bool OutsideSchedule, bool SomethingToRun, bool On);

/// <summary>What setting an override came to.</summary>
public enum RunNowOutcome
{
    /// <summary>It holds, as set now or before.</summary>
    Set,

    /// <summary>The user has no such project, feature or card.</summary>
    NoSuch,

    /// <summary>It has nothing left to run.</summary>
    NothingToRun,

    /// <summary>The card is a card of a feature, which runs with its feature.</summary>
    CardOfAFeature,
}

/// <summary>
/// "Run now anyway": the owner's override of a project's schedules for one feature or one card on
/// its own, a row of <c>run_now</c>. While it holds, the dispatcher hands out every job of what it
/// covers outside the schedules, and nothing else of the project: <see cref="RunDecision"/> reads
/// the project's next kind among covered jobs alone, and each claim filters by
/// <see cref="OnlyCards"/> or <see cref="OnlyFeatures"/> when the project is outside. It skips the
/// schedules and nothing else. It holds until what it covers has nothing left to run,
/// <see cref="Left"/>, and the dispatcher deletes it the first time it reads that,
/// <see cref="EndIn"/>. Every read and write runs through the door as the user. See "Schedules"
/// under "Jobs" in devflow/README.md and "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunNowStore
{
    /// <summary>The states a card has a job handed out for, now or next; every other, waiting for
    /// the owner, needing answers, done, stopped, refused, held or ignored, has nothing left to
    /// run.</summary>
    private const string CardStates = "('queued', 'reading', 'building', 'checking', 'reviewing', 'fixing')";

    /// <summary>Whether card <paramref name="c"/> has something left to run, as SQL.</summary>
    private static string CardLeft(string c) => $"{c}.state IN {CardStates}";

    /// <summary>
    /// Whether feature <paramref name="f"/> has something left to run, as SQL: not stopped by the
    /// owner, nor held, ignored, merged or closed, and either a job of its own is due or under way,
    /// bringing in, resolving, reviewing or fixing; or a card of it has something left to run; or
    /// it is building with every card done, so its end, and then its review, come next. Waiting for
    /// the owner or ready, with no card left to run, it has nothing.
    /// </summary>
    private static string FeatureLeft(string f) => $"""
        ({f}.stopped_by_owner_at IS NULL AND {f}.state NOT IN ('held', 'ignored', 'merged', 'closed') AND (
            {f}.state IN ('bringing_in', 'resolving', 'reviewing', 'fixing')
            OR EXISTS (SELECT 1 FROM cards fc WHERE fc.project_id = {f}.project_id AND fc.feature_id = {f}.id AND {CardLeft("fc")})
            OR ({f}.state = 'building'
                AND EXISTS (SELECT 1 FROM cards fc WHERE fc.project_id = {f}.project_id AND fc.feature_id = {f}.id)
                AND NOT EXISTS (SELECT 1 FROM cards fc WHERE fc.project_id = {f}.project_id AND fc.feature_id = {f}.id AND fc.state <> 'done'))))
        """;

    /// <summary>Whether card <paramref name="c"/> is on its own and has something left to run, as
    /// SQL.</summary>
    private static string CardOnItsOwnLeft(string c) => $"({c}.feature_id IS NULL AND {CardLeft(c)})";

    /// <summary>Whether the override <paramref name="o"/>, a row of <c>run_now</c>, still holds, as
    /// SQL: what it is for has something left to run.</summary>
    private static string Left(string o) => $"""
        (CASE WHEN {o}.feature_number IS NOT NULL
              THEN EXISTS (SELECT 1 FROM features lf WHERE lf.project_id = {o}.project_id AND lf.number = {o}.feature_number AND {FeatureLeft("lf")})
              ELSE EXISTS (SELECT 1 FROM cards lc WHERE lc.project_id = {o}.project_id AND lc.number = {o}.card_number AND {CardOnItsOwnLeft("lc")}) END)
        """;

    /// <summary>Whether a card, as SQL over <c>cards</c>, is covered by an override that holds: its
    /// own, or its feature's.</summary>
    internal static readonly string CoversCard = $"""
        EXISTS (SELECT 1 FROM run_now o
                WHERE o.project_id = cards.project_id
                  AND (o.card_number = cards.number
                       OR o.feature_number = (SELECT cf.number FROM features cf WHERE cf.id = cards.feature_id))
                  AND {Left("o")})
        """;

    /// <summary>Whether a feature, as SQL over <c>features</c>, is covered by an override that
    /// holds.</summary>
    internal static readonly string CoversFeature = $"""
        EXISTS (SELECT 1 FROM run_now o
                WHERE o.project_id = features.project_id AND o.feature_number = features.number AND {Left("o")})
        """;

    /// <summary>What a claim of a card's job adds to its condition, as SQL over <c>cards</c>: with
    /// <c>@runNowOnly</c>, the project being outside its schedules, only a covered card.</summary>
    internal static readonly string OnlyCards = $"(NOT @runNowOnly OR {CoversCard})";

    /// <summary>What a claim of a feature's own job adds to its condition, as SQL over
    /// <c>features</c>: with <c>@runNowOnly</c>, only a covered feature.</summary>
    internal static readonly string OnlyFeatures = $"(NOT @runNowOnly OR {CoversFeature})";

    /// <summary>Whether project <c>@projectId</c> has an override that holds, as SQL.</summary>
    internal static readonly string AnySql = $"EXISTS (SELECT 1 FROM run_now o WHERE o.project_id = @projectId AND {Left("o")})";

    /// <summary>
    /// Deletes every override of the project with nothing left to run, inside
    /// <paramref name="transaction"/>, which runs as its user: the dispatcher's read of the
    /// project's situation, before it reads anything else, so an override ends the first time the
    /// dispatcher finds it has nothing left, and a card added or sent back later keeps to the
    /// schedules. How many it deleted.
    /// </summary>
    public static async Task<int> EndIn(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM run_now o WHERE o.project_id = @projectId AND NOT {Left("o")}",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Sets the override of the feature or card with <paramref name="number"/> at
    /// <paramref name="now"/>, in one transaction as the user: one already stored is kept as it is.
    /// Refused for a project, feature or card the user does not have, for one with nothing left to
    /// run, and for a card of a feature, whose override is its feature's.
    /// </summary>
    public static async Task<RunNowOutcome> Set(UserDb database, Guid userId, Guid projectId, RunNowOf of, int number, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var found = await Found(transaction, projectId, of, number, cancellationToken);
            if (found is null)
            {
                return RunNowOutcome.NoSuch;
            }

            if (found.OfAFeature)
            {
                return RunNowOutcome.CardOfAFeature;
            }

            if (!found.Left)
            {
                return RunNowOutcome.NothingToRun;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO run_now (project_id, user_id, feature_number, card_number, created_at)
                VALUES (@projectId, @userId, @feature, @card, @now)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    projectId,
                    userId,
                    feature = of == RunNowOf.Feature ? number : (int?)null,
                    card = of == RunNowOf.Card ? number : (int?)null,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
            return RunNowOutcome.Set;
        }, cancellationToken);
    }

    /// <summary>
    /// Takes the override of the feature or card back, in one transaction as the user, after taking
    /// the project's row <c>FOR NO KEY UPDATE</c>, which a job being handed out waits for under its
    /// <c>FOR SHARE</c>, so once this answers no job is handed out by it. Taking back one not set
    /// changes nothing. False when the user has no such project, feature or card.
    /// </summary>
    public static async Task<bool> TakeBack(UserDb database, Guid userId, Guid projectId, RunNowOf of, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            if (await transaction.Connection!.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM projects WHERE id = @projectId FOR NO KEY UPDATE",
                    new { projectId },
                    transaction,
                    cancellationToken: cancellationToken)) is null
                || await Found(transaction, projectId, of, number, cancellationToken) is null)
            {
                return false;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                of == RunNowOf.Feature
                    ? "DELETE FROM run_now WHERE project_id = @projectId AND feature_number = @number"
                    : "DELETE FROM run_now WHERE project_id = @projectId AND card_number = @number",
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// What the page of the feature or card says of "Run now anyway" at <paramref name="now"/>, read
    /// inside <paramref name="transaction"/>, which runs as its user: whether its project is outside
    /// its schedules, by <see cref="Schedules.ScheduleStore.StateIn"/>, whether it has something
    /// left to run, and whether its override holds. Null when there is no such project, feature or
    /// card.
    /// </summary>
    public static async Task<RunNowState?> StateIn(NpgsqlTransaction transaction, Guid projectId, RunNowOf of, int number, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (await Found(transaction, projectId, of, number, cancellationToken) is not { } found
            || await Schedules.ScheduleStore.StateIn(transaction, projectId, now, cancellationToken) is not { } schedule)
        {
            return null;
        }

        var left = found.Left && !found.OfAFeature;
        return new RunNowState(!schedule.Inside, left, left && found.Stored);
    }

    /// <summary>The feature or card as an override reads it, or null when there is none.</summary>
    private static async Task<Thing?> Found(NpgsqlTransaction transaction, Guid projectId, RunNowOf of, int number, CancellationToken cancellationToken) =>
        await transaction.Connection!.QuerySingleOrDefaultAsync<Thing>(new CommandDefinition(
            of == RunNowOf.Feature
                ? $"""
                  SELECT false AS OfAFeature, {FeatureLeft("f")} AS "Left",
                         EXISTS (SELECT 1 FROM run_now o WHERE o.project_id = f.project_id AND o.feature_number = f.number) AS Stored
                  FROM features f WHERE f.project_id = @projectId AND f.number = @number
                  """
                : $"""
                  SELECT c.feature_id IS NOT NULL AS OfAFeature, {CardLeft("c")} AS "Left",
                         EXISTS (SELECT 1 FROM run_now o WHERE o.project_id = c.project_id AND o.card_number = c.number) AS Stored
                  FROM cards c WHERE c.project_id = @projectId AND c.number = @number
                  """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));

    private sealed class Thing
    {
        public bool OfAFeature { get; init; }

        public bool Left { get; init; }

        public bool Stored { get; init; }
    }
}
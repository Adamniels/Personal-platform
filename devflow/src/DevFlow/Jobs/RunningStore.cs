using Dapper;

using DevFlow.Link;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A run under way, as a panel on "Running now" shows it.</summary>
/// <param name="RunId">The run, which its log stream and the stop route name.</param>
/// <param name="ProjectId">Its project.</param>
/// <param name="ProjectName">Its project's repository's name.</param>
/// <param name="WorkerId">The worker holding it.</param>
/// <param name="WorkerName">The name that worker said hello with.</param>
/// <param name="Kind">The job's kind, one of <see cref="Protocol.Job"/>'s.</param>
/// <param name="State"><c>checking</c> once the agent has finished and the check runs,
/// <c>running</c> before.</param>
/// <param name="Stopping">Whether the owner marked it to stop, which its worker has not yet
/// ended.</param>
/// <param name="CardNumber">Its card, or null for a feature's run, a setup's or a check.</param>
/// <param name="FeatureNumber">Its feature, or null for a card's run, a setup's or a check.</param>
/// <param name="Title">The card's or the feature's title as last read, or null with neither.</param>
/// <param name="FixRound">A fix's round, of a card or of a whole feature, or null.</param>
/// <param name="Model">The model its job was handed out with, null for a check, which runs
/// none.</param>
/// <param name="StartedAt">When it was handed out.</param>
/// <param name="Tokens">Its tokens so far by the one definition, <see cref="Usage.Tokens"/>.</param>
/// <param name="CacheReads">Its cache reads so far, shown beside the tokens, never in them.</param>
public sealed record RunningRun(
    Guid RunId,
    Guid ProjectId,
    string ProjectName,
    Guid WorkerId,
    string WorkerName,
    string Kind,
    string State,
    bool Stopping,
    int? CardNumber,
    int? FeatureNumber,
    string? Title,
    int? FixRound,
    string? Model,
    DateTimeOffset StartedAt,
    long Tokens,
    long CacheReads);

/// <summary>What <c>GET /devflow/api/running</c> answers, and each <c>running</c> event of its
/// stream.</summary>
/// <param name="Runs">Every run under way of the caller's projects, oldest first.</param>
/// <param name="BusyWorkers">How many workers hold them, which the tab carries.</param>
/// <param name="Next">The cards that come next, "Next up", in the order work starts, at most
/// <see cref="NextUp.Most"/>.</param>
/// <param name="NextMore">How many more come after them.</param>
public sealed record RunningList(IReadOnlyList<RunningRun> Runs, int BusyWorkers, IReadOnlyList<NextCard> Next, int NextMore);

/// <summary>
/// The runs under way of a user's projects, read through the door as the user, so another user's
/// run is never one. See "The pages" and "Jobs" in devflow/README.md.
/// </summary>
public static class RunningStore
{
    /// <summary>Every run under way of <paramref name="userId"/>'s projects, oldest first, with how
    /// many workers hold them, and the cards that come next at <paramref name="now"/>, by
    /// <see cref="NextUp"/> with <paramref name="image"/> the base image released.</summary>
    public static async Task<RunningList> For(UserDb database, Guid userId, string image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT r.id AS RunId, r.project_id AS ProjectId, p.name AS ProjectName, r.worker_id AS WorkerId, w.name AS WorkerName,
                   r.kind AS Kind, CASE WHEN r.checking_at IS NULL THEN 'running' ELSE 'checking' END AS State,
                   r.stop_requested_at IS NOT NULL AS Stopping, r.card_number AS CardNumber, r.feature_number AS FeatureNumber,
                   coalesce(c.title, f.title) AS Title, r.fix_round AS FixRound, r.model AS Model, r.started_at AS StartedAt,
                   {Usage.Tokens.Sql("r")} AS Tokens, {Usage.Tokens.CacheReadsSql("r")} AS CacheReads
            FROM runs r
            JOIN projects p ON p.id = r.project_id
            JOIN workers w ON w.project_id = r.project_id AND w.id = r.worker_id
            LEFT JOIN cards c ON c.project_id = r.project_id AND c.number = r.card_number
            LEFT JOIN features f ON f.project_id = r.project_id AND f.number = r.feature_number
            WHERE r.finished_at IS NULL
            ORDER BY r.started_at, r.id
            """,
            transaction: transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        var runs = rows
            .Select(row => new RunningRun(row.RunId, row.ProjectId, row.ProjectName, row.WorkerId, row.WorkerName, row.Kind, row.State, row.Stopping, row.CardNumber, row.FeatureNumber, row.Title, row.FixRound, row.Model, GitHubLink.Utc(row.StartedAt), row.Tokens, row.CacheReads))
            .ToList();
        var next = await NextUp.For(database, userId, image, now, cancellationToken);
        return new RunningList(runs, runs.Select(run => (run.ProjectId, run.WorkerId)).Distinct().Count(), next.Cards, next.More);
    }

    private sealed record Row(Guid RunId, Guid ProjectId, string ProjectName, Guid WorkerId, string WorkerName, string Kind, string State, bool Stopping, int? CardNumber, int? FeatureNumber, string? Title, int? FixRound, string? Model, DateTime StartedAt, long Tokens, long CacheReads);
}
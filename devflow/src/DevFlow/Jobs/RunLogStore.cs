using Dapper;

using DevFlow.Protocol;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A line of a run's log as Dev flow keeps it: its rules checked, scrubbed and cut.</summary>
/// <param name="Seq">Its place in the run's log, from 1.</param>
/// <param name="At">When the worker wrote it, to the microsecond, as Postgres keeps it.</param>
/// <param name="Kind">One of <see cref="LogKinds.All"/>.</param>
/// <param name="Text">1 to <see cref="LogLine.MostText"/> characters.</param>
public sealed record RunLogLine(long Seq, DateTimeOffset At, string Kind, string Text);

/// <summary>A run's token figures so far, each checked to be 0 or more.</summary>
public sealed record RunUsageSoFar(long InputTokens, long CacheCreationInputTokens, long CacheReadInputTokens, long OutputTokens);

/// <summary>
/// A run's log, through the door as the project's user: each post from the worker holding the run
/// under way is its heartbeat, sets its usage so far when it carries one, and stores the lines the
/// run does not hold yet, at most <see cref="MostLines"/> a run. See "Jobs" in devflow/README.md and
/// "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunLogStore
{
    /// <summary>The most lines a run keeps, besides the one that says the rest was not.</summary>
    public const int MostLines = 20_000;

    /// <summary>The <see cref="LogKinds.System"/> line that takes the place of every line past
    /// <see cref="MostLines"/>.</summary>
    public const string NotKept = "The rest of this run's log was not kept";

    /// <summary>
    /// Records a post to the run's log, in one transaction, when <paramref name="workerId"/> holds
    /// the run under way: sets its <c>last_heard_at</c> to <paramref name="now"/>, and its token
    /// figures to <paramref name="usage"/> when given; then stores each of <paramref name="lines"/>
    /// whose seq the run does not hold yet, the first of a seq given twice, until the run holds
    /// <see cref="MostLines"/>, and in place of the first line past that one
    /// <see cref="NotKept"/>, after which nothing more. The lines stored, by seq, which may be none;
    /// null when the worker holds no such run.
    /// </summary>
    public static async Task<IReadOnlyList<RunLogLine>?> Heard(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, IReadOnlyList<RunLogLine> lines, RunUsageSoFar? usage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(lines);
        return await database.InUserTransaction<IReadOnlyList<RunLogLine>?>(userId, async transaction =>
        {
            // Takes the run's row, so two posts for one run store their lines one after the other,
            // and a result being recorded waits, or is waited for.
            var held = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE runs SET last_heard_at = @now,
                    input_tokens = coalesce(@inputTokens, input_tokens),
                    cache_creation_input_tokens = coalesce(@cacheCreation, cache_creation_input_tokens),
                    cache_read_input_tokens = coalesce(@cacheRead, cache_read_input_tokens),
                    output_tokens = coalesce(@outputTokens, output_tokens)
                WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND finished_at IS NULL
                """,
                new
                {
                    runId,
                    projectId,
                    workerId,
                    now,
                    inputTokens = usage?.InputTokens,
                    cacheCreation = usage?.CacheCreationInputTokens,
                    cacheRead = usage?.CacheReadInputTokens,
                    outputTokens = usage?.OutputTokens,
                },
                transaction,
                cancellationToken: cancellationToken));
            if (held == 0)
            {
                return null;
            }

            if (lines.Count == 0)
            {
                return [];
            }

            var seqs = lines.Select(line => line.Seq).Distinct().ToArray();
            var count = await transaction.Connection!.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM run_log_lines WHERE run_id = @runId",
                new { runId },
                transaction,
                cancellationToken: cancellationToken));
            if (count > MostLines)
            {
                // Past the most, with the line that says so already held.
                return [];
            }

            var known = (await transaction.Connection!.QueryAsync<long>(new CommandDefinition(
                "SELECT seq FROM run_log_lines WHERE run_id = @runId AND seq = ANY(@seqs)",
                new { runId, seqs },
                transaction,
                cancellationToken: cancellationToken))).ToHashSet();
            var fresh = lines
                .Where(line => !known.Contains(line.Seq))
                .DistinctBy(line => line.Seq)
                .OrderBy(line => line.Seq)
                .ToList();
            var room = (int)(MostLines - count);
            var stored = fresh.Take(room).ToList();
            if (fresh.Count > room)
            {
                stored.Add(new RunLogLine(fresh[room].Seq, Microseconds(now), LogKinds.System, NotKept));
            }

            if (stored.Count == 0)
            {
                return [];
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO run_log_lines (project_id, user_id, run_id, seq, at, kind, text)
                SELECT @projectId, @userId, @runId, line.seq, line.at, line.kind, line.text
                FROM unnest(@seqs, @ats, @kinds, @texts) AS line (seq, at, kind, text)
                ON CONFLICT (run_id, seq) DO NOTHING
                """,
                new
                {
                    projectId,
                    userId,
                    runId,
                    seqs = stored.Select(line => line.Seq).ToArray(),
                    ats = stored.Select(line => line.At.ToUniversalTime()).ToArray(),
                    kinds = stored.Select(line => line.Kind).ToArray(),
                    texts = stored.Select(line => line.Text).ToArray(),
                },
                transaction,
                cancellationToken: cancellationToken));
            return stored;
        }, cancellationToken);
    }

    /// <summary><paramref name="time"/> to the microsecond, as Postgres keeps it, so a line handed
    /// on is the line stored.</summary>
    public static DateTimeOffset Microseconds(DateTimeOffset time) => time.AddTicks(-(time.Ticks % 10));
}
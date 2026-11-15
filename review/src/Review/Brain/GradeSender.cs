using Dapper;

using PlatformData;

using Review.Scheduling;

namespace Review.Brain;

/// <summary>
/// Sends a user's grades to the brain, after their requests, so grading never waits for it and a
/// grade is never lost while it is down. See "What Review tells the brain" in review/README.md.
///
/// After any authenticated request under <c>/review/api/</c>, a grade's own included,
/// <see cref="Schedule"/> starts a send in the background with that request's token: the user's
/// unsent grades, oldest first, at most <see cref="Batch"/>, each with its own events. The token is
/// the user's, since the brain accepts only that, so a user's grades go only after their own
/// requests, and only through the door as them. One send per user runs at a time; a request
/// arriving during one asks for another after it, with the newer token, so a grade logged while a
/// send was reading is not left until the next request.
///
/// A grade every event of which the brain recorded is marked sent. One the brain refused for good
/// is marked refused with the status, logged once, and not sent again. When the brain is
/// unavailable, the grade stays unsent and the send stops there, since the next grade would meet
/// the same brain; the next request tries again.
/// </summary>
internal sealed partial class GradeSender(UserDb db, IBrain brain, TimeProvider time, ILogger<GradeSender> log) : IAsyncDisposable
{
    /// <summary>The most grades one send takes.</summary>
    public const int Batch = 50;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Running> _running = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Sends <paramref name="user"/>'s unsent grades with <paramref name="token"/>, in
    /// the background, or asks the send already running for them to go again after it.</summary>
    public void Schedule(Guid user, string token)
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            if (_running.TryGetValue(user, out var running))
            {
                running.Again = token;
                return;
            }

            running = new Running();
            _running[user] = running;
            // Assigned under the lock, so the send's own removal, which takes it, comes after.
            running.Task = Task.Run(() => Loop(user, token, running));
        }
    }

    /// <summary>Done when every send running now, and every one it asked for after it, has
    /// finished. For tests.</summary>
    internal Task WhenIdle()
    {
        lock (_lock)
        {
            return Task.WhenAll(_running.Values.Select(running => running.Task));
        }
    }

    /// <summary>Stops every send, which leaves its grades unsent for later, and waits for them.</summary>
    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_lock)
        {
            _stopping.Cancel();
            running = Task.WhenAll(_running.Values.Select(r => r.Task));
        }

        await running;
        _stopping.Dispose();
    }

    private async Task Loop(Guid user, string token, Running running)
    {
        while (true)
        {
            try
            {
                await SendUnsent(user, token, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Stopping: what was not sent stays unsent.
            }
            catch (Exception failed)
            {
                // The database, most likely. Caught whatever it is, since a send that ended any
                // other way would stay in _running and no later request would start one: the
                // grades stay unsent for the next request.
                LogFailed(log, user, failed);
            }

            lock (_lock)
            {
                if (running.Again is not { } again || _stopping.IsCancellationRequested)
                {
                    _running.Remove(user);
                    return;
                }

                token = again;
                running.Again = null;
            }
        }
    }

    /// <summary>One send: the oldest unsent grades, each to the brain and then marked.</summary>
    private async Task SendUnsent(Guid user, string token, CancellationToken ct)
    {
        foreach (var grade in await Unsent(user, ct))
        {
            int? refused = null;
            foreach (var assessment in AssessmentEvents.For(grade))
            {
                switch (await brain.RecordEvent(token, assessment, ct))
                {
                    case BrainAnswer.RecordedAnswer:
                        break;
                    case BrainAnswer.RefusedAnswer refusal:
                        // The other topics' events are still sent: each stands alone.
                        refused ??= refusal.Status;
                        break;
                    case BrainAnswer.UnavailableAnswer unavailable:
                        LogUnavailable(log, grade.Id, unavailable.Why);
                        return;
                }
            }

            await Mark(user, grade.Id, refused, ct);
        }
    }

    /// <summary>The user's oldest grades neither sent nor refused, with their card's deck, front
    /// and topics as they are now.</summary>
    private Task<List<GradeToSend>> Unsent(Guid user, CancellationToken ct) =>
        db.InUserTransaction(user, async transaction =>
        {
            var rows = await transaction.Connection!.QueryAsync<UnsentRow>(new CommandDefinition(
                """
                SELECT g.id AS Id, g.card_id AS CardId, c.deck_id AS DeckId, d.name AS DeckName, c.front AS Front,
                       ARRAY(SELECT t.slug FROM card_topics t WHERE t.user_id = c.user_id AND t.card_id = c.id ORDER BY t.position) AS Topics,
                       g.rating AS Rating, g.reviewed_at AS ReviewedAt, g.elapsed_days AS ElapsedDays,
                       g.state_before AS StateBefore, g.stability_before AS StabilityBefore,
                       g.stability_after AS StabilityAfter, g.difficulty_after AS DifficultyAfter, g.due_after AS DueAfter
                FROM grades g
                JOIN cards c ON c.user_id = g.user_id AND c.id = g.card_id
                JOIN decks d ON d.user_id = c.user_id AND d.id = c.deck_id
                WHERE g.brain_sent_at IS NULL AND g.brain_refused_at IS NULL
                ORDER BY g.reviewed_at, g.id
                LIMIT @batch
                """,
                new { batch = Batch }, transaction, cancellationToken: ct));
            return rows.Select(row => row.ToGrade()).ToList();
        }, ct);

    /// <summary>Marks the grade sent, or refused with <paramref name="refused"/>, unless it was
    /// marked already, by a send that overlapped; only the one that marks it logs a refusal.</summary>
    private async Task Mark(Guid user, Guid grade, int? refused, CancellationToken ct)
    {
        var now = GradeEndpoints.Now(time);
        var changed = await db.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            refused is null
                ? "UPDATE grades SET brain_sent_at = @now WHERE id = @grade AND brain_sent_at IS NULL AND brain_refused_at IS NULL"
                : """
                  UPDATE grades SET brain_refused_at = @now, brain_refused_status = @status
                  WHERE id = @grade AND brain_sent_at IS NULL AND brain_refused_at IS NULL
                  """,
            new { grade, now, status = (short?)refused }, transaction, cancellationToken: ct)), ct);
        if (refused is { } status && changed == 1)
        {
            LogRefused(log, grade, status);
        }
    }

    // Never the token: only the grade's id, the user's id and what went wrong.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The brain refused grade {Grade} with status {Status}; it will not be sent again")]
    private static partial void LogRefused(ILogger logger, Guid grade, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "The brain is unavailable for grade {Grade} ({Why}); it stays unsent until the user's next request")]
    private static partial void LogUnavailable(ILogger logger, Guid grade, string why);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending the grades of user {User} failed; they stay unsent")]
    private static partial void LogFailed(ILogger logger, Guid user, Exception failed);

    /// <summary>A user's send while it runs, and the token of a request that asked for another.</summary>
    private sealed class Running
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public string? Again { get; set; }
    }

    /// <summary>A row as the query reads it, the times in UTC as Npgsql gives them.</summary>
    private sealed class UnsentRow
    {
        public Guid Id { get; init; }

        public Guid CardId { get; init; }

        public Guid DeckId { get; init; }

        public string DeckName { get; init; } = "";

        public string Front { get; init; } = "";

        public string[] Topics { get; init; } = [];

        public short Rating { get; init; }

        public DateTime ReviewedAt { get; init; }

        public double ElapsedDays { get; init; }

        public string StateBefore { get; init; } = "";

        public double? StabilityBefore { get; init; }

        public double StabilityAfter { get; init; }

        public double DifficultyAfter { get; init; }

        public DateTime DueAfter { get; init; }

        public GradeToSend ToGrade() => new(
            Id, CardId, DeckId, DeckName, Front, Topics, Rating, new DateTimeOffset(ReviewedAt, TimeSpan.Zero),
            ElapsedDays, StateBefore, StabilityBefore, StabilityAfter, DifficultyAfter, new DateTimeOffset(DueAfter, TimeSpan.Zero));
    }
}
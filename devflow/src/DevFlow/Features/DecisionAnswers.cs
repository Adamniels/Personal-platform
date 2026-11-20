using Dapper;

using DevFlow.Cards;

using PlatformData;

namespace DevFlow.Features;

/// <summary>What answering a decision came to.</summary>
public abstract record AnswerOutcome
{
    private AnswerOutcome()
    {
    }

    /// <summary>The answer kept, as first given.</summary>
    public sealed record Kept(DecisionAnswer Answer) : AnswerOutcome;

    /// <summary>The user has no such project or feature, or no card of it has that decision marked
    /// for them in that run's report.</summary>
    public sealed record NoSuchDecision : AnswerOutcome;

    /// <summary>The run's report is no longer the card's latest, so the page that showed it is
    /// older than what the card says now.</summary>
    public sealed record Superseded : AnswerOutcome;
}

/// <summary>
/// The owner's answers to the decisions a feature's cards marked for him, in
/// <c>decision_answers</c>, through the door as the user. Kept in Dev flow alone: nothing here
/// reaches the code host. See "A feature's page" in devflow/README.md and why in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class DecisionAnswers
{
    /// <summary>The answer <c>fine</c>, "Fine as it is", to the decision at
    /// <paramref name="position"/> of the report of <paramref name="runId"/>, a run of a card of
    /// the user's feature <paramref name="feature"/>, when that decision is marked for the owner
    /// and that report is still the card's latest, the one its page shows, as
    /// <see cref="CardStory.Read"/> reads it. Kept once, in one transaction with what it checked:
    /// an answer already kept for it, whatever it is, is kept as it was.</summary>
    public static async Task<AnswerOutcome> Fine(UserDb database, Guid userId, Guid projectId, int feature, Guid runId, int position, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<AnswerOutcome>(userId, async transaction =>
        {
            var card = await transaction.Connection!.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                """
                SELECT r.card_number FROM runs r
                JOIN cards c ON c.project_id = r.project_id AND c.number = r.card_number
                JOIN features f ON f.project_id = c.project_id AND f.id = c.feature_id
                WHERE r.project_id = @projectId AND r.id = @runId AND f.number = @feature AND r.kind IN ('build', 'fix')
                """,
                new { projectId, runId, feature },
                transaction,
                cancellationToken: cancellationToken));
            if (card is not { } number)
            {
                return new AnswerOutcome.NoSuchDecision();
            }

            var reports = await transaction.Connection!.QueryAsync<(Guid Id, string? Report)>(new CommandDefinition(
                """
                SELECT id, report::text FROM runs
                WHERE project_id = @projectId AND card_number = @number AND kind IN ('build', 'fix')
                ORDER BY started_at DESC, id DESC
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            var read = reports.Select(each => (each.Id, Report: CardStory.Read(each.Report))).Where(each => each.Report is not null).ToList();
            var marked = read.FirstOrDefault(each => each.Id == runId).Report?.Decisions is { } decisions
                && position >= 0 && position < decisions.Count && decisions[position].ForOwner;
            if (!marked)
            {
                return new AnswerOutcome.NoSuchDecision();
            }

            if (read[0].Id != runId)
            {
                return new AnswerOutcome.Superseded();
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO decision_answers (project_id, user_id, card_number, run_id, position, answer, answered_at)
                VALUES (@projectId, @userId, @number, @runId, @position, 'fine', @now)
                ON CONFLICT (project_id, run_id, position) DO NOTHING
                """,
                new { projectId, userId, number, runId, position, now = now.AddTicks(-(now.Ticks % 10)) },
                transaction,
                cancellationToken: cancellationToken));
            var kept = await transaction.Connection!.QuerySingleAsync<(string Answer, DateTime AnsweredAt)>(new CommandDefinition(
                "SELECT answer, answered_at FROM decision_answers WHERE project_id = @projectId AND run_id = @runId AND position = @position",
                new { projectId, runId, position },
                transaction,
                cancellationToken: cancellationToken));
            return new AnswerOutcome.Kept(new DecisionAnswer(kept.Answer, new DateTimeOffset(DateTime.SpecifyKind(kept.AnsweredAt, DateTimeKind.Utc))));
        }, cancellationToken);
    }
}
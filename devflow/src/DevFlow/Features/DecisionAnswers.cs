using Dapper;

using DevFlow.Cards;

using Npgsql;

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

/// <summary>A decision of a feature's card looked up to be answered.</summary>
public abstract record DecisionFound
{
    private DecisionFound()
    {
    }

    /// <summary>The decision the feature's page shows.</summary>
    /// <param name="Card">The card whose report it is in.</param>
    /// <param name="Chosen">What the agent chose.</param>
    /// <param name="Alternative">What it chose against.</param>
    /// <param name="Answered">Whether an answer is kept for it already.</param>
    /// <param name="Sendable">Whether its card can be sent back now, by
    /// <see cref="SendingBack.Sendable"/>.</param>
    public sealed record Shown(int Card, string Chosen, string Alternative, bool Answered, bool Sendable) : DecisionFound;

    /// <summary>The user has no such project or feature, or no card of it has that decision marked
    /// for them in that run's report.</summary>
    public sealed record NoSuchDecision : DecisionFound;

    /// <summary>The run's report is no longer the card's latest.</summary>
    public sealed record Superseded : DecisionFound;
}

/// <summary>
/// The owner's answers to the decisions a feature's cards marked for him, in
/// <c>decision_answers</c>, through the door as the user. Kept in Dev flow alone: nothing here
/// reaches the code host. A send back's comment is posted by its route, as the owner, before its
/// answer is kept. See "A feature's page" and "Sending a card back" in devflow/README.md and why in
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
            await Find(transaction, projectId, feature, runId, position, cancellationToken) switch
            {
                DecisionFound.Shown shown => new AnswerOutcome.Kept(await Keep(transaction, projectId, userId, shown.Card, runId, position, "fine", now, cancellationToken)),
                DecisionFound.Superseded => new AnswerOutcome.Superseded(),
                _ => new AnswerOutcome.NoSuchDecision(),
            }, cancellationToken);
    }

    /// <summary>
    /// The decision at <paramref name="position"/> of the report of <paramref name="runId"/>, by the
    /// same rules as <see cref="Fine"/>, to send its card back with: what was chosen and the
    /// alternative, the card, whether the decision is already answered, and whether its card can be
    /// sent back now, by <see cref="SendingBack.Sendable"/>. Writes nothing: the send back is the
    /// owner's comment on GitHub, and its answer is kept by <see cref="SentBack"/> once it is
    /// posted.
    /// </summary>
    public static async Task<DecisionFound> ToSendBack(UserDb database, Guid userId, Guid projectId, int feature, Guid runId, int position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => Find(transaction, projectId, feature, runId, position, cancellationToken), cancellationToken);
    }

    /// <summary>The answer <c>sent_back</c> to the decision, kept once its card's send back was
    /// posted; an answer already kept for it is kept as it was.</summary>
    public static async Task<DecisionAnswer> SentBack(UserDb database, Guid userId, Guid projectId, int card, Guid runId, int position, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => Keep(transaction, projectId, userId, card, runId, position, "sent_back", now, cancellationToken), cancellationToken);
    }

    /// <summary>The decision as the feature's page can show it: its run a build or fix of a card of
    /// the feature, its report's decision at the position marked for the owner, and that report the
    /// card's latest that reads.</summary>
    private static async Task<DecisionFound> Find(NpgsqlTransaction transaction, Guid projectId, int feature, Guid runId, int position, CancellationToken cancellationToken)
    {
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
            $"""
            SELECT r.card_number AS Number, {SendingBack.Sendable} AS Sendable
            FROM runs r
            JOIN cards ON cards.project_id = r.project_id AND cards.number = r.card_number
            JOIN features f ON f.project_id = cards.project_id AND f.id = cards.feature_id
            WHERE r.project_id = @projectId AND r.id = @runId AND f.number = @feature AND r.kind IN ('build', 'fix')
            """,
            new { projectId, runId, feature },
            transaction,
            cancellationToken: cancellationToken));
        if (card is null)
        {
            return new DecisionFound.NoSuchDecision();
        }

        var number = card.Number;
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
        if (read.FirstOrDefault(each => each.Id == runId).Report?.Decisions is not { } decisions
            || position < 0 || position >= decisions.Count || !decisions[position].ForOwner)
        {
            return new DecisionFound.NoSuchDecision();
        }

        if (read[0].Id != runId)
        {
            return new DecisionFound.Superseded();
        }

        var answered = await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM decision_answers WHERE project_id = @projectId AND run_id = @runId AND position = @position)",
            new { projectId, runId, position },
            transaction,
            cancellationToken: cancellationToken));
        return new DecisionFound.Shown(number, decisions[position].Chosen, decisions[position].Alternative, answered, card.Sendable);
    }

    /// <summary>Keeps <paramref name="answer"/> to the decision unless one is kept already, and
    /// answers the one kept.</summary>
    private static async Task<DecisionAnswer> Keep(NpgsqlTransaction transaction, Guid projectId, Guid userId, int card, Guid runId, int position, string answer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO decision_answers (project_id, user_id, card_number, run_id, position, answer, answered_at)
            VALUES (@projectId, @userId, @card, @runId, @position, @answer, @now)
            ON CONFLICT (project_id, run_id, position) DO NOTHING
            """,
            new { projectId, userId, card, runId, position, answer, now = now.AddTicks(-(now.Ticks % 10)) },
            transaction,
            cancellationToken: cancellationToken));
        var kept = await transaction.Connection!.QuerySingleAsync<(string Answer, DateTime AnsweredAt)>(new CommandDefinition(
            "SELECT answer, answered_at FROM decision_answers WHERE project_id = @projectId AND run_id = @runId AND position = @position",
            new { projectId, runId, position },
            transaction,
            cancellationToken: cancellationToken));
        return new DecisionAnswer(kept.Answer, new DateTimeOffset(DateTime.SpecifyKind(kept.AnsweredAt, DateTimeKind.Utc)));
    }

    private sealed record CardRow(int Number, bool Sendable);
}
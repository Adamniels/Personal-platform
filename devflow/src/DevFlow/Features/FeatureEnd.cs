using System.Globalization;

using Dapper;

using Npgsql;

namespace DevFlow.Features;

/// <summary>A card of a feature as its end reads it: its number and state, in the feature's
/// order.</summary>
public sealed record EndCard(int Number, string State);

/// <summary>
/// When a feature's end is due, decided from what Dev flow stored and nothing else: a feature
/// whose cards are all <c>done</c> moves from <c>building</c> to <c>bringing_in</c>; one waiting
/// for the owner, or ready, moves there again when the default branch, as its watch last named it,
/// has moved past the commit last brought in and the one bringing in last failed at; and a card not
/// done sends a feature past building back to <c>building</c>, with the first card that holds its
/// end named, a reviewing one among them, whose review then reads a branch about to change. A
/// feature resolving, fixing or bringing in is left as it is, and a reviewing one whose cards are
/// all done: a conflict's job ends <c>resolving</c>, and a review's <c>reviewing</c>, see
/// <see cref="Jobs.RunStore"/>. The read of a
/// project runs it, inside its transaction, and so does the end's minute before it acts, see
/// <see cref="FeatureEnds"/>, so a feature's end never waits for the next read. It writes nothing
/// to the code host. See "The end of a feature" in devflow/README.md and
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class FeatureEnd
{
    /// <summary>The states of a card that is not done and holds its feature's end, since nothing
    /// moves it on without the owner.</summary>
    private static readonly string[] Holding = ["stopped", "held", "waiting_for_owner", "ignored"];

    /// <summary>The states of a feature whose end this decides: building, waiting for the owner or
    /// ready at its end, and reviewing, which only a card not done moves.</summary>
    private static readonly string[] Decided = ["building", "waiting_for_owner", "ready", "reviewing"];

    /// <summary>What a feature says while an agent resolves the default branch's conflict with its
    /// branch.</summary>
    public static string Conflict(string defaultBranch) => $"{defaultBranch} conflicts with the feature's branch; an agent resolves it";

    /// <summary>Why a feature waits for the owner when its conflict job failed in the job:
    /// <paramref name="reason"/> is its run's.</summary>
    public static string NotResolved(string defaultBranch, string reason) =>
        $"Dev flow could not resolve the conflict with {defaultBranch}: {reason}";

    /// <summary>Why a feature waits for the owner when the code host refused to bring the default
    /// branch in.</summary>
    public static string Refused(string defaultBranch, int status) =>
        string.Create(CultureInfo.InvariantCulture, $"GitHub refused to bring {defaultBranch} into the feature's branch, with {status}");

    /// <summary>Why a feature's end waits for a card that is not done.</summary>
    public static string HeldBy(int card, string state) =>
        string.Create(CultureInfo.InvariantCulture, $"Its end waits for #{card}, which {Word(state)}");

    private static string Word(string state) => state switch
    {
        "stopped" => "is stopped",
        "held" => "is held",
        "waiting_for_owner" => "waits for you",
        "ignored" => "is ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a state that holds a feature's end"),
    };

    /// <summary>
    /// The state and reason a feature moves to, or null when it stays as it is. <paramref name="cards"/>
    /// are its cards in its order; <paramref name="broughtIn"/> the default branch's commit last
    /// brought in, <paramref name="failedAt"/> the one bringing in last failed at, and
    /// <paramref name="defaultHead"/> the default branch's commit as its watch last named it, each
    /// null when not known. A feature with no card is left alone.
    /// </summary>
    public static (string State, string? Reason)? Next(string state, string? reason, IReadOnlyList<EndCard> cards, string? broughtIn, string? failedAt, string? defaultHead)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0 || !Decided.Contains(state, StringComparer.Ordinal))
        {
            return null;
        }

        (string State, string? Reason) next;
        if (cards.Any(card => card.State != "done"))
        {
            // Building, or back to it when a card was added or sent back; the first card that
            // nothing moves on without the owner is named, since the end waits for it.
            var holder = cards.FirstOrDefault(card => Holding.Contains(card.State, StringComparer.Ordinal));
            next = ("building", holder is null ? null : HeldBy(holder.Number, holder.State));
        }
        else if (state == "reviewing")
        {
            // Reviewing with every card done: its review goes on, and the default branch is brought
            // in again once it waits for the owner.
            return null;
        }
        else if (state == "building")
        {
            next = ("bringing_in", null);
        }
        else if (defaultHead is not null && defaultHead != broughtIn && defaultHead != failedAt)
        {
            // Waiting for the owner, or ready, while the default branch has moved: brought in again.
            next = ("bringing_in", null);
        }
        else
        {
            return null;
        }

        return next == (state, reason) ? null : next;
    }

    /// <summary>
    /// Moves each of the project's features whose branch is made as <see cref="Next"/> says, inside
    /// <paramref name="transaction"/>, which runs as the project's user. Each change is made only
    /// while the feature is still in the state it was read in, so one the end's minute moved
    /// meanwhile is left to it.
    /// </summary>
    public static async Task Advance(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var features = (await transaction.Connection!.QueryAsync<FeatureRow>(new CommandDefinition(
            """
            SELECT id AS Id, state AS State, reason AS Reason, brought_in_commit AS BroughtIn, bring_in_failed_at AS FailedAt
            FROM features
            WHERE project_id = @projectId AND branch_base_commit IS NOT NULL AND state = ANY (@decided)
            ORDER BY number
            """,
            new { projectId, decided = Decided },
            transaction,
            cancellationToken: cancellationToken))).ToList();
        if (features.Count == 0)
        {
            return;
        }

        var cards = (await transaction.Connection!.QueryAsync<CardRow>(new CommandDefinition(
            "SELECT feature_id AS FeatureId, number AS Number, state AS State FROM cards WHERE project_id = @projectId AND feature_id = ANY (@ids) ORDER BY position",
            new { projectId, ids = features.Select(feature => feature.Id).ToArray() },
            transaction,
            cancellationToken: cancellationToken))).ToLookup(card => card.FeatureId);
        var head = await transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT last_commit FROM branch_watches WHERE project_id = @projectId AND feature_id IS NULL",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var feature in features)
        {
            var theirs = cards[feature.Id].Select(card => new EndCard(card.Number, card.State)).ToList();
            if (Next(feature.State, feature.Reason, theirs, feature.BroughtIn, feature.FailedAt, head) is not { } next)
            {
                continue;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE features SET state = @state, reason = @reason, changed_at = @now WHERE id = @id AND state = @before",
                new { id = feature.Id, before = feature.State, state = next.State, reason = next.Reason, now },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    private sealed record FeatureRow(Guid Id, string State, string? Reason, string? BroughtIn, string? FailedAt);

    private sealed record CardRow(Guid FeatureId, int Number, string State);
}
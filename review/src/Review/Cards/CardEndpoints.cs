using System.Globalization;
using System.Text;

using Dapper;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

using Review.Api;
using Review.Database;
using Review.Scheduling;

namespace Review.Cards;

/// <summary>
/// <c>/review/api/cards</c>: a user's cards, made, listed, read, changed and deleted. A change never
/// touches the schedule, and names the version it was made from, so two tabs cannot silently
/// overwrite each other. See "What a card is" and "What it serves" in review/README.md.
/// </summary>
public static class CardEndpoints
{
    /// <summary>The most cards one list answers.</summary>
    public const int ListMax = 500;

    // A card as answered, its deck's name and its topics in order beside it, in one statement; the
    // policies narrow every table to the user. Each column is named for a property of Row. The
    // queue and grading read cards with it too.
    internal const string Select = """
        SELECT c.id AS Id, c.deck_id AS DeckId, d.name AS DeckName, c.front AS Front, c.back AS Back,
               c.source_path AS SourcePath, c.source_title AS SourceTitle, c.state AS State, c.step AS Step,
               c.stability AS Stability, c.difficulty AS Difficulty, c.due AS Due, c.last_review AS LastReview, c.version AS Version, c.created_at AS CreatedAt, c.updated_at AS UpdatedAt,
               ARRAY(SELECT t.slug FROM card_topics t WHERE t.user_id = c.user_id AND t.card_id = c.id ORDER BY t.position) AS TopicSlugs,
               ARRAY(SELECT t.name FROM card_topics t WHERE t.user_id = c.user_id AND t.card_id = c.id ORDER BY t.position) AS TopicNames
        FROM cards c
        JOIN decks d ON d.user_id = c.user_id AND d.id = c.deck_id
        """;

    /// <summary>The cards of one deck, <c>@deck</c>, about one topic, <c>@topic</c>, both or
    /// neither, each null for any; the queue narrows the same way.</summary>
    internal const string Narrowed = """
        (@deck::uuid IS NULL OR c.deck_id = @deck)
          AND (@topic::text IS NULL OR EXISTS (
              SELECT FROM card_topics t WHERE t.user_id = c.user_id AND t.card_id = c.id AND t.slug = @topic))
        """;

    public static IEndpointRouteBuilder MapCards(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var cards = routes.MapGroup("/review/api/cards");

        cards.MapGet("", List)
            .UserRoute("listCards", "The cards, newest first", $"At most {ListMax}, each as a single read answers it, narrowed to a deck by ?deck=<id>, to a topic by ?topic=<slug>, or both. 422 for a topic that is no slug.",
                StatusCodes.Status422UnprocessableEntity);

        cards.MapPost("", Create)
            .UserRoute("createCard", "Make a card", "201 with the card, new and due now. 422 naming the field for a rule it breaks, a deck the user does not have included.",
                StatusCodes.Status422UnprocessableEntity);

        cards.MapGet("/{id:guid}", Get)
            .UserRoute("getCard", "One card", "404 for a card the user does not have.",
                StatusCodes.Status404NotFound);

        cards.MapPatch("/{id:guid}", Change)
            .UserRoute("changeCard", "Change a card", "Changes the fields given, topics replaced whole, never the schedule, and moves the version on. 404 for a card the user does not have, 409 for a version that is not the card's, 422 naming the field for a rule it breaks.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        cards.MapDelete("/{id:guid}", Delete)
            .UserRoute("deleteCard", "Delete a card", "204, its topics with it. 404 for a card the user does not have.",
                StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Results<Ok<Card[]>, ProblemHttpResult>> List(
        [FromQuery] Guid? deck, [FromQuery] string? topic, HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        if (topic is not null && !CardRules.IsSlug(topic))
        {
            var broken = new Broken();
            broken.Add("topic", "must be a topic's slug");
            return broken.Problem();
        }

        var rows = await db.InUserTransaction(context.UserId(), transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"""
            {Select}
            WHERE {Narrowed}
            ORDER BY c.created_at DESC, c.id DESC
            LIMIT {ListMax}
            """,
            new { deck, topic }, transaction, cancellationToken: cancellationToken)), cancellationToken);
        return TypedResults.Ok(rows.Select(row => row.Card).ToArray());
    }

    private static async Task<Results<Ok<Card>, ProblemHttpResult>> Get(
        Guid id, HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        var card = await db.InUserTransaction(context.UserId(), transaction => Read(transaction, id, cancellationToken), cancellationToken);
        return card is null ? Problems.NotFound("the card") : TypedResults.Ok(card);
    }

    private static async Task<Results<Created<Card>, ProblemHttpResult>> Create(
        CardInput input, HttpContext context, ReviewDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        var broken = new Broken();
        var deck = CardRules.Deck(broken, input.DeckId);
        var front = CardRules.Front(broken, input.Front);
        var back = CardRules.Back(broken, input.Back);
        var topics = CardRules.Topics(broken, input.Topics);
        var source = CardRules.Source(broken, input.Source);
        if (broken.Any)
        {
            return broken.Problem();
        }

        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var user = context.UserId();
        try
        {
            var card = await db.InUserTransaction(user, async transaction =>
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO cards (id, user_id, deck_id, front, back, source_path, source_title, due, created_at, updated_at)
                    VALUES (@id, @user, @deck, @front, @back, @path, @title, @now, @now, @now)
                    """,
                    new { id, user, deck, front, back, path = source?.Path, title = source?.Title, now }, transaction, cancellationToken: cancellationToken));
                await WriteTopics(transaction, user, id, topics!, cancellationToken);
                return (await Read(transaction, id, cancellationToken))!;
            }, cancellationToken);
            return TypedResults.Created($"/review/api/cards/{id}", card);
        }
        catch (PostgresException missing) when (IsNotTheUsersDeck(missing))
        {
            return DeckNotFound();
        }
    }

    private static async Task<Results<Ok<Card>, ProblemHttpResult>> Change(
        Guid id, CardChange change, HttpContext context, ReviewDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        var broken = new Broken();
        if (change.Version is null)
        {
            broken.Add("version", "is required: the version of the card the change was made from");
        }

        // Each field given is held to its rule, and named in the statement below; the rest stay.
        var set = new StringBuilder("version = version + 1, updated_at = @now");
        var deck = change.Has(CardChange.Field.Deck) ? CardRules.Deck(broken, change.DeckId) : null;
        set.Append(deck is null ? "" : ", deck_id = @deck");
        var front = change.Has(CardChange.Field.Front) ? CardRules.Front(broken, change.Front) : null;
        set.Append(front is null ? "" : ", front = @front");
        var back = change.Has(CardChange.Field.Back) ? CardRules.Back(broken, change.Back) : null;
        set.Append(back is null ? "" : ", back = @back");
        var topics = change.Has(CardChange.Field.Topics) ? CardRules.Topics(broken, change.Topics) : null;
        var source = change.Has(CardChange.Field.Source) ? CardRules.Source(broken, change.Source) : null;
        set.Append(change.Has(CardChange.Field.Source) ? ", source_path = @path, source_title = @title" : "");
        if (broken.Any)
        {
            return broken.Problem();
        }

        var user = context.UserId();
        var now = time.GetUtcNow();
        try
        {
            var outcome = await db.InUserTransaction(user, async transaction =>
            {
                // Only from the version named: a change made from an older copy changes nothing.
                var changed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    $"UPDATE cards SET {set} WHERE id = @id AND version = @version",
                    new { id, version = change.Version, now, deck, front, back, path = source?.Path, title = source?.Title },
                    transaction, cancellationToken: cancellationToken));
                if (changed == 0)
                {
                    return (Card: (Card?)null, Exists: await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS (SELECT FROM cards WHERE id = @id)", new { id }, transaction, cancellationToken: cancellationToken)));
                }

                if (topics is not null)
                {
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM card_topics WHERE card_id = @id", new { id }, transaction, cancellationToken: cancellationToken));
                    await WriteTopics(transaction, user, id, topics, cancellationToken);
                }

                return (Card: await Read(transaction, id, cancellationToken), Exists: true);
            }, cancellationToken);

            return outcome switch
            {
                { Card: { } card } => TypedResults.Ok(card),
                { Exists: true } => Problems.Conflict(string.Create(CultureInfo.InvariantCulture,
                    $"the card has changed since version {change.Version}: read it again and make the change from there")),
                _ => Problems.NotFound("the card"),
            };
        }
        catch (PostgresException missing) when (IsNotTheUsersDeck(missing))
        {
            return DeckNotFound();
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        // Its topics go with it, ON DELETE CASCADE, and so will its grade log.
        var deleted = await db.InUserTransaction(context.UserId(), transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM cards WHERE id = @id", new { id }, transaction, cancellationToken: cancellationToken)), cancellationToken);
        return deleted == 0 ? Problems.NotFound("the card") : TypedResults.NoContent();
    }

    /// <summary>One card's row, its schedule included, or null for one the user does not have.</summary>
    internal static Task<Row?> ReadRow(NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken) =>
        transaction.Connection!.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"{Select} WHERE c.id = @id", new { id }, transaction, cancellationToken: cancellationToken));

    private static async Task<Card?> Read(NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken) =>
        (await ReadRow(transaction, id, cancellationToken))?.Card;

    private static Task<int> WriteTopics(NpgsqlTransaction transaction, Guid user, Guid card, CardTopic[] topics, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO card_topics (card_id, user_id, position, slug, name) VALUES (@card, @user, @position, @slug, @name)",
            topics.Select((topic, position) => new { card, user, position = (short)position, slug = topic.Slug, name = topic.Name }),
            transaction, cancellationToken: cancellationToken));

    // The key from (user_id, deck_id) to the user's own decks: a deck id that is not one of theirs,
    // another user's included, fails it, whether it never existed or was deleted a moment ago. The
    // same answer either way, so it never says whether a deck exists.
    private static bool IsNotTheUsersDeck(PostgresException error) =>
        error is { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "cards_user_id_deck_id_fkey" };

    private static ProblemHttpResult DeckNotFound()
    {
        var broken = new Broken();
        broken.Add("deck_id", "must be one of the user's decks");
        return broken.Problem();
    }

    /// <summary>A card as one row of <see cref="Select"/>, set by Dapper property by property, by
    /// name: a constructor would not do, since Npgsql types the topic arrays as plain arrays.</summary>
    internal sealed class Row
    {
        public Guid Id { get; init; }

        public Guid DeckId { get; init; }

        public string DeckName { get; init; } = "";

        public string Front { get; init; } = "";

        public string Back { get; init; } = "";

        public string? SourcePath { get; init; }

        public string? SourceTitle { get; init; }

        public string State { get; init; } = "";

        public int? Step { get; init; }

        public double? Stability { get; init; }

        public double? Difficulty { get; init; }

        public DateTime Due { get; init; }

        public DateTime? LastReview { get; init; }

        public int Version { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }

        public string[] TopicSlugs { get; init; } = [];

        public string[] TopicNames { get; init; } = [];

        public Card Card => new(
            Id,
            new CardDeck(DeckId, DeckName),
            Front,
            Back,
            [.. TopicSlugs.Zip(TopicNames, (slug, name) => new CardTopic(slug, name))],
            SourcePath is null ? null : new CardSource(SourcePath, SourceTitle!),
            State,
            Due,
            LastReview,
            Version,
            CreatedAt,
            UpdatedAt);

        /// <summary>Its schedule, as the scheduler reads it. Npgsql reads a timestamptz as UTC.</summary>
        public CardState Schedule => new(
            CardState.ParseState(State),
            Step,
            Stability,
            Difficulty,
            new DateTimeOffset(Due, TimeSpan.Zero),
            LastReview is { } last ? new DateTimeOffset(last, TimeSpan.Zero) : null);
    }
}
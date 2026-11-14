using Dapper;

using Microsoft.AspNetCore.Http.HttpResults;

using Npgsql;

using Review.Api;
using Review.Database;

namespace Review.Decks;

/// <summary>A deck, where cards live, as the API answers it.</summary>
/// <param name="CardCount">How many cards it holds.</param>
public sealed record Deck(Guid Id, string Name, int CardCount, DateTime CreatedAt);

/// <summary>What a deck is made or renamed with.</summary>
/// <param name="Name">1 to 100 characters, stored trimmed; unique among the user's decks in any
/// letter case.</param>
public sealed record DeckInput(string? Name);

/// <summary>
/// <c>/review/api/decks</c>: a user's decks, made, listed, renamed and deleted. A deck holding cards
/// is never deleted. See "What a card is" and "What it serves" in review/README.md.
/// </summary>
public static class DeckEndpoints
{
    public const int NameMax = 100;

    // A deck as answered, with its count; the policy narrows both to the user. The columns are in
    // the order of Deck's parameters, which Dapper matches them by.
    private const string Select = """
        SELECT d.id AS Id, d.name AS Name,
               (SELECT count(*)::int FROM cards c WHERE c.user_id = d.user_id AND c.deck_id = d.id) AS CardCount,
               d.created_at AS CreatedAt
        FROM decks d
        """;

    public static IEndpointRouteBuilder MapDecks(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var decks = routes.MapGroup("/review/api/decks");

        decks.MapGet("", List)
            .UserRoute("listDecks", "Every deck", "By name, without letter case, each with how many cards it holds.");

        decks.MapPost("", Create)
            .UserRoute("createDeck", "Make a deck", "201 with the deck. 409 when the user has a deck of that name in any letter case, 422 for a name that breaks the rule.",
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        decks.MapPatch("/{id:guid}", Rename)
            .UserRoute("renameDeck", "Rename a deck", "The deck renamed. 404 for a deck the user does not have, 409 when another of theirs has the name, 422 for a name that breaks the rule.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        decks.MapDelete("/{id:guid}", Delete)
            .UserRoute("deleteDeck", "Delete an empty deck", "204. 404 for a deck the user does not have, 409 while it holds cards.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<Ok<Deck[]>> List(HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        var decks = await db.InUserTransaction(context.UserId(), transaction => transaction.Connection!.QueryAsync<Deck>(new CommandDefinition(
            $"{Select} ORDER BY lower(d.name), d.name, d.id", transaction: transaction, cancellationToken: cancellationToken)), cancellationToken);
        return TypedResults.Ok(decks.ToArray());
    }

    private static async Task<Results<Created<Deck>, ProblemHttpResult>> Create(
        DeckInput input, HttpContext context, ReviewDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        var broken = new Broken();
        var name = broken.Trimmed("name", input.Name, NameMax);
        if (broken.Any)
        {
            return broken.Problem();
        }

        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        try
        {
            return TypedResults.Created((string?)null, await db.InUserTransaction(context.UserId(), async transaction =>
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO decks (id, user_id, name, created_at) VALUES (@id, @user, @name, @now)",
                    new { id, user = context.UserId(), name, now }, transaction, cancellationToken: cancellationToken));
                return await Read(transaction, id, cancellationToken);
            }, cancellationToken));
        }
        catch (PostgresException clash) when (IsNameClash(clash))
        {
            return NameTaken(name!);
        }
    }

    private static async Task<Results<Ok<Deck>, ProblemHttpResult>> Rename(
        Guid id, DeckInput input, HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        var broken = new Broken();
        var name = broken.Trimmed("name", input.Name, NameMax);
        if (broken.Any)
        {
            return broken.Problem();
        }

        try
        {
            var deck = await db.InUserTransaction(context.UserId(), async transaction =>
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "UPDATE decks SET name = @name WHERE id = @id", new { id, name }, transaction, cancellationToken: cancellationToken)) == 0
                    ? null
                    : await Read(transaction, id, cancellationToken), cancellationToken);
            return deck is null ? Problems.NotFound("the deck") : TypedResults.Ok(deck);
        }
        catch (PostgresException clash) when (IsNameClash(clash))
        {
            return NameTaken(name!);
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, HttpContext context, ReviewDb db, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await db.InUserTransaction(context.UserId(), transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM decks WHERE id = @id", new { id }, transaction, cancellationToken: cancellationToken)), cancellationToken);
            return deleted == 0 ? Problems.NotFound("the deck") : TypedResults.NoContent();
        }
        // The cards' key refuses it, ON DELETE RESTRICT, in the same statement, so a card added
        // meanwhile cannot slip past a count read first.
        catch (PostgresException holding) when (holding is { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "cards_user_id_deck_id_fkey" })
        {
            return Problems.Conflict("the deck holds cards: move or delete them first");
        }
    }

    private static Task<Deck> Read(NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken) =>
        transaction.Connection!.QuerySingleAsync<Deck>(new CommandDefinition(
            $"{Select} WHERE d.id = @id", new { id }, transaction, cancellationToken: cancellationToken));

    // The unique index, rather than a read first, decides, so two requests at once cannot both pass.
    private static bool IsNameClash(PostgresException error) =>
        error is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "decks_name" };

    private static ProblemHttpResult NameTaken(string name) =>
        Problems.Conflict($"a deck named \"{name}\" exists, in some letter case");
}
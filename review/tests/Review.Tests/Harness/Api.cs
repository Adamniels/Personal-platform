using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using Microsoft.AspNetCore.Http;

using Review.Database;

namespace Review.Tests;

/// <summary>
/// Review's API as the browser calls it, over a database of its own: a copy of the migrated one,
/// a door to it as review_app, and the app on a test server using that door. Every call carries the
/// committed token, so it is made as <see cref="Contract.Sub"/>, and the platform's own
/// <c>Origin</c>, which a request that changes state needs.
///
/// Another user's rows are written by <see cref="AsUser"/>, through the door as that user, in SQL
/// rather than through the API, since the committed token names one user only.
/// </summary>
internal sealed class Api : IAsyncDisposable
{
    public const string Origin = "https://platform.test";

    /// <summary>A user who is not the token's, for rows the caller must not reach.</summary>
    public static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private readonly TestDatabase _database;

    private Api(TestDatabase database, ReviewDb door, Harness review)
    {
        _database = database;
        Door = door;
        Review = review;
    }

    public ReviewDb Door { get; }

    /// <summary>A time on the wire, an RFC 3339 string, as an instant.</summary>
    public static DateTimeOffset Time(JsonNode? value) =>
        DateTimeOffset.Parse((string)value!, System.Globalization.CultureInfo.InvariantCulture);

    public Harness Review { get; }

    public static Guid Me => Guid.Parse(Contract.Sub);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Api> Start()
    {
        var database = await TestDatabase.Migrated(Ct);
        var door = await ReviewDb.Open(database.App(), Ct);
        var review = await Harness.Start(database: door);
        return new Api(database, door, review);
    }

    /// <summary>A call as the token's user, with the platform's origin, through the test server's
    /// own client, which sends a body as a browser does.</summary>
    public async Task<Answer> Call(string method, string pathAndQuery, JsonNode? body = null)
    {
        using var client = Review.Server.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        request.Headers.Add("Cookie", $"__Host-access={Contract.Token}");
        request.Headers.Add("Origin", Origin);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return new Answer(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.Location?.ToString() ?? "",
            text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public Task<Answer> Get(string pathAndQuery) => Call("GET", pathAndQuery);

    public Task<Answer> Post(string path, JsonNode body) => Call("POST", path, body);

    public Task<Answer> Patch(string path, JsonNode body) => Call("PATCH", path, body);

    public Task<Answer> Delete(string path) => Call("DELETE", path);

    /// <summary>A deck of the token's user, through the API, its id.</summary>
    public async Task<Guid> Deck(string name)
    {
        var made = await Post("/review/api/decks", new JsonObject { ["name"] = name });
        Assert.Equal(StatusCodes.Status201Created, made.Status);
        return made.Id;
    }

    /// <summary>A card of the token's user, through the API, its answer. The clock moves a second
    /// first, so cards made one after another are ordered by when they were made.</summary>
    public async Task<Answer> Card(Guid deck, string front, params string[] slugs)
    {
        Review.Clock.Advance(TimeSpan.FromSeconds(1));
        var made = await Post("/review/api/cards", CardBody(deck, front, slugs));
        Assert.Equal(StatusCodes.Status201Created, made.Status);
        return made;
    }

    /// <summary>A valid card's body, every topic named after its slug.</summary>
    public static JsonObject CardBody(Guid deck, string front = "What is MVCC?", params string[] slugs) => new()
    {
        ["deck_id"] = deck.ToString(),
        ["front"] = front,
        ["back"] = "Multiversion concurrency control.",
        ["topics"] = new JsonArray([.. (slugs.Length == 0 ? ["postgres"] : slugs)
            .Select(slug => (JsonNode)new JsonObject { ["slug"] = slug, ["name"] = slug.ToUpperInvariant() })]),
        ["source"] = null,
    };

    /// <summary>Runs <paramref name="sql"/> through the door as <paramref name="user"/>.</summary>
    public Task<int> AsUser(Guid user, string sql, object? parameters = null) =>
        Door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            sql, parameters, transaction, cancellationToken: Ct)), Ct);

    /// <summary>A deck of <see cref="Bob"/>'s holding one card with the topic <c>postgres</c>,
    /// written as he would have written them: their ids.</summary>
    public async Task<(Guid Deck, Guid Card)> BobsDeckAndCard()
    {
        var deck = Guid.CreateVersion7();
        var card = Guid.CreateVersion7();
        await AsUser(Bob, """
            INSERT INTO decks (id, user_id, name, created_at) VALUES (@deck, @user, 'Databases', now());
            INSERT INTO cards (id, user_id, deck_id, front, back, due, created_at, updated_at)
                VALUES (@card, @user, @deck, 'Bob''s front', 'Bob''s back', now(), now(), now());
            INSERT INTO card_topics (card_id, user_id, position, slug, name) VALUES (@card, @user, 0, 'postgres', 'Postgres');
            """, new { deck, card, user = Bob });
        return (deck, card);
    }

    /// <summary>Rows read as the superuser, past every policy, as a test checks what was
    /// written.</summary>
    public async Task<List<T>> AsAdmin<T>(string sql, object? parameters = null)
    {
        await using var admin = await _database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: Ct))];
    }

    /// <summary>What a user's rows are, read as the superuser past every policy: each deck's name
    /// and each card's front, version and topics, in one line each, ordered.</summary>
    public async Task<List<string>> RowsOf(Guid user)
    {
        await using var admin = await _database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<string>(new CommandDefinition(
            """
            SELECT 'deck ' || name FROM decks WHERE user_id = @user
            UNION ALL
            SELECT 'card ' || c.front || ' v' || c.version || ' ' || coalesce(
                (SELECT string_agg(t.slug, ',' ORDER BY t.position) FROM card_topics t WHERE t.card_id = c.id), '')
            FROM cards c WHERE c.user_id = @user
            ORDER BY 1
            """,
            new { user }, cancellationToken: Ct))];
    }

    public async ValueTask DisposeAsync()
    {
        // The harness disposes the door.
        await Review.DisposeAsync();
        await _database.DisposeAsync();
    }
}

/// <summary>A response: its status, content type, Location and JSON body, if any.</summary>
internal sealed record Answer(int Status, string? ContentType, string Location, JsonNode? Body)
{
    public Guid Id => Guid.Parse((string)Body!["id"]!);

    /// <summary>The fields a 422's problem document names, from its <c>errors</c>.</summary>
    public IEnumerable<string> Fields =>
        Body?["errors"]?.AsObject().Select(pair => pair.Key) ?? [];
}
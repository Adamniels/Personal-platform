using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace Review.Tests;

/// <summary>
/// Decks, where a card lives, through <c>/review/api/decks</c>, on a database of each test's own.
/// See "What a card is" and "What it serves" in review/README.md.
/// </summary>
public sealed class DeckTests : IAsyncLifetime
{
    private const string Decks = "/review/api/decks";

    private Api? _api;

    private Api Api => _api!;

    public async ValueTask InitializeAsync() => _api = await Api.Start();

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();

    private static JsonObject Named(string name) => new() { ["name"] = name };

    private async Task<List<string>> Names() =>
        [.. (await Api.Get(Decks)).Body!.AsArray().Select(deck => (string)deck!["name"]!)];

    [Fact]
    public async Task ADeckIsCreatedListedRenamedAndDeleted()
    {
        var made = await Api.Post(Decks, Named("  Databases \n"));

        Assert.Equal(StatusCodes.Status201Created, made.Status);
        Assert.StartsWith("application/json", made.ContentType, StringComparison.Ordinal);
        Assert.Equal(["card_count", "created_at", "id", "name"], made.Body!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Databases", (string)made.Body["name"]!);
        Assert.Equal(0, (int)made.Body["card_count"]!);
        Assert.Equal(Api.Review.Clock.GetUtcNow(), Api.Time(made.Body["created_at"]));
        // A UUIDv7, made by the service.
        Assert.Equal(7, made.Id.Version);

        await Api.Deck("algorithms");
        // By name, without letter case.
        Assert.Equal(["algorithms", "Databases"], await Names());
        var listed = (await Api.Get(Decks)).Body!.AsArray().Single(deck => (string)deck!["name"]! == "Databases");
        Assert.True(JsonNode.DeepEquals(made.Body, listed));

        var renamed = await Api.Patch($"{Decks}/{made.Id}", Named(" Systems "));
        Assert.Equal(StatusCodes.Status200OK, renamed.Status);
        Assert.Equal("Systems", (string)renamed.Body!["name"]!);
        Assert.Equal(made.Id, renamed.Id);
        Assert.Equal(["algorithms", "Systems"], await Names());

        var deleted = await Api.Delete($"{Decks}/{made.Id}");
        Assert.Equal(StatusCodes.Status204NoContent, deleted.Status);
        Assert.Null(deleted.Body);
        Assert.Equal(["algorithms"], await Names());

        // Gone, so neither changed nor deleted again.
        foreach (var gone in new[] { await Api.Delete($"{Decks}/{made.Id}"), await Api.Patch($"{Decks}/{made.Id}", Named("Back")) })
        {
            Assert.Equal(StatusCodes.Status404NotFound, gone.Status);
            Assert.StartsWith("application/problem+json", gone.ContentType, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASecondDeckWithTheSameNameInAnyCaseIs409()
    {
        var rust = await Api.Deck("Rust");
        var go = await Api.Deck("Go");

        foreach (var again in new[] { "Rust", "rust", " RUST " })
        {
            var refused = await Api.Post(Decks, Named(again));
            Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
            Assert.StartsWith("application/problem+json", refused.ContentType, StringComparison.Ordinal);
        }

        // Renamed onto another deck's name, too.
        Assert.Equal(StatusCodes.Status409Conflict, (await Api.Patch($"{Decks}/{go}", Named("rUST"))).Status);
        Assert.Equal(["Go", "Rust"], await Names());

        // A deck may change its own name's letter case.
        Assert.Equal(StatusCodes.Status200OK, (await Api.Patch($"{Decks}/{rust}", Named("RUST"))).Status);

        // Another user's deck of that name is no clash.
        await Api.BobsDeckAndCard();
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Decks, Named("databases"))).Status);
    }

    [Fact]
    public async Task ABodyThatIsNotADeckIs400()
    {
        // Not JSON, or JSON of the wrong shape, is the framework's 400 before any rule is read: a
        // broken client rather than a broken rule.
        foreach (var body in new JsonNode[] { new JsonArray(), new JsonObject { ["name"] = 7 }, JsonValue.Create("Databases") })
        {
            Assert.Equal(StatusCodes.Status400BadRequest, (await Api.Post(Decks, body)).Status);
        }

        Assert.Empty(await Names());
    }

    public static TheoryData<string?> BrokenNames() => [(string?)null, "", "   ", "\n\t", new string('d', 101)];

    [Theory]
    [MemberData(nameof(BrokenNames))]
    public async Task ADeckNameIsOneToAHundredCharacters(string? name)
    {
        var deck = await Api.Deck("Databases");

        foreach (var refused in new[]
        {
            await Api.Post(Decks, new JsonObject { ["name"] = name }),
            await Api.Patch($"{Decks}/{deck}", new JsonObject { ["name"] = name }),
        })
        {
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
            Assert.StartsWith("application/problem+json", refused.ContentType, StringComparison.Ordinal);
            Assert.Equal(["name"], refused.Fields);
        }

        Assert.Equal(["Databases"], await Names());
        // A hundred characters, counted as characters rather than UTF-16 units, is enough.
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Decks, Named(new string('d', 100)))).Status);
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Decks, Named(string.Concat(Enumerable.Repeat("🦀", 100))))).Status);
    }
}
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace Review.Tests;

/// <summary>
/// Cards, a front and a back in a deck about one to five topics, through <c>/review/api/cards</c>,
/// on a database of each test's own. See "What a card is" and "What it serves" in
/// review/README.md.
/// </summary>
public sealed class CardTests : IAsyncLifetime
{
    private const string Cards = "/review/api/cards";

    private Api? _api;

    private Api Api => _api!;

    public async ValueTask InitializeAsync() => _api = await Api.Start();

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();

    private static JsonArray Topics(params (string Slug, string Name)[] topics) =>
        [.. topics.Select(topic => (JsonNode)new JsonObject { ["slug"] = topic.Slug, ["name"] = topic.Name })];

    private async Task<List<string>> Fronts(string query = "") =>
        [.. (await Api.Get(Cards + query)).Body!.AsArray().Select(card => (string)card!["front"]!)];

    [Fact]
    public async Task ACardIsCreatedWithItsDeckTopicsAndSource()
    {
        var deck = await Api.Deck("Databases");
        Api.Review.Clock.Advance(TimeSpan.FromMinutes(3));
        var now = Api.Review.Clock.GetUtcNow();

        var made = await Api.Post(Cards, new JsonObject
        {
            ["deck_id"] = deck.ToString(),
            ["front"] = "  What does MVCC stand for?\n",
            ["back"] = "\nMultiversion\nconcurrency control. ",
            ["topics"] = Topics(("postgres", "Postgres"), ("concurrency-control", " Concurrency control ")),
            ["source"] = new JsonObject { ["path"] = "/learning/sessions/123", ["title"] = "B tree internals" },
        });

        Assert.Equal(StatusCodes.Status201Created, made.Status);
        Assert.StartsWith("application/json", made.ContentType, StringComparison.Ordinal);
        Assert.Equal($"{Cards}/{made.Id}", made.Location);
        Assert.Equal(7, made.Id.Version);
        var card = made.Body!;
        Assert.Equal(
            ["back", "created_at", "deck", "due", "front", "id", "last_review", "source", "state", "topics", "updated_at", "version"],
            card.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = deck.ToString(), ["name"] = "Databases" }, card["deck"]));
        // Trimmed, with the line breaks inside kept.
        Assert.Equal("What does MVCC stand for?", (string)card["front"]!);
        Assert.Equal("Multiversion\nconcurrency control.", (string)card["back"]!);
        // In their order, each name as given, the brain's at the time.
        Assert.True(JsonNode.DeepEquals(Topics(("postgres", "Postgres"), ("concurrency-control", " Concurrency control ")), card["topics"]));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["path"] = "/learning/sessions/123", ["title"] = "B tree internals" }, card["source"]));
        // Ready to be scheduled: new, and due now.
        Assert.Equal("new", (string)card["state"]!);
        Assert.Equal(now, Api.Time(card["due"]));
        Assert.Null(card["last_review"]);
        Assert.Equal(1, (int)card["version"]!);
        Assert.Equal(now, Api.Time(card["created_at"]));
        Assert.Equal(now, Api.Time(card["updated_at"]));

        // Read back the same, and counted in its deck.
        Assert.True(JsonNode.DeepEquals(card, (await Api.Get($"{Cards}/{made.Id}")).Body));
        Assert.Equal(1, (int)(await Api.Get("/review/api/decks")).Body!.AsArray().Single()!["card_count"]!);

        // A source is optional: null, or left out.
        var unsourced = Api.CardBody(deck);
        var leftOut = Api.CardBody(deck);
        leftOut.Remove("source");
        foreach (var body in new[] { unsourced, leftOut })
        {
            var bare = await Api.Post(Cards, body);
            Assert.Equal(StatusCodes.Status201Created, bare.Status);
            Assert.Null(bare.Body!["source"]);
        }
    }

    [Fact]
    public async Task ACardIsReadListedChangedAndDeleted()
    {
        var databases = await Api.Deck("Databases");
        var systems = await Api.Deck("Systems");
        var made = await Api.Post(Cards, new JsonObject
        {
            ["deck_id"] = databases.ToString(),
            ["front"] = "What is MVCC?",
            ["back"] = "Multiversion concurrency control.",
            ["topics"] = Topics(("postgres", "Postgres"), ("concurrency", "Concurrency")),
            ["source"] = new JsonObject { ["path"] = "/learning/sessions/123", ["title"] = "B tree internals" },
        });
        var path = $"{Cards}/{made.Id}";
        var created = made.Body!;

        Assert.True(JsonNode.DeepEquals(created, (await Api.Get(path)).Body));
        Assert.True(JsonNode.DeepEquals(new JsonArray(created.DeepClone()), (await Api.Get(Cards)).Body));

        // Only the fields given change, and the version moves on.
        Api.Review.Clock.Advance(TimeSpan.FromMinutes(1));
        var changed = await Api.Patch(path, new JsonObject { ["version"] = 1, ["front"] = " What does MVCC stand for? " });
        Assert.Equal(StatusCodes.Status200OK, changed.Status);
        var expected = created.DeepClone().AsObject();
        expected["front"] = "What does MVCC stand for?";
        expected["version"] = 2;
        expected["updated_at"] = changed.Body!["updated_at"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(expected, changed.Body));
        Assert.Equal(Api.Review.Clock.GetUtcNow(), Api.Time(changed.Body["updated_at"]));
        Assert.True(JsonNode.DeepEquals(changed.Body, (await Api.Get(path)).Body));

        // Topics are replaced whole, in the order given; the deck moves; the source goes.
        changed = await Api.Patch(path, new JsonObject
        {
            ["version"] = 2,
            ["deck_id"] = systems.ToString(),
            ["back"] = "Readers see a snapshot.",
            ["topics"] = Topics(("mvcc", "MVCC"), ("postgres", "PostgreSQL")),
            ["source"] = null,
        });
        Assert.Equal(StatusCodes.Status200OK, changed.Status);
        var card = changed.Body!;
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = systems.ToString(), ["name"] = "Systems" }, card["deck"]));
        Assert.Equal("What does MVCC stand for?", (string)card["front"]!);
        Assert.Equal("Readers see a snapshot.", (string)card["back"]!);
        Assert.True(JsonNode.DeepEquals(Topics(("mvcc", "MVCC"), ("postgres", "PostgreSQL")), card["topics"]));
        Assert.Null(card["source"]);
        Assert.Equal(3, (int)card["version"]!);
        // The schedule is never touched by a change.
        foreach (var field in new[] { "state", "due", "last_review", "created_at" })
        {
            Assert.True(JsonNode.DeepEquals(created[field], card[field]), field);
        }

        // And a source can come back.
        var source = new JsonObject { ["path"] = "/news/items/9", ["title"] = "Postgres 19 released" };
        changed = await Api.Patch(path, new JsonObject { ["version"] = 3, ["source"] = source.DeepClone() });
        Assert.True(JsonNode.DeepEquals(source, changed.Body!["source"]));
        Assert.Equal(["card What does MVCC stand for? v4 mvcc,postgres", "deck Databases", "deck Systems"], await Api.RowsOf(Api.Me));

        var deleted = await Api.Delete(path);
        Assert.Equal(StatusCodes.Status204NoContent, deleted.Status);
        Assert.Null(deleted.Body);
        foreach (var gone in new[] { await Api.Get(path), await Api.Delete(path), await Api.Patch(path, new JsonObject { ["version"] = 4 }) })
        {
            Assert.Equal(StatusCodes.Status404NotFound, gone.Status);
            Assert.StartsWith("application/problem+json", gone.ContentType, StringComparison.Ordinal);
        }

        // Its topics went with it.
        Assert.Equal(["deck Databases", "deck Systems"], await Api.RowsOf(Api.Me));
        Assert.Empty(await Fronts());
    }

    [Fact]
    public async Task TheListIsNarrowedByDeckAndByTopic()
    {
        var databases = await Api.Deck("Databases");
        var systems = await Api.Deck("Systems");
        await Api.Card(databases, "first", "postgres");
        await Api.Card(systems, "second", "postgres", "rust");
        await Api.Card(databases, "third", "rust");
        // Another user's card, in a deck and with a topic of the same name, is never listed.
        await Api.BobsDeckAndCard();

        // Newest first.
        Assert.Equal(["third", "second", "first"], await Fronts());
        Assert.Equal(["third", "first"], await Fronts($"?deck={databases}"));
        Assert.Equal(["second"], await Fronts($"?deck={systems}"));
        Assert.Equal(["second", "first"], await Fronts("?topic=postgres"));
        Assert.Equal(["third", "second"], await Fronts("?topic=rust"));
        Assert.Equal(["third"], await Fronts($"?deck={databases}&topic=rust"));
        Assert.Equal(["second"], await Fronts($"?topic=postgres&deck={systems}"));
        Assert.Empty(await Fronts("?topic=go"));
        Assert.Empty(await Fronts($"?deck={Guid.CreateVersion7()}"));

        // Each card in the list as a single read answers it.
        var listed = (await Api.Get(Cards)).Body!.AsArray();
        foreach (var card in listed)
        {
            Assert.True(JsonNode.DeepEquals(card, (await Api.Get($"{Cards}/{(string)card!["id"]!}")).Body));
        }

        // A topic filter that is no slug is a broken rule.
        var refused = await Api.Get(Cards + "?topic=Not%20a%20slug");
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
        Assert.Equal(["topic"], refused.Fields);
    }

    /// <summary>One way each rule is broken: the change to a valid card's body, and the field the
    /// answer must name.</summary>
    public static TheoryData<string, string> BrokenRules() => new()
    {
        { "no front", "front" },
        { "empty front", "front" },
        { "blank front", "front" },
        { "long front", "front" },
        { "no back", "back" },
        { "empty back", "back" },
        { "long back", "back" },
        { "no topics", "topics" },
        { "empty topics", "topics" },
        { "six topics", "topics" },
        { "repeated slug", "topics[1].slug" },
        { "capital slug", "topics[0].slug" },
        { "hyphen first slug", "topics[0].slug" },
        { "spaced slug", "topics[0].slug" },
        { "long slug", "topics[0].slug" },
        { "empty slug", "topics[0].slug" },
        { "empty topic name", "topics[0].name" },
        { "long topic name", "topics[0].name" },
        { "no deck", "deck_id" },
        { "unknown deck", "deck_id" },
        { "another user's deck", "deck_id" },
        { "relative source", "source.path" },
        { "protocol relative source", "source.path" },
        { "long source", "source.path" },
        { "no source path", "source.path" },
        { "empty source title", "source.title" },
        { "long source title", "source.title" },
    };

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public async Task EveryRuleOfACardIsEnforced(string rule, string field)
    {
        var deck = await Api.Deck("Databases");
        var (bobsDeck, _) = await Api.BobsDeckAndCard();
        var body = Api.CardBody(deck);
        static JsonArray Many(int count) =>
            [.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["slug"] = $"topic-{i}", ["name"] = $"Topic {i}" })];
        static JsonObject Source(string? path, string? title) => new() { ["path"] = path, ["title"] = title };
        switch (rule)
        {
            case "no front": body.Remove("front"); break;
            case "empty front": body["front"] = ""; break;
            case "blank front": body["front"] = " \n\t "; break;
            case "long front": body["front"] = new string('f', 4001); break;
            case "no back": body["back"] = null; break;
            case "empty back": body["back"] = ""; break;
            case "long back": body["back"] = " " + new string('b', 4001) + " "; break;
            case "no topics": body.Remove("topics"); break;
            case "empty topics": body["topics"] = new JsonArray(); break;
            case "six topics": body["topics"] = Many(6); break;
            case "repeated slug": body["topics"] = Topics(("rust", "Rust"), ("rust", "Rust again")); break;
            case "capital slug": body["topics"] = Topics(("Rust", "Rust")); break;
            case "hyphen first slug": body["topics"] = Topics(("-rust", "Rust")); break;
            case "spaced slug": body["topics"] = Topics(("ru st", "Rust")); break;
            case "long slug": body["topics"] = Topics((new string('r', 81), "Rust")); break;
            case "empty slug": body["topics"] = Topics(("", "Rust")); break;
            case "empty topic name": body["topics"] = Topics(("rust", "")); break;
            case "long topic name": body["topics"] = Topics(("rust", new string('n', 201))); break;
            case "no deck": body.Remove("deck_id"); break;
            case "unknown deck": body["deck_id"] = Guid.CreateVersion7().ToString(); break;
            case "another user's deck": body["deck_id"] = bobsDeck.ToString(); break;
            case "relative source": body["source"] = Source("learning/sessions/123", "B trees"); break;
            case "protocol relative source": body["source"] = Source("//evil.test/learning", "B trees"); break;
            case "long source": body["source"] = Source("/" + new string('p', 500), "B trees"); break;
            case "no source path": body["source"] = Source(null, "B trees"); break;
            case "empty source title": body["source"] = Source("/learning/sessions/123", ""); break;
            case "long source title": body["source"] = Source("/learning/sessions/123", new string('t', 201)); break;
            default: throw new ArgumentOutOfRangeException(nameof(rule));
        }

        var refused = await Api.Post("/review/api/cards", body);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
        Assert.StartsWith("application/problem+json", refused.ContentType, StringComparison.Ordinal);
        Assert.Equal([field], refused.Fields);
        Assert.Contains(field, (string)refused.Body!["detail"]!, StringComparison.Ordinal);
        Assert.Equal(["deck Databases"], await Api.RowsOf(Api.Me));

        // A change is held to the same rules, a card's deck and version aside.
        var card = await Api.Card(deck, "What is MVCC?");
        if (field is not "deck_id" || rule is not "no deck")
        {
            var change = body.DeepClone().AsObject();
            change["version"] = 1;
            if (rule is "no front" or "no topics")
            {
                // Left out of a change, it is left as it is; given as null, it is broken.
                change[rule[3..]] = null;
            }

            var refusedChange = await Api.Patch($"{Cards}/{card.Id}", change);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, refusedChange.Status);
            Assert.Equal([field], refusedChange.Fields);
            Assert.True(JsonNode.DeepEquals(card.Body, (await Api.Get($"{Cards}/{card.Id}")).Body));
        }
    }

    [Fact]
    public async Task TheLongestOfEachIsAccepted()
    {
        var deck = await Api.Deck("Databases");
        var body = new JsonObject
        {
            ["deck_id"] = deck.ToString(),
            // Counted in characters, not UTF-16 units: each crab is two.
            ["front"] = string.Concat(Enumerable.Repeat("🦀", 4000)),
            ["back"] = new string('b', 4000),
            ["topics"] = new JsonArray([.. Enumerable.Range(0, 4)
                .Select(i => (JsonNode)new JsonObject { ["slug"] = $"topic-{i}", ["name"] = "n" })
                .Append(new JsonObject { ["slug"] = "9" + new string('-', 79), ["name"] = new string('n', 200) })]),
            ["source"] = new JsonObject { ["path"] = "/" + new string('p', 499), ["title"] = new string('t', 200) },
        };

        var made = await Api.Post(Cards, body);

        Assert.Equal(StatusCodes.Status201Created, made.Status);
        Assert.Equal(5, made.Body!["topics"]!.AsArray().Count);
    }

    [Fact]
    public async Task AnotherUsersDeckOrCardIs404()
    {
        var (bobsDeck, bobsCard) = await Api.BobsDeckAndCard();
        var bobs = await Api.RowsOf(Api.Bob);
        var mine = await Api.Deck("Mine");
        var myCard = await Api.Card(mine, "What is MVCC?");

        foreach (var refused in new[]
        {
            await Api.Get($"{Cards}/{bobsCard}"),
            await Api.Patch($"{Cards}/{bobsCard}", new JsonObject { ["version"] = 1, ["front"] = "taken" }),
            await Api.Delete($"{Cards}/{bobsCard}"),
            await Api.Patch($"/review/api/decks/{bobsDeck}", new JsonObject { ["name"] = "taken" }),
            await Api.Delete($"/review/api/decks/{bobsDeck}"),
        })
        {
            Assert.Equal(StatusCodes.Status404NotFound, refused.Status);
            Assert.StartsWith("application/problem+json", refused.ContentType, StringComparison.Ordinal);
        }

        // Another user's deck named in a body is a deck the user does not have: the same 422 as an
        // id nobody has, so the answer never says whether it exists. See "Decisions" in the pull
        // request for #234.
        foreach (var refused in new[]
        {
            await Api.Post(Cards, Api.CardBody(bobsDeck)),
            await Api.Patch($"{Cards}/{myCard.Id}", new JsonObject { ["version"] = 1, ["deck_id"] = bobsDeck.ToString() }),
        })
        {
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
            Assert.Equal(["deck_id"], refused.Fields);
        }

        Assert.Equal(bobs, await Api.RowsOf(Api.Bob));
        Assert.Equal(["card What is MVCC? v1 postgres", "deck Mine"], await Api.RowsOf(Api.Me));
        Assert.Equal(["What is MVCC?"], await Fronts());
    }

    [Fact]
    public async Task AStaleChangeIs409()
    {
        var deck = await Api.Deck("Databases");
        var card = await Api.Card(deck, "What is MVCC?");
        var path = $"{Cards}/{card.Id}";

        // Two tabs read version 1; the first saves.
        var first = await Api.Patch(path, new JsonObject { ["version"] = 1, ["front"] = "from the first tab" });
        Assert.Equal(StatusCodes.Status200OK, first.Status);

        // The second, still on version 1, changes nothing, nor does a version from the future.
        foreach (var version in new[] { 1, 3 })
        {
            var stale = await Api.Patch(path, new JsonObject { ["version"] = version, ["front"] = "from the second tab", ["topics"] = Topics(("rust", "Rust")) });
            Assert.Equal(StatusCodes.Status409Conflict, stale.Status);
            Assert.StartsWith("application/problem+json", stale.ContentType, StringComparison.Ordinal);
        }

        Assert.True(JsonNode.DeepEquals(first.Body, (await Api.Get(path)).Body));
        Assert.Equal(["card from the first tab v2 postgres", "deck Databases"], await Api.RowsOf(Api.Me));

        // The version is required.
        var unversioned = await Api.Patch(path, new JsonObject { ["front"] = "no version" });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unversioned.Status);
        Assert.Equal(["version"], unversioned.Fields);
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using Review.Display;

namespace Review.Tests;

/// <summary>
/// <c>GET /review/api/display</c>, what review offers the core's front page: how many cards are
/// due now and before the day ends, when the next one comes back, and the decks with cards due.
/// The data is a contract between review and the core, pinned by contracts/display/review.json.
/// See "What a feature exposes for the front page" in Core/docs/core-architecture.md.
/// </summary>
public sealed class DisplayTests : IAsyncLifetime
{
    private const string Display = "/review/api/display";

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private Api? _api;

    private Api Api => _api!;

    public async ValueTask InitializeAsync() => _api = await Api.Start();

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();

    /// <summary>2027-01-15 08:00 UTC, 09:00 in Stockholm: the fixture token's iat.</summary>
    private DateTimeOffset Now => Api.Review.Clock.GetUtcNow();

    /// <summary>Makes a card of the token's user due at <paramref name="due"/>, its id.</summary>
    private async Task<Guid> DueAt(Guid deck, string front, DateTimeOffset due)
    {
        var id = (await Api.Card(deck, front)).Id;
        await Api.AsUser(Api.Me, "UPDATE cards SET due = @due WHERE id = @id", new { id, due = due.ToUniversalTime() });
        return id;
    }

    /// <summary>A time in Stockholm, as the owner's clock on the wall shows it.</summary>
    private static DateTimeOffset InStockholm(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Stockholm.GetUtcOffset(local));
    }

    private Task<DisplayData> Read(DateTimeOffset now) =>
        DisplayEndpoints.Read(Api.Door, Api.Me, now, Stockholm, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheDisplayDataCountsWhatIsDue()
    {
        var databases = await Api.Deck("Databases");
        var compilers = await Api.Deck("compilers");
        var algorithms = await Api.Deck("Algorithms");
        var empty = await Api.Deck("Nothing due");
        // The clock moved a second per card made; the cards are placed from the time they end at.
        var now = Now.AddMinutes(1);
        await DueAt(databases, "a week ago", now.AddDays(-7));
        await DueAt(databases, "an hour ago", now.AddHours(-1));
        await DueAt(compilers, "this very second", now);
        await DueAt(compilers, "yesterday", now.AddDays(-1));
        await DueAt(algorithms, "a minute ago", now.AddMinutes(-1));
        await DueAt(databases, "in an hour", now.AddHours(1));
        // 2027-01-16 04:00 in Stockholm is 03:00 UTC: just before it is today, at it is not.
        var dayEnd = new DateTimeOffset(2027, 1, 16, 3, 0, 0, TimeSpan.Zero);
        await DueAt(algorithms, "just before the day ends", dayEnd.AddSeconds(-1));
        await DueAt(algorithms, "as the day ends", dayEnd);
        await DueAt(empty, "next week", now.AddDays(7));
        // Another user's due card is never counted.
        await Api.BobsDeckAndCard();
        Api.Review.Clock.SetUtcNow(now);

        var display = await Api.Get(Display);

        Assert.Equal(StatusCodes.Status200OK, display.Status);
        Assert.StartsWith("application/json", display.ContentType, StringComparison.Ordinal);
        Assert.Equal(5, (int)display.Body!["due_now"]!);
        Assert.Equal(7, (int)display.Body!["due_today"]!);
        Assert.Equal(now.AddHours(1), Api.Time(display.Body!["next_due_at"]));
        // UTC, written with Z.
        Assert.EndsWith("Z", (string)display.Body!["next_due_at"]!, StringComparison.Ordinal);
        // Only decks with a card due now, the most due first, then by name in any letter case.
        Assert.True(JsonNode.DeepEquals(
            new JsonArray(
                new JsonObject { ["name"] = "compilers", ["due_now"] = 2 },
                new JsonObject { ["name"] = "Databases", ["due_now"] = 2 },
                new JsonObject { ["name"] = "Algorithms", ["due_now"] = 1 }),
            display.Body!["decks"]));
    }

    [Fact]
    public async Task AtMost20DecksAreSent()
    {
        for (var i = 0; i < 22; i++)
        {
            var deck = await Api.Deck($"Deck {i:D2}");
            await DueAt(deck, "due", Now.AddDays(-1));
        }

        var display = await Api.Get(Display);

        Assert.Equal(22, (int)display.Body!["due_now"]!);
        var decks = display.Body!["decks"]!.AsArray();
        Assert.Equal(20, decks.Count);
        Assert.Equal("Deck 00", (string)decks[0]!["name"]!);
        Assert.Equal("Deck 19", (string)decks[19]!["name"]!);
    }

    public static TheoryData<string, DateTimeOffset, DateTimeOffset> DayEnds() => new()
    {
        // An ordinary winter day.
        { "03:59 is still the day before", InStockholm(2027, 1, 15, 3, 59), InStockholm(2027, 1, 15, 4, 0) },
        { "04:00 starts a new day", InStockholm(2027, 1, 15, 4, 0), InStockholm(2027, 1, 16, 4, 0) },
        { "just before midnight", InStockholm(2027, 1, 15, 23, 59), InStockholm(2027, 1, 16, 4, 0) },
        // Summer time starts at 02:00 on 2027-03-28, so that day is 23 hours long.
        { "the day before summer time, 03:59", InStockholm(2027, 3, 27, 3, 59), InStockholm(2027, 3, 27, 4, 0) },
        { "the day before summer time, 04:00", InStockholm(2027, 3, 27, 4, 0), InStockholm(2027, 3, 28, 4, 0) },
        { "the first summer morning, 03:59", InStockholm(2027, 3, 28, 3, 59), InStockholm(2027, 3, 28, 4, 0) },
        { "the first summer morning, 04:00", InStockholm(2027, 3, 28, 4, 0), InStockholm(2027, 3, 29, 4, 0) },
        // Summer time ends at 03:00 on 2027-10-31, so the day before it is 25 hours long.
        { "the day before winter time, 03:59", InStockholm(2027, 10, 30, 3, 59), InStockholm(2027, 10, 30, 4, 0) },
        { "the day before winter time, 04:00", InStockholm(2027, 10, 30, 4, 0), InStockholm(2027, 10, 31, 4, 0) },
        { "the first winter morning, 03:59", InStockholm(2027, 10, 31, 3, 59), InStockholm(2027, 10, 31, 4, 0) },
        { "the first winter morning, 04:00", InStockholm(2027, 10, 31, 4, 0), InStockholm(2027, 11, 1, 4, 0) },
    };

    [Theory]
    [MemberData(nameof(DayEnds))]
    public async Task TheDayEndsAtFourInTheMorningInTheConfiguredZone(string when, DateTimeOffset now, DateTimeOffset dayEnd)
    {
        Assert.True(dayEnd == DisplayEndpoints.DayEnd(now, Stockholm), $"{when}: the day should end at {dayEnd:O}");

        // Counted against the database: a card due a second before the day ends is today, one
        // due as it ends is not, and one due a second after now is today but not due now.
        var deck = await Api.Deck("Deck");
        await DueAt(deck, "now", now);
        await DueAt(deck, "a second after now", now.AddSeconds(1));
        await DueAt(deck, "a second before the day ends", dayEnd.AddSeconds(-1));
        await DueAt(deck, "as the day ends", dayEnd);

        var display = await Read(now);

        Assert.Equal(1, display.DueNow);
        Assert.Equal(3, display.DueToday);
        Assert.Equal(now.AddSeconds(1).UtcDateTime, display.NextDueAt);
    }

    [Fact]
    public async Task NothingDueGivesZeroesAndTheNextTime()
    {
        var display = await Api.Get(Display);

        // With no cards at all, nothing comes next.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"due_now": 0, "due_today": 0, "next_due_at": null, "decks": []}"""),
            display.Body));

        var deck = await Api.Deck("Deck");
        var inTwoHours = Now.AddHours(2);
        await DueAt(deck, "tomorrow afternoon", Now.AddDays(1).AddHours(6));
        await DueAt(deck, "in two hours", inTwoHours);

        display = await Api.Get(Display);

        Assert.Equal(0, (int)display.Body!["due_now"]!);
        Assert.Equal(1, (int)display.Body!["due_today"]!);
        Assert.Equal(inTwoHours, Api.Time(display.Body!["next_due_at"]));
        Assert.Empty(display.Body!["decks"]!.AsArray());
    }

    [Fact]
    public async Task TheResponseMatchesTheCommittedFixture()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(
            Repository.Path("contracts", "display", "review.json"), TestContext.Current.CancellationToken));
        var deck = await Api.Deck("Deck");
        await DueAt(deck, "due", Now.AddHours(-1));
        await DueAt(deck, "later", Now.AddHours(1));

        var display = await Api.Get(Display);

        Assert.Equal(StatusCodes.Status200OK, display.Status);
        Assert.Equal(Shape(fixture), Shape(display.Body));
    }

    /// <summary>A JSON value's keys and the JSON type of each value, nested objects and the first
    /// element of an array included, as one line: what the core's type reads.</summary>
    private static string Shape(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(", ", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {Shape(pair.Value)}")) + "}",
        JsonArray array => "[" + (array.Count == 0 ? "" : Shape(array[0])) + "]",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.Number => value.ToString().Contains('.', StringComparison.Ordinal) ? "float" : "integer",
            var kind => kind.ToString().ToLowerInvariant(),
        },
        _ => "null",
    };

    [Fact]
    public async Task WithoutATokenIs401()
    {
        // The core sends the user's token in the Authorization header, having no cookie jar.
        var withToken = await Api.Review.Send("GET", Display, token: null, ("Authorization", $"Bearer {Contract.Token}"));
        Assert.Equal(StatusCodes.Status200OK, withToken.Response.StatusCode);

        var without = await Api.Review.Send("GET", Display);
        Assert.Equal(StatusCodes.Status401Unauthorized, without.Response.StatusCode);
        Assert.Empty(await Harness.Body(without));

        var forged = await Api.Review.Send("GET", Display, token: null, ("Authorization", $"Bearer {Contract.Token}x"));
        Assert.Equal(StatusCodes.Status401Unauthorized, forged.Response.StatusCode);
    }
}
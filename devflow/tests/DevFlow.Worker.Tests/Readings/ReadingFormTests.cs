using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Tests;

using static DevFlow.Worker.Tests.Support;

using ReadingForm = DevFlow.Protocol.Reading;

namespace DevFlow.Worker.Tests.Readings;

/// <summary>
/// The reading a reader agent hands back is a fixed form, as the developer's report and the
/// reviewer's verdict are: its JSON Schema, contracts/devflow/reading.schema.json, is written from
/// <see cref="ReadingForm"/>, and a reading is read into that type with a missing, unknown, null or
/// mistyped field refused, and the form's own rules held. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class ReadingFormTests
{
    /// <summary>A reading in its form of cards 12 and 13: 12 ready, 13 with one question.</summary>
    internal static JsonObject Good() => new()
    {
        ["cards"] = new JsonArray(
            new JsonObject { ["number"] = 12, ["outcome"] = "ready", ["questions"] = new JsonArray(), ["missing"] = new JsonArray() },
            new JsonObject
            {
                ["number"] = 13,
                ["outcome"] = "questions",
                ["questions"] = new JsonArray(new JsonObject
                {
                    ["question"] = "Is the list sorted by name or by date?",
                    ["recommendation"] = "By date, newest first",
                    ["why"] = "The page beside it sorts so",
                }),
                ["missing"] = new JsonArray(),
            }),
        ["touches"] = "web/src/list.tsx: the list both cards change.",
    };

    /// <summary>The schema's text as the type writes it, as the contract tests write each form's.</summary>
    private static string SchemaText()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            ReadingForm.Schema().WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    [Fact]
    public async Task TheSchemaIsWrittenFromTheType()
    {
        // contracts/devflow/reading.schema.json is what Reading.Schema() writes, the schema every
        // reading job hands the reader, so the type that reads a reading and the schema that shapes
        // it hold the same fields.
        var file = Repository.Path("contracts", "devflow", "reading.schema.json");
        var generated = SchemaText();
        if (System.Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(file, generated, Ct);
        }

        Assert.True(File.Exists(file), $"{file} is missing, write it with UPDATE_CONTRACTS=1");
        Assert.True(
            await File.ReadAllTextAsync(file, Ct) == generated,
            "the reading's type changed, regenerate contracts/devflow/reading.schema.json with UPDATE_CONTRACTS=1 and review the diff");

        // Draft-07, which Claude Code reads; every object closed and every field required; the
        // slice's fields in its order; the three outcomes; the note at most 4000 characters.
        var schema = ReadingForm.Schema();
        Assert.Equal("http://json-schema.org/draft-07/schema#", (string)schema["$schema"]!);
        Assert.Equal(["cards", "touches"], schema["properties"]!.AsObject().Select(pair => pair.Key));
        var card = schema["properties"]!["cards"]!["items"]!;
        Assert.Equal(["number", "outcome", "questions", "missing"], card["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["ready", "questions", "too_thin"], card["properties"]!["outcome"]!["enum"]!.AsArray().Select(word => (string)word!));
        Assert.Equal(["question", "recommendation", "why"], card["properties"]!["questions"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(ReadingForm.MostTouches, (int)schema["properties"]!["touches"]!["maxLength"]!);
        foreach (var obj in Objects(schema))
        {
            Assert.False((bool)obj["additionalProperties"]!);
            Assert.Equal(
                obj["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal),
                obj["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));
        }

        // And a reading with a missing, unknown, null or mistyped field is refused, as the slice's
        // Acceptance names it here; the rest of the form's rules are the theory below.
        foreach (var (what, error) in new[] { ("a missing field", "touches"), ("an unknown field", "verdict"), ("a null field", "touches"), ("a mistyped field", "number") })
        {
            AReadingIsCheckedAgainstItsForm(what, error);
        }
    }

    public static TheoryData<string, string> NotInItsForm() => new()
    {
        { "a missing field", "touches" },
        { "an unknown field", "verdict" },
        { "a null field", "touches" },
        { "a mistyped field", "number" },
        { "a word that is no outcome", "outcome" },
        { "a null card", "cards: holds a null" },
        { "a null question", "questions: holds a null" },
        { "not an object", "the reading is not a JSON object" },
        { "a card it was not given", "cards: #14 was not given to read" },
        { "a card twice", "cards: #12 is read more than once" },
        { "a card left out", "cards: #13 was given and not read" },
        { "a ready card with a question", "cards: #12 is ready, so it has no questions and nothing missing" },
        { "questions without one", "cards: #13 has questions, so it lists at least one" },
        { "too thin without what is missing", "cards: #13 is too thin, so it lists what is missing" },
        { "an empty recommendation", "cards: #13 has a question without its question, recommendation or why" },
        { "a note too long", "touches: is at most 4000 characters" },
    };

    [Theory]
    [MemberData(nameof(NotInItsForm))]
    public void AReadingIsCheckedAgainstItsForm(string what, string error)
    {
        var reading = Good();
        JsonNode? answer = reading;
        var second = reading["cards"]![1]!.AsObject();
        switch (what)
        {
            case "a missing field":
                reading.Remove("touches");
                break;
            case "an unknown field":
                reading["verdict"] = "approve";
                break;
            case "a null field":
                reading["touches"] = null;
                break;
            case "a mistyped field":
                reading["cards"]![0]!["number"] = "12";
                break;
            case "a word that is no outcome":
                second["outcome"] = "maybe";
                break;
            case "a null card":
                reading["cards"]!.AsArray().Add(null);
                break;
            case "a null question":
                second["questions"]!.AsArray().Add(null);
                break;
            case "not an object":
                answer = new JsonArray();
                break;
            case "a card it was not given":
                second["number"] = 14;
                break;
            case "a card twice":
                second["number"] = 12;
                break;
            case "a card left out":
                reading["cards"]!.AsArray().RemoveAt(1);
                break;
            case "a ready card with a question":
                reading["cards"]![0]!["questions"] = second["questions"]!.DeepClone();
                break;
            case "questions without one":
                second["questions"] = new JsonArray();
                break;
            case "too thin without what is missing":
                second["outcome"] = "too_thin";
                second["questions"] = new JsonArray();
                break;
            case "an empty recommendation":
                second["questions"]![0]!["recommendation"] = "  ";
                break;
            case "a note too long":
                reading["touches"] = new string('x', ReadingForm.MostTouches + 1);
                break;
        }

        Assert.False(ReadingForm.TryRead(JsonSerializer.SerializeToElement(answer), [12, 13], out var read, out var said), what);
        Assert.Null(read);
        Assert.Contains(error, said, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingInItsFormReads()
    {
        var reading = Good();
        reading["cards"]!.AsArray().Add(new JsonObject
        {
            ["number"] = 14,
            ["outcome"] = "too_thin",
            ["questions"] = new JsonArray(),
            ["missing"] = new JsonArray("What the page shows when the list is empty"),
        });

        Assert.True(ReadingForm.TryRead(JsonSerializer.SerializeToElement(reading), [12, 13, 14], out var read, out var error), error);
        Assert.Equal([12, 13, 14], read.Cards.Select(card => card.Number));
        Assert.Equal(
            [Protocol.ReadingOutcome.Ready, Protocol.ReadingOutcome.Questions, Protocol.ReadingOutcome.TooThin],
            read.Cards.Select(card => card.Outcome));
        Assert.Equal("By date, newest first", Assert.Single(read.Cards[1].Questions).Recommendation);
        Assert.Equal("What the page shows when the list is empty", Assert.Single(read.Cards[2].Missing));
        Assert.Equal("web/src/list.tsx: the list both cards change.", read.Touches);

        // The note at its longest is in its form, and the order the cards come in is the reader's.
        reading["touches"] = new string('x', ReadingForm.MostTouches);
        Assert.True(ReadingForm.TryRead(JsonSerializer.SerializeToElement(reading), [14, 13, 12], out _, out error), error);
    }

    /// <summary>Every schema of an object in <paramref name="node"/>, itself included.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonObject obj when obj["properties"] is JsonObject => [obj, .. obj.SelectMany(pair => Objects(pair.Value))],
        JsonObject obj => obj.SelectMany(pair => Objects(pair.Value)),
        JsonArray array => array.SelectMany(Objects),
        _ => [],
    };
}
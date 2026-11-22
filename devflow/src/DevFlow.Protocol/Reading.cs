using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevFlow.Protocol;

/// <summary>What the reader says of one card it read.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadingOutcome>))]
public enum ReadingOutcome
{
    /// <summary>It can be built as it is written.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>It can be built, but leaves a choice that is the owner's open; each question comes
    /// with the reader's recommendation.</summary>
    [JsonStringEnumMemberName("questions")]
    Questions,

    /// <summary>It cannot be built from as it is written; what it lacks is listed.</summary>
    [JsonStringEnumMemberName("too_thin")]
    TooThin,
}

/// <summary>
/// The reading a reader agent hands back, the fixed form Dev flow reads, as <see cref="Report"/> is
/// the developer's and <see cref="ReviewVerdict"/> the reviewer's: every field required, lists may
/// be empty. Its JSON Schema, contracts/devflow/reading.schema.json, is <see cref="Schema"/>,
/// written from this type, and the reader is given it; <see cref="TryRead"/> reads a reading into
/// this type with a missing, unknown, null or mistyped field refused, and its own rules held. See
/// "Reading a card" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <param name="Cards">One answer per card it was given to read, each exactly once.</param>
/// <param name="Touches">The note of the code the feature, or the card on its own, touches: files
/// and modules, with a line each on why; at most <see cref="MostTouches"/> characters. It goes into
/// every brief of the feature.</param>
public sealed record Reading(IReadOnlyList<ReadingCard> Cards, string Touches)
{
    /// <summary>The longest the note may be, so it stays a note in every brief.</summary>
    public const int MostTouches = 4000;

    /// <summary>How a reading is written and read, as a report is.</summary>
    public static JsonSerializerOptions Json => Form.Json;

    /// <summary>The reading's JSON Schema, written from this type with <see cref="Json"/>: every
    /// object closed, every field required, nothing nullable, and the note's length bounded.</summary>
    public static JsonObject Schema()
    {
        var schema = Form.Schema<Reading>("Dev flow's reading");
        schema["properties"]!["touches"]!.AsObject()["maxLength"] = MostTouches;
        return schema;
    }

    /// <summary>
    /// Reads <paramref name="reading"/> as a reading of exactly the cards <paramref name="given"/>.
    /// False, with <paramref name="error"/> saying what is wrong, when it is not an object; a field
    /// is missing, unknown, null or of another type; a list holds a null; a card is read that was not
    /// given, given and not read, or read twice; a <c>ready</c> card has a question or something
    /// missing; a card with <c>questions</c> has none, or something missing; a <c>too_thin</c> card
    /// lists nothing missing; a question lacks its question, recommendation or why; something missing
    /// is blank; or the note is longer than <see cref="MostTouches"/>.
    /// </summary>
    public static bool TryRead(JsonElement reading, IReadOnlyCollection<int> given, [NotNullWhen(true)] out Reading? read, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(given);
        read = null;
        if (!Form.TryRead<Reading>(reading, "reading", out var parsed, out error))
        {
            return false;
        }

        error = Problem(parsed, given);
        if (error is not null)
        {
            return false;
        }

        read = parsed;
        return true;
    }

    /// <summary>What breaks the form's rules beyond the schema, or null when nothing does.</summary>
    private static string? Problem(Reading reading, IReadOnlyCollection<int> given)
    {
        if (reading.Cards.Contains(null))
        {
            return "cards: holds a null";
        }

        if (reading.Cards.Any(card => card.Questions.Contains(null)))
        {
            return "questions: holds a null";
        }

        if (reading.Cards.Any(card => card.Missing.Contains(null)))
        {
            return "missing: holds a null";
        }

        var seen = new HashSet<int>();
        foreach (var card in reading.Cards)
        {
            if (!given.Contains(card.Number))
            {
                return Said($"cards: #{card.Number} was not given to read");
            }

            if (!seen.Add(card.Number))
            {
                return Said($"cards: #{card.Number} is read more than once");
            }

            if (Broken(card) is { } broken)
            {
                return broken;
            }
        }

        if (given.FirstOrDefault(number => !seen.Contains(number)) is var left and > 0)
        {
            return Said($"cards: #{left} was given and not read");
        }

        return reading.Touches.Length > MostTouches
            ? Said($"touches: is at most {MostTouches} characters")
            : null;
    }

    /// <summary>What breaks one card's rules, or null.</summary>
    private static string? Broken(ReadingCard card)
    {
        if (card.Questions.Any(question => Blank(question.Question) || Blank(question.Recommendation) || Blank(question.Why)))
        {
            return Said($"cards: #{card.Number} has a question without its question, recommendation or why");
        }

        if (card.Missing.Any(Blank))
        {
            return Said($"cards: #{card.Number} lists something missing that is blank");
        }

        return card.Outcome switch
        {
            ReadingOutcome.Ready when card.Questions.Count > 0 || card.Missing.Count > 0 =>
                Said($"cards: #{card.Number} is ready, so it has no questions and nothing missing"),
            ReadingOutcome.Questions when card.Questions.Count == 0 =>
                Said($"cards: #{card.Number} has questions, so it lists at least one"),
            ReadingOutcome.Questions when card.Missing.Count > 0 =>
                Said($"cards: #{card.Number} has questions and is not too thin, so nothing is missing"),
            ReadingOutcome.TooThin when card.Missing.Count == 0 =>
                Said($"cards: #{card.Number} is too thin, so it lists what is missing"),
            _ => null,
        };
    }

    private static bool Blank(string text) => text.Trim().Length == 0;

    private static string Said(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The reader's answer for one card.</summary>
/// <param name="Number">The card's issue number, one it was given.</param>
/// <param name="Outcome">Ready, questions, or too thin.</param>
/// <param name="Questions">Each choice the card leaves open that is the owner's, with the reader's
/// recommendation; empty when it is ready. A contradiction with another card is a question here,
/// naming the other.</param>
/// <param name="Missing">What the card lacks to be built, when it is too thin; empty
/// otherwise.</param>
public sealed record ReadingCard(int Number, ReadingOutcome Outcome, IReadOnlyList<ReadingQuestion> Questions, IReadOnlyList<string> Missing);

/// <summary>A choice a card leaves open that is the owner's.</summary>
/// <param name="Question">The question, as the owner would be asked it.</param>
/// <param name="Recommendation">What the reader recommends, the answer the card is built on when
/// the owner gives none.</param>
/// <param name="Why">Why it recommends so.</param>
public sealed record ReadingQuestion(string Question, string Recommendation, string Why);
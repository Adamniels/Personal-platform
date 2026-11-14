using System.Text.Json.Serialization;

namespace Review.Cards;

/// <summary>
/// A card as the API answers it, a single read and each item of the list alike. See "What a card
/// is" in review/README.md.
/// </summary>
/// <param name="Topics">In the card's order.</param>
/// <param name="Source">Where it came from, or null.</param>
/// <param name="State">FSRS's: <c>new</c>, <c>learning</c>, <c>review</c> or <c>relearning</c>.</param>
/// <param name="Due">When it is next due; a new card is due when it was made.</param>
/// <param name="LastReview">When it was last graded, or null.</param>
/// <param name="Version">Moves on with every change; a change names the one it was made from.</param>
public sealed record Card(
    Guid Id,
    CardDeck Deck,
    string Front,
    string Back,
    CardTopic[] Topics,
    CardSource? Source,
    string State,
    DateTime Due,
    DateTime? LastReview,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>The deck a card lives in.</summary>
public sealed record CardDeck(Guid Id, string Name);

/// <summary>One of the brain's topics, by its slug, with the brain's name for it when the card was
/// saved.</summary>
public sealed record CardTopic(string Slug, string Name);

/// <summary>Another feature's record a card came from: an address on the platform, a path, and its
/// title when the card was saved.</summary>
public sealed record CardSource(string Path, string Title);

/// <summary>A topic as a request gives it.</summary>
/// <param name="Slug">Lowercase letters, digits and hyphens, starting with a letter or digit, 1 to
/// 80 characters, the brain's rule.</param>
/// <param name="Name">1 to 200 characters, stored as given.</param>
public sealed record TopicInput(string? Slug, string? Name);

/// <summary>A source as a request gives it.</summary>
/// <param name="Path">Starts with one <c>/</c>, at most 500 characters.</param>
/// <param name="Title">1 to 200 characters.</param>
public sealed record SourceInput(string? Path, string? Title);

/// <summary>What a card is made with.</summary>
/// <param name="DeckId">One of the user's decks.</param>
/// <param name="Front">1 to 4000 characters once trimmed, stored trimmed, line breaks kept.</param>
/// <param name="Back">As the front.</param>
/// <param name="Topics">1 to 5, with distinct slugs, in the card's order.</param>
/// <param name="Source">Optional: left out or null, the card has none.</param>
public sealed record CardInput(Guid? DeckId, string? Front, string? Back, TopicInput?[]? Topics, SourceInput? Source = null);

/// <summary>
/// A change to a card: the version it was made from, and the fields to change, each by the rule
/// <see cref="CardInput"/> holds it to. A field left out stays as it is; given, even as null, it is
/// changed, so <c>"source": null</c> takes the source away and <c>"front": null</c> breaks the rule.
/// Topics are replaced whole. Which fields were given is what the JSON named, recorded as each is
/// read.
/// </summary>
public sealed class CardChange
{
    private readonly Guid? _deckId;
    private readonly string? _front;
    private readonly string? _back;
    private readonly TopicInput?[]? _topics;
    private readonly SourceInput? _source;

    /// <summary>The card's version the change was made from. Required: a stale one is a 409.</summary>
    public int? Version { get; init; }

    public Guid? DeckId { get => _deckId; init { _deckId = value; Gives |= Field.Deck; } }

    public string? Front { get => _front; init { _front = value; Gives |= Field.Front; } }

    public string? Back { get => _back; init { _back = value; Gives |= Field.Back; } }

    public TopicInput?[]? Topics { get => _topics; init { _topics = value; Gives |= Field.Topics; } }

    public SourceInput? Source { get => _source; init { _source = value; Gives |= Field.Source; } }

    /// <summary>The fields the JSON named.</summary>
    [JsonIgnore]
    public Field Gives { get; private set; }

    [Flags]
    public enum Field
    {
        None = 0,
        Deck = 1,
        Front = 2,
        Back = 4,
        Topics = 8,
        Source = 16,
    }

    public bool Has(Field field) => (Gives & field) != 0;
}
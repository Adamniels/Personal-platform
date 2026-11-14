using System.Text.RegularExpressions;

using Review.Api;

namespace Review.Cards;

/// <summary>
/// The rules a card is held to, read before anything is written, so a broken one is a 422 naming
/// its field. migrations/0002_decks_and_cards.sql checks the same in Postgres. See "What a card
/// is" in review/README.md.
/// </summary>
internal static partial class CardRules
{
    public const int TextMax = 4000;
    public const int TopicsMax = 5;
    public const int SlugMax = 80;
    public const int TopicNameMax = 200;
    public const int SourcePathMax = 500;
    public const int SourceTitleMax = 200;

    /// <summary>The brain's slug rule, <c>SLUG_PATTERN</c> in Brain/src/brain/topics.py, ending at
    /// <c>\z</c>, since <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    public static bool IsSlug(string? value) => value is not null && Slug().IsMatch(value);

    /// <summary>
    /// A path on the platform, never another host: the core's rule for a return path, "A return
    /// path is only ever a relative path" in Core/docs/core-architecture.md. It starts with one
    /// <c>/</c>, since two name another host, and holds no backslash, control character or
    /// whitespace anywhere, since browsers read <c>\</c> as <c>/</c> and drop tabs and newlines,
    /// which turns <c>/\evil</c> and <c>/&lt;tab&gt;/evil</c> into <c>//evil</c>.
    /// </summary>
    public static bool IsPlatformPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Any(c => c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c));
    }

    public static string? Front(Broken broken, string? value) => broken.Trimmed("front", value, TextMax);

    public static string? Back(Broken broken, string? value) => broken.Trimmed("back", value, TextMax);

    public static Guid? Deck(Broken broken, Guid? value)
    {
        if (value is null)
        {
            broken.Add("deck_id", "is required");
        }

        return value;
    }

    /// <summary>1 to 5 topics with distinct slugs, each name as given; null, recorded, when
    /// broken.</summary>
    public static CardTopic[]? Topics(Broken broken, TopicInput?[]? topics)
    {
        if (topics is null)
        {
            broken.Add("topics", "is required");
            return null;
        }

        if (topics.Length is < 1 or > TopicsMax)
        {
            broken.Add("topics", $"must hold 1 to {TopicsMax} topics");
            return null;
        }

        var valid = new List<CardTopic>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < topics.Length; i++)
        {
            if (topics[i] is not { } topic)
            {
                broken.Add($"topics[{i}]", "is required");
                continue;
            }

            var slug = topic.Slug;
            if (slug is null)
            {
                broken.Add($"topics[{i}].slug", "is required");
            }
            else if (!IsSlug(slug))
            {
                broken.Add($"topics[{i}].slug", $"must be lowercase letters, digits and hyphens, starting with a letter or digit, 1 to {SlugMax} characters");
                slug = null;
            }
            else if (seen.TryGetValue(slug, out var first))
            {
                broken.Add($"topics[{i}].slug", $"repeats topics[{first}].slug");
                slug = null;
            }
            else
            {
                seen.Add(slug, i);
            }

            var name = broken.Text($"topics[{i}].name", topic.Name, TopicNameMax);
            if (slug is not null && name is not null)
            {
                valid.Add(new CardTopic(slug, name));
            }
        }

        return valid.Count == topics.Length ? [.. valid] : null;
    }

    /// <summary>A source, or none: null either way, so read <see cref="Broken.Any"/> to tell a
    /// broken one from none.</summary>
    public static CardSource? Source(Broken broken, SourceInput? source)
    {
        if (source is null)
        {
            return null;
        }

        var path = source.Path;
        if (path is null)
        {
            broken.Add("source.path", "is required");
        }
        else if (Broken.Characters(path) is not { } length || length > SourcePathMax || !IsPlatformPath(path))
        {
            broken.Add("source.path", $"must be a path on the platform, starting with one /, with no backslash, control character or whitespace, at most {SourcePathMax} characters");
            path = null;
        }

        var title = broken.Text("source.title", source.Title, SourceTitleMax);
        return path is null || title is null ? null : new CardSource(path, title);
    }
}
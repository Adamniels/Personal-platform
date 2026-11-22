using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevFlow.Protocol;

/// <summary>
/// One of a project's values as a job carries it: the name the project reads from its environment
/// and the value the owner gave. It prints its name alone, never the value, so a log line or an
/// exception that names it does not reveal it. See "Jobs" in devflow/README.md.
/// </summary>
/// <param name="Name">The variable's name.</param>
/// <param name="Value">The value, for the environment of the agent, the check and the start, and
/// for scrubbing what the worker sends, and nowhere else.</param>
public sealed record JobValue(string Name, string Value)
{
    public override string ToString() => $"JobValue {{ Name = {Name} }}";
}

/// <summary>
/// Replaces each known value of a project wherever it is written, as itself, URL-encoded, and in
/// standard and URL-safe base64, with <c>[value of NAME]</c>. The one function Dev flow scrubs
/// what a worker sends and what it posts on GitHub with, and the worker scrubs what it sends and
/// logs with. Base64 is matched at each of the three alignments a value can have inside a longer
/// encoding, such as <c>user:value</c> in a Basic header, by the characters that depend on the
/// value alone. It finds only these forms: a value used, or printed transformed in another way, is
/// not caught, which is why a value is a throwaway or a key made for the automation, see "Security
/// decisions and accepted risks" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class ValueScrub
{
    private readonly Regex? _pattern;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    /// <summary>A scrub that knows no value and changes nothing.</summary>
    public static ValueScrub None { get; } = new([]);

    public ValueScrub(IEnumerable<JobValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values.Where(value => value is { Value.Length: > 0 }).OrderByDescending(value => value.Value.Length).ThenBy(value => value.Name, StringComparer.Ordinal))
        {
            foreach (var form in Forms(value.Value))
            {
                _names.TryAdd(form, value.Name);
            }
        }

        if (_names.Count > 0)
        {
            // Longest first, so where one form holds another the longer is replaced whole; the
            // alternation is tried in order at each position, and the replacement is never read
            // again.
            var alternatives = _names.Keys.OrderByDescending(form => form.Length).ThenBy(form => form, StringComparer.Ordinal).Select(Regex.Escape);
            _pattern = new Regex(string.Join('|', alternatives), RegexOptions.CultureInvariant);
        }
    }

    /// <summary>Whether it knows no value.</summary>
    public bool IsEmpty => _pattern is null;

    /// <summary>What stands in for the value named <paramref name="name"/>.</summary>
    public static string Placeholder(string name) => $"[value of {name}]";

    /// <summary><paramref name="text"/> with every form of every known value replaced.</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _pattern is null ? text : _pattern.Replace(text, match => Placeholder(_names[match.Value]));
    }

    /// <summary><see cref="Scrub(string)"/>, or null for null.</summary>
    public string? ScrubOrNull(string? text) => text is null ? null : Scrub(text);

    /// <summary><paramref name="element"/> with every string in it, and every property's name,
    /// scrubbed; its shape and every other value kept.</summary>
    public JsonElement Scrub(JsonElement element)
    {
        if (_pattern is null || element.ValueKind is JsonValueKind.Undefined)
        {
            return element;
        }

        return JsonSerializer.SerializeToElement(Scrub(JsonNode.Parse(element.GetRawText())));
    }

    /// <summary><see cref="Scrub(JsonElement)"/>, or null for null.</summary>
    public JsonElement? ScrubOrNull(JsonElement? element) => element is { } value ? Scrub(value) : null;

    /// <summary>A copy of <paramref name="node"/> with every string in it, and every property's
    /// name, scrubbed.</summary>
    public JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject properties:
                var scrubbed = new JsonObject();
                foreach (var (name, value) in properties)
                {
                    scrubbed[Scrub(name)] = Scrub(value);
                }

                return scrubbed;
            case JsonArray items:
                return new JsonArray([.. items.Select(Scrub)]);
            default:
                return node.GetValueKind() == JsonValueKind.String
                    ? JsonValue.Create(Scrub(node.GetValue<string>()))
                    : node.DeepClone();
        }
    }

    /// <summary>
    /// The forms of <paramref name="value"/> scrubbing finds: itself; URL-encoded, with its hex in
    /// upper and in lower case, and as a form encodes it, a space as <c>+</c>; and in standard and
    /// URL-safe base64, encoded on its own whole, with and without its padding, and at each of the
    /// three alignments inside a longer encoding, by the characters that depend on the value alone.
    /// Each once.
    /// </summary>
    public static IReadOnlyList<string> Forms(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var forms = new List<string> { value };
        var escaped = Uri.EscapeDataString(value);
        forms.Add(escaped);
        forms.Add(LowerHex().Replace(escaped, match => match.Value.ToLowerInvariant()));
        forms.Add(WebUtility.UrlEncode(value));

        var bytes = Encoding.UTF8.GetBytes(value);
        // The value encoded on its own, whole: its last character mixes its bits with the padding.
        var whole = Convert.ToBase64String(bytes);
        forms.Add(whole);
        forms.Add(whole.TrimEnd('='));
        forms.Add(whole.TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        for (var shift = 0; shift < 3; shift++)
        {
            var aligned = new byte[shift + bytes.Length];
            bytes.CopyTo(aligned, shift);
            var encoded = Convert.ToBase64String(aligned);
            // The first characters hold bits of the bytes before the value, and the last whole one
            // ends where the value's bits do; the rest depend on the value alone.
            var skip = shift switch { 0 => 0, 1 => 2, _ => 3 };
            var end = aligned.Length * 8 / 6;
            if (end - skip > 0)
            {
                var core = encoded[skip..end];
                forms.Add(core);
                forms.Add(core.Replace('+', '-').Replace('/', '_'));
            }
        }

        return [.. forms.Where(form => form.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ValueScrub {{ Forms = {_names.Count} }}");

    [GeneratedRegex("%[0-9A-F]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex();
}
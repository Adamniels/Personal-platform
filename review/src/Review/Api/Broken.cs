using System.Buffers;
using System.Text;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Review.Api;

/// <summary>
/// The rules one request breaks, by the field that breaks each, for a 422 that names them. A
/// field is named as it is on the wire, such as <c>front</c>, <c>topics[1].slug</c> or
/// <c>source.path</c>; the first rule a field breaks is the one it is named for.
/// </summary>
internal sealed class Broken
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    public bool Any => _fields.Count > 0;

    public void Add(string field, string why) => _fields.TryAdd(field, why);

    /// <summary>Plain text, stored without surrounding whitespace and with any inside it kept, 1
    /// to <paramref name="max"/> characters once trimmed; null, recorded, when it is not.</summary>
    public string? Trimmed(string field, string? value, int max) =>
        Text(field, value?.Trim(), max, " once surrounding whitespace is trimmed");

    /// <summary>Text stored as given, 1 to <paramref name="max"/> characters; null, recorded, when
    /// it is not.</summary>
    public string? Text(string field, string? value, int max, string note = "")
    {
        if (value is null)
        {
            Add(field, "is required");
            return null;
        }

        if (Characters(value) is not { } count)
        {
            Add(field, "must be text Postgres can store, with no NUL and no unpaired surrogate");
            return null;
        }

        if (count < 1 || count > max)
        {
            Add(field, $"must be 1 to {max} characters{note}");
            return null;
        }

        return value;
    }

    /// <summary>
    /// How many characters <paramref name="value"/> holds, as Postgres's <c>char_length</c> counts
    /// them, Unicode scalar values rather than UTF-16 units, so a limit means the same here as in
    /// a migration's check; null for what Postgres cannot store, a NUL or an unpaired surrogate.
    /// </summary>
    public static int? Characters(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = 0;
        for (var rest = value.AsSpan(); !rest.IsEmpty; count++)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done || rune.Value == 0)
            {
                return null;
            }

            rest = rest[used..];
        }

        return count;
    }

    /// <summary>The 422: each field in <c>errors</c>, and all of them in <c>detail</c>.</summary>
    public ProblemHttpResult Problem() =>
        TypedResults.Problem(new HttpValidationProblemDetails(_fields.ToDictionary(pair => pair.Key, pair => new[] { pair.Value }))
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = string.Join("; ", _fields.Select(pair => $"{pair.Key} {pair.Value}")),
        });
}
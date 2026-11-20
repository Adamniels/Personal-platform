using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

using DevFlow.Protocol;

namespace DevFlow.Jobs;

/// <summary>
/// Text a worker sent, made storable. Postgres holds neither a NUL, in <c>text</c> or as a
/// <c>\u0000</c> escape in <c>jsonb</c>, nor half of a surrogate pair, which is not UTF-8; JSON
/// allows both, and a process's output can hold a NUL. Each is replaced by U+FFFD, the replacement
/// character, so a result is always stored and judged rather than refused by the database, and the
/// owner can still see where something was. See "Jobs" in devflow/README.md.
/// </summary>
public static class Storable
{
    /// <summary>What stands in for a character Postgres cannot hold.</summary>
    public const char Replacement = (char)0xFFFD;

    /// <summary><paramref name="result"/> with every text and JSON it carries made storable. Its
    /// branch, head commit and pictures' names are compared or checked against a rule, so they stay
    /// as they came.</summary>
    public static RunResult Result(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result with
        {
            Report = result.Report is { } report ? Json(report) : null,
            FailureReason = result.FailureReason is { } reason ? Text(reason) : null,
            CheckOutput = result.CheckOutput is { } output ? Text(output) : null,
            AuthMethod = result.AuthMethod is { } method ? Text(method) : null,
            Usage = Json(result.Usage),
            PermissionDenials = result.PermissionDenials is { } denials ? Json(denials) : null,
            Stderr = Text(result.Stderr),
            Screenshots = result.Screenshots is { } screenshots
                ? screenshots with { Note = screenshots.Note is { } note ? Text(note) : null }
                : null,
            RemergeDiff = result.RemergeDiff is { } diff ? Text(diff) : null,
        };
    }

    /// <summary><paramref name="text"/> with each NUL and each lone half of a surrogate pair
    /// replaced; the same instance when it holds neither.</summary>
    public static string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? built = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var bad = c == '\0' || (char.IsSurrogate(c) && !pair);
            if (bad && built is null)
            {
                built = new StringBuilder(text.Length).Append(text, 0, i);
            }

            if (pair)
            {
                built?.Append(c).Append(text[i + 1]);
                i++;
            }
            else
            {
                built?.Append(bad ? Replacement : c);
            }
        }

        return built?.ToString() ?? text;
    }

    /// <summary>
    /// <paramref name="element"/> written again with every string and property name made storable
    /// by <see cref="Text"/>, and every number, literal and the structure as they came. A lone
    /// surrogate's escape cannot be read as a string, so each string is unescaped here from its
    /// raw JSON instead.
    /// </summary>
    public static JsonElement Json(JsonElement element)
    {
        var raw = Encoding.UTF8.GetBytes(element.GetRawText());
        var reader = new Utf8JsonReader(raw);
        var buffer = new ArrayBufferWriter<byte>(raw.Length);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(Text(Unescape(reader)));
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(Text(Unescape(reader)));
                        break;
                    default:
                        // A number, true, false or null, its bytes as they came.
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                }
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The string or property name under <paramref name="reader"/>, its escapes undone,
    /// keeping a lone surrogate's escape as the lone code unit it names. The reader has already
    /// checked the escapes are well formed.</summary>
    private static string Unescape(Utf8JsonReader reader)
    {
        var escaped = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
        if (!reader.ValueIsEscaped)
        {
            return escaped;
        }

        var text = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] != '\\')
            {
                text.Append(escaped[i]);
                continue;
            }

            i++;
            switch (escaped[i])
            {
                case 'b':
                    text.Append('\b');
                    break;
                case 'f':
                    text.Append('\f');
                    break;
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'u':
                    text.Append((char)int.Parse(escaped.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    // \" \\ and \/ stand for themselves.
                    text.Append(escaped[i]);
                    break;
            }
        }

        return text.ToString();
    }
}
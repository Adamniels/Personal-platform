using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Protocol;

/// <summary>
/// A project's <c>.devflow/project.json</c>: how a card's working copy is checked, and how the
/// project is started for its screenshots. Read strictly by <see cref="TryRead"/>, with every field
/// required and no other allowed; contracts/devflow/project.schema.json says the same for people
/// and editors, and a test keeps the two in step. .NET can write a JSON Schema but not validate
/// against one, so the rules are here, written out. See "The format of .devflow/" in
/// devflow/README.md.
/// </summary>
/// <param name="Version">The file's format, <see cref="Version1"/>, the one there is.</param>
/// <param name="Check">The command that checks a working copy, its packages installed first; never
/// empty.</param>
/// <param name="Start">The command that starts the project for its screenshots, empty when it has
/// none.</param>
/// <param name="Url">Where the started project answers, an <c>http</c> address on
/// <c>localhost</c>, empty when it has none.</param>
public sealed partial record ProjectFile(int Version, string Check, string Start, string Url)
{
    /// <summary>The one version of the format.</summary>
    public const int Version1 = 1;

    /// <summary>What <see cref="Url"/> must match, the schema's own pattern: empty, or
    /// <c>http://localhost</c> with an optional port and path.</summary>
    public const string LocalUrlPattern = "^$|^http://localhost(:[0-9]{1,5})?(/[^\\s]*)?$";

    private static readonly string[] Fields = ["version", "check", "start", "url"];

    /// <summary>
    /// Reads <paramref name="json"/>, the file's text. False, with <paramref name="error"/> naming
    /// the field first, or <c>the file</c> when it is not a JSON object, when anything is missing,
    /// of another type, against its rule, or not a field of the format.
    /// </summary>
    public static bool TryRead(string json, [NotNullWhen(true)] out ProjectFile? file, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(json);
        file = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "the file is not JSON";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the file is not a JSON object";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!Fields.Contains(property.Name, StringComparer.Ordinal))
                {
                    error = $"{property.Name} is not a field of project.json";
                    return false;
                }
            }

            foreach (var field in Fields)
            {
                if (!root.TryGetProperty(field, out _))
                {
                    error = $"{field} is missing; every field is written, empty where it may be";
                    return false;
                }
            }

            var version = root.GetProperty("version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != Version1)
            {
                error = $"version must be the number {Version1}";
                return false;
            }

            if (Text(root, "check") is not { } check || check.Trim().Length == 0)
            {
                error = "check must be a command, text that is not empty";
                return false;
            }

            if (Text(root, "start") is not { } start)
            {
                error = "start must be text, empty when the project has nothing to start";
                return false;
            }

            // .NET's $ also matches before a last newline, which JSON Schema's does not.
            if (Text(root, "url") is not { } url || url.EndsWith('\n') || !LocalUrl().IsMatch(url))
            {
                error = "url must be empty or an http address on localhost, such as http://localhost:5173";
                return false;
            }

            file = new ProjectFile(number, check, start, url);
            error = null;
            return true;
        }
    }

    private static string? Text(JsonElement root, string field) =>
        root.GetProperty(field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    [GeneratedRegex(LocalUrlPattern, RegexOptions.CultureInvariant)]
    private static partial Regex LocalUrl();
}
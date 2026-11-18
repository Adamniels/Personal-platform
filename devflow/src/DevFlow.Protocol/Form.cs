using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace DevFlow.Protocol;

/// <summary>
/// What an agent's answer is read by, the developer's <see cref="Report"/> and the reviewer's
/// <see cref="ReviewVerdict"/> alike: a fixed form whose JSON Schema is written from its type, and
/// which is read into that type with a missing, unknown, null or mistyped field refused, since
/// .NET can write a JSON Schema but not validate against one.
/// </summary>
internal static class Form
{
    /// <summary>How a form is written and read: snake case, every constructor parameter required,
    /// no field the type does not have, no null where the type has none.</summary>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    /// <summary>The JSON Schema of <typeparamref name="T"/>, written with <see cref="Json"/>: every
    /// object closed, every field required, and nothing nullable, under <paramref name="title"/>.</summary>
    public static JsonObject Schema<T>(string title)
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(Json, typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
        }).AsObject();
        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = title,
        };
        foreach (var (name, value) in schema)
        {
            document[name] = value?.DeepClone();
        }

        return document;
    }

    /// <summary>Reads <paramref name="answer"/> as a <typeparamref name="T"/>, its rules beyond the
    /// schema's left to the caller. False, with <paramref name="error"/> saying what is wrong, when
    /// it is not an object, or a field is missing, unknown, null or of another type.</summary>
    public static bool TryRead<T>(JsonElement answer, string what, [NotNullWhen(true)] out T? read, [NotNullWhen(false)] out string? error)
        where T : class
    {
        read = null;
        if (answer.ValueKind != JsonValueKind.Object)
        {
            error = $"the {what} is not a JSON object";
            return false;
        }

        try
        {
            read = answer.Deserialize<T>(Json);
        }
        catch (JsonException e)
        {
            // System.Text.Json names the field by its path, and says what it found.
            error = e.Path is { Length: > 1 } path ? $"{path[2..]}: {Plain(e.Message)}" : Plain(e.Message);
            return false;
        }

        if (read is null)
        {
            error = $"the {what} is null";
            return false;
        }

        error = null;
        return true;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The message without .NET's own advice and the type names it adds.</summary>
    private static string Plain(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut < 0 ? message : message[..cut]).Replace("DevFlow.Protocol.", "", StringComparison.Ordinal);
    }
}
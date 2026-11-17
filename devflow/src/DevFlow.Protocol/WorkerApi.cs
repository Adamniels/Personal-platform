using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFlow.Protocol;

/// <summary>
/// The worker API: where its routes are, and how their JSON is written, snake case as all of Dev
/// flow's is. A worker calls them with its project's key as <c>Authorization: Bearer dfw_…</c>.
/// See "Workers" and "The worker" in devflow/README.md.
/// </summary>
public static class WorkerApi
{
    /// <summary>Where every worker route is.</summary>
    public const string Prefix = "/devflow/api/worker";

    /// <summary>A worker introduces itself, with a <see cref="HelloRequest"/>.</summary>
    public const string HelloPath = Prefix + "/hello";

    /// <summary>A worker asks for work, with a <see cref="NextJobRequest"/>.</summary>
    public const string NextJobPath = Prefix + "/jobs/next";

    /// <summary>The JSON the worker writes and reads: snake case, numbers as numbers only, the
    /// options Dev flow serves with.</summary>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
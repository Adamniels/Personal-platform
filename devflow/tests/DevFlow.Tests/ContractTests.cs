using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests;

/// <summary>
/// Dev flow's HTTP schema, pinned by contracts/devflow/openapi.json, so a change to what Dev flow
/// serves shows up as a diff to review rather than as a caller failing at runtime. The C# twin of
/// the brain's tests/test_contract.py; see contracts/README.md.
/// </summary>
public sealed class ContractTests
{
    private static readonly string Schema = Repository.Path("contracts", "devflow", "openapi.json");

    /// <summary>Keys sorted, two space indent, a trailing newline.</summary>
    internal static string Canonical(JsonNode document)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            // Readable in a diff: only what JSON requires is escaped.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            Sorted(document)!.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        _ => node?.DeepClone(),
    };

    private static async Task<string> Generated()
    {
        await using var devflow = await Harness.Start(Harness.Defaults with { ExposeOpenApi = true });
        var response = await devflow.Get("/devflow/api/openapi.json");
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return Canonical(JsonNode.Parse(await Harness.Body(response))!);
    }

    [Fact]
    public async Task TheAppGeneratesTheCommittedSchema()
    {
        var generated = await Generated();

        // Regenerated on purpose, and its diff reviewed, like the token fixture.
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Schema)!);
            await File.WriteAllTextAsync(Schema, generated, TestContext.Current.CancellationToken);
            return;
        }

        Assert.True(File.Exists(Schema), $"{Schema} is missing, write it with UPDATE_CONTRACTS=1");
        Assert.True(
            await File.ReadAllTextAsync(Schema, TestContext.Current.CancellationToken) == generated,
            "Dev flow's schema changed, regenerate contracts/devflow/openapi.json with UPDATE_CONTRACTS=1 and review the diff");
    }

    [Fact]
    public async Task TheSchemaIsGeneratedTheSameEachTime()
    {
        Assert.Equal(await Generated(), await Generated());
    }

    [Fact]
    public async Task TheWorkersRoutesArePinned()
    {
        // The user's two and the worker's two, each under its own way to authenticate, so a worker
        // program written against the file finds its calls there.
        var paths = JsonNode.Parse(await File.ReadAllTextAsync(Schema, TestContext.Current.CancellationToken))!["paths"]!.AsObject();

        foreach (var (path, method, operation) in new[]
        {
            ("/devflow/api/projects/{id}/worker-key", "post", "makeWorkerKey"),
            ("/devflow/api/projects/{id}/workers", "get", "listWorkers"),
            ("/devflow/api/worker/hello", "post", "workerHello"),
            ("/devflow/api/worker/jobs/next", "post", "workerNextJob"),
        })
        {
            Assert.Equal(operation, (string?)paths[path]?[method]?["operationId"]);
        }

        Assert.Contains("Authorization: Bearer dfw_", (string)paths["/devflow/api/worker/hello"]!["post"]!["description"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunningServiceServesNoSchema()
    {
        await using var devflow = await Harness.Start();

        foreach (var token in new[] { null, Contract.Token })
        {
            var response = await devflow.Get("/devflow/api/openapi.json", token);

            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        }
    }
}
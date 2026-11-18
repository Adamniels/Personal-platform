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
    public void TheRecordedWriteAnswersHoldWhatTheAdapterReads()
    {
        // GitHub's answers to the app's writes, recorded in contracts/github/ and read by
        // GitHubAppWriteTests: each still has every field the adapter reads, of the kind it reads,
        // so recording one again that GitHub changed fails here, naming the file and the field.
        static JsonNode Recorded(string name) =>
            JsonNode.Parse(File.ReadAllText(Repository.Path("contracts", "github", name)))!;

        static void Has(JsonNode node, string file, params (string Path, JsonValueKind Kind)[] fields)
        {
            foreach (var (path, kind) in fields)
            {
                var names = path.Split('.');
                var parent = names[..^1].Aggregate((JsonNode?)node, (at, name) => at?[name]) as JsonObject;
                // A JSON null is a null node, so a field that is null is told from one that is missing.
                var found = parent is not null && parent.TryGetPropertyValue(names[^1], out var value)
                    ? value?.GetValueKind() ?? JsonValueKind.Null
                    : JsonValueKind.Undefined;
                Assert.True(found == kind, $"{file}: {path} is {(found == JsonValueKind.Undefined ? "missing" : found)}, not {kind}");
            }
        }

        var pull = Recorded("pull-opened.json");
        Has(pull, "pull-opened.json",
            ("number", JsonValueKind.Number), ("title", JsonValueKind.String), ("user.login", JsonValueKind.String),
            ("labels", JsonValueKind.Array), ("head.sha", JsonValueKind.String), ("head.ref", JsonValueKind.String),
            ("base.ref", JsonValueKind.String), ("draft", JsonValueKind.False), ("html_url", JsonValueKind.String),
            ("updated_at", JsonValueKind.String), ("state", JsonValueKind.String), ("merged_at", JsonValueKind.Null));
        Assert.Equal("open", (string)pull["state"]!);

        var labels = Recorded("labels.json").AsArray();
        Assert.NotEmpty(labels);
        Assert.All(labels, label => Has(label!, "labels.json", ("name", JsonValueKind.String)));

        var issue = Recorded("issue.json");
        Has(issue, "issue.json",
            ("number", JsonValueKind.Number), ("title", JsonValueKind.String), ("labels", JsonValueKind.Array), ("html_url", JsonValueKind.String),
            ("user.login", JsonValueKind.String), ("body", JsonValueKind.String), ("created_at", JsonValueKind.String));
        Assert.Null(issue["pull_request"]);

        Has(Recorded("merge.json"), "merge.json", ("sha", JsonValueKind.String), ("merged", JsonValueKind.True), ("message", JsonValueKind.String));

        // No recorded answer holds a token, made up or not, but the token fixture's own.
        foreach (var name in new[] { "pull-opened.json", "labels.json", "issue.json", "merge.json" })
        {
            Assert.DoesNotMatch(@"gh[pousr]_[A-Za-z0-9]", File.ReadAllText(Repository.Path("contracts", "github", name)));
        }
    }

    [Fact]
    public async Task TheJobRoutesArePinned()
    {
        // Asking for work answers a job, and the job's three routes take the worker's key, so a
        // worker program written against the file finds them there.
        var paths = JsonNode.Parse(await File.ReadAllTextAsync(Schema, TestContext.Current.CancellationToken))!["paths"]!.AsObject();

        foreach (var (path, operation) in new[]
        {
            ("/devflow/api/worker/jobs/{id}/push-key", "workerPushKey"),
            ("/devflow/api/worker/jobs/{id}/progress", "workerJobProgress"),
            ("/devflow/api/worker/jobs/{id}/result", "workerJobResult"),
        })
        {
            Assert.Equal(operation, (string?)paths[path]?["post"]?["operationId"]);
            Assert.Contains("Authorization: Bearer dfw_", (string)paths[path]!["post"]!["description"]!, StringComparison.Ordinal);
        }

        var next = paths["/devflow/api/worker/jobs/next"]!["post"]!["responses"]!.AsObject();
        Assert.True(next.ContainsKey("200"), "jobs/next answers a job with 200");
        Assert.True(next.ContainsKey("204"), "jobs/next answers no work with 204");
        Assert.Null(paths["/devflow/api/fake/worker/run"]);
    }

    /// <summary>The report's JSON Schema as the type writes it: two space indent, a trailing
    /// newline, its fields in the type's order, which is the order the agent reads them in.</summary>
    private static string ReportSchemaText() => SchemaText(DevFlow.Protocol.Report.Schema());

    /// <summary>A form's JSON Schema as its type writes it, as <see cref="ReportSchemaText"/>.</summary>
    private static string SchemaText(JsonObject schema)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            schema.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    [Fact]
    public async Task TheReportSchemaIsTheReportType()
    {
        // contracts/devflow/report.schema.json is what Report.Schema() writes, the schema every job
        // hands the agent, so the type that reads a report and the schema that shapes it hold the
        // same fields.
        var file = Repository.Path("contracts", "devflow", "report.schema.json");
        var generated = ReportSchemaText();
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(file, generated, TestContext.Current.CancellationToken);
            return;
        }

        Assert.True(File.Exists(file), $"{file} is missing, write it with UPDATE_CONTRACTS=1");
        Assert.True(
            await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken) == generated,
            "the report's type changed, regenerate contracts/devflow/report.schema.json with UPDATE_CONTRACTS=1 and review the diff");
    }

    [Fact]
    public async Task TheVerdictSchemaIsTheVerdictType()
    {
        // contracts/devflow/verdict.schema.json is what ReviewVerdict.Schema() writes, the schema
        // every review job hands the reviewer, so the type that reads a verdict and the schema that
        // shapes it hold the same fields.
        var file = Repository.Path("contracts", "devflow", "verdict.schema.json");
        var generated = SchemaText(DevFlow.Protocol.ReviewVerdict.Schema());
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(file, generated, TestContext.Current.CancellationToken);
            return;
        }

        Assert.True(File.Exists(file), $"{file} is missing, write it with UPDATE_CONTRACTS=1");
        Assert.True(
            await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken) == generated,
            "the verdict's type changed, regenerate contracts/devflow/verdict.schema.json with UPDATE_CONTRACTS=1 and review the diff");
    }

    [Fact]
    public void TheVerdictSchemaHoldsTheTypesFieldsEveryOneRequired()
    {
        // Every object closed and every field required, the top level's fields the slice's, in its
        // order, and no field for a commit: the commit a verdict is for is the worker's.
        var schema = DevFlow.Protocol.ReviewVerdict.Schema();
        Assert.Equal(["verdict", "summary", "findings", "for_owner"], schema["properties"]!.AsObject().Select(pair => pair.Key));
        foreach (var obj in Objects(schema))
        {
            Assert.False((bool)obj["additionalProperties"]!);
            Assert.Equal(
                obj["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal),
                obj["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));
        }

        Assert.Equal(["approve", "changes"], schema["properties"]!["verdict"]!["enum"]!.AsArray().Select(word => (string)word!));
        Assert.Equal(
            ["blocking", "file", "line", "what", "why"],
            schema["properties"]!["findings"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["decision", "view"], schema["properties"]!["for_owner"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.DoesNotContain("commit", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportSchemaHoldsTheTypesFieldsEveryOneRequired()
    {
        // Every object of the schema is closed and requires each of its fields, and the top level's
        // fields are the slice's, in its order.
        var schema = DevFlow.Protocol.Report.Schema();
        Assert.Equal(
            ["outcome", "stopped_reason", "summary", "tasks", "decisions", "tests", "pages", "installed", "follow_ups", "replies", "handover"],
            schema["properties"]!.AsObject().Select(pair => pair.Key));
        foreach (var obj in Objects(schema))
        {
            Assert.False((bool)obj["additionalProperties"]!);
            Assert.Equal(
                obj["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal),
                obj["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));
        }

        Assert.Equal(["done", "stopped"], schema["properties"]!["outcome"]!["enum"]!.AsArray().Select(word => (string)word!));
    }

    /// <summary>Every schema of an object in <paramref name="node"/>, itself included.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonObject obj when obj["properties"] is JsonObject => [obj, .. obj.SelectMany(pair => Objects(pair.Value))],
        JsonObject obj => obj.SelectMany(pair => Objects(pair.Value)),
        JsonArray array => array.SelectMany(Objects),
        _ => [],
    };

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
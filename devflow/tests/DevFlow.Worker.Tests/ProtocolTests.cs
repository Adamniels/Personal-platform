using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Tests;

using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevFlow.Worker.Tests;

/// <summary>
/// The worker and Dev flow say the same thing: Dev flow's worker routes take
/// <c>DevFlow.Protocol</c>'s types, both write them as the same JSON, and that JSON is what
/// contracts/devflow/openapi.json says. See "The worker" in devflow/README.md.
/// </summary>
public sealed class ProtocolTests
{
    /// <summary>Each worker route, the request it takes, one filled in, and every answer the
    /// worker reads.</summary>
    private static readonly (string Path, Type Request, object Sample, string[] Answers)[] Routes =
    [
        (WorkerApi.HelloPath, typeof(HelloRequest), new HelloRequest(Guid.CreateVersion7(), "fodr-worker-1", "3", "base"), ["204", "401", "422"]),
        (WorkerApi.NextJobPath, typeof(NextJobRequest), new NextJobRequest(Guid.CreateVersion7()), ["204", "401", "409", "422"]),
    ];

    [Fact]
    public async Task TheWorkerAndDevFlowAgree()
    {
        await using var devFlow = await Harness.Start();
        var endpoints = devFlow.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var devFlowJson = devFlow.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var contract = JsonNode.Parse(File.ReadAllText(Repository.Path("contracts", "devflow", "openapi.json")))!;

        foreach (var (path, request, sample, answers) in Routes)
        {
            // Dev flow's route takes the shared type, so a change to it compiles on both sides or
            // on neither.
            var endpoint = Assert.Single(endpoints, e => e.RoutePattern.RawText == path);
            var handler = endpoint.Metadata.GetMetadata<MethodInfo>()!;
            Assert.Contains(request, handler.GetParameters().Select(p => p.ParameterType));
            Assert.Equal(typeof(WorkerApi).Assembly, request.Assembly);

            // Both write it the same.
            var sent = JsonSerializer.Serialize(sample, request, WorkerApi.Json);
            Assert.Equal(JsonSerializer.Serialize(sample, request, devFlowJson), sent);

            // And it is what the contract says, field for field, every one required.
            var operation = contract["paths"]![path]!["post"]!;
            var reference = (string)operation["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!;
            Assert.Equal($"#/components/schemas/{request.Name}", reference);
            var schema = contract["components"]!["schemas"]![request.Name]!;
            var fields = JsonNode.Parse(sent)!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(fields, schema["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal(fields, schema["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));

            // Every answer the route may give is one the worker reads.
            Assert.Equal(answers, operation["responses"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        }
    }
}
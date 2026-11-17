using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Tests;

namespace DevFlow.Worker.Tests;

/// <summary>
/// A project's <c>.devflow/project.json</c>, read into <see cref="ProjectFile"/> strictly: every
/// field there, none other, each by its rule; and the type and
/// contracts/devflow/project.schema.json, which people and editors read, hold the same fields. See
/// "The format of .devflow/" in devflow/README.md.
/// </summary>
public sealed class ProjectFileTests
{
    private const string Valid = """
        {"version": 1, "check": "npm ci && npm test", "start": "npm run dev", "url": "http://localhost:5173"}
        """;

    [Fact]
    public void ProjectJsonIsReadStrictly()
    {
        Assert.True(ProjectFile.TryRead(Valid, out var file, out var error), error);
        Assert.Equal(new ProjectFile(1, "npm ci && npm test", "npm run dev", "http://localhost:5173"), file);

        // A project with nothing to start leaves start and url empty.
        Assert.True(ProjectFile.TryRead("""{"version": 1, "check": "make test", "start": "", "url": ""}""", out file, out error), error);
        Assert.Equal(new ProjectFile(1, "make test", "", ""), file);
        Assert.True(ProjectFile.TryRead("""{"version": 1, "check": "c", "start": "s", "url": "http://localhost:8080/app/"}""", out _, out error), error);

        foreach (var (json, field) in new[]
        {
            ("""{"version": 1, "start": "", "url": ""}""", "check"),
            ("""{"version": 1, "check": "", "start": "", "url": ""}""", "check"),
            ("""{"version": 1, "check": "  ", "start": "", "url": ""}""", "check"),
            ("""{"version": 1, "check": null, "start": "", "url": ""}""", "check"),
            ("""{"version": 2, "check": "c", "start": "", "url": ""}""", "version"),
            ("""{"version": "1", "check": "c", "start": "", "url": ""}""", "version"),
            ("""{"check": "c", "start": "", "url": ""}""", "version"),
            ("""{"version": 1, "check": "c", "url": ""}""", "start"),
            ("""{"version": 1, "check": "c", "start": "", "url": "", "env": {}}""", "env"),
            ("""{"version": 1, "check": "c", "start": "", "url": "http://example.com:5173"}""", "url"),
            ("""{"version": 1, "check": "c", "start": "", "url": "https://localhost:5173"}""", "url"),
            ("""{"version": 1, "check": "c", "start": "", "url": "http://localhost.example.com"}""", "url"),
            ("""{"version": 1, "check": "c", "start": "", "url": "http://user@localhost:5173"}""", "url"),
            ("""{"version": 1, "check": "c", "start": "", "url": "localhost:5173"}""", "url"),
            ("""{"version": 1, "check": "c", "start": ""}""", "url"),
            ("""[1]""", "the file"),
            ("""not json""", "the file"),
        })
        {
            Assert.False(ProjectFile.TryRead(json, out var refused, out var why), $"{json} was read as {refused}");
            Assert.Null(refused);
            Assert.StartsWith(field, why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheTypeAndTheSchemaHoldTheSameFields()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Repository.Path("contracts", "devflow", "project.schema.json")))!;
        var properties = schema["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal);
        var required = schema["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal);
        var fields = typeof(ProjectFile).GetConstructors().Single().GetParameters()
            .Select(parameter => JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!))
            .Order(StringComparer.Ordinal);

        Assert.Equal(fields, properties);
        Assert.Equal(fields, required);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal("object", (string)schema["type"]!);
        Assert.Equal(ProjectFile.Version1, (int)schema["properties"]!["version"]!["const"]!);
        Assert.Equal(1, (int)schema["properties"]!["check"]!["minLength"]!);
        Assert.Equal(ProjectFile.LocalUrlPattern, (string)schema["properties"]!["url"]!["pattern"]!);

        // Each property's type is the parameter's.
        foreach (var parameter in typeof(ProjectFile).GetConstructors().Single().GetParameters())
        {
            var type = (string)schema["properties"]![JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!)]!["type"]!;
            Assert.Equal(parameter.ParameterType == typeof(int) ? "integer" : "string", type);
        }

        Assert.NotNull(typeof(ProjectFile).GetMethod(nameof(ProjectFile.TryRead), BindingFlags.Public | BindingFlags.Static));
    }
}
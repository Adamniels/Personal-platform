using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Tests;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Setup;

/// <summary>
/// The report a setup run's agent hands back is a fixed form, as the developer's report and the
/// reader's reading are: its JSON Schema, contracts/devflow/setup.schema.json, is written from
/// <see cref="SetupReport"/>, and a report is read into that type with a missing, unknown, null or
/// mistyped field refused, and the form's own rules held. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public sealed class SetupFormTests
{
    /// <summary>A setup report in its form: guided, one variable, two things it could not
    /// settle.</summary>
    internal static JsonObject Good() => new()
    {
        ["summary"] = "Wrote a recipe with Node 26 and Postgres's client, and a check that runs the tests.",
        ["proposed"] = new JsonObject
        {
            ["preset"] = "guided",
            ["why"] = "The project has a database migration folder worth a look before it merges.",
            ["paths"] = new JsonArray("migrations/**"),
        },
        ["variables"] = new JsonArray(new JsonObject
        {
            ["name"] = "DATABASE_URL",
            ["why"] = "The tests connect to a throwaway Postgres the check starts.",
        }),
        ["unsettled"] = new JsonArray(
            new JsonObject { ["kind"] = "real_secret", ["detail"] = "The payment tests call Stripe with a live key." },
            new JsonObject { ["kind"] = "containers_in_tests", ["detail"] = "The integration tests start Postgres with Testcontainers." }),
        ["decisions"] = new JsonArray(new JsonObject
        {
            ["chosen"] = "Node 26 from NodeSource",
            ["alternative"] = "The image's Node",
            ["why"] = "The project's .node-version names 26",
            ["for_owner"] = false,
        }),
        ["handover"] = "The check installs packages with npm ci.",
    };

    /// <summary>The schema's text as the type writes it, as the contract tests write each form's.</summary>
    private static string SchemaText()
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
            SetupReport.Schema().WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    [Fact]
    public async Task TheSchemaIsWrittenFromTheType()
    {
        // contracts/devflow/setup.schema.json is what SetupReport.Schema() writes, the schema every
        // setup job hands its agent, so the type that reads a setup report and the schema that
        // shapes it hold the same fields.
        var file = Repository.Path("contracts", "devflow", "setup.schema.json");
        var generated = SchemaText();
        if (System.Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(file, generated, Ct);
        }

        Assert.True(File.Exists(file), $"{file} is missing, write it with UPDATE_CONTRACTS=1");
        Assert.True(
            await File.ReadAllTextAsync(file, Ct) == generated,
            "the setup report's type changed, regenerate contracts/devflow/setup.schema.json with UPDATE_CONTRACTS=1 and review the diff");

        // Draft-07, which Claude Code reads; every object closed and every field required; the
        // slice's fields in its order; the three presets and the four kinds of what it could not
        // settle.
        var schema = SetupReport.Schema();
        Assert.Equal("http://json-schema.org/draft-07/schema#", (string)schema["$schema"]!);
        Assert.Equal(["summary", "proposed", "variables", "unsettled", "decisions", "handover"], schema["properties"]!.AsObject().Select(pair => pair.Key));
        var proposed = schema["properties"]!["proposed"]!;
        Assert.Equal(["preset", "why", "paths"], proposed["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["fully_automatic", "guided", "only_i_merge"], proposed["properties"]!["preset"]!["enum"]!.AsArray().Select(word => (string)word!));
        Assert.Equal(["name", "why"], schema["properties"]!["variables"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));
        var unsettled = schema["properties"]!["unsettled"]!["items"]!;
        Assert.Equal(["kind", "detail"], unsettled["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["real_secret", "containers_in_tests", "macos_needed", "other"], unsettled["properties"]!["kind"]!["enum"]!.AsArray().Select(word => (string)word!));
        Assert.Equal(["chosen", "alternative", "why", "for_owner"], schema["properties"]!["decisions"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));
        foreach (var obj in Objects(schema))
        {
            Assert.False((bool)obj["additionalProperties"]!);
            Assert.Equal(
                obj["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal),
                obj["required"]!.AsArray().Select(name => (string)name!).Order(StringComparer.Ordinal));
        }

        // And a report out of its form is refused, as the slice's Acceptance names it here; the
        // rest of the form's rules are the theory below.
        foreach (var (what, error) in new[] { ("a missing field", "handover"), ("an unknown field", "outcome"), ("a null field", "summary"), ("a mistyped field", "for_owner") })
        {
            AReportIsCheckedAgainstItsForm(what, error);
        }
    }

    public static TheoryData<string, string> NotInItsForm() => new()
    {
        { "a missing field", "handover" },
        { "an unknown field", "outcome" },
        { "a null field", "summary" },
        { "a mistyped field", "for_owner" },
        { "a word that is no preset", "preset" },
        { "a word that is no kind", "kind" },
        { "a null variable", "variables: holds a null" },
        { "a null path", "proposed.paths: holds a null" },
        { "not an object", "the setup report is not a JSON object" },
        { "a blank summary", "summary: says what the setup wrote" },
        { "a blank why", "proposed.why: says why the preset is proposed" },
        { "a blank path", "proposed.paths: lists a path that is blank" },
        { "a name no shell takes", "variables: DATABASE URL is not a variable's name" },
        { "a variable without why", "variables: DATABASE_URL does not say why it is needed" },
        { "a blank detail", "unsettled: says what it could not settle" },
    };

    [Theory]
    [MemberData(nameof(NotInItsForm))]
    public void AReportIsCheckedAgainstItsForm(string what, string error)
    {
        var report = Good();
        JsonNode? answer = report;
        switch (what)
        {
            case "a missing field":
                report.Remove("handover");
                break;
            case "an unknown field":
                report["outcome"] = "done";
                break;
            case "a null field":
                report["summary"] = null;
                break;
            case "a mistyped field":
                report["decisions"]![0]!["for_owner"] = "no";
                break;
            case "a word that is no preset":
                report["proposed"]!["preset"] = "custom";
                break;
            case "a word that is no kind":
                report["unsettled"]![0]!["kind"] = "secret";
                break;
            case "a null variable":
                report["variables"]!.AsArray().Add(null);
                break;
            case "a null path":
                report["proposed"]!["paths"]!.AsArray().Add(null);
                break;
            case "not an object":
                answer = new JsonArray();
                break;
            case "a blank summary":
                report["summary"] = " ";
                break;
            case "a blank why":
                report["proposed"]!["why"] = "";
                break;
            case "a blank path":
                report["proposed"]!["paths"]!.AsArray().Add("  ");
                break;
            case "a name no shell takes":
                report["variables"]![0]!["name"] = "DATABASE URL";
                break;
            case "a variable without why":
                report["variables"]![0]!["why"] = "";
                break;
            case "a blank detail":
                report["unsettled"]![1]!["detail"] = " ";
                break;
        }

        Assert.False(SetupReport.TryRead(JsonSerializer.SerializeToElement(answer), out var read, out var said), what);
        Assert.Null(read);
        Assert.Contains(error, said, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportInItsFormReads()
    {
        Assert.True(SetupReport.TryRead(JsonSerializer.SerializeToElement(Good()), out var read, out var error), error);
        Assert.Equal(SetupPreset.Guided, read.Proposed.Preset);
        Assert.Equal(["migrations/**"], read.Proposed.Paths);
        Assert.Equal("DATABASE_URL", Assert.Single(read.Variables).Name);
        Assert.Equal([SetupUnsettledKind.RealSecret, SetupUnsettledKind.ContainersInTests], read.Unsettled.Select(each => each.Kind));
        Assert.False(Assert.Single(read.Decisions).ForOwner);

        // Nothing to propose paths for, no variables and nothing unsettled is in its form too.
        var bare = Good();
        bare["proposed"]!["paths"] = new JsonArray();
        bare["variables"] = new JsonArray();
        bare["unsettled"] = new JsonArray();
        bare["decisions"] = new JsonArray();
        Assert.True(SetupReport.TryRead(JsonSerializer.SerializeToElement(bare), out _, out error), error);
    }

    /// <summary>Every schema of an object in <paramref name="node"/>, itself included.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonObject obj when obj["properties"] is JsonObject => [obj, .. obj.SelectMany(pair => Objects(pair.Value))],
        JsonObject obj => obj.SelectMany(pair => Objects(pair.Value)),
        JsonArray array => array.SelectMany(Objects),
        _ => [],
    };
}
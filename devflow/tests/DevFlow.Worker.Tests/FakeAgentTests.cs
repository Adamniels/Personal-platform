using System.Text.Json.Nodes;

using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>The fake agent: a prepared report and prepared changes, so the worker's own tests
/// spend no usage.</summary>
public sealed class FakeAgentTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("devflow-fake-agent-");

    [Fact]
    public async Task ItAnswersThePreparedReport()
    {
        File.WriteAllText(Path.Combine(_work.FullName, "old.txt"), "old");
        var report = new JsonObject { ["summary"] = "Added the empty state.", ["files_changed"] = new JsonArray("src/list.tsx") };
        var agent = new FakeAgent(report, new Dictionary<string, string?>
        {
            ["src/list.tsx"] = "export const empty = true;\n",
            ["old.txt"] = null,
        });

        var result = await agent.Run(new AgentRun(_work.FullName, "brief", "{}", "opus", Limits, new AgentCredential("unused")), Ct);

        Assert.True(result.Succeeded);
        Assert.True(JsonNode.DeepEquals(report, result.Report));
        // A copy, so a caller changing it changes nothing prepared.
        Assert.NotSame(report, result.Report);
        Assert.Empty(result.Usage);
        Assert.Equal("", result.Stderr);
        Assert.Null(result.AuthMethod);
        Assert.Equal("export const empty = true;\n", File.ReadAllText(Path.Combine(_work.FullName, "src", "list.tsx")));
        Assert.False(File.Exists(Path.Combine(_work.FullName, "old.txt")));
    }

    [Fact]
    public async Task ItWritesAFewLinesToTheRunsLog()
    {
        File.WriteAllText(Path.Combine(_work.FullName, "old.txt"), "old");
        var log = new MemoryRunLog(new WatchedClock());
        var agent = new FakeAgent(new JsonObject(), new Dictionary<string, string?> { ["src/list.tsx"] = "x", ["old.txt"] = null });

        await agent.Run(new AgentRun(_work.FullName, "brief", "{}", "opus", Limits, new AgentCredential("unused"), Log: log), Ct);

        Assert.Equal(
            [
                (RunLineKind.Text, "The fake agent makes 2 changes."),
                (RunLineKind.Tool, "Write: src/list.tsx"),
                (RunLineKind.Tool, "Bash: rm old.txt"),
                (RunLineKind.Text, "Done."),
            ],
            log.Lines.Select(line => (line.Kind, line.Text)));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/outside.txt")]
    [InlineData("src/../../outside.txt")]
    public async Task ItChangesNothingOutsideTheWorkingCopy(string path)
    {
        var agent = new FakeAgent(new JsonObject(), new Dictionary<string, string?> { [path] = "x" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.Run(new AgentRun(_work.FullName, "brief", "{}", "opus", Limits, new AgentCredential("unused")), Ct));
    }

    public void Dispose() => _work.Delete(recursive: true);
}
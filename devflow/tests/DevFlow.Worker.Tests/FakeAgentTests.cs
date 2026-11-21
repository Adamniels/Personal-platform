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
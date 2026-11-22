using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Setup;

/// <summary>
/// A setup job on the worker, in process against Dev flow with the fake code host's real git: the
/// worker makes the setup's branch at the default branch, runs the agent with the setup report's
/// schema in its copy, commits what it left, and pushes the branch without running any check, since
/// a worker started from the base image cannot install the project's tools. See "Setting up a
/// project" in devflow/README.md.
/// </summary>
public sealed class SetupWorkerTests
{
    private const string SetupBranch = "devflow/setup";

    private const string Recipe = "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nRUN apt-get update && apt-get install -y postgresql-client\n";

    private const string ProjectJson = """{"version": 1, "check": "npm ci && npm test", "start": "", "url": ""}""";

    /// <summary>
    /// A setup job, made from the job Dev flow hands out, a build, keeping its id so the push key
    /// route still answers for it: no card, the setup's branch from the default branch, the setup
    /// report's schema and no check, start or address. Its result names another branch than the
    /// build's, so Dev flow does not take it; the test reads what the worker sent.
    /// </summary>
    private static async Task<JsonElement> SetupJob(Bench bench, JsonObject? toFix = null)
    {
        var job = JsonNode.Parse((await bench.NextJob()).GetRawText())!.AsObject();
        job["kind"] = Job.Setup;
        job["card"] = null;
        job["branch"] = SetupBranch;
        job["base"] = "main";
        job["brief"] = "Read the repository and write .devflow/.";
        job["report_schema"] = SetupReport.Schema();
        job["check"] = "";
        job["start"] = "";
        job["url"] = "";
        job["to_fix"] = toFix;
        return JsonSerializer.SerializeToElement(job);
    }

    /// <summary>A setup report in its form, as the agent answers it.</summary>
    private static JsonObject Report() => new()
    {
        ["summary"] = "Wrote a recipe with Postgres's client.",
        ["proposed"] = new JsonObject { ["preset"] = "only_i_merge", ["why"] = "A new project.", ["paths"] = new JsonArray() },
        ["variables"] = new JsonArray(),
        ["unsettled"] = new JsonArray(),
        ["decisions"] = new JsonArray(),
        ["handover"] = "",
    };

    [Fact]
    public async Task ASetupJobPushesWithoutChecking()
    {
        await using var bench = await Bench.Start();
        var main = (await bench.RemoteHead("main"))!;
        var job = await SetupJob(bench);
        var jobId = job.GetProperty("job_id").GetGuid();
        var agent = new ScriptedAgent(
            async (run, _) =>
            {
                // The agent writes both files and commits neither, as a run that ran out of turns
                // to commit would leave them.
                await ScriptedAgent.Write(run, ".devflow/Dockerfile", Recipe);
                await ScriptedAgent.Write(run, ".devflow/project.json", ProjectJson);
            },
            () => AgentResult.Success(Report(), new JsonObject(), "", "oauth_token"));

        await bench.Build(agent).Run(job, Ct);

        // The agent ran in its copy of the setup's branch, made at the default branch, with the
        // setup report's schema.
        Assert.NotNull(agent.Given);
        Assert.Equal(SetupReport.Schema().ToJsonString(), JsonNode.Parse(agent.Given.ReportSchema)!.ToJsonString());

        // What it left was committed as the worker's commit and pushed to the setup's branch, on
        // top of the default branch.
        var head = await bench.RemoteHead(SetupBranch);
        Assert.NotNull(head);
        Assert.Equal(main, await Bench.Git(bench.Remote, "rev-parse", $"{head}^"));
        Assert.Equal(BuildJob.LeftUncommitted, await Bench.Git(bench.Remote, "log", "-1", "--format=%s", head!));
        Assert.Equal(Recipe, await Bench.Git(bench.Remote, "show", $"{head}:.devflow/Dockerfile") + "\n");
        Assert.Equal(ProjectJson, await Bench.Git(bench.Remote, "show", $"{head}:.devflow/project.json"));
        Assert.Equal(main, await bench.RemoteHead("main"));

        // No check ran, and nothing was started for pictures: the worker started no shell command
        // as the agent, and said no progress.
        Assert.DoesNotContain(bench.AsAgent.Started, started => started.Spec.FileName == "sh");
        Assert.Empty(bench.Starter.Started);
        Assert.DoesNotContain(bench.Handler.Calls, call => call.Path.EndsWith("/progress", StringComparison.Ordinal));

        // The result: the report as the agent answered it, no check, the commit pushed, and no
        // pictures.
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Equal("Wrote a recipe with Postgres's client.", (string)sent["report"]!["summary"]!);
        Assert.Null(sent["check"]);
        Assert.Null(sent["screenshots"]);
        Assert.Equal(SetupBranch, (string)sent["branch"]!);
        Assert.Equal(head, (string)sent["head_commit"]!);
        Assert.Empty(Bench.Files(bench.Folders.Work));
    }

    [Fact]
    public async Task ASetupFixStartsFromTheCommitItWasHanded()
    {
        await using var bench = await Bench.Start();
        var first = await SetupJob(bench);
        await bench.Build(new ScriptedAgent(
            async (run, _) => await ScriptedAgent.Write(run, ".devflow/Dockerfile", Recipe),
            () => AgentResult.Success(Report(), new JsonObject(), "", "oauth_token"))).Run(first, Ct);
        var pushed = (await bench.RemoteHead(SetupBranch))!;

        // A fix of the setup's pull request starts on its branch at the commit handed, and pushes
        // on top of it, again without a check.
        var fix = JsonNode.Parse(first.GetRawText())!.AsObject();
        fix["job_id"] = first.GetProperty("job_id").GetGuid();
        fix["to_fix"] = new JsonObject { ["commit"] = pushed, ["round"] = 1 };
        var agent = new ScriptedAgent(
            async (run, _) => await ScriptedAgent.Commit(run, ".devflow/project.json", ProjectJson, "Add project.json"),
            () => AgentResult.Success(Report(), new JsonObject(), "", "oauth_token"));

        await bench.Build(agent).Run(JsonSerializer.SerializeToElement(fix), Ct);

        var head = (await bench.RemoteHead(SetupBranch))!;
        Assert.NotEqual(pushed, head);
        Assert.Equal(pushed, await Bench.Git(bench.Remote, "rev-parse", $"{head}^"));
        Assert.DoesNotContain(bench.AsAgent.Started, started => started.Spec.FileName == "sh");
    }
}
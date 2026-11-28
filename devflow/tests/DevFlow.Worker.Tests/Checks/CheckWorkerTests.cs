using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Checks;

/// <summary>
/// A check job on the worker, in process against Dev flow with the fake code host's real git: the
/// worker clones the default branch's commit it was handed afresh, runs the project's check there,
/// then, when the job has a start and an address, starts the project and takes its address at both
/// sizes, and sends the exit code, the output and the pictures. It runs no agent and pushes
/// nothing. See "Setting up a project" in devflow/README.md.
/// </summary>
public sealed class CheckWorkerTests
{
    private const string Start = "npm ci && npm run dev";
    private const string Url = "http://localhost:5173";

    /// <summary>A check job of <paramref name="commit"/>, made from the job Dev flow hands out, a
    /// build, under a job id of its own, so the result goes nowhere: no card, the default branch,
    /// no brief, model or token, the check's 30 minutes.</summary>
    private static async Task<JsonElement> CheckJob(Bench bench, string commit, string check, string start = Start, string url = Url)
    {
        var job = JsonNode.Parse((await bench.NextJob()).GetRawText())!.AsObject();
        job["job_id"] = Guid.CreateVersion7();
        job["kind"] = Job.RecipeCheck;
        job["card"] = null;
        job["branch"] = "main";
        job["base"] = "main";
        job["brief"] = "";
        job["report_schema"] = null;
        job["check"] = check;
        job["credential"] = null;
        job["model"] = "";
        job["start"] = start;
        job["url"] = url;
        job["limits"] = new JsonObject { ["turns"] = 0, ["budget_usd"] = 0, ["minutes"] = 30 };
        job["to_check"] = new JsonObject { ["version"] = "1", ["commit"] = commit };
        return JsonSerializer.SerializeToElement(job);
    }

    /// <summary>The remote's refs, each with its commit, as the test reads them.</summary>
    private static async Task<string> Refs(Bench bench) => await Bench.Git(bench.Remote, "for-each-ref", "--format=%(refname) %(objectname)");

    [Fact]
    public async Task ACheckClonesRunsAndLooks()
    {
        await using var bench = await Bench.Start();
        var handed = (await bench.RemoteHead("main"))!;
        var job = await CheckJob(bench, handed, $"git rev-parse HEAD > {bench.OutFile} && ls -A >> {bench.OutFile} && echo checked");
        // The default branch moves after the check was handed out: the worker checks the commit it
        // was handed, not the newer head.
        await bench.PushAsSomeoneElse("main", "main", "A later commit", ("later.txt", "later\n"));
        string? startedAt = null;
        bench.Starter.OnStart = async spec => startedAt = await Bench.Git(spec.WorkingDirectory, "rev-parse", "HEAD");
        var before = await Refs(bench);
        var jobId = job.GetProperty("job_id").GetGuid();
        var paths = bench.Folders.For(jobId);
        var agent = new ScriptedAgent((_, _) => Task.CompletedTask);

        await bench.Build(agent).Run(job, Ct);

        // The check ran on a clean copy of exactly the commit handed, as the agent user, with CI set.
        var output = (await File.ReadAllTextAsync(bench.OutFile, Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(handed, output[0]);
        Assert.Equal([".git", ".gitignore", "README.md"], output[1..].Order(StringComparer.Ordinal));
        var check = Assert.Single(bench.AsAgent.Started, started => started.Spec is { FileName: "sh" } && started.Spec.Arguments[1].Contains("echo checked", StringComparison.Ordinal));
        Assert.Equal(paths.Check, check.Spec.WorkingDirectory);
        Assert.Contains(check.Spec.Environment, pair => pair.Key == "CI" && pair.Value == "true");

        // Then the project started from a clean copy of the same commit, its address taken at both
        // sizes, and ended.
        Assert.Equal(handed, startedAt);
        var project = Assert.Single(bench.Starter.Started);
        Assert.Equal(1, project.Ends);
        Assert.Equal(
            [
                $"screenshot --full-page --viewport-size 1280,800 {Url}/ {paths.Pictures}/home-desktop.png browsers={bench.Pages.BrowsersPath}",
                $"screenshot --full-page --viewport-size 390,844 {Url}/ {paths.Pictures}/home-phone.png browsers={bench.Pages.BrowsersPath}",
            ],
            bench.Pictures);

        // The result: the check ran, its exit code and output, no report and no commit, and the two
        // pictures, named and with their bytes.
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Null(sent["report"]);
        Assert.Null(sent["failure"]);
        Assert.Null(sent["head_commit"]);
        Assert.Equal("main", (string)sent["branch"]!);
        Assert.Equal(0, (int)sent["check"]!["exit_code"]!);
        Assert.EndsWith("checked\n", (string)sent["check"]!["output"]!, StringComparison.Ordinal);
        Assert.Equal(["home-desktop", "home-phone"], sent["screenshots"]!["names"]!.AsArray().Select(name => (string)name!));
        var pictures = sent["pictures"]!.AsArray();
        Assert.Equal(["home-desktop", "home-phone"], pictures.Select(picture => (string)picture!["name"]!));
        Assert.All(pictures, picture => Assert.True(Convert.FromBase64String((string)picture!["png"]!).AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47])));

        // The check's log was sent as it ran, with the worker's steps. This job is the test's own, so
        // Dev flow does not know it, and what the worker sent is read whatever it answered.
        var system = bench.Handler.Calls
            .Where(call => call.Path == WorkerApi.LogPath(jobId))
            .SelectMany(call => JsonNode.Parse(call.Body)!["lines"]!.AsArray())
            .Where(line => (string)line!["kind"]! == "system")
            .Select(line => (string)line!["text"]!)
            .ToList();
        Assert.Contains("Cloning Adamniels/Personal-platform", system);
        Assert.Contains(system, line => line.StartsWith("Running the check: git rev-parse HEAD > ", StringComparison.Ordinal));
        Assert.Contains("Taking pictures", system);
        // And the check's output, as it was written.
        var checkLines = bench.Handler.Calls
            .Where(call => call.Path == WorkerApi.LogPath(jobId))
            .SelectMany(call => JsonNode.Parse(call.Body)!["lines"]!.AsArray())
            .Where(line => (string)line!["kind"]! == "check")
            .Select(line => (string)line!["text"]!);
        Assert.Equal(["checked"], checkLines);

        // No agent ran, no push key was asked for, nothing was committed or pushed.
        Assert.Null(agent.Given);
        Assert.Equal(before, await Refs(bench));
        Assert.DoesNotContain(bench.Handler.Calls, call => call.Path.EndsWith("/push-key", StringComparison.Ordinal));
        Assert.DoesNotContain(
            bench.AsRoot.Started.Concat(bench.AsAgent.Started),
            started => started.Spec.FileName == "git" && started.Spec.Arguments.Any(argument => argument is "push" or "commit"));
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Handover));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task AFailedCheckSendsItsOutputAndTakesNoPictures()
    {
        await using var bench = await Bench.Start();
        var job = await CheckJob(bench, (await bench.RemoteHead("main"))!, "echo the tests failed; exit 3");

        await bench.Build(new ScriptedAgent((_, _) => Task.CompletedTask)).Run(job, Ct);

        // The check ran, so the run succeeded with its exit code; pictures of a failed check are not
        // taken, as of a build's.
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Equal(3, (int)sent["check"]!["exit_code"]!);
        Assert.Contains("the tests failed", (string)sent["check"]!["output"]!, StringComparison.Ordinal);
        Assert.Null(sent["pictures"]);
        Assert.Empty(bench.Starter.Started);
    }

    [Fact]
    public async Task ACommitThatIsNotThereFailsOutside()
    {
        // The commit handed is not in the repository: nothing is checked, and the failure is the
        // machine's or the code host's, never a verdict on the recipe.
        await using var bench = await Bench.Start();
        var job = await CheckJob(bench, new string('a', 40), "echo checked");

        await bench.Build(new ScriptedAgent((_, _) => Task.CompletedTask)).Run(job, Ct);

        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.False((bool)sent["failure"]!["in_card"]!);
        Assert.Contains(new string('a', 40), (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
        Assert.Null(sent["check"]);
        Assert.DoesNotContain(bench.AsAgent.Started, started => started.Spec is { FileName: "sh" });
    }
}
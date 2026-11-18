using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Reviews;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A review job on the worker, in process against Dev flow with the fake code host's real git: the
/// reviewer gets a copy at the commit and the evidence beside it as files, runs read only, and
/// nothing is committed or pushed. See "Review" in devflow/README.md.
/// </summary>
public sealed class ReviewWorkerTests
{
    /// <summary>The files a review is given beside the copy.</summary>
    private static readonly string[] Evidence = ["diff.patch", "card.md", "report.json", "check.txt", "earlier.json"];

    /// <summary>A verdict in its form, changes with one blocking finding.</summary>
    private static JsonObject Verdict() => new()
    {
        ["verdict"] = "changes",
        ["summary"] = "The greeting has no test.",
        ["findings"] = new JsonArray(new JsonObject { ["blocking"] = true, ["file"] = "greeting.txt", ["line"] = 1, ["what"] = "No test reads it", ["why"] = "The card asks for one" }),
        ["for_owner"] = new JsonArray(),
    };

    [Fact]
    public async Task TheReviewerGetsTheDiffAndTheEvidenceAsFiles()
    {
        await using var bench = await Bench.Start(check: "echo all 3 checks passed");
        var head = await bench.BuiltAndOpened();
        var job = await bench.NextJob();
        Assert.Equal(Job.Review, job.GetProperty("kind").GetString());

        string[] entries = [];
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string? copyHead = null;
        string? greeting = null;
        var agent = new ScriptedAgent(
            async (run, _) =>
            {
                // What the reviewer finds where it runs: the copy and the review folder beside it.
                entries = [.. Directory.GetFileSystemEntries(run.WorkingDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
                foreach (var name in Evidence)
                {
                    files[name] = await File.ReadAllTextAsync(Path.Combine(run.WorkingDirectory, "review", name), Ct);
                }

                var copy = Path.Combine(run.WorkingDirectory, "copy");
                copyHead = await Bench.Git(copy, "rev-parse", "HEAD");
                greeting = await File.ReadAllTextAsync(Path.Combine(copy, "greeting.txt"), Ct);
            },
            () => AgentResult.Success(Verdict(), [], "", "oauth_token"));

        await bench.Review(agent).Run(job, Ct);

        Assert.Equal(["copy", "review"], entries);

        // The agent's copy is at the commit the build left.
        Assert.Equal(head, copyHead);
        Assert.Equal("Hello\n", greeting);

        // The diff from the base to the commit: the build's change, and nothing of the base.
        var diff = files["diff.patch"];
        Assert.Contains("+++ b/greeting.txt", diff, StringComparison.Ordinal);
        Assert.Contains("+Hello", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", diff, StringComparison.Ordinal);

        // The card as it was picked, the build's report as it came, and the check as the build's
        // worker ran it.
        Assert.Equal("#1 Add the greeting\n\nBuild the thing of card 1.\n", files["card.md"]);
        Assert.True(JsonNode.DeepEquals(Jobs.GoodReport(), JsonNode.Parse(files["report.json"])), files["report.json"]);
        Assert.Contains("$ echo all 3 checks passed", files["check.txt"], StringComparison.Ordinal);
        Assert.Contains("exit 0", files["check.txt"], StringComparison.Ordinal);
        Assert.Contains("\nall 3 checks passed", files["check.txt"], StringComparison.Ordinal);
        // A first review has no earlier rounds.
        Assert.Equal("[]\n", files["earlier.json"]);

        // The reviewer's brief and the verdict's form, read only.
        var given = agent.Given!;
        Assert.Equal(AgentAccess.ReadOnly, given.Access);
        Assert.Equal(job.GetProperty("brief").GetString(), given.Brief);
        Assert.True(JsonNode.DeepEquals(ReviewVerdict.Schema(), JsonNode.Parse(given.ReportSchema)));

        // The verdict went back for the commit the worker checked out, and nothing of the job is
        // left on the machine.
        var jobId = job.GetProperty("job_id").GetGuid();
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Equal(head, (string)sent["head_commit"]!);
        Assert.Equal("changes", (string)sent["report"]!["verdict"]!);
        Assert.Null(sent["check"]);
        // A changes verdict sends the card to its first fix round.
        Assert.Equal("fixing", (await bench.Jobs.Card(1)).State);
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Handover));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task TheReviewerCannotWrite()
    {
        await using var bench = await Bench.Start();
        var head = await bench.BuiltAndOpened();
        var job = await bench.NextJob();
        var jobId = job.GetProperty("job_id").GetGuid();
        var calledBefore = bench.Handler.Calls.Count;
        var rootBefore = bench.AsRoot.Started.Count;

        // Claude Code, as it answers a run that tried to write and was refused.
        var answer = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Claude", "refused-write.json")))!;
        answer["structured_output"] = Verdict();
        var starter = new FakeStarter(
            new ProcessOutcome(0, """{"loggedIn": true, "authMethod": "oauth_token"}""", "", false),
            new ProcessOutcome(0, answer.ToJsonString(), "", false));
        var agent = new ClaudeCodeAgent(starter, ClaudeCodeOptions.Default, new WorkerLog(TextWriter.Null, bench.Clock));

        await bench.Review(agent).Run(job, Ct);

        // Read only: the default permission mode, no prompts, three tools that read, never a bypass.
        var arguments = starter.Started[1].Spec.Arguments;
        Assert.Equal("default", After(arguments, "--permission-mode"));
        Assert.Equal("none", After(arguments, "--permission-prompts"));
        Assert.Equal("Read,Grep,Glob", After(arguments, "--allowedTools"));
        Assert.DoesNotContain(arguments, argument => argument.Contains("bypass", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("claude-opus-5-5", After(arguments, "--model"));

        // A refused write is recorded, and does not fail a review: it cannot write by construction.
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Equal("Write", (string)Assert.Single(sent["permission_denials"]!.AsArray())!["tool_name"]!);

        // Nothing is pushed after a review: no push, no push key asked for, the branch where the
        // build left it.
        Assert.DoesNotContain(bench.AsRoot.Started.Skip(rootBefore), started => started.Spec.Arguments.Contains("push"));
        Assert.DoesNotContain(bench.AsAgent.Started, started => started.Spec.Arguments.Contains("push"));
        Assert.DoesNotContain(bench.Handler.Calls.Skip(calledBefore), call => call.Path.EndsWith("/push-key", StringComparison.Ordinal));
        Assert.Equal(head, await bench.RemoteHead());
    }

    /// <summary>The argument after <paramref name="flag"/>, which must appear once.</summary>
    private static string After(IReadOnlyList<string> arguments, string flag)
    {
        Assert.Single(arguments, argument => argument == flag);
        var at = arguments.ToList().IndexOf(flag);
        return arguments[at + 1];
    }
}
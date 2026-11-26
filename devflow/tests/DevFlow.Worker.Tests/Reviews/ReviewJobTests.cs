using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Reviews;

/// <summary>
/// The jobs that push nothing, stopped by the owner: a review, a reading and a check each end
/// what runs, send the result <c>stopped</c> with no commit, and leave the remote as it was. See
/// "The worker" in devflow/README.md.
/// </summary>
public sealed class ReviewJobTests
{
    [Fact]
    public async Task AStoppedReviewPushesNothing()
    {
        // A review stopped while its reviewer reads.
        await using (var bench = await Bench.Start())
        {
            await bench.BuiltAndOpened();
            var job = await bench.NextJob();
            Assert.Equal(Job.Review, job.GetProperty("kind").GetString());
            var before = await bench.Refs();

            await bench.Review(StoppedAgent(bench)).Run(job, Ct);

            Assert.Equal(before, await bench.Refs());
            var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
            Assert.Equal(("stopped", (string?)null), ((string)sent["outcome"]!, (string?)sent["head_commit"]));
            Assert.Null(sent["report"]);
            Assert.Equal("stopped_by_owner", (await bench.Jobs.Runs())[^1].Result);
        }

        // A reading stopped.
        await using (var bench = await Bench.Start(read: false))
        {
            var job = await bench.NextJob();
            Assert.Equal(Job.Read, job.GetProperty("kind").GetString());
            var before = await bench.Refs();

            await bench.Read(StoppedAgent(bench)).Run(job, Ct);

            Assert.Equal(before, await bench.Refs());
            var sent = bench.SentResult();
            Assert.Equal(("stopped", (string?)null), ((string)sent["outcome"]!, (string?)sent["head_commit"]));
            Assert.Equal("stopped_by_owner", (await bench.Run()).Result);
        }

        // A check stopped while it runs, made from the job Dev flow hands out under a job id of its
        // own, as the check's own tests make one; Dev flow's answer stop is the handler's.
        await using (var bench = await Bench.Start())
        {
            var job = await CheckJob(bench, (await bench.RemoteHead("main"))!, $"touch '{bench.OutFile}'; sleep 60; echo finished");
            var jobId = job.GetProperty("job_id").GetGuid();
            var before = await bench.Refs();

            var running = bench.Build(new ScriptedAgent((_, _) => Task.CompletedTask)).Run(job, Ct);
            for (var i = 0; i < 400 && !File.Exists(bench.OutFile) && !running.IsCompleted; i++)
            {
                await Task.Delay(25, Ct);
            }

            Assert.False(running.IsCompleted);
            bench.Handler.Stop(jobId);
            await bench.MoveUntil(() => running.IsCompleted);
            await running;

            Assert.Equal(before, await bench.Refs());
            var sent = bench.SentResult(jobId);
            Assert.Equal(("stopped", (string?)null), ((string)sent["outcome"]!, (string?)sent["head_commit"]));
            Assert.Null(sent["check"]);
            Assert.Null(sent["pictures"]);
            Assert.DoesNotContain("finished", bench.Logged("check", jobId));
        }
    }

    /// <summary>An agent the owner stops while it works, which ends as Claude Code ends on
    /// <c>SIGTERM</c>, with no report.</summary>
    private static ScriptedAgent StoppedAgent(Bench bench) => new(
        async (run, _) =>
        {
            await bench.StopTheRun();
            await bench.MoveUntil(() => run.Stop.IsCancellationRequested);
        },
        () => AgentResult.Stopped([], "", "oauth_token"));

    /// <summary>A check job of <paramref name="commit"/>, made from the job Dev flow hands out under a
    /// job id of its own, as "CheckWorkerTests" makes one.</summary>
    private static async Task<JsonElement> CheckJob(Bench bench, string commit, string check)
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
        job["claude_token"] = "";
        job["model"] = "";
        job["start"] = "";
        job["url"] = "";
        job["limits"] = new JsonObject { ["turns"] = 0, ["budget_usd"] = 0, ["minutes"] = 30 };
        job["to_check"] = new JsonObject { ["version"] = "1", ["commit"] = commit };
        return JsonSerializer.SerializeToElement(job);
    }
}
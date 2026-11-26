using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Tests.Values;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A run's log: the lines its worker posts while it runs, kept under the run in their order, each
/// scrubbed of the project's values, a retried line never stored twice, at most 20,000 a run; and
/// each post its heartbeat, carrying its usage so far and answered with whether to stop. Against
/// real Postgres and the fake code host. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class RunLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Jobs Jobs, Guid Id, string Branch)> Handed()
    {
        var jobs = await Jobs.Start();
        var job = await jobs.Job();
        return (jobs, (Guid)job["job_id"]!, (string)job["branch"]!);
    }

    /// <summary>A line as the worker sends it.</summary>
    internal static JsonObject Line(long seq, string text, string kind = "text", DateTimeOffset? at = null) => new()
    {
        ["seq"] = seq,
        ["at"] = (at ?? new DateTimeOffset(2026, 11, 17, 8, 1, 0, TimeSpan.Zero)).AddSeconds(seq),
        ["kind"] = kind,
        ["text"] = text,
    };

    /// <summary>A post of <paramref name="lines"/> as <paramref name="worker"/> sends it, with
    /// <paramref name="usage"/> unless null.</summary>
    internal static JsonObject Post(Guid worker, IEnumerable<JsonNode> lines, JsonObject? usage = null)
    {
        var body = new JsonObject { ["worker_id"] = worker, ["lines"] = new JsonArray([.. lines]) };
        if (usage is not null)
        {
            body["usage"] = usage;
        }

        return body;
    }

    /// <summary>The run's stored lines, by seq, read past row level security.</summary>
    internal static async Task<List<(long Seq, string Kind, string Text, DateTime At)>> Stored(Jobs jobs, Guid run)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(long, string, string, DateTime)>(new CommandDefinition(
            "SELECT seq, kind, text, at FROM run_log_lines WHERE run_id = @run ORDER BY seq",
            new { run },
            cancellationToken: Ct))];
    }

    [Fact]
    public async Task LinesAreStoredInOrder()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;
        Assert.Equal(StatusCodes.Status204NoContent, (await ProjectValuesTests.Give(jobs.Routes.App, jobs.Project, ScrubTests.Name, ScrubTests.Value)).Response.StatusCode);

        var first = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, "Cloning", "system"), Line(2, $"Connecting with {ScrubTests.Value}")]));
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);

        // The second post's lines come in any order, and a gap is allowed.
        var second = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(5, "npm test", "tool"), Line(3, $"curl -u ci:{ScrubTests.UrlEncoded}", "tool_result"), Line(4, "Tests: 12 passed", "check")]));
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);

        var lines = await Stored(jobs, id);
        Assert.Equal([1L, 2, 3, 4, 5], lines.Select(line => line.Seq));
        Assert.Equal(["system", "text", "tool_result", "check", "tool"], lines.Select(line => line.Kind));
        Assert.Equal("Cloning", lines[0].Text);
        Assert.Equal($"Connecting with {ScrubTests.Placeholder}", lines[1].Text);
        Assert.Equal($"curl -u ci:{ScrubTests.Placeholder}", lines[2].Text);
        Assert.All(lines, line => Assert.False(ScrubTests.Holds(line.Text), line.Text));
        Assert.Equal(new DateTime(2026, 11, 17, 8, 1, 2, DateTimeKind.Utc), lines[1].At.ToUniversalTime());
    }

    [Fact]
    public async Task ALongTextIsCut()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;

        var response = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, new string('x', LogLine.MostText + 1)), Line(2, new string('y', LogLine.MostText))]));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var lines = await Stored(jobs, id);
        Assert.Equal(LogLine.MostText, lines[0].Text.Length);
        Assert.EndsWith(LogLine.CutMark, lines[0].Text, StringComparison.Ordinal);
        Assert.Equal(new string('y', LogLine.MostText), lines[1].Text);
    }

    [Fact]
    public async Task ARetriedLineIsNotStoredTwice()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, "first"), Line(2, "second")]))).Response.StatusCode);

        // The post retried with a line more, and line 1 sent again with other text: only line 3
        // is new, and line 1 keeps what it first said.
        var retried = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, "first, again"), Line(2, "second"), Line(3, "third"), Line(3, "third, twice in one post")]));

        Assert.Equal(StatusCodes.Status200OK, retried.Response.StatusCode);
        var lines = await Stored(jobs, id);
        Assert.Equal(["first", "second", "third"], lines.Select(line => line.Text));
    }

    [Fact]
    public async Task OnlyTheHoldingWorkerIsHeard()
    {
        var (jobs, id, branch) = await Handed();
        await using var _jobs = jobs;
        await jobs.SayHello(Jobs.SecondWorker);

        // Another worker of the project, and a run that is not one: 404, and nothing is kept.
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.SecondWorker, [Line(1, "hello")]))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.LogPath(Guid.NewGuid()), Post(Jobs.FirstWorker, [Line(1, "hello")]))).Response.StatusCode);
        Assert.Empty(await Stored(jobs, id));

        // Each rule broken is 422 naming its field.
        await Invalid(jobs, id, new JsonObject { ["lines"] = new JsonArray(Line(1, "no worker")) }, "worker_id");
        await Invalid(jobs, id, new JsonObject { ["worker_id"] = Jobs.FirstWorker }, "lines");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [Line(1, "what", "thinking")]), "lines[0].kind");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [Line(1, "fine"), Line(2, "")]), "lines[1].text");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [Line(0, "from nothing")]), "lines[0].seq");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [new JsonObject { ["seq"] = 1, ["kind"] = "text", ["text"] = "when?" }]), "lines[0].at");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, Enumerable.Range(1, LogRequest.MostLines + 1).Select(seq => (JsonNode)Line(seq, "many"))), "lines");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [], new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 2 }), "usage");
        await Invalid(jobs, id, Post(Jobs.FirstWorker, [], new JsonObject { ["input_tokens"] = -1, ["output_tokens"] = 2, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = 0 }), "usage");
        Assert.Empty(await Stored(jobs, id));

        // The run finished: its log is closed.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, "hello")]))).Response.StatusCode);
        Assert.Empty(await Stored(jobs, id));
    }

    private static async Task Invalid(Jobs jobs, Guid id, JsonObject body, string field)
    {
        var response = await jobs.Post(WorkerApi.LogPath(id), body);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
        var errors = (await Workers.Workers.Json(response))["errors"]!.AsObject();
        Assert.True(errors.ContainsKey(field), $"{field} not named in {errors.ToJsonString()}");
    }

    [Fact]
    public async Task ARunKeepsAtMostItsLines()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;

        // 19,998 lines already held, staged straight into the table rather than posted in 40.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO run_log_lines (project_id, user_id, run_id, seq, at, kind, text)
                SELECT @project, @user, @id, n, now(), 'text', 'line ' || n FROM generate_series(1, 19998) n
                """,
                new { project = jobs.Project, user = Jobs.User, id },
                cancellationToken: Ct));
        }

        // Four more: two are kept, the 19,999th and the 20,000th, and one system line says the
        // rest was not, in place of the first not kept.
        var full = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(19999, "kept"), Line(20000, "kept too"), Line(20001, "dropped"), Line(20002, "dropped too")]));
        Assert.Equal(StatusCodes.Status200OK, full.Response.StatusCode);

        // And after that nothing more, not even another note.
        var after = await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(20003, "dropped as well")]));
        Assert.Equal(StatusCodes.Status200OK, after.Response.StatusCode);

        var lines = await Stored(jobs, id);
        Assert.Equal(20001, lines.Count);
        Assert.Equal(("kept", "kept too"), (lines[19998].Text, lines[19999].Text));
        var last = lines[^1];
        Assert.Equal((20001L, "system", "The rest of this run's log was not kept"), (last.Seq, last.Kind, last.Text));
    }

    [Fact]
    public async Task UsageSoFarIsKept()
    {
        var (jobs, id, branch) = await Handed();
        await using var _jobs = jobs;

        var usage = new JsonObject { ["input_tokens"] = 12, ["cache_creation_input_tokens"] = 3400, ["cache_read_input_tokens"] = 56000, ["output_tokens"] = 780 };
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, [Line(1, "working")], usage))).Response.StatusCode);
        var running = Assert.Single(await jobs.Runs());
        Assert.Equal((12L, 3400L, 56000L, 780L), (running.InputTokens, running.CacheCreationInputTokens, running.CacheReadInputTokens, running.OutputTokens));
        Assert.Null(running.Result);

        // A post without usage leaves the figures as they were.
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), Post(Jobs.FirstWorker, []))).Response.StatusCode);
        Assert.Equal(780L, Assert.Single(await jobs.Runs()).OutputTokens);

        // The result's usage replaces them.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch))).Response.StatusCode);
        var finished = Assert.Single(await jobs.Runs());
        Assert.Equal((41L, 18204L, 512870L, 6113L), (finished.InputTokens, finished.CacheCreationInputTokens, finished.CacheReadInputTokens, finished.OutputTokens));
    }

    [Fact]
    public async Task TheAnswerSaysWhetherToStop()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;

        foreach (var body in new[] { Post(Jobs.FirstWorker, [Line(1, "working")]), Post(Jobs.FirstWorker, []) })
        {
            var response = await jobs.Post(WorkerApi.LogPath(id), body);
            Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
            Assert.Equal("""{"stop":false}""", (await Workers.Workers.Json(response)).ToJsonString());
        }
    }
}
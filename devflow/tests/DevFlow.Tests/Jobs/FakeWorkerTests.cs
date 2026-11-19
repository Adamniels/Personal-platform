using System.Diagnostics;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// The fake worker, in the fake mode only: one call takes a project's next job and completes it
/// with a prepared result through the same code a real worker's result goes through, after a commit
/// on the job's branch in the fake code host, so an acceptance test drives handing out, the result
/// and what follows without a container. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class FakeWorkerTests
{
    private const string Path = "/devflow/api/fake/worker/run";

    private static Task<HttpContext> Run(Jobs jobs, JsonObject result, Guid? project = null) =>
        jobs.Routes.App.SendJson("POST", Path, Contract.Token, new JsonObject
        {
            ["project_id"] = project ?? jobs.Project,
            ["result"] = result,
        }.ToJsonString());

    /// <summary>A prepared result: what a worker sends but its id, branch and head commit, which
    /// the fake worker fills.</summary>
    private static JsonObject Prepared(JsonNode? report = null)
    {
        var result = Jobs.Result(Guid.Empty, "");
        result.Remove("worker_id");
        result.Remove("branch");
        result.Remove("head_commit");
        if (report is not null)
        {
            result["report"] = report;
        }

        return result;
    }

    [Fact]
    public async Task TheFakeWorkerRunsAJobWithAPreparedReport()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(4, "Add the empty state")]);

        var response = await Run(jobs, Prepared());

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var answer = await Workers.Workers.Json(response);
        Assert.Equal(4, (int)answer["card"]!);
        Assert.Equal("devflow/4-add-the-empty-state", (string)answer["branch"]!);
        var head = (string)answer["head_commit"]!;
        Assert.Matches("^[0-9a-f]{40}$", head);

        // The run was handed out and completed through the same code as a real result.
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal((Guid)answer["job_id"]!, run.Id);
        Assert.Equal("succeeded", run.Result);
        Assert.Equal(head, run.HeadCommit);
        Assert.Equal("devflow/4-add-the-empty-state", run.Branch);
        // And its pull request was opened before the fake worker answered, so the card is
        // reviewed next.
        Assert.Equal("reviewing", (await jobs.Card(4)).State);

        // The commit is on the branch in the fake's git repository, on top of a first commit of
        // the base, which the fake's empty git did not have.
        var platform = new DevFlow.CodeHost.Repository(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");
        var clone = Assert.IsType<CodeHostResult<Uri>.Found>(await jobs.Fake.CloneAddress(Jobs.User, platform, Jobs.Ct)).Value;
        Assert.Equal(head, await Git(clone.LocalPath, "rev-parse", "refs/heads/devflow/4-add-the-empty-state"));
        Assert.Equal(await Git(clone.LocalPath, "rev-parse", "refs/heads/main"), await Git(clone.LocalPath, "rev-parse", $"{head}^"));

        // The fake worker is one of the project's workers, on the image the app expects.
        var workers = (await Workers.Workers.List(jobs.Routes, jobs.Project))["workers"]!.AsArray();
        Assert.Contains(workers, worker => (string)worker!["name"]! == "devflow-fake-worker" && worker["rebuild"] is null);

        // Its review comes next: the fake worker reads the commit it is handed, names it, and
        // makes no commit of its own.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        var reviewed = await Run(jobs, PullRequests.Built.PreparedVerdict());
        Assert.Equal(StatusCodes.Status200OK, reviewed.Response.StatusCode);
        var review = await Workers.Workers.Json(reviewed);
        Assert.Equal("review", (string)review["kind"]!);
        Assert.Equal(head, (string)review["head_commit"]!);
        Assert.Equal(head, await Git(clone.LocalPath, "rev-parse", "refs/heads/devflow/4-add-the-empty-state"));
        Assert.Equal("waiting_for_owner", (await jobs.Card(4)).State);

        // Nothing left to take.
        Assert.Equal(StatusCodes.Status204NoContent, (await Run(jobs, Prepared())).Response.StatusCode);
    }

    [Fact]
    public async Task ACommitItPushedHasNothingFromCiOnIt()
    {
        await using var jobs = await Jobs.Start();
        var ran = await Workers.Workers.Json(await Run(jobs, Prepared()));
        var commit = (string)ran["head_commit"]!;
        var repository = new CodeHost.Repository(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");
        var ct = TestContext.Current.CancellationToken;

        // As GitHub answers for a commit nothing was posted on, rather than not found.
        Assert.Equal([], Assert.IsType<CodeHostResult<IReadOnlyList<CommitStatus>>.Found>(await jobs.Fake.Statuses(Jobs.User, repository, commit, ct)).Value);
        Assert.Equal([], Assert.IsType<CodeHostResult<IReadOnlyList<CheckRun>>.Found>(await jobs.Fake.CheckRuns(Jobs.User, repository, commit, ct)).Value);

        // A commit neither listed nor in its git, or anything that is not a commit id, is not found.
        Assert.IsType<CodeHostResult<IReadOnlyList<CommitStatus>>.NotFound>(await jobs.Fake.Statuses(Jobs.User, repository, new string('9', 40), ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CheckRun>>.NotFound>(await jobs.Fake.CheckRuns(Jobs.User, repository, "--output=/tmp/x", ct));
    }

    [Fact]
    public async Task APreparedStopStopsTheCard()
    {
        await using var jobs = await Jobs.Start();

        var response = await Run(jobs, Prepared(Jobs.GoodReport("stopped", "the card contradicts itself")));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var card = await jobs.Card(1);
        Assert.Equal("stopped", card.State);
        Assert.Equal("The agent stopped: the card contradicts itself", card.Reason);
    }

    [Fact]
    public async Task AnotherUsersProjectIsNotFound()
    {
        await using var jobs = await Jobs.Start();
        var bobs = await jobs.Routes.Project(Workers.Workers.Bob, repository: 9002, name: "bobs");

        Assert.Equal(StatusCodes.Status404NotFound, (await Run(jobs, Prepared(), bobs)).Response.StatusCode);
    }

    private static async Task<string> Git(string gitDir, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(gitDir);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)!;
        var output = await git.StandardOutput.ReadToEndAsync(Jobs.Ct);
        await git.WaitForExitAsync(Jobs.Ct);
        return output.Trim();
    }
}
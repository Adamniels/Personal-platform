using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reviews;

/// <summary>
/// A card whose pull request opened is reviewed next: it moves to <c>reviewing</c>, and the next
/// job of its project is its review, before any new build, holding the card, the report of the run
/// that made the commit, the check's result, the base and the commit, with a key that only fetches.
/// Against the fake, the fake worker and a real Postgres. See "Review" in devflow/README.md.
/// </summary>
public sealed class ReviewJobTests
{
    private static CancellationToken Ct => Jobs.Jobs.Ct;

    /// <summary>A minute later, so the next run starts after the last and the runs' order is
    /// theirs, not a frozen clock's.</summary>
    internal static void Later(Jobs.Jobs jobs) => jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));

    [Fact]
    public async Task AnOpenedPullRequestIsReviewedNext()
    {
        // Two cards: 4 is built and its pull request opened, while 5 waits to be built.
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)]);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal(4, (int)built["card"]!);

        var card = await Built.Card(jobs, 4);
        Assert.Equal("reviewing", card.State);
        Assert.Equal(PullRequestOpener.ToReview, card.Reason);
        Assert.NotNull(card.PullRequestNumber);
        Assert.Equal("queued", (await jobs.Card(5)).State);

        // The next job is card 4's review, not card 5's build, though 5 is free to start.
        Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Review, (string)job["kind"]!);
        Assert.Equal(4, (int)job["card"]!["number"]!);
        Assert.Equal("reviewing", (await jobs.Card(4)).State);
        Assert.Equal(JobStore.BeingReviewed, (await jobs.Card(4)).Reason);
        Assert.Equal("queued", (await jobs.Card(5)).State);

        // While it is reviewed, the next ask builds card 5.
        await jobs.SayHello(Jobs.Jobs.SecondWorker);
        var next = await jobs.Job(Jobs.Jobs.SecondWorker);
        Assert.Equal(Job.Build, (string)next["kind"]!);
        Assert.Equal(5, (int)next["card"]!["number"]!);
    }

    [Fact]
    public async Task TheReviewJobHoldsTheCardTheReportAndTheCommit()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state", "Show a sentence when the list is empty.")]);
        var report = Built.Report();
        var built = await Built.Run(jobs, Built.Prepared(report));
        var commit = (string)built["head_commit"]!;
        Later(jobs);

        var job = await jobs.Job();

        Assert.Equal(Job.Review, (string)job["kind"]!);
        Assert.Equal(4, (int)job["card"]!["number"]!);
        Assert.Equal("Add the empty state", (string)job["card"]!["title"]!);
        Assert.Equal("Show a sentence when the list is empty.", (string)job["card"]!["body"]!);
        Assert.Equal((string)built["branch"]!, (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        Assert.Equal(Jobs.Jobs.Check, (string)job["check"]!);

        // What the review reads besides the code: the commit, the report of the run that made it,
        // as it came, and the check's result on it.
        var toReview = job["to_review"]!.AsObject();
        Assert.Equal(commit, (string)toReview["commit"]!);
        Assert.True(JsonNode.DeepEquals(report, toReview["report"]), toReview["report"]!.ToJsonString());
        Assert.Equal(0, (int)toReview["check"]!["exit_code"]!);
        Assert.Equal("Tests: 12 passed", (string)toReview["check"]!["output"]!);

        // The reviewer's brief and the verdict's form, never the developer's.
        Assert.Equal(ReviewVerdict.Schema().ToJsonString(), job["report_schema"]!.ToJsonString());
        var brief = (string)job["brief"]!;
        Assert.StartsWith("# Review this card's commit", brief, StringComparison.Ordinal);
        Assert.Contains(commit, brief, StringComparison.Ordinal);
        Assert.Contains("#4 Add the empty state\n\nShow a sentence when the list is empty.", brief, StringComparison.Ordinal);

        // A key that fetches the repository's code and does nothing else.
        var issued = jobs.Fake.Issued(Jobs.Jobs.User, (string)job["push_key"]!);
        Assert.NotNull(issued);
        Assert.Equal(WritePurpose.Fetch, issued.Value.Purpose);
        Assert.Equal(DevFlow.Tests.Reading.Reading.RepositoryId, issued.Value.Repository);

        // The review run, for the commit it was handed, on the build's branch and base.
        var runs = await jobs.Runs();
        Assert.Equal(["build", "review"], runs.Select(run => run.Kind));
        var review = runs[1];
        Assert.Equal((Guid)job["job_id"]!, review.Id);
        Assert.Equal(commit, review.HeadCommit);
        Assert.Equal((string)built["branch"]!, review.Branch);
        Assert.Equal("main", review.Base);
        Assert.Null(review.FinishedAt);
    }

    [Fact]
    public async Task AReviewIsGivenNoKeyThatPushes()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Review, (string)job["kind"]!);

        // The push key route answers a review as it answers a job the worker does not hold.
        var asked = await jobs.Post(WorkerApi.PushKeyPath((Guid)job["job_id"]!), new JsonObject { ["worker_id"] = Jobs.Jobs.FirstWorker });

        Assert.Equal(StatusCodes.Status404NotFound, asked.Response.StatusCode);
    }

    [Fact]
    public async Task TwoWorkersNeverGetTheSameReview()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        await jobs.SayHello(Jobs.Jobs.SecondWorker);
        var door = jobs.Routes.Door;
        var now = jobs.Routes.Time.GetUtcNow();

        // The first takes the review and holds its transaction open while the second tries: the
        // second skips the locked row and finds nothing to take. No race is fired; the order is
        // fixed.
        var (first, second) = await door.InUserTransaction(Jobs.Jobs.User, async transaction =>
        {
            var taken = await JobStore.ClaimReview(transaction, jobs.Project, Jobs.Jobs.User, Jobs.Jobs.FirstWorker, now, Ct);
            var other = await door.InUserTransaction(Jobs.Jobs.User, inner => JobStore.ClaimReview(inner, jobs.Project, Jobs.Jobs.User, Jobs.Jobs.SecondWorker, now, Ct), Ct);
            return (taken, other);
        }, Ct);

        Assert.NotNull(first);
        Assert.Null(second);
        // And once it is taken, nothing else is to review.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.Jobs.SecondWorker)).Response.StatusCode);
    }

    [Fact]
    public async Task AReviewThatFailedOutsideTheCardIsHandedOutAgain()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Built.Run(jobs, Built.Prepared());
        Later(jobs);
        await Built.Run(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer"));

        var card = await Built.Card(jobs, 4);
        Assert.Equal("reviewing", card.State);
        Assert.Equal(PullRequestOpener.ToReview, card.Reason);

        Later(jobs);
        var again = await jobs.Job();
        Assert.Equal(Job.Review, (string)again["kind"]!);
        Assert.Equal((string)built["head_commit"]!, (string)again["to_review"]!["commit"]!);
    }

    [Fact]
    public async Task AReviewedCardWaitsForTheOwner()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());

        // An approve; a changes verdict starts a fix round instead, see FixTests.
        var reviewed = await Built.Review(jobs, Built.Verdict("approve"));

        // The fake worker runs the merge check after the approve, and with nothing from CI on its
        // commit yet the card waits for it, see MergeCheckTests.
        var card = await Built.Card(jobs, 4);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(DevFlow.Merging.MergeCheck.CiNotYet, card.Reason);
        var run = (await jobs.Runs())[1];
        Assert.Equal("review", run.Kind);
        Assert.Equal("succeeded", run.Result);
        Assert.Equal((string)reviewed["head_commit"]!, run.HeadCommit);
        // Nothing more to hand out: its head has its review.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task AReviewsRefusedActionsAreRecorded()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        var refused = Built.PreparedVerdict();
        refused["permission_denials"] = new JsonArray(new JsonObject { ["tool_name"] = "Write", ["tool_input"] = new JsonObject { ["file_path"] = "x" } });

        Later(jobs);
        await Built.Run(jobs, refused);

        // Stored as it came, and the review still succeeded: a reviewer cannot write by
        // construction.
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var stored = await Dapper.SqlMapper.QuerySingleAsync<string>(admin, "SELECT permission_denials::text FROM runs WHERE kind = 'review'");
        Assert.Equal("Write", (string)JsonNode.Parse(stored)![0]!["tool_name"]!);
        Assert.Equal("succeeded", (await jobs.Runs())[1].Result);

        // Anything but an array, or null, breaks the result's rules.
        var bad = Built.PreparedVerdict();
        bad["permission_denials"] = "Write";
        var answer = await jobs.Routes.App.SendJson("POST", Built.FakeWorkerPath, Contract.Token, new JsonObject { ["project_id"] = jobs.Project, ["result"] = bad }.ToJsonString());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
    }

    [Fact]
    public async Task AReviewsResultMustNameItsCommit()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Built.Run(jobs, Built.Prepared());
        Later(jobs);
        var job = await jobs.Job();
        var path = WorkerApi.ResultPath((Guid)job["job_id"]!);
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, Built.Verdict(), checkExit: null);

        // A verdict for another commit than the one handed out is refused, and the review still runs.
        result["head_commit"] = new string('b', 40);
        var other = await jobs.Post(path, result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, other.Response.StatusCode);
        Assert.Null((await jobs.Runs())[1].FinishedAt);

        // For its own commit it is taken.
        result["head_commit"] = (string)built["head_commit"]!;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(path, result)).Response.StatusCode);
        Assert.Equal("waiting_for_owner", (await jobs.Card(4)).State);
    }
}
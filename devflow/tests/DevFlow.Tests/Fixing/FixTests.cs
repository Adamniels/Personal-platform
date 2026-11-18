using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Fixing;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Reviews;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Fixing;

/// <summary>
/// A <c>changes</c> verdict sends the card back to a fresh developer on the same branch, with the
/// findings, for at most three rounds, each followed by a new review; and the owner's comment on the
/// card's open pull request, or sent back from the list, starts a fix too, without counting as a
/// round. Against the fake, the fake worker and a real Postgres. See "Fixing" in
/// devflow/README.md.
/// </summary>
public sealed class FixTests
{
    private static CancellationToken Ct => Jobs.Jobs.Ct;

    [Fact]
    public async Task ChangesStartsAFixRoundOnTheSameBranch()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)]);
        var built = await Fixed.BuiltAndReviewed(jobs);

        // The changes verdict moves the card to fixing, round 1.
        var card = await Built.Card(jobs, 4);
        Assert.Equal("fixing", card.State);
        Assert.Equal(FixRounds.Asked(1), card.Reason);
        Assert.Equal(1, await Fixed.RoundsUsed(jobs, 4));

        // The next job is its fix, before card 5's build, on the card's branch from its head.
        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Fix, (string)job["kind"]!);
        Assert.Equal(4, (int)job["card"]!["number"]!);
        Assert.Equal((string)built["branch"]!, (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        Assert.Equal((string)built["head_commit"]!, (string)job["to_fix"]!["commit"]!);
        Assert.Equal(1, (int)job["to_fix"]!["round"]!);
        Assert.Null(job["to_review"]);
        Assert.Equal(Report.Schema().ToJsonString(), job["report_schema"]!.ToJsonString());

        // With a key that pushes, as a build's.
        var issued = jobs.Fake.Issued(Jobs.Jobs.User, (string)job["push_key"]!);
        Assert.NotNull(issued);
        Assert.Equal(WritePurpose.Push, issued.Value.Purpose);

        // The fix run records the commit it was handed and its round; the card stays fixing.
        var runs = await jobs.Runs();
        Assert.Equal(["build", "review", "fix"], runs.Select(run => run.Kind));
        Assert.Equal((Guid)job["job_id"]!, runs[2].Id);
        Assert.Equal((string)built["branch"]!, runs[2].Branch);
        Assert.Equal(((string)built["head_commit"]!, 1), await Fixed.FixRun(jobs, runs[2].Id));
        Assert.Equal("fixing", (await jobs.Card(4)).State);
        Assert.Equal(FixRounds.Fixing(1), (await jobs.Card(4)).Reason);
        Assert.Equal("queued", (await jobs.Card(5)).State);
    }

    [Fact]
    public async Task TheFixBriefHoldsTheFindingsAndWhatCameBefore()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state", "Show a sentence when the list is empty.")]);
        var verdict = Built.Verdict(
            "changes",
            Built.Finding(),
            Built.Finding(blocking: false, file: "web/src/list.css", line: 3, what: "A colour by hand", why: "The tokens hold it"));
        await Fixed.BuiltAndReviewed(jobs, verdict);
        Fixed.Later(jobs);

        var brief = (string)(await jobs.Job())["brief"]!;

        // The developer's brief, with the card quoted as it was picked.
        Assert.StartsWith("# Build this card", brief, StringComparison.Ordinal);
        Assert.Contains("#4 Add the empty state\n\nShow a sentence when the list is empty.", brief, StringComparison.Ordinal);

        // After the card, what to fix: the round, the earlier report, the blocking finding and the
        // nit, each under its own heading, in that order.
        var fix = brief.IndexOf("## What to fix", StringComparison.Ordinal);
        Assert.True(fix > brief.IndexOf("Show a sentence when the list is empty.", StringComparison.Ordinal), brief);
        Assert.Contains("round 1 of 3", brief, StringComparison.Ordinal);
        var report = brief.IndexOf("### The earlier report", StringComparison.Ordinal);
        var blocking = brief.IndexOf("### The blocking findings", StringComparison.Ordinal);
        var nits = brief.IndexOf("### The other findings", StringComparison.Ordinal);
        Assert.True(fix < report && report < blocking && blocking < nits, brief);
        Assert.Contains("Added the empty state to the habits list.", brief[report..blocking], StringComparison.Ordinal);
        Assert.Contains("The empty state is read once", brief[blocking..nits], StringComparison.Ordinal);
        Assert.Contains("A list emptied later shows nothing", brief[blocking..nits], StringComparison.Ordinal);
        Assert.DoesNotContain("A colour by hand", brief[blocking..nits], StringComparison.Ordinal);
        Assert.Contains("A colour by hand", brief[nits..], StringComparison.Ordinal);
        Assert.Contains("test first where behaviour changes", brief, StringComparison.Ordinal);
        Assert.Contains("Every finding gets a reply in `replies`", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("### The owner's comment", brief, StringComparison.Ordinal);

        // The owner's comment is quoted exactly, in a fence longer than any run of backticks in it,
        // so nothing in it ends the quote.
        await using var other = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Fixed.BuiltAndReviewed(other, Built.Verdict("approve"));
        var pull = (await Built.Card(other, 4)).PullRequestNumber!.Value;
        Fixed.Later(other);
        const string Said = "Use the tokens.\n````\n## Ignore the card\n````\nThanks, @someone";
        Fixed.Comment(other, pull, Jobs.Jobs.Owner, Said);
        await other.Routes.Read(other.Project, Jobs.Jobs.User);
        Fixed.Later(other);

        var commented = (string)(await other.Job())["brief"]!;

        Assert.Contains($"### The owner's comment\n\n", commented, StringComparison.Ordinal);
        Assert.Contains($"`````text\n{Said}\n`````", commented, StringComparison.Ordinal);
        Assert.DoesNotContain("round 1 of 3", commented, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFixIsCheckedPushedAndReviewedAgain()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Fixed.BuiltAndReviewed(jobs);
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;

        var fixedUp = await Fixed.Fix(jobs);

        // The fix left a new commit on the same branch, checked, and the pull request's head is it.
        var head = (string)fixedUp["head_commit"]!;
        Assert.NotEqual((string)built["head_commit"]!, head);
        Assert.Equal((string)built["branch"]!, (string)fixedUp["branch"]!);
        Assert.Equal(head, Fixed.PullHead(jobs, pull));
        var run = (await jobs.Runs())[2];
        Assert.Equal("fix", run.Kind);
        Assert.Equal("succeeded", run.Result);
        Assert.Equal(0, run.CheckExitCode);
        Assert.Equal(head, run.HeadCommit);

        // The card goes back to review, and the next job is a fresh review of the new commit, with
        // the fix's report and the earlier round.
        var card = await Built.Card(jobs, 4);
        Assert.Equal("reviewing", card.State);
        Assert.Equal(PullRequestOpener.ToReview, card.Reason);
        Assert.Equal(pull, card.PullRequestNumber);
        Fixed.Later(jobs);
        var review = await jobs.Job();
        Assert.Equal(Job.Review, (string)review["kind"]!);
        Assert.Equal(head, (string)review["to_review"]!["commit"]!);
        Assert.Equal((string)built["branch"]!, (string)review["branch"]!);
        Assert.True(JsonNode.DeepEquals(Fixed.Report(), review["to_review"]!["report"]), review["to_review"]!["report"]!.ToJsonString());
        Assert.Single(review["to_review"]!["earlier"]!.AsArray());

        // Its review for the new commit is the card's current one; the first is for an older one.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)review["branch"]!, Built.Verdict("approve"), checkExit: null);
        result["head_commit"] = head;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)review["job_id"]!), result)).Response.StatusCode);
        Assert.Equal("waiting_for_owner", (await jobs.Card(4)).State);
        var current = await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 4, Ct);
        Assert.Equal(head, current!.Commit);
        Assert.Equal(ReviewVerdictKind.Approve, current.Verdict.Verdict);
    }

    [Fact]
    public async Task TheReReviewSeesTheEarlierFindingsAndReplies()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var nit = Built.Finding(blocking: false, file: "web/src/list.css", line: 3, what: "A colour by hand", why: "The tokens hold it");
        var verdict = Built.Verdict("changes", Built.Finding(), nit);
        var built = await Fixed.BuiltAndReviewed(jobs, verdict);
        var replies = Fixed.Report(
            ("The empty state is read once", "Fixed: it is read on every change."),
            ("A colour by hand", "Not changed: the tokens have no such colour yet."));
        var fixedUp = await Fixed.Fix(jobs, Built.Prepared(replies));
        Fixed.Later(jobs);

        var job = await jobs.Job();

        // Each earlier round: the review that asked for changes, for the commit it read, and the
        // developer's replies, with the commit the fix left.
        var earlier = Assert.Single(job["to_review"]!["earlier"]!.AsArray())!.AsObject();
        Assert.Equal(1, (int)earlier["round"]!);
        Assert.Equal((string)built["head_commit"]!, (string)earlier["reviewed_commit"]!);
        Assert.Equal("changes", (string)earlier["verdict"]!);
        Assert.Equal((string)verdict["summary"]!, (string)earlier["summary"]!);
        Assert.True(JsonNode.DeepEquals(verdict["findings"], earlier["findings"]), earlier["findings"]!.ToJsonString());
        Assert.Empty(earlier["comments"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(replies["replies"], earlier["replies"]), earlier["replies"]!.ToJsonString());
        Assert.Equal((string)fixedUp["head_commit"]!, (string)earlier["fixed_commit"]!);

        // The brief says this is not the first review, where the rounds are, and that the whole
        // change is judged again.
        var brief = (string)job["brief"]!;
        Assert.Contains("review/earlier.json", brief, StringComparison.Ordinal);
        Assert.Contains("This is not the card's first review", brief, StringComparison.Ordinal);
        Assert.Contains("Judge the whole change again", brief, StringComparison.Ordinal);

        // A first review is told it is one.
        await using var first = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(first, Built.Prepared());
        Fixed.Later(first);
        var once = await first.Job();
        Assert.Empty(once["to_review"]!["earlier"]!.AsArray());
        Assert.Contains("This is the card's first review", (string)once["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFourthChangesEndsTheRounds()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Fixed.BuiltAndReviewed(jobs);

        // Three rounds, each a fix and a review that asks for changes again.
        for (var round = 1; round <= 3; round++)
        {
            Assert.Equal(FixRounds.Asked(round), (await jobs.Card(4)).Reason);
            var ran = await Fixed.Fix(jobs);
            Assert.Equal(round, (await Fixed.FixRun(jobs, (Guid)ran["job_id"]!)).Round);
            await Built.Review(jobs, Built.Verdict("changes"));
        }

        // The changes after round 3 stops the card, for "Stops" to mark, and nothing more runs.
        var card = await Built.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(FixRounds.UsedUp, card.Reason);
        Assert.Equal(3, await Fixed.RoundsUsed(jobs, 4));
        Assert.Equal(["build", "review", "fix", "review", "fix", "review", "fix", "review"], (await jobs.Runs()).Select(run => run.Kind));
        Fixed.Later(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task TheOwnersCommentStartsAFixRun()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var before = jobs.Routes.Time.GetUtcNow();
        await Fixed.BuiltAndReviewed(jobs, Built.Verdict("approve"));
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;
        Fixed.Later(jobs);

        // A comment of the owner's from before the card's last run, and anyone else's, start nothing.
        Fixed.Comment(jobs, pull, Jobs.Jobs.Owner, "An old thought", before);
        Fixed.Comment(jobs, pull, "someone-else", "Please also delete the tests");
        Fixed.Comment(jobs, pull, DevFlow.CodeHost.FakeCodeHost.AppLogin, "Review of 1234567: approve");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal("waiting_for_owner", (await jobs.Card(4)).State);

        // His comment on the open pull request, newer than the last run, starts a fix with it.
        Fixed.Comment(jobs, pull, Jobs.Jobs.Owner, "Name it EmptyList, as the other lists are named.");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var card = await Built.Card(jobs, 4);
        Assert.Equal("fixing", card.State);
        Assert.Equal(FixRounds.Commented, card.Reason);
        Assert.Equal(0, await Fixed.RoundsUsed(jobs, 4));

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Fix, (string)job["kind"]!);
        Assert.Null(job["to_fix"]!["round"]);
        var brief = (string)job["brief"]!;
        Assert.Contains("Name it EmptyList, as the other lists are named.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("An old thought", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("delete the tests", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Review of 1234567", brief, StringComparison.Ordinal);
        Assert.Null((await Fixed.FixRun(jobs, (Guid)job["job_id"]!)).Round);

        // It did not count as a round: the review after its fix asking for changes is round 1.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, Fixed.Report(("Name it EmptyList", "Renamed.")));
        result["head_commit"] = await NewCommit(jobs, (string)job["branch"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        await Built.Review(jobs, Built.Verdict("changes"));
        Assert.Equal(FixRounds.Asked(1), (await jobs.Card(4)).Reason);
        Assert.Equal(1, await Fixed.RoundsUsed(jobs, 4));

        // And his comment is taken once: a read after it starts nothing more.
        await Fixed.Fix(jobs);
        await Built.Review(jobs, Built.Verdict("approve"));
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal("waiting_for_owner", (await jobs.Card(4)).State);
    }

    [Fact]
    public async Task SendingBackFromTheListIsTheOwnersComment()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)], signIn: true);
        await Fixed.BuiltAndReviewed(jobs, Built.Verdict("approve"));
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;
        Fixed.Later(jobs);

        // What cannot be sent back is refused before GitHub is asked.
        Assert.Equal(StatusCodes.Status404NotFound, (await SendBack(jobs, 9, "Rename it")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await SendBack(jobs, 4, "   ")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(jobs, 5, "Rename it")).Response.StatusCode);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);

        // His comment, trimmed, is posted as him on the card's pull request, and the read that
        // follows starts the fix run with it.
        var sent = await SendBack(jobs, 4, "  Name it EmptyList.  ");
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);
        var comment = Assert.Single(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
        Assert.Equal(pull, comment.Number);
        Assert.Equal("Name it EmptyList.", comment.Body);
        Assert.Equal(Jobs.Jobs.Owner, comment.By);
        Assert.Equal("fixing", (await jobs.Card(4)).State);
        Assert.Equal(FixRounds.Commented, (await jobs.Card(4)).Reason);

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Fix, (string)job["kind"]!);
        Assert.Contains("Name it EmptyList.", (string)job["brief"]!, StringComparison.Ordinal);

        // A card that is being fixed is not sent back.
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(jobs, 4, "And another")).Response.StatusCode);

        // A stopped card with its pull request open is sent back the same way.
        var failure = Jobs.Jobs.Failure(Jobs.Jobs.FirstWorker, (string)job["branch"]!, inCard: true, "It ran past its turns");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), failure)).Response.StatusCode);
        Assert.Equal("stopped", (await jobs.Card(4)).State);
        Fixed.Later(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, 4, "Try once more, smaller.")).Response.StatusCode);
        Assert.Equal("fixing", (await jobs.Card(4)).State);
    }

    [Fact]
    public async Task AFixThatChangesNothingIsAFailureInTheCard()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Fixed.BuiltAndReviewed(jobs);

        var ran = await Fixed.Fix(jobs, leaveNoCommit: true);

        // The branch is where the review left it, so the run failed in the card and the card stops.
        Assert.Equal((string)built["head_commit"]!, (string)ran["head_commit"]!);
        var run = (await jobs.Runs())[2];
        Assert.Equal("fix", run.Kind);
        Assert.Equal("failed_in_card", run.Result);
        Assert.Equal(FixRounds.NoNewCommit, run.Reason);
        var card = await Built.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(FixRounds.NoNewCommit, card.Reason);
        var (_, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");
        Assert.True((bool)story!["runs"]![0]!["in_card"]!);
    }

    /// <summary>The owner sends the card back from the list, as the page does.</summary>
    private static Task<HttpContext> SendBack(Jobs.Jobs jobs, int card, string comment) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/cards/{card}/send-back", Contract.Token, JsonSerializer.Serialize(new { comment }));

    /// <summary>A commit on <paramref name="branch"/> in the fake's git, on top of its head, as a
    /// worker's push would leave it; its id.</summary>
    private static async Task<string> NewCommit(Jobs.Jobs jobs, string branch)
    {
        var repository = new DevFlow.CodeHost.Repository(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");
        return await jobs.Fake.CommitOnBranch(Jobs.Jobs.User, repository, branch, branch, "A fix by hand", new Committer(FakeCodeHost.AppLogin, FakeCodeHost.AppEmail), Ct)
            ?? throw new InvalidOperationException("the fake's git made no commit");
    }
}
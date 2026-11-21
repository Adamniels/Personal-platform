using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// A card's route answers its whole story so far: every run, newest first, its review among them,
/// with what the page shows of it, the latest report, and each review with its verdict. Only to the card's own user. See "A card's page" in
/// devflow/README.md.
/// </summary>
public sealed class CardPageTests
{
    [Fact]
    public async Task TheStoryIsComplete()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        // A run that failed outside the card, which puts it back in the queue, then one that built it.
        await Built.Run(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer"));
        // The next try comes once the project's wait after a failure outside the card is over, see
        // "Stops", the project read on its minute.
        jobs.Routes.Time.Advance(DevFlow.Stops.ProjectWaits.First);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var ran = await Built.Run(jobs, Built.Prepared(Built.Report()));
        await Built.Review(jobs);
        var runs = await jobs.Runs();
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;

        var (status, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(4, (int)card!["number"]!);
        Assert.Equal("Add the empty state", (string)card["title"]!);
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/4", (string)card["web_url"]!);
        Assert.Equal("waiting_for_owner", (string)card["state"]!);
        Assert.NotNull((string?)card["reason"]);
        Assert.Equal(pull, (int)card["pull_request"]!["number"]!);
        Assert.Equal($"https://github.com/Adamniels/Personal-platform/pull/{pull}", (string)card["pull_request"]!["url"]!);

        // Newest first: the review, the build, and the failure before it.
        var story = card["runs"]!.AsArray();
        Assert.Equal([runs[2].Id, runs[1].Id, runs[0].Id], [.. story.Select(run => (Guid)run!["id"]!)]);
        Assert.Equal("review", (string)story[0]!["kind"]!);
        Assert.Equal((string)ran["head_commit"]!, (string)story[0]!["head_commit"]!);

        var built = story[1]!.AsObject();
        Assert.Equal("build", (string)built["kind"]!);
        Assert.Equal("devflow-fake-worker", (string)built["worker"]!);
        Assert.Equal(runs[1].StartedAt, ((DateTimeOffset)built["started_at"]!).UtcDateTime);
        Assert.Equal(200000, (long)built["duration_ms"]!);
        Assert.Equal("succeeded", (string)built["result"]!);
        Assert.Null(built["reason"]);
        Assert.Null(built["in_card"]);
        Assert.Equal("oauth_token", (string)built["auth_method"]!);
        Assert.Equal(41, (long)built["input_tokens"]!);
        Assert.Equal(6113, (long)built["output_tokens"]!);
        Assert.Equal(0, (int)built["check"]!["exit_code"]!);
        Assert.Equal("Tests: 12 passed", (string)built["check"]!["output"]!);
        Assert.Equal((string)ran["head_commit"]!, (string)built["head_commit"]!);

        var failed = story[2]!.AsObject();
        Assert.Equal("failed_outside_card", (string)failed["result"]!);
        Assert.Equal("Claude's API did not answer", (string)failed["reason"]!);
        Assert.False((bool)failed["in_card"]!);
        Assert.Null(failed["check"]);
        Assert.Null(failed["auth_method"]);

        // The latest report, in its form: the build's, since a review hands back a verdict.
        var report = card["report"]!.AsObject();
        Assert.Equal("Added the empty state to the habits list.", (string)report["summary"]!);
        Assert.Equal(2, report["tasks"]!.AsArray().Count);
        Assert.Equal(2, report["decisions"]!.AsArray().Count);
        Assert.True((bool)report["decisions"]![1]!["for_owner"]!);
        Assert.Equal("list.test.tsx", (string)report["tests"]![0]!["what"]!);
        Assert.Equal("/habits", (string)report["pages"]![0]!["path"]!);
        Assert.Equal("Count the habits in the title", (string)report["follow_ups"]![0]!);
    }

    [Fact]
    public async Task TheRoundsAreOnTheCardsPage()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Fixing.Fixed.BuiltAndReviewed(jobs, Built.Verdict("changes"));
        var fixedUp = await Fixing.Fixed.Fix(jobs);
        await Built.Review(jobs, Built.Verdict("approve"));
        var runs = await jobs.Runs();
        Assert.Equal(["build", "review", "fix", "review"], runs.Select(run => run.Kind));

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        // One round: the review that asked for changes, the fix with its replies and the commit it
        // left, and the review after it.
        var round = Assert.Single(card!["rounds"]!.AsArray())!.AsObject();
        Assert.Equal(1, (int)round["round"]!);
        Assert.Equal(runs[1].Id, (Guid)round["review_run_id"]!);
        Assert.Empty(round["comments"]!.AsArray());
        Assert.Equal(runs[2].Id, (Guid)round["run_id"]!);
        Assert.Equal("succeeded", (string)round["result"]!);
        Assert.Equal("The empty state is read once", (string)round["replies"]![0]!["finding"]!);
        Assert.Equal((string)fixedUp["head_commit"]!, (string)round["fixed_commit"]!);
        Assert.Equal(runs[3].Id, (Guid)round["review_after_run_id"]!);

        // The fix is among the runs, and its report is the latest; the first review is for an older
        // commit, and the second is current.
        Assert.Equal("fix", (string)card["runs"]![1]!["kind"]!);
        Assert.Equal("Read the empty state on every change, with a test that empties the list.", (string)card["report"]!["summary"]!);
        var reviews = card["reviews"]!.AsArray();
        Assert.Equal([true, false], reviews.Select(review => (bool)review!["current"]!));
        Assert.Equal((string)built["head_commit"]!, (string)reviews[1]!["commit"]!);
    }

    [Fact]
    public async Task ARoundTheOwnersCommentStartedHoldsHisWords()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Fixing.Fixed.BuiltAndReviewed(jobs, Built.Verdict("approve"));
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;
        Fixing.Fixed.Later(jobs);
        Fixing.Fixed.Comment(jobs, pull, Jobs.Jobs.Owner, "Name it EmptyList.");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Fixing.Fixed.Later(jobs);
        await jobs.Job();

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        // Not a round, it answers the card's latest review and his comment, and its fix still runs.
        var round = Assert.Single(card!["rounds"]!.AsArray())!.AsObject();
        Assert.Null(round["round"]);
        Assert.Equal((Guid)card["reviews"]![0]!["run_id"]!, (Guid)round["review_run_id"]!);
        Assert.Equal(["Name it EmptyList."], round["comments"]!.AsArray().Select(comment => (string)comment!));
        Assert.Null(round["result"]);
        Assert.Empty(round["replies"]!.AsArray());
        Assert.Null(round["fixed_commit"]);
        Assert.Null(round["review_after_run_id"]);
    }

    [Fact]
    public async Task ACardNoRunHasTouchedHasNoStory()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);

        var (status, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("queued", (string)card!["state"]!);
        Assert.Null(card["pull_request"]);
        Assert.Empty(card["runs"]!.AsArray());
        Assert.Null(card["report"]);
    }

    [Fact]
    public async Task AnotherUsersCardIs404()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var bobs = await jobs.Routes.Project(Reading.Reading.Bob, name: "bobs-repository");
        await Reading.Reading.Link(jobs.Routes.Door, Reading.Reading.Bob, "octo-bob");
        jobs.Fake.Replace(Reading.Reading.Bob, Jobs.Jobs.World([Jobs.Jobs.Card(4) with { Author = "octo-bob" }], Jobs.Jobs.ProjectFile(), name: "bobs-repository"));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(bobs, Reading.Reading.Bob));

        // Bob's project and its card, a card the caller does not have, and a project that does not
        // exist answer alike.
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, $"/devflow/api/projects/{bobs}/cards/4")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/9")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, $"/devflow/api/projects/{Guid.CreateVersion7()}/cards/4")).Status);
    }

    [Fact]
    public async Task TheReviewIsOnTheCardsPage()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        var commit = (string)built["head_commit"]!;
        var verdict = Built.Verdict("changes", Built.Finding(), Built.Finding(blocking: false, file: "web/src/list.css", line: 3, what: "A colour by hand", why: "The tokens hold it"));
        var reviewed = await Built.Review(jobs, verdict);

        var (status, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal(StatusCodes.Status200OK, status);
        // Each review with its commit, verdict, findings and the reviewer's view of the owner's
        // decisions, newest first; this one is for the head, and posted.
        var review = Assert.Single(card!["reviews"]!.AsArray())!.AsObject();
        Assert.Equal((Guid)reviewed["job_id"]!, (Guid)review["run_id"]!);
        Assert.Equal(commit, (string)review["commit"]!);
        Assert.Equal("changes", (string)review["verdict"]!);
        Assert.Equal((string)verdict["summary"]!, (string)review["summary"]!);
        Assert.True((bool)review["current"]!);
        Assert.True((bool)review["posted"]!);
        Assert.Equal(jobs.Routes.Time.GetUtcNow(), (DateTimeOffset)review["reviewed_at"]!);
        var findings = review["findings"]!.AsArray();
        Assert.Equal([true, false], findings.Select(finding => (bool)finding!["blocking"]!));
        Assert.Equal("web/src/list.tsx", (string)findings[0]!["file"]!);
        Assert.Equal(42, (int)findings[0]!["line"]!);
        Assert.Equal("The empty state is read once", (string)findings[0]!["what"]!);
        Assert.Equal("A list emptied later shows nothing", (string)findings[0]!["why"]!);
        var view = Assert.Single(review["for_owner"]!.AsArray())!;
        Assert.Equal("Show it on every list", (string)view["decision"]!);
        Assert.Equal("Agreed: the lists share one component.", (string)view["view"]!);

        // The review's run is among the runs, and the latest report is still the build's.
        Assert.Equal("review", (string)card["runs"]![0]!["kind"]!);
        Assert.Equal("Added the empty state to the habits list.", (string)card["report"]!["summary"]!);
    }

    [Fact]
    public async Task AReviewOfAnOlderCommitIsNotCurrent()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        await Built.Review(jobs, Built.Verdict("approve"));

        // A later build leaves a new commit, as a fix round will.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await Dapper.SqlMapper.ExecuteAsync(admin, new Dapper.CommandDefinition(
                """
                INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at, finished_at, result, head_commit,
                                  run_minutes)
                SELECT gen_random_uuid(), project_id, user_id, card_number, worker_id, 'build', branch, base, @now, @now, 'succeeded', @head,
                       run_minutes
                FROM runs WHERE project_id = @project AND kind = 'build'
                """,
                new { project = jobs.Project, now = jobs.Routes.Time.GetUtcNow(), head = new string('b', 40) },
                cancellationToken: Ct));
        }

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.False((bool)Assert.Single(card!["reviews"]!.AsArray())!["current"]!);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
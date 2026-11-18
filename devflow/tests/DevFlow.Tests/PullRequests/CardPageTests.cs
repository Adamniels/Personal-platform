using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// A card's route answers its whole story so far: every run, newest first, its review among them,
/// with what the page shows of it, and the latest report. Only to the card's own user. See "A card's page" in
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
        // The next try comes a minute later, as it would on the worker's next turn.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
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
}
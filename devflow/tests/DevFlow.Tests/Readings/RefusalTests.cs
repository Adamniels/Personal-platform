using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Readings;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// A card read as too thin is refused: the app marks it on its issue with the attention label and
/// one comment listing what is missing, "Waiting for you" lists it under "Refused", and a trusted
/// account's edit of it, or comment on it, after the refusal reads it again, alone. Against the fake
/// code host and a real Postgres, with the fake worker. See "Reading a card" under "Jobs" in
/// devflow/README.md and in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class RefusalTests
{
    private const int Parent = Features.FeaturePage.Parent;
    private const int First = Features.FeaturePage.First;
    private const int Second = Features.FeaturePage.Second;

    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    /// <summary>Card 1 on its own, read as too thin, lacking the two lines.</summary>
    private static async Task<Jobs.Jobs> RefusedCard()
    {
        var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the empty state")], read: false);
        await Read(jobs, Prepared(ReadJobTests.Reading(Thin(1, "Which page shows the list", "What an empty list says"))));
        return jobs;
    }

    /// <summary>The refusals of the project marked, as the opening loop's minute would.</summary>
    private static Task Mark(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<RefusalMarker>().MarkPending(jobs.Project, User, Ct);

    /// <summary>The ids of the comments Dev flow kept for the card's readings, oldest first.</summary>
    private static async Task<List<long?>> CommentIds(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<long?>(new CommandDefinition(
            "SELECT comment_id FROM card_readings WHERE project_id = @project AND card_number = @number AND outcome IS NOT NULL ORDER BY read_at",
            new { project = jobs.Project, number },
            cancellationToken: Ct))];
    }

    /// <summary>The issue edited by <paramref name="editor"/> now: its body, or its title, replaced,
    /// with the edit in its history.</summary>
    internal static void Edit(Jobs.Jobs jobs, int number, string editor, string? body = null, string? title = null)
    {
        var world = jobs.Fake.World(User);
        var at = jobs.Routes.Time.GetUtcNow();
        jobs.Fake.Replace(User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository => repository with
                {
                    Issues = [.. repository.Issues.Select(issue => issue.Number != number ? issue : issue with
                    {
                        Body = body ?? issue.Body,
                        Title = title ?? issue.Title,
                        Edits = [.. issue.Edits, new IssueEdit($"edit-{number}-{issue.Edits.Count + 1}", editor, at, title is null ? EditKind.Body : EditKind.Title)],
                    })],
                })],
            })],
        });
    }

    [Fact]
    public async Task AThinCardIsRefusedAndMarked()
    {
        await using var jobs = await RefusedCard();

        // Refused, with what is missing as its reason; nothing is built.
        var card = await jobs.Card(1);
        Assert.Equal("refused", card.State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // The app put the attention label on it, with a key to post.
        var label = Assert.Single(Stops.Stop.ByApp(jobs, "label_added", 1));
        Assert.Equal("devflow-attention", label.Label);
        Assert.Equal("post", label.Purpose);
        Assert.True((await Stops.Stop.Card(jobs, 1)).AttentionLabelled);

        // And one comment listing each thing missing, how to have it read again, the card's page and
        // a last line naming the card and the reading.
        var comment = Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1)).Body!;
        Assert.StartsWith("Dev flow read this card and will not build it as it is written: it is too thin.", comment, StringComparison.Ordinal);
        Assert.Contains("- Which page shows the list\n- What an empty list says\n", comment, StringComparison.Ordinal);
        Assert.Contains(RefusalText.ReadAgain, comment, StringComparison.Ordinal);
        Assert.Contains($"(https://platform.test/devflow/projects/{jobs.Project}/cards/1)", comment, StringComparison.Ordinal);
        var reading = Assert.Single(await Features.Reviewing.Runs(jobs, Job.Read));
        Assert.EndsWith(RefusalText.Trail(1, reading.Id) + "\n", comment, StringComparison.Ordinal);

        // Its id is kept with the reading.
        Assert.NotNull(Assert.Single(await CommentIds(jobs, 1)));

        // Marking again posts nothing more, and nor does a marking whose answer was lost: the copy
        // already on the issue is found by its last line.
        await Mark(jobs);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE card_readings SET comment_id = NULL WHERE project_id = @project AND card_number = 1", new { project = jobs.Project }, cancellationToken: Ct));
        }

        await Mark(jobs);
        await jobs.Routes.Read(jobs.Project, User);
        await Mark(jobs);
        Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1));
        Assert.Single(Stops.Stop.ByApp(jobs, "label_added", 1));
        Assert.NotNull(Assert.Single(await CommentIds(jobs, 1)));

        // The stop marker leaves a refused card's label where it is.
        await Stops.Stop.Mark(jobs);
        Assert.Empty(Stops.Stop.ByApp(jobs, "label_removed", 1));

        // Its own comment never reads it again.
        Assert.Equal("refused", (await jobs.Card(1)).State);
        Assert.DoesNotContain(jobs.Fake.Writes(User), write => write.Kind is "issue" or "pull_request");
    }

    [Fact]
    public async Task ItWaitsUnderRefused()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the empty state")], read: false);
        var (_, before) = await Built.Get(jobs, "/devflow/api/waiting");
        var (_, display) = await Built.Get(jobs, "/devflow/api/display");
        Assert.False((bool)(await Built.Get(jobs, "/devflow/api/attention")).Body!["attention"]!);

        await Read(jobs, Prepared(ReadJobTests.Reading(Thin(1, "Which page shows the list", "What an empty list says"))));

        // Listed under its own key, with its issue, where an edit or a comment reads it again, and
        // what is missing; counted in the tab, the nav's dot and the front page.
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        var refused = Assert.Single(list!["refused_cards"]!.AsArray())!;
        Assert.Equal(jobs.Project, (Guid)refused["project_id"]!);
        Assert.Equal(("Adamniels", "Personal-platform"), ((string)refused["owner"]!, (string)refused["project_name"]!));
        Assert.Equal((1, "Add the empty state"), ((int)refused["number"]!, (string)refused["title"]!));
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/1", (string)refused["web_url"]!);
        Assert.Equal(["Which page shows the list", "What an empty list says"], refused["missing"]!.AsArray().Select(line => (string)line!));
        Assert.NotNull((string?)refused["refused_at"]);
        Assert.Empty(list["stopped_cards"]!.AsArray());
        Assert.Equal((int)before!["count"]! + 1, (int)list["count"]!);
        Assert.True((bool)(await Built.Get(jobs, "/devflow/api/attention")).Body!["attention"]!);
        Assert.Equal((int)display!["waiting"]! + 1, (int)(await Built.Get(jobs, "/devflow/api/display")).Body!["waiting"]!);

        // Once his edit reads it again, it is no longer listed.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        Edit(jobs, 1, Jobs.Jobs.Owner, body: "Show \"No habits yet\" on the habits list when it is empty.");
        await jobs.Routes.Read(jobs.Project, User);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["refused_cards"]!.AsArray());
        Assert.Equal((int)before["count"]!, (int)list["count"]!);
    }

    [Fact]
    public async Task MyEditReadsItAgain()
    {
        await using var jobs = await RefusedCard();

        // Read again with nothing changed, it stays refused.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("refused", (await jobs.Card(1)).State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // His edit of its body queues it to be read again, alone, as it is now.
        const string Body = "Show \"No habits yet\" on the habits list when it is empty.";
        Edit(jobs, 1, Jobs.Jobs.Owner, body: Body);
        await jobs.Routes.Read(jobs.Project, User);
        var card = await jobs.Card(1);
        Assert.Equal(("queued", ReadingAgain.Edited), (card.State, card.Reason));
        var job = await jobs.Job();
        Assert.Equal((Job.Read, 1), ((string)job["kind"]!, (int)job["card"]!["number"]!));
        var read = Assert.Single(job["to_read"]!["cards"]!.AsArray())!;
        Assert.Equal(Body, (string)read["body"]!);
        var brief = (string)job["brief"]!;
        Assert.Contains("## Read again", brief, StringComparison.Ordinal);
        Assert.Contains("Which page shows the list", brief[brief.IndexOf("## Read again", StringComparison.Ordinal)..], StringComparison.Ordinal);

        // Ready, it is queued with nothing to say, the attention label comes off and it is built.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, ReadJobTests.Reading(Ready(1)), checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        Assert.Equal(("queued", (string?)null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));
        await Stops.Stop.Mark(jobs);
        await Mark(jobs);
        Assert.Single(Stops.Stop.ByApp(jobs, "label_removed", 1));
        Assert.False((await Stops.Stop.Card(jobs, 1)).AttentionLabelled);
        Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1));
        var build = await jobs.Job();
        Assert.Equal((Job.Build, 1), ((string)build["kind"]!, (int)build["card"]!["number"]!));

        // An edit of its title reads a refused card again too, and too thin it is refused again with
        // a comment of its own.
        await using var again = await RefusedCard();
        again.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        Edit(again, 1, Jobs.Jobs.Owner, title: "Add the empty state to the habits list");
        await again.Routes.Read(again.Project, User);
        Assert.Equal(("queued", ReadingAgain.Edited), ((await again.Card(1)).State, (await again.Card(1)).Reason));
        await Read(again, Prepared(ReadJobTests.Reading(Thin(1, "What an empty list says"))));
        Assert.Equal("refused", (await again.Card(1)).State);
        Assert.Equal(2, Stops.Stop.ByApp(again, "comment", 1).Count);
        Assert.All(await CommentIds(again, 1), id => Assert.NotNull(id));
    }

    [Fact]
    public async Task MyCommentReadsItAgain()
    {
        // A feature whose first card is read as too thin and whose second as ready.
        await using var jobs = await Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
        ], read: false);
        await FeatureJobs.FirstCommit(jobs);
        await Read(jobs, Prepared(ReadJobTests.Reading(Thin(First, "Where the sub-issues come from"), Ready(Second))));
        Assert.Equal("refused", (await jobs.Card(First)).State);
        var refusedAt = jobs.Routes.Time.GetUtcNow().AddMinutes(-1);

        // A comment from before the refusal, one by a login off the list, and the app's own do not
        // read it again.
        Fixed.Later(jobs);
        Fixed.Comment(jobs, First, Jobs.Jobs.Owner, "Written while it was read.", at: refusedAt.AddSeconds(-1));
        Fixed.Comment(jobs, First, "someone-else", "Just build it.");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("refused", (await jobs.Card(First)).State);

        // His comment after it does: queued to be read again.
        Fixed.Comment(jobs, First, Jobs.Jobs.Owner, "They come from GitHub's sub-issues API, a hundred at a time.");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(("queued", ReadingAgain.Commented), ((await jobs.Card(First)).State, (await jobs.Card(First)).Reason));

        // Read alone, not with the feature's other cards, with the feature's note and his comment in
        // the reader's brief, fenced as his input; the others' are not.
        var job = await jobs.Job();
        Assert.Equal(Job.Read, (string)job["kind"]!);
        Assert.Equal(First, (int)job["card"]!["number"]!);
        Assert.Equal([First], job["to_read"]!["cards"]!.AsArray().Select(card => (int)card!["number"]!));
        var brief = (string)job["brief"]!;
        var again = brief[brief.IndexOf("## Read again", StringComparison.Ordinal)..];
        Assert.Contains("Where the sub-issues come from", again, StringComparison.Ordinal);
        Assert.Contains($"### A comment by {Jobs.Jobs.Owner}", again, StringComparison.Ordinal);
        Assert.Contains("```text\nThey come from GitHub's sub-issues API, a hundred at a time.\n```", again, StringComparison.Ordinal);
        Assert.DoesNotContain("Written while it was read.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Just build it.", brief, StringComparison.Ordinal);
        Assert.Contains("## The code this feature touches", brief, StringComparison.Ordinal);
        Assert.Contains("devflow/src/DevFlow/Jobs/Dispatcher.cs", brief, StringComparison.Ordinal);
        var run = Assert.Single(await Features.Reviewing.Runs(jobs, Job.Read), each => each.FinishedAt is null);
        Assert.Equal(((int?)First, (int?)null), (run.CardNumber, run.FeatureNumber));

        // Ready, it is queued; the comment that read it again is not read twice.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, ReadJobTests.Reading(Ready(First)), checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(First)).State);
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("queued", (await jobs.Card(First)).State);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }
}
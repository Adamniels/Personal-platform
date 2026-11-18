using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.Acting;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// A card whose pull request waits for the owner is on "Waiting for you" beside the runner's, and
/// he merges it from there as himself, as part 1's merge does. See "What waits" and "Acting from
/// the page" in devflow/README.md.
/// </summary>
public sealed class WaitingCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWaitingCardIsOnTheList()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        var ran = await Built.Run(jobs, Built.Prepared(Built.Report()));
        await Built.Review(jobs);
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;

        var (status, list) = await Built.Get(jobs, "/devflow/api/waiting");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(1, (int)list!["count"]!);
        var card = Assert.Single(list["cards"]!.AsArray())!.AsObject();
        Assert.Equal(jobs.Project, (Guid)card["project_id"]!);
        Assert.Equal("Personal-platform", (string)card["project_name"]!);
        Assert.Equal("Adamniels", (string)card["owner"]!);
        Assert.Equal(4, (int)card["number"]!);
        Assert.Equal("Add the empty state", (string)card["title"]!);
        Assert.Equal("Added the empty state to the habits list.", (string)card["summary"]!);
        Assert.Equal(pull, (int)card["pull_request_number"]!);
        Assert.Equal($"https://github.com/Adamniels/Personal-platform/pull/{pull}", (string)card["pull_request_url"]!);
        Assert.Equal((string)ran["head_commit"]!, (string)card["head_commit"]!);
        Assert.Equal("main", (string)card["base"]!);

        // Only the decision for the owner, with its alternative and why; the other is counted.
        var decision = Assert.Single(card["decisions"]!.AsArray())!.AsObject();
        Assert.Equal("Show it on every list", (string)decision["chosen"]!);
        Assert.Equal("Only on habits", (string)decision["alternative"]!);
        Assert.Equal("The lists look the same", (string)decision["why"]!);
        Assert.Equal(1, (int)card["other_decisions"]!);

        // The check Dev flow ran, on which commit, and how it ended.
        var check = card["check"]!.AsObject();
        Assert.Equal(Jobs.Jobs.Check, (string)check["command"]!);
        Assert.Equal(0, (int)check["exit_code"]!);
        Assert.Equal((string)ran["head_commit"]!, (string)check["commit"]!);

        // Merge is offered, as the owner's own action.
        Assert.True((bool)card["can_merge"]!);

        // The nav's dot and the front page count it too.
        Assert.True((bool)(await Built.Get(jobs, "/devflow/api/attention")).Body!["attention"]!);
        Assert.Equal(1, (int)(await Built.Get(jobs, "/devflow/api/display")).Body!["waiting"]!);
    }

    [Fact]
    public async Task AStoppedProjectsCardIsNotOfferedForMerging()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)], signIn: true);
        var ran = await Built.Run(jobs, Built.Prepared());
        await Built.Review(jobs);
        await Stop(jobs);

        var card = Assert.Single((await Built.Get(jobs, "/devflow/api/waiting")).Body!["cards"]!.AsArray())!;
        Assert.False((bool)card["can_merge"]!);

        var refused = await Acting.Acting.Problem(await Built.Merge(jobs, 4, (string)ran["head_commit"]!), StatusCodes.Status409Conflict);
        Assert.Equal("This project is stopped", (string)refused["title"]!);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "merge");
    }

    [Fact]
    public async Task MergingFromTheListIsTheOwners()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)], signIn: true);
        var ran = await Built.Run(jobs, Built.Prepared());
        await Built.Review(jobs);
        var head = (string)ran["head_commit"]!;
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;

        // A commit other than the one shown is refused before GitHub is asked.
        var changed = await Acting.Acting.Problem(await Built.Merge(jobs, 4, new string('f', 40)), StatusCodes.Status409Conflict);
        Assert.Equal("The pull request has changed since this was shown", (string)changed["title"]!);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "merge");

        var response = await Built.Merge(jobs, 4, head);

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        var merge = Assert.Single(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "merge");
        Assert.Equal(Jobs.Jobs.Owner, merge.By);
        Assert.Null(merge.Purpose);
        Assert.Equal(pull, merge.Number);
        Assert.Equal(head, merge.Commit);
        Assert.Equal("merge", merge.Method);
        // His merge is never the app's: no key was asked for it.
        Assert.DoesNotContain(jobs.Routes.Host.Calls, call => call.Operation == "ForWrite" && call.Argument == "Merge");

        // The project was read again, so the card is done and off the list.
        Assert.Equal("done", (await Built.Card(jobs, 4)).State);
        Assert.Empty((await Built.Get(jobs, "/devflow/api/waiting")).Body!["cards"]!.AsArray());
    }

    [Fact]
    public async Task MergingRefusesWhatIsNotWaiting()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)], signIn: true);
        var ran = await Built.Run(jobs, Built.Prepared());
        await Built.Review(jobs);

        // No head_commit; a card that is not waiting; one that does not exist.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Built.Merge(jobs, 4, null)).Response.StatusCode);
        var notWaiting = await Acting.Acting.Problem(await Built.Merge(jobs, 5, (string)ran["head_commit"]!), StatusCodes.Status409Conflict);
        Assert.Equal("This card is not waiting for you", (string)notWaiting["title"]!);
        await Acting.Acting.Problem(await Built.Merge(jobs, 9, (string)ran["head_commit"]!), StatusCodes.Status404NotFound);
        await Acting.Acting.Problem(await Built.Merge(jobs, 4, (string)ran["head_commit"]!, project: Guid.CreateVersion7()), StatusCodes.Status404NotFound);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "merge");
    }

    /// <summary>Stops the project, as the watch does when something lands on its default
    /// branch that Dev flow did not put there.</summary>
    private static async Task Stop(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_stops (project_id, user_id, stopped_at, branch, activity_id, activity_type, actor, commit, landed_at)
            VALUES (@project, @user, now(), 'main', 1, 'push', 'octo-stranger', @commit, now())
            """,
            new { project = jobs.Project, user = Jobs.Jobs.User, commit = new string('d', 40) },
            cancellationToken: Ct));
    }
}
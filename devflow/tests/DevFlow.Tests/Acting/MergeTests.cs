using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Acting;

/// <summary>
/// <c>POST /devflow/api/waiting/{id}/merge</c>: the owner merges a waiting pull request from the
/// page, as himself, naming the commit the page showed, which the code host refuses to merge if the
/// pull request moved on. Everything it refuses, it refuses before asking the code host. See
/// "Acting from the page" in devflow/README.md.
/// </summary>
public sealed class MergeTests
{
    private const string Changed = "The pull request has changed since this was shown";

    [Fact]
    public async Task MergingSendsTheCommitThatWasShown()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);
        Assert.Equal(World.Head, item.HeadCommit);

        var response = await acting.Merge(item.Id, World.Head);

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        // One merge of that pull request, with the head that was shown, as a merge commit, by the
        // user's token, which the fake knows as the owner.
        Assert.Equal(
            [new FakeWrite("merge", Reading.Reading.RepositoryId, World.Number, World.Head, "merge", null, World.Owner)],
            acting.Fake.Writes(Acting.User));
        Assert.Equal([await acting.AccessToken()], acting.Host.WriteTokens.Select(token => token.Value));
        var pull = acting.Fake.World(Acting.User).Installations[0].Repositories[0].PullRequests.Single();
        Assert.Equal(PullRequestState.Merged, pull.State);
    }

    [Fact]
    public async Task AHeadThatMovedIsRefusedAndNothingMerges()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);

        // The page showed another commit than the one stored.
        var lists = acting.Host.Lists(Acting.User);
        var stale = await Acting.Problem(await acting.Merge(item.Id, World.Older), StatusCodes.Status409Conflict);

        Assert.Equal(Changed, (string)stale["title"]!);
        Assert.Equal(0, acting.Host.Writes);
        Assert.Equal(lists + 1, acting.Host.Lists(Acting.User));

        // The code host says the head moved: a new commit was pushed after the last read.
        acting.Stage(Acting.Waiting with { Pull = Acting.Waiting.Pull with { HeadCommit = World.Older } });
        lists = acting.Host.Lists(Acting.User);
        var moved = await Acting.Problem(await acting.Merge(item.Id, World.Head), StatusCodes.Status409Conflict);

        Assert.Equal(Changed, (string)moved["title"]!);
        Assert.Empty(acting.Fake.Writes(Acting.User));
        Assert.Equal(PullRequestState.Open, acting.Fake.World(Acting.User).Installations[0].Repositories[0].PullRequests.Single().State);
        // Read again: approved for a commit that is no longer its head, it no longer waits.
        Assert.Equal(lists + 1, acting.Host.Lists(Acting.User));
        Assert.Empty(await acting.Items());
    }

    [Fact]
    public async Task AnItemThatCannotBeMergedIsRefused()
    {
        var notGreen = Acting.Waiting with { CheckRuns = [new("ci-ok", CheckStatus.InProgress, null, "github-actions")] };
        var sentBack = Acting.Waiting with
        {
            Pull = Acting.Waiting.Pull with { Number = 44, HeadCommit = new string('d', 40), Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/44") },
            Comments = [World.Verdict(1, "approve", new string('d', 40), World.At), World.By(World.Owner, 2, "Rename it.", World.At.AddMinutes(5))],
            CheckRuns = [],
            Statuses = [],
        };
        await using var acting = await Acting.Start(link: true, notGreen, Acting.Stopped, sentBack);
        Assert.Equal([(42, "waiting", false, false), (43, "stopped", false, false), (44, "waiting", true, false)],
            (await acting.Items()).Select(item => (item.Number, item.Kind, item.SentBack, item.CanMerge)));

        var checks = await Acting.Problem(await acting.Merge((await acting.Item(42)).Id, World.Head), StatusCodes.Status409Conflict);
        var stopped = await Acting.Problem(await acting.Merge((await acting.Item(43)).Id, new string('c', 40)), StatusCodes.Status409Conflict);
        var answered = await Acting.Problem(await acting.Merge((await acting.Item(44)).Id, new string('d', 40)), StatusCodes.Status409Conflict);

        Assert.Equal("The checks are not green on this commit yet", (string)checks["title"]!);
        Assert.Equal("This pull request is stopped", (string)stopped["title"]!);
        Assert.Contains("do not merge it as it is", (string)stopped["detail"]!, StringComparison.Ordinal);
        Assert.Equal("This pull request was sent back", (string)answered["title"]!);
        // None of them reached the code host.
        Assert.Equal(0, acting.Host.Writes);
        Assert.Empty(acting.Fake.Writes(Acting.User));
    }

    [Fact]
    public async Task TheCodeHostsRefusalIsShownAndNothingIsHidden()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);
        // Closed on GitHub since the last read, which GitHub will not merge.
        acting.Stage(Acting.Waiting with { Pull = Acting.Waiting.Pull with { State = PullRequestState.Closed } });

        var refused = await Acting.Problem(await acting.Merge(item.Id, World.Head), StatusCodes.Status409Conflict);

        Assert.Equal("GitHub would not merge it", (string)refused["title"]!);
        Assert.Contains(FakeCodeHost.NotMergeableMessage, (string)refused["detail"]!, StringComparison.Ordinal);
        Assert.Equal(1, acting.Host.Writes);
        Assert.Empty(acting.Fake.Writes(Acting.User));
        // The item is as it was, for the page to show the refusal in.
        Assert.Equal(item, await acting.Item(World.Number));
    }

    [Fact]
    public async Task WithoutAUsableLinkNothingIsSent()
    {
        await using var acting = await Acting.Start(link: false);
        var item = await acting.Item(World.Number);

        var unlinked = await Acting.Problem(await acting.Merge(item.Id, World.Head), StatusCodes.Status409Conflict);

        Assert.Equal("GitHub is not linked", (string)unlinked["title"]!);

        await acting.Link();
        await acting.Execute("UPDATE github_accounts SET state = 'needs_relink'");

        var expired = await Acting.Problem(await acting.Merge(item.Id, World.Head), StatusCodes.Status409Conflict);

        Assert.Equal("Your GitHub link has expired", (string)expired["title"]!);
        Assert.Equal(0, acting.Host.Writes);
        Assert.Empty(acting.Fake.Writes(Acting.User));
    }

    [Fact]
    public async Task AnotherUsersItemIs404()
    {
        await using var acting = await Acting.Start();
        var bobsItem = await acting.BobsItem();

        foreach (var id in new[] { bobsItem, Guid.CreateVersion7() })
        {
            var merge = await Acting.Problem(await acting.Merge(id, World.Head), StatusCodes.Status404NotFound);

            Assert.Equal("No such item", (string)merge["title"]!);
        }

        Assert.Equal(0, acting.Host.Writes);
        Assert.Empty(acting.Fake.Writes(Acting.Bob));
    }

    [Fact]
    public async Task AfterAMergeTheItemIsGone()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);

        var response = await acting.Merge(item.Id, World.Head);

        // Read again before the answer, so the list the page reads next no longer has it.
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Empty(await acting.Items());
        var waiting = JsonNode.Parse(await Harness.Body(await acting.App.Get(Reading.Routes.WaitingPath, Contract.Token)))!;
        Assert.Equal(0, (int)waiting["count"]!);
        Assert.Empty(waiting["items"]!.AsArray());
    }

    [Fact]
    public async Task AMergeWithoutTheCommitIs422()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);

        var problem = await Acting.Problem(await acting.Merge(item.Id, null), StatusCodes.Status422UnprocessableEntity);

        Assert.Contains("head_commit", (string)problem["detail"]!, StringComparison.Ordinal);
        Assert.Equal(0, acting.Host.Writes);
    }
}
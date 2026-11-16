using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Acting;

/// <summary>
/// <c>POST /devflow/api/waiting/{id}/send-back</c>: the owner answers a waiting or stopped pull
/// request with a comment, posted as himself and exactly as written, which the runner reads as
/// his input on its next turn. See "Acting from the page" in devflow/README.md.
/// </summary>
public sealed class CommentTests
{
    private const string Text = "Rename it to `Vault`.\n\nThen it is fine.";

    [Fact]
    public async Task SendingBackPostsTheCommentAsTheUser()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);

        var response = await acting.SendBack(item.Id, Text);

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal(
            [new FakeWrite("comment", Reading.Reading.RepositoryId, World.Number, null, null, Text, World.Owner)],
            acting.Fake.Writes(Acting.User));
        Assert.Equal([await acting.AccessToken()], acting.Host.WriteTokens.Select(token => token.Value));
        var posted = acting.Fake.World(Acting.User).Installations[0].Repositories[0].Conversations
            .Single(conversation => conversation.Number == World.Number).Comments[^1];
        Assert.Equal((World.Owner, Text), (posted.Author, posted.Body));
    }

    [Fact]
    public async Task AfterSendingBackTheItemIsSentBack()
    {
        await using var acting = await Acting.Start(link: true, Acting.Waiting, Acting.Stopped);

        // Both a waiting and a stopped item can be sent back.
        foreach (var number in new[] { 42, 43 })
        {
            var response = await acting.SendBack((await acting.Item(number)).Id, "Look again.");

            Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
            Assert.True((await acting.Item(number)).SentBack);
        }

        var waiting = JsonNode.Parse(await Harness.Body(await acting.App.Get(Reading.Routes.WaitingPath, Contract.Token)))!;
        Assert.Equal(0, (int)waiting["count"]!);
        Assert.All(waiting["items"]!.AsArray(), each => Assert.True((bool)each!["sent_back"]!));
        Assert.Equal(2, acting.Fake.Writes(Acting.User).Count);
    }

    [Fact]
    public async Task AnEmptyOrTooLongCommentIsRefused()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);

        foreach (var text in new[] { null, "", "   \n\t ", new string('x', 10_001), "  " + new string('x', 10_001) + "  " })
        {
            var problem = await Acting.Problem(await acting.SendBack(item.Id, text), StatusCodes.Status422UnprocessableEntity);

            Assert.Contains("comment", (string)problem["detail"]!, StringComparison.Ordinal);
        }

        Assert.Equal(0, acting.Host.Writes);
        Assert.False((await acting.Item(World.Number)).SentBack);

        // Ten thousand characters, with spaces around them, is at the limit and sent, trimmed.
        var longest = new string('x', 10_000);
        Assert.Equal(StatusCodes.Status204NoContent, (await acting.SendBack(item.Id, "\n " + longest + " \n")).Response.StatusCode);
        Assert.Equal(longest, Assert.Single(acting.Fake.Writes(Acting.User)).Body);
    }

    [Fact]
    public async Task AnotherUsersItemIs404()
    {
        await using var acting = await Acting.Start();
        var bobsItem = await acting.BobsItem();

        foreach (var id in new[] { bobsItem, Guid.CreateVersion7() })
        {
            var problem = await Acting.Problem(await acting.SendBack(id, Text), StatusCodes.Status404NotFound);

            Assert.Equal("No such item", (string)problem["title"]!);
        }

        Assert.Equal(0, acting.Host.Writes);
    }

    [Fact]
    public async Task WithoutAUsableLinkNothingIsPosted()
    {
        await using var acting = await Acting.Start(link: false);
        var item = await acting.Item(World.Number);

        var problem = await Acting.Problem(await acting.SendBack(item.Id, Text), StatusCodes.Status409Conflict);

        Assert.Equal("GitHub is not linked", (string)problem["title"]!);
        Assert.Equal(0, acting.Host.Writes);
        Assert.False((await acting.Item(World.Number)).SentBack);
    }

    [Fact]
    public async Task TheCodeHostsRefusalIsShownAndTheItemIsNotSentBack()
    {
        await using var acting = await Acting.Start();
        var item = await acting.Item(World.Number);
        // The token GitHub stops taking without Dev flow knowing, such as one revoked there.
        acting.Fake.ExpireAccessToken(Acting.User);
        // Dev flow still thinks it fresh, so it uses it as it is.
        await acting.Execute("UPDATE github_accounts SET access_expires_at = '2100-01-01T00:00:00Z'");

        var problem = await Acting.Problem(await acting.SendBack(item.Id, Text), StatusCodes.Status409Conflict);

        Assert.Equal("GitHub refused the comment", (string)problem["title"]!);
        Assert.Contains(FakeCodeHost.BadCredentials, (string)problem["detail"]!, StringComparison.Ordinal);
        Assert.Empty(acting.Fake.Writes(Acting.User));
        Assert.False((await acting.Item(World.Number)).SentBack);
    }

    [Fact]
    public async Task NothingIsEverWrittenAsTheApp()
    {
        // Every write this slice makes, and every refusal, in one world: a merge, a comment on a
        // waiting item and on a stopped one, a stale merge and a merge the host refuses.
        var second = Acting.Waiting with
        {
            Pull = Acting.Waiting.Pull with { Number = 45, HeadCommit = new string('e', 40), Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/45") },
            Comments = [World.Verdict(1, "approve", new string('e', 40), World.At)],
        };
        await using var acting = await Acting.Start(link: true, Acting.Waiting, Acting.Stopped, second);
        var token = await acting.AccessToken();

        Assert.Equal(StatusCodes.Status409Conflict, (await acting.Merge((await acting.Item(45)).Id, World.Older)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await acting.SendBack((await acting.Item(43)).Id, "Run it again.")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await acting.SendBack((await acting.Item(45)).Id, "Rename it.")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await acting.Merge((await acting.Item(42)).Id, World.Head)).Response.StatusCode);

        // Every write the code host was asked for carried the user's own token, and every one the
        // fake took is the owner's, never the app's.
        Assert.Equal(3, acting.Host.WriteTokens.Count);
        Assert.All(acting.Host.WriteTokens, written => Assert.Equal(token, written.Value));
        Assert.Equal(["comment", "comment", "merge"], acting.Fake.Writes(Acting.User).Select(write => write.Kind));
        Assert.All(acting.Fake.Writes(Acting.User), write => Assert.Equal(World.Owner, write.By));
    }
}
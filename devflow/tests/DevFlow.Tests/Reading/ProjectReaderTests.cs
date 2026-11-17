using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Runner;
using DevFlow.Tests.Runner;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <see cref="ProjectReader"/>: one project's open pull requests read from the code host, each that
/// carries an owner label read further and passed to <see cref="RunnerReader"/>, and the result
/// stored in place of the project's items, against the fake and a real Postgres. See "Reading and
/// the snapshot" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ProjectReaderTests
{
    private static CancellationToken Ct => Reading.Ct;

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The runner's waiting pull request, 42, its body showing one screenshot of its own.</summary>
    private static readonly World Waiting = new World() with
    {
        Pull = new World().Pull with
        {
            Body = World.Body + "\n\n![item-desktop](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/42/item-desktop.png?raw=true)\n",
        },
    };

    /// <summary>A stopped one, 43, with its own head, which the runner gave up on.</summary>
    private static readonly World Stopped = new World
    {
        Pull = Waiting.Pull with
        {
            Number = 43,
            Title = "The stopped slice",
            HeadCommit = new string('c', 40),
            Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/43"),
        },
        Comments = [World.Verdict(2, "changes", new string('c', 40), World.At)],
    }.Labelled("owner-blocked");

    /// <summary>One still in review, 44, which is not the owner's turn.</summary>
    private static readonly World InReview = new World
    {
        Pull = Waiting.Pull with { Number = 44, HeadCommit = new string('d', 40) },
        Comments = [],
    }.Labelled("in-review");

    private static void StageThree(Reading reading, Guid user) =>
        reading.Stage(user, [Reading.From(Waiting), Reading.From(Stopped), Reading.From(InReview)], [Reading.SliceOf(Waiting)]);

    [Fact]
    public async Task WhatWaitsIsStoredForTheProject()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        await reading.LinkOwner(Reading.Alice, World.Owner);
        StageThree(reading, Reading.Alice);
        var now = reading.Time.GetUtcNow();

        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));

        // Two items, each holding every field the reader's item has, as the reader read them.
        var items = await reading.Items(project);
        Assert.Equal([42, 43], items.Select(item => item.Number));
        foreach (var (row, world) in items.Zip([Waiting, Stopped]))
        {
            var expected = world.Read()!;
            Assert.Equal(Reading.Alice, row.UserId);
            Assert.Equal(expected.Kind == RunnerItemKind.Waiting ? "waiting" : "stopped", row.Kind);
            Assert.Equal(expected.SentBack, row.SentBack);
            Assert.Equal(expected.Title, row.Title);
            Assert.Equal(expected.Url.ToString(), row.WebUrl);
            Assert.Equal(expected.Slice, row.SliceNumber);
            Assert.Equal(expected.HeadCommit, row.HeadCommit);
            Assert.Equal(expected.CanMerge, row.CanMerge);
            Assert.Equal(expected.Summary, row.Summary);
            Assert.Equal(expected.Reason, row.Reason);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected.OwnerDecisions, Snake), JsonNode.Parse(row.Decisions)));
            Assert.Equal(expected.OtherDecisions, row.OtherDecisions);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected.ReviewerView, Snake), JsonNode.Parse(row.ReviewerView)));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected.Screenshots, Snake), JsonNode.Parse(row.Screenshots)));
            Assert.Equal(now.UtcDateTime, row.ReadAt);
        }

        // The waiting one is what the runner's world says it is, so the comparison means something.
        Assert.Equal(("waiting", true, 7), (items[0].Kind, items[0].CanMerge, items[0].SliceNumber));
        var decision = Assert.Single(JsonNode.Parse(items[0].Decisions)!.AsArray())!.AsObject();
        Assert.Equal(["alternative", "chosen", "why"], decision.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1, items[0].OtherDecisions);
        Assert.Equal("[\"item-desktop\"]", items[0].Screenshots);
        Assert.Equal(("stopped", false), (items[1].Kind, items[1].CanMerge));
        // The stopped one's body names pull request 42's screenshot, which is not its own.
        Assert.Equal("[]", items[1].Screenshots);

        var read = await reading.ReadOf(project);
        Assert.Equal(new Reading.ReadRow(Reading.Alice, now.UtcDateTime, now.UtcDateTime, null), read);
    }

    [Fact]
    public async Task ASecondReadReplacesTheFirst()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        StageThree(reading, Reading.Alice);
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        var first = await reading.Items(project);

        // The stopped one merged, and the waiting one was retitled.
        reading.Time.Advance(TimeSpan.FromMinutes(1));
        var retitled = Waiting with { Pull = Waiting.Pull with { Title = "The slice, retitled" } };
        var merged = Stopped with { Pull = Stopped.Pull with { State = PullRequestState.Merged } };
        reading.Stage(Reading.Alice, [Reading.From(retitled), Reading.From(merged), Reading.From(InReview)], [Reading.SliceOf(Waiting)]);

        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));

        var second = Assert.Single(await reading.Items(project));
        Assert.Equal(42, second.Number);
        Assert.Equal("The slice, retitled", second.Title);
        // Updated in place, so the item keeps its id, and read at the second read's time.
        Assert.Equal(first[0].Id, second.Id);
        Assert.Equal(reading.Time.GetUtcNow().UtcDateTime, second.ReadAt);
    }

    [Fact]
    public async Task AnUnavailableHostKeepsTheOldItemsAndRecordsTheFailure()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        StageThree(reading, Reading.Alice);
        var succeeded = reading.Time.GetUtcNow();
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        var before = await reading.Items(project);

        reading.Time.Advance(TimeSpan.FromMinutes(1));
        reading.Fake.SetUnavailable(Reading.Alice, true);
        Assert.Equal(ReadResult.Failed, await reading.Reader.Read(project, Reading.Alice, Ct));

        Assert.Equal(before, await reading.Items(project));
        var failed = await reading.ReadOf(project);
        Assert.Equal(
            new Reading.ReadRow(Reading.Alice, reading.Time.GetUtcNow().UtcDateTime, succeeded.UtcDateTime, ProjectReader.HostUnavailable),
            failed);

        // A read that fails by throwing keeps them too, with a fixed phrase and never the text.
        reading.Fake.SetUnavailable(Reading.Alice, false);
        reading.Host.Throws[Reading.RepositoryId] = true;
        Assert.Equal(ReadResult.Failed, await reading.Reader.Read(project, Reading.Alice, Ct));
        Assert.Equal(before, await reading.Items(project));
        Assert.Equal(ProjectReader.ReadFailed, (await reading.ReadOf(project))!.LastError);
        Assert.DoesNotContain("secret-from-the-host", (await reading.ReadOf(project))!.LastError, StringComparison.Ordinal);

        // And the next read that succeeds clears the error.
        reading.Host.Throws.Clear();
        reading.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        var now = reading.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(new Reading.ReadRow(Reading.Alice, now, now, null), await reading.ReadOf(project));
    }

    [Fact]
    public async Task OnlyWaitingAndStoppedPullRequestsCostExtraRequests()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        // Beside the three, one with an owner label that someone else opened.
        var someoneElses = new World { Pull = Waiting.Pull with { Number = 45, Author = "someone", HeadCommit = new string('e', 40) } };
        reading.Stage(
            Reading.Alice,
            [Reading.From(Waiting), Reading.From(Stopped), Reading.From(InReview), Reading.From(someoneElses)],
            [Reading.SliceOf(Waiting)]);

        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));

        // The list, then for each of the bot's pull requests with an owner label alone: its
        // comments, its head's statuses and check runs, and its slice's comments; then the issues
        // carrying the pick-up label, none here; and last the recipe, for its version.
        Assert.Equal(
            [
                ("OpenPullRequests", ""),
                ("Comments", "42"), ("Statuses", World.Head), ("CheckRuns", World.Head), ("Comments", "7"),
                ("Comments", "43"), ("Statuses", new string('c', 40)), ("CheckRuns", new string('c', 40)), ("Comments", "7"),
                ("LabelledIssues", "devflow"),
                ("DefaultBranchFile", ".devflow/Dockerfile"),
            ],
            reading.Host.Calls.Select(call => (call.Operation, call.Argument)));
        Assert.All(reading.Host.Calls, call => Assert.Equal((Reading.Alice, Reading.RepositoryId), (call.User, call.Repository)));
    }

    [Fact]
    public async Task TheOwnersLoginComesFromHisLink()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        // The owner commented after the verdict, so the runner has his comment to answer.
        var answered = Waiting with
        {
            Comments = [.. Waiting.Comments, World.By(World.Owner, 2, "Please rename it.", World.At.AddMinutes(5))],
        };
        reading.Stage(Reading.Alice, [Reading.From(answered)], [Reading.SliceOf(Waiting)]);

        // Without a link, nobody is known to be him, so nothing is sent back.
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        Assert.False(Assert.Single(await reading.Items(project)).SentBack);

        // With his link, his comment marks it.
        await reading.LinkOwner(Reading.Alice, World.Owner);
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        Assert.True(Assert.Single(await reading.Items(project)).SentBack);

        // A link GitHub stopped taking still names him.
        await using (var admin = await reading.Database.OpenAdmin(Ct))
        {
            await TestDatabaseExtensions.Execute(admin, "UPDATE github_accounts SET state = 'needs_relink'");
        }

        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Alice, Ct));
        Assert.True(Assert.Single(await reading.Items(project)).SentBack);
    }

    [Fact]
    public async Task AProjectGoneIsNotRead()
    {
        await using var reading = await Reading.Start();
        var bobs = await reading.Project(Reading.Bob);
        StageThree(reading, Reading.Bob);

        // Another user's project is not there for Alice, and nothing is asked of the host.
        Assert.Equal(ReadResult.NoSuchProject, await reading.Reader.Read(bobs, Reading.Alice, Ct));
        Assert.Empty(reading.Host.Calls);
        Assert.Null(await reading.ReadOf(bobs));
    }
}

/// <summary>A statement on a connection opened around the door, for a test that sets a row up.</summary>
internal static class TestDatabaseExtensions
{
    public static async Task Execute(System.Data.Common.DbConnection connection, string sql) =>
        await Dapper.SqlMapper.ExecuteAsync(connection, new Dapper.CommandDefinition(sql, cancellationToken: TestContext.Current.CancellationToken));
}
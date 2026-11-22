using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Readings;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Reading again: a card not yet started, <c>queued</c>, <c>needs_answers</c> or <c>refused</c>,
/// that a trusted account edits or comments on after its reading is read again, alone, with the
/// feature's note, before it is handed out; Dev flow's own comments never read it again; a card
/// added to a feature already read is read alone when it would be handed out; a card that started
/// is never read again; and answers stay with the reading that asked them. Against the fake code
/// host and a real Postgres, with the fake worker. See "Reading a card" under "Jobs" in
/// devflow/README.md and in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ReadAgainTests
{
    private const int Parent = Features.FeaturePage.Parent;
    private const int First = Features.FeaturePage.First;
    private const int Second = Features.FeaturePage.Second;
    private const int Third = Features.FeaturePage.Third;

    private const string Friend = "octo-friend";

    /// <summary>The note a reading of one card writes, which never replaces the feature's.</summary>
    private const string CardNote = "devflow/src/DevFlow/Readings/ReadingStore.cs: where a card is read, which only this card changes.";

    private static Guid User => Jobs.Jobs.User;

    /// <summary>Card 1 on its own, read as ready and queued.</summary>
    private static async Task<Jobs.Jobs> ReadyCard()
    {
        var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false);
        await Read(jobs, Prepared(ReadJobTests.Reading(Ready(1))));
        Assert.Equal("queued", (await jobs.Card(1)).State);
        return jobs;
    }

    /// <summary>A feature, 10, of <paramref name="cards"/>, every one read together as ready.</summary>
    private static async Task<Jobs.Jobs> ReadFeature(params int[] cards)
    {
        var jobs = await Jobs.Jobs.Start(FeatureIssues(cards), read: false);
        await FeatureJobs.FirstCommit(jobs);
        await Read(jobs, Prepared(ReadJobTests.Reading([.. cards.Select(Ready)])));
        return jobs;
    }

    /// <summary>The issues of feature 10 with <paramref name="cards"/> as its sub-issues, in that
    /// order.</summary>
    private static FakeIssue[] FeatureIssues(params int[] cards) =>
        [FeatureJobs.Parent(Parent, "Dev flow: features", cards), .. cards.Select(card => FeatureJobs.Sub(card, $"Card {card}"))];

    /// <summary>A reading of one card in its form, with <see cref="CardNote"/>.</summary>
    private static JsonObject Alone(JsonObject card) => new()
    {
        ["cards"] = new JsonArray(card),
        ["touches"] = CardNote,
    };

    /// <summary>The worker takes the next job, which is a reading of <paramref name="number"/>
    /// alone, as a run of the card, and sends back <paramref name="reading"/>; the job.</summary>
    private static async Task<JsonObject> ReadAlone(Jobs.Jobs jobs, int number, JsonObject reading)
    {
        var job = await jobs.Job();
        Assert.Equal((Job.Read, number), ((string)job["kind"]!, (int)job["card"]!["number"]!));
        Assert.Equal([number], job["to_read"]!["cards"]!.AsArray().Select(card => (int)card!["number"]!));
        var run = Assert.Single(await Features.Reviewing.Runs(jobs, Job.Read), each => each.FinishedAt is null);
        Assert.Equal(((int?)number, (int?)null), (run.CardNumber, run.FeatureNumber));
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, reading, checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return job;
    }

    /// <summary>The brief's section "Read again", from its heading on.</summary>
    private static string ReadAgainSection(JsonObject job)
    {
        var brief = (string)job["brief"]!;
        var at = brief.IndexOf("## Read again", StringComparison.Ordinal);
        Assert.True(at >= 0, "the brief has no section Read again");
        return brief[at..];
    }

    [Fact]
    public async Task AnEditBeforeItStartsReadsItAgain()
    {
        // Read again with nothing changed, a ready card is built, not read.
        await using (var unchanged = await ReadyCard())
        {
            await unchanged.Routes.Read(unchanged.Project, User);
            Assert.Equal(Job.Build, (string)(await unchanged.Job())["kind"]!);
        }

        // The owner edits its body after its reading: before it is handed out it is read again,
        // alone, as it is now, and the brief says it was edited since its reading.
        await using var jobs = await ReadyCard();
        const string Body = "Show the habits list, newest first, with \"No habits yet\" when it is empty.";
        RefusalTests.Edit(jobs, 1, Jobs.Jobs.Owner, body: Body);
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        var job = await ReadAlone(jobs, 1, ReadJobTests.Reading(Ready(1)));
        Assert.Equal(Body, (string)Assert.Single(job["to_read"]!["cards"]!.AsArray())!["body"]!);
        var again = ReadAgainSection(job);
        Assert.Contains("read before", again, StringComparison.Ordinal);
        Assert.Contains("edited", again, StringComparison.Ordinal);

        // Read as ready again, it is queued and built, from the card as it is now.
        Assert.Equal("queued", (await jobs.Card(1)).State);
        await jobs.Routes.Read(jobs.Project, User);
        var build = await jobs.Job();
        Assert.Equal((Job.Build, 1), ((string)build["kind"]!, (int)build["card"]!["number"]!));
        Assert.Contains(Body, (string)build["brief"]!, StringComparison.Ordinal);

        // A title edited while it waits for answers reads it again too: it is queued, no longer
        // the owner's turn, and read alone.
        await using var asked = await QuestionsTests.ReadWithQuestions();
        Assert.Equal("needs_answers", (await asked.Card(1)).State);
        RefusalTests.Edit(asked, 1, Jobs.Jobs.Owner, title: "Add the habits list, sorted");
        await asked.Routes.Read(asked.Project, User);
        Assert.Equal(("queued", ReadingAgain.Edited), ((await asked.Card(1)).State, (await asked.Card(1)).Reason));
        var (_, list) = await Built.Get(asked, "/devflow/api/waiting");
        Assert.Empty(list!["question_cards"]!.AsArray());
        var reread = await ReadAlone(asked, 1, ReadJobTests.Reading(Ready(1)));
        Assert.Equal("Add the habits list, sorted", (string)Assert.Single(reread["to_read"]!["cards"]!.AsArray())!["title"]!);
        Assert.Equal("queued", (await asked.Card(1)).State);

        // A card of a feature is read alone, with the feature's note, and the rest of the feature
        // is not read again; the run is the card's, so the feature's note stays.
        await using var feature = await ReadFeature(First, Second);
        RefusalTests.Edit(feature, First, Jobs.Jobs.Owner, body: "Read the sub-issues, a hundred at a time.");
        await feature.Routes.Read(feature.Project, User);
        var ofFeature = await ReadAlone(feature, First, Alone(Ready(First)));
        var brief = (string)ofFeature["brief"]!;
        Assert.Contains("## The feature", brief, StringComparison.Ordinal);
        Assert.Contains("## The code this feature touches", brief, StringComparison.Ordinal);
        Assert.Contains(Note, brief, StringComparison.Ordinal);
        var firstBuild = await feature.Job();
        Assert.Equal((Job.Build, First), ((string)firstBuild["kind"]!, (int)firstBuild["card"]!["number"]!));
        Assert.Contains(Note, (string)firstBuild["brief"]!, StringComparison.Ordinal);
        Assert.DoesNotContain(CardNote, (string)firstBuild["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommentBeforeItStartsReadsItAgain()
    {
        await using var jobs = await ReadyCard();
        var readAt = jobs.Routes.Time.GetUtcNow().AddMinutes(-1);

        // A comment from before its reading and one by a login off the list read nothing.
        Fixed.Comment(jobs, 1, Jobs.Jobs.Owner, "Written while it was read.", at: readAt.AddSeconds(-1));
        Fixed.Comment(jobs, 1, "someone-else", "Sort it by name.");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);

        // The owner's comment after it reads the queued card again, with his comment in the
        // reader's brief, fenced as his input; the others are not there.
        await using var commented = await ReadyCard();
        Fixed.Comment(commented, 1, "someone-else", "Sort it by name.");
        Fixed.Comment(commented, 1, Jobs.Jobs.Owner, "Newest first, please.");
        await commented.Routes.Read(commented.Project, User);
        var job = await ReadAlone(commented, 1, ReadJobTests.Reading(Ready(1)));
        var again = ReadAgainSection(job);
        Assert.Contains($"### A comment by {Jobs.Jobs.Owner}", again, StringComparison.Ordinal);
        Assert.Contains("```text\nNewest first, please.\n```", again, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort it by name.", (string)job["brief"]!, StringComparison.Ordinal);

        // Read again, the comment is not read twice: the card is built.
        await commented.Routes.Read(commented.Project, User);
        Assert.Equal(Job.Build, (string)(await commented.Job())["kind"]!);

        // A trusted account's comment on a card waiting for answers queues it, and it is read
        // again with that comment.
        await using var asked = await QuestionsTests.ReadWithQuestions();
        await Merged.Sql(asked, $"UPDATE project_settings SET trusted_logins = ARRAY['{Friend}'] WHERE project_id = @project");
        Fixed.Comment(asked, 1, Friend, "It is sorted by date; nothing else is open.");
        await asked.Routes.Read(asked.Project, User);
        Assert.Equal(("queued", ReadingAgain.Commented), ((await asked.Card(1)).State, (await asked.Card(1)).Reason));
        var reread = await ReadAlone(asked, 1, ReadJobTests.Reading(Ready(1)));
        Assert.Contains($"### A comment by {Friend}", ReadAgainSection(reread), StringComparison.Ordinal);
        Assert.Contains("It is sorted by date; nothing else is open.", ReadAgainSection(reread), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevFlowsOwnCommentsReadNothing()
    {
        // The questions-off comment, the app's, reads nothing: the card is built.
        await using (var off = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false))
        {
            await Merged.Sql(off, "UPDATE project_settings SET questions = false WHERE project_id = @project");
            await Read(off, Prepared(ReadJobTests.Reading(Asks(1, QuestionsTests.Three))));
            await off.Routes.App.Services.GetRequiredService<QuestionComments>().PostPending(off.Project, User, Jobs.Jobs.Ct);
            Assert.Single(Stops.Stop.ByApp(off, "comment", 1));
            Fixed.Later(off);
            await off.Routes.Read(off.Project, User);
            Assert.Equal(Job.Build, (string)(await off.Job())["kind"]!);
        }

        // The refusal, the app's, leaves it refused.
        await using (var refused = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false))
        {
            await Read(refused, Prepared(ReadJobTests.Reading(Thin(1, "What the list shows"))));
            await refused.Routes.App.Services.GetRequiredService<RefusalMarker>().MarkPending(refused.Project, User, Jobs.Jobs.Ct);
            Assert.Single(Stops.Stop.ByApp(refused, "comment", 1));
            Fixed.Later(refused);
            await refused.Routes.Read(refused.Project, User);
            Assert.Equal("refused", (await refused.Card(1)).State);
            Assert.Equal(StatusCodes.Status204NoContent, (await refused.Next()).Response.StatusCode);
        }

        // The owner's answers, posted as him from the page, a trusted account's comment by its
        // author, are known by their stored id: the card is queued and built, not read again.
        await using var jobs = await QuestionsTests.ReadWithQuestions(signIn: true);
        var asked = await QuestionsTests.Asked(jobs);
        var sent = await QuestionsTests.Answer(jobs, (Guid)asked["id"]!, (Guid)asked["reading_id"]!, null, "Nothing at all", null);
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);
        Fixed.Later(jobs);
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
        Assert.Single(await jobs.Runs(), run => run.Kind == Job.Read);
    }

    [Fact]
    public async Task ACardAddedToARunningFeatureIsReadAlone()
    {
        // Feature 10 is read with 11 and 12, and 11 is building.
        await using var jobs = await ReadFeature(First, Second);
        var building = await jobs.Job();
        Assert.Equal((Job.Build, First), ((string)building["kind"]!, (int)building["card"]!["number"]!));

        // The owner adds 13 to it, between 11 and 12. Nothing is read while 11 is building, and 13
        // is not built before it is read.
        jobs.Fake.Replace(User, Jobs.Jobs.World(FeatureIssues(First, Third, Second), Jobs.Jobs.ProjectFile()));
        await jobs.Routes.Read(jobs.Project, User);
        var (_, listed) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards");
        var ofFeature = Assert.Single(listed!["features"]!.AsArray())!;
        Assert.Equal([First, Third, Second], ofFeature["cards"]!.AsArray().Select(card => (int)card!["number"]!));
        Assert.Equal("queued", (await jobs.Card(Third)).State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Once 11 is done, 13 is next in the sub-issues' order: read alone, as a run of the card,
        // with the feature, its note and what 11 left, and the reader told the rest was read before.
        await FeatureJobs.Finish(jobs, First, "done");
        var job = await ReadAlone(jobs, Third, Alone(Ready(Third)));
        var brief = (string)job["brief"]!;
        Assert.Contains("## The feature", brief, StringComparison.Ordinal);
        Assert.Contains("## The code this feature touches", brief, StringComparison.Ordinal);
        Assert.Contains(Note, brief, StringComparison.Ordinal);
        Assert.Contains("## Read alone", brief, StringComparison.Ordinal);
        Assert.DoesNotContain($"#{Second} Card {Second}", brief, StringComparison.Ordinal);

        // Ready, it is built before 12, and its brief has the feature's note, not the one its own
        // reading wrote.
        var build = await jobs.Job();
        Assert.Equal((Job.Build, Third), ((string)build["kind"]!, (int)build["card"]!["number"]!));
        Assert.Contains(Note, (string)build["brief"]!, StringComparison.Ordinal);
        Assert.DoesNotContain(CardNote, (string)build["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartedCardIsNotReadAgain()
    {
        // Built and under review: an edit and a comment of the owner's read nothing.
        await using var jobs = await ReadyCard();
        await Built.Run(jobs, Built.Prepared(Built.Report()));
        var state = (await jobs.Card(1)).State;
        Fixed.Later(jobs);
        RefusalTests.Edit(jobs, 1, Jobs.Jobs.Owner, body: "Show the habits list, newest first.");
        Fixed.Comment(jobs, 1, Jobs.Jobs.Owner, "Newest first, please.");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(state, (await jobs.Card(1)).State);
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Read && run.FinishedAt is null);

        // Nor in any state from building on, whatever it says.
        foreach (var started in (string[])["building", "reviewing", "fixing", "done"])
        {
            await FeatureJobs.SetState(jobs, 1, started);
            await jobs.Routes.Read(jobs.Project, User);
            Assert.Equal(started, (await jobs.Card(1)).State);
        }

        // Queued again once it was built, as a card run again is, it is built, never read.
        await FeatureJobs.SetState(jobs, 1, "queued");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Single(await jobs.Runs(), run => run.Kind == Job.Read);
        var next = await jobs.Next();
        if (next.Response.StatusCode == StatusCodes.Status200OK)
        {
            Assert.NotEqual(Job.Read, (string)(await Workers.Workers.Json(next))["kind"]!);
        }

        Assert.Single(await jobs.Runs(), run => run.Kind == Job.Read);
    }

    [Fact]
    public async Task AnswersAreKeptOnlyForTheSameQuestions()
    {
        // He answers the card's questions; then edits it, and it is read again with questions.
        await using var jobs = await QuestionsTests.ReadWithQuestions(signIn: true);
        var asked = await QuestionsTests.Asked(jobs);
        var first = (Guid)asked["reading_id"]!;
        Assert.Equal(StatusCodes.Status204NoContent, (await QuestionsTests.Answer(jobs, (Guid)asked["id"]!, first, null, "Nothing at all", null)).Response.StatusCode);
        Fixed.Later(jobs);
        RefusalTests.Edit(jobs, 1, Jobs.Jobs.Owner, body: "Show the habits list, with its count.");
        await jobs.Routes.Read(jobs.Project, User);
        await ReadAlone(jobs, 1, ReadJobTests.Reading(Asks(1, QuestionsTests.Three)));

        // It needs his answers again, to the new reading's questions.
        Assert.Equal("needs_answers", (await jobs.Card(1)).State);
        var again = await QuestionsTests.Asked(jobs);
        Assert.NotEqual(first, (Guid)again["reading_id"]!);

        // Its page shows the new reading, not answered, and the earlier one with his answers.
        var story = await QuestionsTests.Story(jobs, 1);
        Assert.False((bool)story["reading"]!["answered"]!);
        var earlier = Assert.Single(story["earlier_readings"]!.AsArray())!;
        Assert.True((bool)earlier["answered"]!);
        Assert.Equal(
            ["By date, newest first", "Nothing at all", "Yes, in the mono font"],
            earlier["questions"]!.AsArray().Select(question => (string)question!["answer"]!));

        // Answered again, the build's brief carries only the new answers.
        Assert.Equal(StatusCodes.Status204NoContent, (await QuestionsTests.Answer(jobs, (Guid)again["id"]!, (Guid)again["reading_id"]!, null, null, null)).Response.StatusCode);
        var brief = (string)(await jobs.Job())["brief"]!;
        Assert.Contains("## Answers to its questions", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing at all", brief, StringComparison.Ordinal);

        // With questions off, a card read again with questions goes on the new recommendations,
        // and the brief carries none of his earlier answers.
        await using var off = await QuestionsTests.ReadWithQuestions(signIn: true);
        var offAsked = await QuestionsTests.Asked(off);
        Assert.Equal(StatusCodes.Status204NoContent, (await QuestionsTests.Answer(off, (Guid)offAsked["id"]!, (Guid)offAsked["reading_id"]!, null, "Nothing at all", null)).Response.StatusCode);
        await Merged.Sql(off, "UPDATE project_settings SET questions = false WHERE project_id = @project");
        Fixed.Later(off);
        RefusalTests.Edit(off, 1, Jobs.Jobs.Owner, body: "Show the habits list, with its count.");
        await off.Routes.Read(off.Project, User);
        await ReadAlone(off, 1, ReadJobTests.Reading(Asks(1, QuestionsTests.Three)));
        Assert.Equal("queued", (await off.Card(1)).State);
        var offBrief = (string)(await off.Job())["brief"]!;
        Assert.Contains("The owner was not asked, so each answer is the reader's recommendation", offBrief, StringComparison.Ordinal);
        Assert.Contains("A sentence", offBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing at all", offBrief, StringComparison.Ordinal);
    }
}
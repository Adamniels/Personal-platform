using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Readings;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Questions before starting: with the setting on, a card its reading found questions in is
/// <c>needs_answers</c>, posts nothing on GitHub, and waits under "Questions before starting" on
/// "Waiting for you"; with it off, the card goes on the reader's recommendations. Against the fake
/// code host and a real Postgres, with the fake worker. See "Reading a card" under "Jobs" in
/// devflow/README.md and in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class QuestionsTests
{
    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    /// <summary>The three questions card 1 is read with, each with its recommendation.</summary>
    internal static readonly (string Question, string Recommendation)[] Three =
    [
        ("Is the list sorted by name or by date?", "By date, newest first"),
        ("Does an empty list show a sentence or nothing?", "A sentence"),
        ("Is the count shown beside the heading?", "Yes, in the mono font"),
    ];

    /// <summary>Card 1 on its own, read with <see cref="Three"/> questions, the setting as a new
    /// project has it, on.</summary>
    internal static async Task<Jobs.Jobs> ReadWithQuestions()
    {
        var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false);
        await Read(jobs, Prepared(ReadJobTests.Reading(Asks(1, Three))));
        return jobs;
    }

    /// <summary>The questions' comments and the pending app comments posted, as the opening loop's
    /// minute would.</summary>
    private static Task PostPending(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<QuestionComments>().PostPending(jobs.Project, User, Ct);

    /// <summary>A card's story, as its page reads it.</summary>
    internal static async Task<JsonNode> Story(Jobs.Jobs jobs, int number)
    {
        var (status, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/{number}");
        Assert.Equal(StatusCodes.Status200OK, status);
        return story!;
    }

    [Fact]
    public async Task ACardWithQuestionsNeedsAnswers()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false);
        var (_, before) = await Built.Get(jobs, "/devflow/api/waiting");
        var (_, display) = await Built.Get(jobs, "/devflow/api/display");
        var writes = jobs.Fake.Writes(User).Count;

        await Read(jobs, Prepared(ReadJobTests.Reading(Asks(1, Three))));

        // The card waits for the owner's answers, and nothing is built meanwhile.
        var card = await jobs.Card(1);
        Assert.Equal(("needs_answers", ReadingStore.NeedsAnswers), (card.State, card.Reason));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Nothing is posted on GitHub, on the minute or after it: no comment, no label.
        await PostPending(jobs);
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(writes, jobs.Fake.Writes(User).Count);

        // Listed under "Questions before starting", with each question, its recommendation and
        // why; counted in the tab, the nav's dot and the front page.
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        var asked = Assert.Single(list!["question_cards"]!.AsArray())!;
        Assert.Equal(jobs.Project, (Guid)asked["project_id"]!);
        Assert.Equal(("Adamniels", "Personal-platform"), ((string)asked["owner"]!, (string)asked["project_name"]!));
        Assert.Equal((1, "Add the habits list"), ((int)asked["number"]!, (string)asked["title"]!));
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/1", (string)asked["web_url"]!);
        Assert.Null(asked["feature"]);
        var reading = Assert.Single(await Features.Reviewing.Runs(jobs, Job.Read));
        Assert.Equal(reading.Id, (Guid)asked["reading_id"]!);
        Assert.NotEqual(Guid.Empty, (Guid)asked["id"]!);
        Assert.Equal(
            Three.Select(question => (question.Question, question.Recommendation, $"Why {question.Recommendation.ToLowerInvariant()}")),
            asked["questions"]!.AsArray().Select(question => ((string)question!["question"]!, (string)question["recommendation"]!, (string)question["why"]!)));
        Assert.Equal((int)before!["count"]! + 1, (int)list["count"]!);
        Assert.True((bool)(await Built.Get(jobs, "/devflow/api/attention")).Body!["attention"]!);
        Assert.Equal((int)display!["waiting"]! + 1, (int)(await Built.Get(jobs, "/devflow/api/display")).Body!["waiting"]!);

        // Its page says it waits for the answers, and shows the recommendations, not yet answers.
        var story = (await Story(jobs, 1))["reading"]!;
        Assert.Equal("questions", (string)story["outcome"]!);
        Assert.False((bool)story["answered"]!);
        Assert.False((bool)story["commented"]!);
    }

    [Fact]
    public async Task QuestionsOffGoesOnTheRecommendations()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);
        await Merged.Sql(jobs, "UPDATE project_settings SET questions = false WHERE project_id = @project");
        (string, string)[] questions =
        [
            ("Is the list sorted by name or by date?", "By date, newest first"),
            ("Does an empty list show a sentence or nothing?", "A sentence"),
        ];

        // The fake worker waits for what follows a result on the minute, the comment among it.
        await Read(jobs, Prepared(ReadJobTests.Reading(Asks(1, questions))));

        // Queued on the recommendations, without the owner, and not listed for him.
        Assert.Equal(("queued", null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["question_cards"]!.AsArray());

        // One comment on the card, as the app, listing each question and the recommendation chosen;
        // the minute after posts none again.
        await PostPending(jobs);
        var comment = Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1));
        foreach (var (question, recommendation) in questions)
        {
            Assert.Contains(question, comment.Body, StringComparison.Ordinal);
            Assert.Contains(recommendation, comment.Body, StringComparison.Ordinal);
        }

        // Its id is kept, and the page shows the questions with the recommendations as the answers.
        var reading = (await Story(jobs, 1))["reading"]!;
        Assert.Equal("questions", (string)reading["outcome"]!);
        Assert.True((bool)reading["commented"]!);
        Assert.False((bool)reading["answered"]!);
        Assert.Equal(
            questions.Select(question => (question.Item1, question.Item2, true)),
            reading["questions"]!.AsArray().Select(asked => ((string)asked!["question"]!, (string)asked["answer"]!, (bool)asked["recommended"]!)));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            Assert.NotNull(await admin.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT comment_id FROM card_readings WHERE project_id = @project AND card_number = 1", new { project = jobs.Project }, cancellationToken: Ct)));
        }

        // Its build's brief carries them as the answers, each question with the recommendation.
        var brief = (string)(await jobs.Job())["brief"]!;
        var answers = brief[brief.IndexOf("## Answers to its questions", StringComparison.Ordinal)..];
        Assert.Contains("The owner was not asked, so each answer is the reader's recommendation", answers, StringComparison.Ordinal);
        Assert.Contains("Is the list sorted by name or by date?", answers, StringComparison.Ordinal);
        Assert.Contains("By date, newest first", answers, StringComparison.Ordinal);
        Assert.Contains("A sentence", answers, StringComparison.Ordinal);

        // A card on its own is told the code it touches, from its own reading.
        Assert.Contains("## The code this card touches", brief, StringComparison.Ordinal);
        Assert.Contains(Note, brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardPickedAgainStillNeedsAnswers()
    {
        await using var jobs = await ReadWithQuestions();

        // Its label taken off and put back: the card is gone, then picked as new, queued, with
        // the reading it had. Read as the project's minute would, it waits for the answers again,
        // and nothing is built on the recommendations or posted.
        await Merged.Sql(jobs, "DELETE FROM cards WHERE project_id = @project AND number = 1");
        var writes = jobs.Fake.Writes(User).Count;
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("needs_answers", (await jobs.Card(1)).State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        await PostPending(jobs);
        Assert.Equal(writes, jobs.Fake.Writes(User).Count);
    }
}
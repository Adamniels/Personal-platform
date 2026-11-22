using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Readings;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using PlatformData.Testing;

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
    internal static async Task<Jobs.Jobs> ReadWithQuestions(bool signIn = false)
    {
        var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the habits list")], read: false, signIn: signIn);
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

    /// <summary>The card waiting for answers, as "Waiting for you" lists it.</summary>
    internal static async Task<JsonNode> Asked(Jobs.Jobs jobs)
    {
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        return Assert.Single(list!["question_cards"]!.AsArray())!;
    }

    /// <summary>The owner's answers to the card <paramref name="id"/>, sent from the page for the
    /// reading <paramref name="reading"/>: each null to accept the recommendation, or his own text.</summary>
    internal static Task<HttpContext> Answer(Jobs.Jobs jobs, Guid id, Guid reading, params string?[] answers) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/cards/{id}/answers", Contract.Token, new JsonObject
        {
            ["reading_id"] = reading,
            ["answers"] = new JsonArray([.. answers.Select(answer => (JsonNode)(answer is null
                ? new JsonObject { ["accept"] = true }
                : new JsonObject { ["text"] = answer }))]),
        }.ToJsonString());

    /// <summary>The comments the owner posted, as him, on GitHub.</summary>
    private static List<FakeWrite> Mine(Jobs.Jobs jobs) =>
        [.. jobs.Fake.Writes(User).Where(write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner)];

    /// <summary>The issue's comments as the fake's world holds them.</summary>
    private static List<Comment> OnIssue(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(User).Installations.SelectMany(installation => installation.Repositories)
            .SelectMany(repository => repository.Conversations).Where(conversation => conversation.Number == number)
            .SelectMany(conversation => conversation.Comments).ToList();

    [Fact]
    public async Task MyAnswersArePostedAsMe()
    {
        await using var jobs = await ReadWithQuestions(signIn: true);
        var asked = await Asked(jobs);
        var (id, reading) = ((Guid)asked["id"]!, (Guid)asked["reading_id"]!);

        // The first and third accepted, the second written.
        var sent = await Answer(jobs, id, reading, null, "  Nothing at all, the list hides  ", null);
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);

        // One comment, posted with his linked token, as him: each question with its answer, and
        // "(recommended)" where he accepted it; nothing as the app.
        var comment = Assert.Single(Mine(jobs));
        Assert.Equal(1, comment.Number);
        Assert.Contains("1. Is the list sorted by name or by date?\n   - Answer: By date, newest first (recommended)\n", comment.Body, StringComparison.Ordinal);
        Assert.Contains("2. Does an empty list show a sentence or nothing?\n   - Answer: Nothing at all, the list hides\n", comment.Body, StringComparison.Ordinal);
        Assert.Contains("3. Is the count shown beside the heading?\n   - Answer: Yes, in the mono font (recommended)\n", comment.Body, StringComparison.Ordinal);
        Assert.EndsWith(AnswerText.Trail(1, reading) + "\n", comment.Body, StringComparison.Ordinal);
        Assert.Empty(Stops.Stop.ByApp(jobs, "comment", 1));

        // Its id is kept with the reading, so it never reads the card again.
        var posted = Assert.Single(OnIssue(jobs, 1), each => each.Author == Jobs.Jobs.Owner);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            Assert.Equal(posted.Id, await admin.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT comment_id FROM card_readings WHERE project_id = @project AND run_id = @reading AND card_number = 1",
                new { project = jobs.Project, reading },
                cancellationToken: Ct)));
        }

        // The card is queued, no longer listed, and the minute after posts nothing of the app's.
        Assert.Equal(("queued", null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["question_cards"]!.AsArray());
        await PostPending(jobs);
        Assert.Empty(Stops.Stop.ByApp(jobs, "comment", 1));

        // Its page shows his answers, the accepted ones marked.
        var story = (await Story(jobs, 1))["reading"]!;
        Assert.True((bool)story["answered"]!);
        Assert.True((bool)story["commented"]!);
        Assert.Equal(
            [("By date, newest first", true), ("Nothing at all, the list hides", false), ("Yes, in the mono font", true)],
            story["questions"]!.AsArray().Select(question => ((string)question!["answer"]!, (bool)question["recommended"]!)));

        // Its build's brief carries them under "Answers to its questions", as his.
        var brief = (string)(await jobs.Job())["brief"]!;
        var answers = brief[brief.IndexOf("## Answers to its questions", StringComparison.Ordinal)..];
        Assert.Contains("The owner answered each", answers, StringComparison.Ordinal);
        Assert.Contains("Answer: Nothing at all, the list hides", answers, StringComparison.Ordinal);
        Assert.Contains("Answer: By date, newest first (the reader's recommendation, which the owner accepted)", answers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryQuestionNeedsAnAnswer()
    {
        await using var jobs = await ReadWithQuestions(signIn: true);
        var asked = await Asked(jobs);
        var (id, reading) = ((Guid)asked["id"]!, (Guid)asked["reading_id"]!);
        var path = $"/devflow/api/cards/{id}/answers";

        // Refused before anything is posted: 422 naming answers, for one missing, one too many, one
        // both accepted and written, one neither, one empty, and one longer than 4000 characters.
        var refused = new JsonArray[]
        {
            [new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
            [new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
            [new JsonObject { ["accept"] = true, ["text"] = "Both" }, new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
            [new JsonObject(), new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
            [new JsonObject { ["text"] = "   " }, new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
            [new JsonObject { ["text"] = new string('a', 4001) }, new JsonObject { ["accept"] = true }, new JsonObject { ["accept"] = true }],
        };
        foreach (var answers in refused)
        {
            var answer = await jobs.Routes.App.SendJson("POST", path, Contract.Token, new JsonObject { ["reading_id"] = reading, ["answers"] = answers }.ToJsonString());
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
            Assert.True(JsonNode.Parse(await Harness.Body(answer))!["errors"]!.AsObject().ContainsKey("answers"), answers.ToJsonString());
        }

        // Without the reading, 422; for a reading that is not the one asking, 409.
        var noReading = await jobs.Routes.App.SendJson("POST", path, Contract.Token, JsonSerializer.Serialize(new { answers = new[] { new { accept = true } } }));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, noReading.Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await Answer(jobs, id, Guid.CreateVersion7(), null, null, null)).Response.StatusCode);

        // A card the caller does not have is 404, and from another origin 403.
        Assert.Equal(StatusCodes.Status404NotFound, (await Answer(jobs, Guid.CreateVersion7(), reading, null, null, null)).Response.StatusCode);
        var elsewhere = await jobs.Routes.App.Send("POST", path, Contract.Token, ("Origin", "https://evil.example"));
        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);

        // Nothing was posted, and the card still waits.
        Assert.Empty(Mine(jobs));
        Assert.Equal("needs_answers", (await jobs.Card(1)).State);

        // Four thousand characters are taken; once answered, the card no longer needs answers, so
        // sending again is 409 and posts nothing more.
        Assert.Equal(StatusCodes.Status204NoContent, (await Answer(jobs, id, reading, new string('a', 4000), null, null)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await Answer(jobs, id, reading, null, null, null)).Response.StatusCode);
        Assert.Single(Mine(jobs));
    }

    [Fact]
    public async Task AnAnswerAlreadyPostedIsNotPostedTwice()
    {
        await using var jobs = await ReadWithQuestions(signIn: true);
        var asked = await Asked(jobs);
        var (id, reading) = ((Guid)asked["id"]!, (Guid)asked["reading_id"]!);

        // GitHub took the comment but the answer was lost: the card still waits, and the same
        // answers sent again find the comment by its last line and keep it, posting nothing more.
        Assert.Equal(StatusCodes.Status204NoContent, (await Answer(jobs, id, reading, null, "Nothing", null)).Response.StatusCode);
        var posted = Assert.Single(Mine(jobs));
        await Merged.Sql(jobs, "UPDATE cards SET state = 'needs_answers', reason = 'x' WHERE project_id = @project AND number = 1");
        await Merged.Sql(jobs, "UPDATE card_readings SET comment_id = NULL, answers = NULL, answered_at = NULL WHERE project_id = @project AND card_number = 1");

        Assert.Equal(StatusCodes.Status204NoContent, (await Answer(jobs, id, reading, null, "Nothing", null)).Response.StatusCode);
        Assert.Single(Mine(jobs));
        Assert.Equal("queued", (await jobs.Card(1)).State);
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(
            Assert.Single(OnIssue(jobs, 1), each => each.Author == Jobs.Jobs.Owner).Id,
            await admin.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT comment_id FROM card_readings WHERE project_id = @project AND card_number = 1", new { project = jobs.Project }, cancellationToken: Ct)));
        Assert.Equal(posted.Body, Mine(jobs)[0].Body);
    }
}
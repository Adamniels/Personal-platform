using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Readings;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Every card is read before anything is built: a feature's cards together in one reading, a card
/// on its own alone, each answered ready, with questions, or too thin; a ready card is queued, a card
/// with questions is queued on the reader's recommendations, which Dev flow posts on it once and its
/// build's brief carries as the answers, and a card too thin is not built. The feature's note of the
/// code it touches goes into every brief of the feature. A reading is judged as any run. Against the
/// fake code host and a real Postgres, with the fake worker. See "Jobs" in devflow/README.md and
/// "Reading a card" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ReadJobTests
{
    private const int Parent = FeaturePage.Parent;
    private const int First = FeaturePage.First;
    private const int Second = FeaturePage.Second;
    private const int Third = FeaturePage.Third;

    /// <summary>The note the reader writes of the code the cards touch.</summary>
    private const string Note = "devflow/src/DevFlow/Jobs/Dispatcher.cs: where a job is handed out, which each card changes.";

    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    /// <summary>A feature of three cards, 11, 12 and 13, its parent 10, none of them read yet.</summary>
    private static Task<Jobs.Jobs> Feature() =>
        Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second, Third]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
            FeatureJobs.Sub(Third, "The feature's page"),
        ], read: false);

    /// <summary>A card read as ready.</summary>
    internal static JsonObject Ready(int number) => new()
    {
        ["number"] = number,
        ["outcome"] = "ready",
        ["questions"] = new JsonArray(),
        ["missing"] = new JsonArray(),
    };

    /// <summary>A card read with <paramref name="questions"/>, each a question and its
    /// recommendation.</summary>
    internal static JsonObject Asks(int number, params (string Question, string Recommendation)[] questions) => new()
    {
        ["number"] = number,
        ["outcome"] = "questions",
        ["questions"] = new JsonArray([.. questions.Select(question => (JsonNode)new JsonObject
        {
            ["question"] = question.Question,
            ["recommendation"] = question.Recommendation,
            ["why"] = $"Why {question.Recommendation.ToLowerInvariant()}",
        })]),
        ["missing"] = new JsonArray(),
    };

    /// <summary>A card read as too thin, lacking <paramref name="missing"/>.</summary>
    internal static JsonObject Thin(int number, params string[] missing) => new()
    {
        ["number"] = number,
        ["outcome"] = "too_thin",
        ["questions"] = new JsonArray(),
        ["missing"] = new JsonArray([.. missing.Select(line => (JsonNode)line)]),
    };

    /// <summary>A reading in its form of <paramref name="cards"/>, with <see cref="Note"/>.</summary>
    internal static JsonObject Reading(params JsonObject[] cards) => new()
    {
        ["cards"] = new JsonArray([.. cards.Select(card => (JsonNode)card)]),
        ["touches"] = Note,
    };

    /// <summary>A reading's prepared result for the fake worker: succeeded with
    /// <paramref name="reading"/>, and no check, since a reading runs none.</summary>
    internal static JsonObject Prepared(JsonNode reading) => Built.Prepared(reading, checkExit: null);

    /// <summary>The fake worker takes the project's next job, a reading, and ends it with
    /// <paramref name="result"/>; its answer.</summary>
    internal static async Task<JsonObject> Read(Jobs.Jobs jobs, JsonObject result)
    {
        var ran = await Built.Run(jobs, result);
        Assert.Equal(Job.Read, (string)ran["kind"]!);
        // A minute on, so what follows starts after it.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return ran;
    }

    /// <summary>The project's reading runs, oldest first.</summary>
    private static Task<List<Reviewing.FeatureRunRow>> Readings(Jobs.Jobs jobs) => Reviewing.Runs(jobs, Job.Read);

    /// <summary>A card's story, as its page reads it.</summary>
    private static async Task<JsonNode> Story(Jobs.Jobs jobs, int number)
    {
        var (status, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/{number}");
        Assert.Equal(StatusCodes.Status200OK, status);
        return story!;
    }

    [Fact]
    public async Task AFeatureIsReadOnceBeforeItsFirstCard()
    {
        await using var jobs = await Feature();
        await FeatureJobs.FirstCommit(jobs);
        await jobs.SayHello(Jobs.Jobs.SecondWorker);

        // Labelling the feature hands out one reading, before any build: the parent as its card, on
        // main, since the feature has no branch yet, carrying every card of it in its order, each
        // as picked.
        var job = await jobs.Job();
        Assert.Equal(Job.Read, (string)job["kind"]!);
        Assert.Equal((Parent, "Dev flow: features"), ((int)job["card"]!["number"]!, (string)job["card"]!["title"]!));
        Assert.Equal(("main", "main"), ((string)job["branch"]!, (string)job["base"]!));
        var cards = job["to_read"]!["cards"]!.AsArray();
        Assert.Equal([First, Second, Third], cards.Select(card => (int)card!["number"]!));
        Assert.Equal(("Show each feature", "Build the thing of card 12."), ((string)cards[1]!["title"]!, (string)cards[1]!["body"]!));
        var brief = (string)job["brief"]!;
        Assert.StartsWith("# Read these cards", brief, StringComparison.Ordinal);
        Assert.Contains("#10 Dev flow: features\n\nThe whole of feature 10.", brief, StringComparison.Ordinal);
        Assert.Contains("#11 Read the sub-issues\n\nBuild the thing of card 11.", brief, StringComparison.Ordinal);
        Assert.Contains("#13 The feature's page\n\nBuild the thing of card 13.", brief, StringComparison.Ordinal);
        Assert.Null(job["to_fix"]);
        Assert.Equal("", (string)job["check"]!);

        // Its cards read; no build of the feature is handed out while it runs, and no second
        // reading of it.
        Assert.Equal(["reading", "reading", "reading"], (await jobs.Cards()).Select(card => card.State));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.Jobs.SecondWorker)).Response.StatusCode);
        var run = Assert.Single(await Readings(jobs));
        Assert.Equal((null, Parent, "main", "main", null), (run.CardNumber, run.FeatureNumber, run.Branch, run.Base, run.FinishedAt));

        // Read as ready, every card is queued, and the first is built.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, "main", Reading(Ready(First), Ready(Second), Ready(Third)), checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        Assert.Equal(["queued", "queued", "queued"], (await jobs.Cards()).Select(card => card.State));
        var build = await jobs.Job(Jobs.Jobs.SecondWorker);
        Assert.Equal((Job.Build, First), ((string)build["kind"]!, (int)build["card"]!["number"]!));

        // Once: its next card is built with no reading of its own.
        await FeatureJobs.Finish(jobs, First, "done");
        var next = await jobs.Job();
        Assert.Equal((Job.Build, Second), ((string)next["kind"]!, (int)next["card"]!["number"]!));
        Assert.Single(await Readings(jobs));
    }

    [Fact]
    public async Task ACardOnItsOwnIsReadAlone()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the empty state"), Jobs.Jobs.Card(2, "Count the habits")], read: false);
        await jobs.SayHello(Jobs.Jobs.SecondWorker);

        // Each card on its own gets a reading of its own, of it alone, before its build.
        var first = await jobs.Job();
        Assert.Equal((Job.Read, 1), ((string)first["kind"]!, (int)first["card"]!["number"]!));
        Assert.Equal([1], first["to_read"]!["cards"]!.AsArray().Select(card => (int)card!["number"]!));
        Assert.DoesNotContain("Count the habits", (string)first["brief"]!, StringComparison.Ordinal);
        var second = await jobs.Job(Jobs.Jobs.SecondWorker);
        Assert.Equal((Job.Read, 2), ((string)second["kind"]!, (int)second["card"]!["number"]!));
        // Handed out at the same moment, so in no order of their own.
        Assert.Equal([(int?)1, 2], (await Readings(jobs)).Select(run => run.CardNumber).Order());
        Assert.All(await Readings(jobs), run => Assert.Null(run.FeatureNumber));

        // Card 1 read as ready is built next; card 2 is still being read.
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, "main", Reading(Ready(1)), checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)first["job_id"]!), result)).Response.StatusCode);
        var build = await jobs.Job();
        Assert.Equal((Job.Build, 1), ((string)build["kind"]!, (int)build["card"]!["number"]!));
        Assert.Equal("reading", (await jobs.Card(2)).State);
    }

    [Fact]
    public async Task AReadyCardIsQueued()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);

        await Read(jobs, Prepared(Reading(Ready(1))));

        // Queued with nothing to say, the reading's run succeeded, and the card's page shows the
        // reading.
        var card = await jobs.Card(1);
        Assert.Equal(("queued", null), (card.State, card.Reason));
        Assert.Equal("succeeded", Assert.Single(await Readings(jobs)).Result);
        var reading = (await Story(jobs, 1))["reading"]!;
        Assert.Equal("ready", (string)reading["outcome"]!);
        Assert.Empty(reading["questions"]!.AsArray());
        Assert.Empty(reading["missing"]!.AsArray());
        Assert.NotNull((string?)reading["read_at"]);

        // And it is built next.
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task ACardWithQuestionsGoesOnTheRecommendations()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);
        (string, string)[] questions =
        [
            ("Is the list sorted by name or by date?", "By date, newest first"),
            ("Does an empty list show a sentence or nothing?", "A sentence"),
        ];

        // The fake worker waits for what follows a result on the minute, the comment among it.
        await Read(jobs, Prepared(Reading(Asks(1, questions))));

        // Queued on the recommendations, without the owner.
        Assert.Equal(("queued", null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));

        // One comment on the card, as the app, listing each question and the recommendation chosen;
        // the minute after posts none again.
        await jobs.Routes.App.Services.GetRequiredService<QuestionComments>().PostPending(jobs.Project, User, Ct);
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
        Assert.Equal(
            questions.Select(question => (question.Item1, question.Item2)),
            reading["questions"]!.AsArray().Select(asked => ((string)asked!["question"]!, (string)asked["answer"]!)));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            Assert.NotNull(await admin.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT comment_id FROM card_readings WHERE project_id = @project AND card_number = 1", new { project = jobs.Project }, cancellationToken: Ct)));
        }

        // Its build's brief carries them as the answers, each question with the recommendation.
        var brief = (string)(await jobs.Job())["brief"]!;
        var answers = brief[brief.IndexOf("## Answers to its questions", StringComparison.Ordinal)..];
        Assert.Contains("Is the list sorted by name or by date?", answers, StringComparison.Ordinal);
        Assert.Contains("By date, newest first", answers, StringComparison.Ordinal);
        Assert.Contains("A sentence", answers, StringComparison.Ordinal);

        // A card on its own is told the code it touches, from its own reading.
        Assert.Contains("## The code this card touches", brief, StringComparison.Ordinal);
        Assert.Contains(Note, brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThinCardIsNotBuilt()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);

        await Read(jobs, Prepared(Reading(Thin(1, "Which page shows the list", "What an empty list says"))));

        // Not queued: refused, its reason what is missing, and nothing is handed out for it.
        var card = await jobs.Card(1);
        Assert.Equal("refused", card.State);
        Assert.Contains("Which page shows the list", card.Reason, StringComparison.Ordinal);
        Assert.Contains("What an empty list says", card.Reason, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Build);
        var reading = (await Story(jobs, 1))["reading"]!;
        Assert.Equal("too_thin", (string)reading["outcome"]!);
        Assert.Equal(["Which page shows the list", "What an empty list says"], reading["missing"]!.AsArray().Select(line => (string)line!));
    }

    [Fact]
    public async Task ATooThinCardHoldsBackOnlyItselfInItsFeature()
    {
        await using var jobs = await Feature();
        await FeatureJobs.FirstCommit(jobs);

        // The first card too thin: the rest of the feature goes on, as after a stop.
        await Read(jobs, Prepared(Reading(Thin(First, "What the cards read"), Ready(Second), Ready(Third))));

        Assert.Equal(["refused", "queued", "queued"], (await jobs.Cards()).Select(card => card.State));
        var build = await jobs.Job();
        Assert.Equal((Job.Build, Second), ((string)build["kind"]!, (int)build["card"]!["number"]!));
    }

    [Fact]
    public async Task AHeldCardIsReadOnceReleasedAndNotBuiltWhenTooThin()
    {
        static FakeIssue[] Issues(bool held) =>
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second, Third]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature") with { Labels = held ? ["devflow-hold"] : [] },
            FeatureJobs.Sub(Third, "The feature's page"),
        ];

        await using var jobs = await Jobs.Jobs.Start(Issues(held: true), read: false);
        await FeatureJobs.FirstCommit(jobs);

        // The feature's reading leaves the held card out: it would not be handed out, so it is not
        // read, and it stays held with no reading of its own.
        Assert.Equal([First, Third], await Answer(jobs, Reading(Ready(First), Ready(Third))));
        Assert.Equal(["queued", "held", "queued"], (await jobs.Cards()).Select(card => card.State));
        Assert.Null((await Story(jobs, Second))["reading"]);

        // Released by the owner, it is queued and not read, so not built; the first card is.
        jobs.Fake.Replace(User, Jobs.Jobs.World(Issues(held: false), Jobs.Jobs.ProjectFile()));
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("queued", (await jobs.Card(Second)).State);
        var build = await jobs.Job();
        Assert.Equal((Job.Build, First), ((string)build["kind"]!, (int)build["card"]!["number"]!));

        // Its turn come, it is read alone, before the card after it is built; read as too thin, it
        // is refused and never built, and the feature goes on past it.
        await FeatureJobs.Finish(jobs, First, "done");
        Assert.Equal([Second], await Answer(jobs, Reading(Thin(Second, "What each feature shows"))));
        Assert.Equal("refused", (await jobs.Card(Second)).State);
        var next = await jobs.Job();
        Assert.Equal((Job.Build, Third), ((string)next["kind"]!, (int)next["card"]!["number"]!));
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Build && run.CardNumber == Second);
    }

    /// <summary>The first worker takes the project's next job, a reading, and sends back
    /// <paramref name="reading"/> as its result; the numbers of the cards it was handed.</summary>
    private static async Task<List<int>> Answer(Jobs.Jobs jobs, JsonObject reading)
    {
        var job = await jobs.Job();
        Assert.Equal(Job.Read, (string)job["kind"]!);
        var result = Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!, reading, checkExit: null);
        result["head_commit"] = null;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return [.. job["to_read"]!["cards"]!.AsArray().Select(card => (int)card!["number"]!)];
    }

    [Fact]
    public async Task ACardWhoseReadingWasTooThinIsNeverBuilt()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);
        await Read(jobs, Prepared(Reading(Thin(1, "Which page shows the list"))));

        // Queued again by whatever path, its newest reading still says too thin: nothing is built.
        await FeatureJobs.SetState(jobs, 1, "queued");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Build);
    }

    [Fact]
    public async Task TheNoteIsInEveryBrief()
    {
        // A card's build, review and fix, on a feature read with the note.
        await using (var jobs = await Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
        ], read: false))
        {
            await FeatureJobs.FirstCommit(jobs);
            await Read(jobs, Prepared(Reading(Ready(First), Ready(Second))));
            var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
            Assert.Equal(Job.Build, (string)built["kind"]!);
            HasTheNote((string)built["brief"]!);
            await jobs.Routes.Read(jobs.Project, User);
            var reviewed = await Built.Review(jobs, Built.Verdict("changes"));
            HasTheNote((string)reviewed["brief"]!);
            HasTheNote((string)(await Fixed.Fix(jobs))["brief"]!);
        }

        // A conflict of the feature, read with the note.
        await using (var jobs = await Ending.Feature())
        {
            await NoteOf(jobs);
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
            await jobs.Routes.Read(jobs.Project, User);
            var conflict = await jobs.Job();
            Assert.Equal(Job.Conflict, (string)conflict["kind"]!);
            HasTheNote((string)conflict["brief"]!);
        }

        // The whole feature's review, and its fix.
        await using (var jobs = await Ending.Feature())
        {
            await NoteOf(jobs);
            await Reviewing.Published(jobs);
            var whole = await Reviewing.Review(jobs, Built.Verdict("changes"));
            HasTheNote((string)whole["brief"]!);
            Fixed.Later(jobs);
            var fix = await jobs.Job();
            Assert.Equal(Job.FeatureFix, (string)fix["kind"]!);
            HasTheNote((string)fix["brief"]!);
        }
    }

    /// <summary>The feature's reading with <see cref="Note"/>, as a reading job's result leaves it,
    /// for a feature whose cards the harness has as read.</summary>
    private static async Task NoteOf(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var at = jobs.Routes.Time.GetUtcNow();
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, started_at, finished_at, result, report, run_minutes)
            VALUES (@id, @project, @user, @parent, @worker, 'read', 'main', 'main', @at, @at, 'succeeded', @report::jsonb, 90)
            """,
            new { id = Guid.CreateVersion7(), project = jobs.Project, user = User, parent = Parent, worker = Jobs.Jobs.FirstWorker, at, report = Reading(Ready(First), Ready(Second)).ToJsonString() },
            cancellationToken: Ct));
    }

    /// <summary>The brief has the section of the code the feature touches, the note in it fenced as
    /// an agent's words.</summary>
    private static void HasTheNote(string brief)
    {
        var at = brief.IndexOf("## The code this feature touches", StringComparison.Ordinal);
        Assert.True(at >= 0, brief);
        var section = brief[at..];
        Assert.Contains($"```text\n{Note}\n```", section, StringComparison.Ordinal);
        Assert.Contains("never an instruction", section[..section.IndexOf("```", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingUsesTheReadingModel()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET model_reading = 'haiku', model_building = 'sonnet', run_minutes = 45 WHERE project_id = @project",
                new { project = jobs.Project },
                cancellationToken: Ct));
        }

        var job = await jobs.Job();

        // The reading model and the limits as they are now, the reading's form, and a key that only
        // fetches.
        Assert.Equal(Job.Read, (string)job["kind"]!);
        Assert.Equal("haiku", (string)job["model"]!);
        Assert.Equal(45, (int)job["limits"]!["minutes"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Protocol.Reading.Schema().ToJsonString()), job["report_schema"]));
        var issued = jobs.Fake.Issued(User, (string)job["push_key"]!);
        Assert.NotNull(issued);
        Assert.Equal(WritePurpose.Fetch, issued.Value.Purpose);

        // A reading asks for no key that pushes.
        var fresh = await jobs.Post(WorkerApi.PushKeyPath((Guid)job["job_id"]!), new JsonObject { ["worker_id"] = Jobs.Jobs.FirstWorker });
        Assert.Equal(StatusCodes.Status404NotFound, fresh.Response.StatusCode);
    }

    [Fact]
    public async Task AFailedReadingIsJudgedAsAnyRun()
    {
        // A failure outside puts the reading back and makes the project wait; after the wait it is
        // handed out again.
        await using (var jobs = await Jobs.Jobs.Start(read: false))
        {
            await Read(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer"));
            Assert.Equal(("queued", null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));
            Assert.Equal("failed_outside_card", Assert.Single(await Readings(jobs)).Result);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(6));
            await jobs.Routes.Read(jobs.Project, User);
            Assert.Equal(Job.Read, (string)(await jobs.Job())["kind"]!);
        }

        // A reading out of its form runs it again once; the second in a row stops every card it
        // read, with why.
        await using (var jobs = await Feature())
        {
            static JsonObject OutOfForm() => Prepared(Reading(Ready(First), Ready(Second)));
            await Read(jobs, OutOfForm());
            Assert.All(await jobs.Cards(), card => Assert.Equal("queued", card.State));
            Assert.All(await jobs.Cards(), card => Assert.Contains("The reading did not match its form: cards: #13 was given and not read", card.Reason, StringComparison.Ordinal));
            Assert.All(await jobs.Cards(), card => Assert.StartsWith("Failed once in the card", card.Reason, StringComparison.Ordinal));

            await Read(jobs, OutOfForm());
            Assert.All(await jobs.Cards(), card => Assert.Equal("stopped", card.State));
            Assert.All(await jobs.Cards(), card => Assert.StartsWith("Failed twice in a row", card.Reason, StringComparison.Ordinal));
            Assert.Equal(["report_invalid", "report_invalid"], (await Readings(jobs)).Select(run => run.Result));
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        }
    }

    [Fact]
    public async Task ALostReadingIsPutBack()
    {
        await using var jobs = await Jobs.Jobs.Start(read: false);
        var job = await jobs.Job();
        Assert.Equal("reading", (await jobs.Card(1)).State);

        // Its time and the margin past, its worker gone: lost, outside the card, and read again.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(101));
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("lost", Assert.Single(await Readings(jobs)).Result);
        Assert.Equal(("queued", null), ((await jobs.Card(1)).State, (await jobs.Card(1)).Reason));
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(6));
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(Job.Read, (string)(await jobs.Job())["kind"]!);
        Assert.NotEqual((Guid)job["job_id"]!, (await Readings(jobs)).Last().Id);
    }
}
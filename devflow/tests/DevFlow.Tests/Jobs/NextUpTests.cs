using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// "Next up" on "Running now": every card of the caller's projects that Dev flow will run without
/// the owner first, in the order work starts, each with the reason <see cref="DevFlow.Jobs.RunDecision"/>
/// gives it and where that reason is acted on, carried by the running list and its stream. Against
/// real Postgres and the fake code host. See "The pages" and "Jobs" in devflow/README.md.
/// </summary>
public sealed class NextUpTests
{
    private const string Path = "/devflow/api/running";
    private const string Events = "/devflow/api/running/events";

    /// <summary>The second project's repository, fodr, which no worker has said hello in.</summary>
    private const long Fodr = 2002;

    private static readonly string[] High = ["devflow", "priority: high"];
    private static readonly string[] Low = ["devflow", "priority: low"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CardsInTheOrderTheyRun()
    {
        // Personal-platform, with its worker: card 1 low, card 2 with no priority, card 3 high but
        // written late, and 46 more with no priority written later still.
        await using var jobs = await Jobs.Start([
            Jobs.Card(1, labels: Low),
            Jobs.Card(2),
            Jobs.Card(3, labels: High, minutes: 30),
            .. Enumerable.Range(10, 46).Select(number => Jobs.Card(number, minutes: 100 + number)),
        ]);
        // fodr, with no worker: card 6 high, written after card 3, and card 5 with no priority,
        // written after card 2 and before card 10.
        var fodr = await SecondProject(jobs, [Jobs.Card(5, minutes: 10), Jobs.Card(6, labels: High, minutes: 40)]);

        // A card of another user's, queued in a project of theirs, is never one.
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at,
                                   picked_body_sha256, picked_title, picked_body)
                VALUES (gen_random_uuid(), @bobs, @bob, 7, 'Bob''s card', 'https://github.com/bob/bobs-repository/issues/7',
                        'queued', 'high', now() - interval '1 year', now(), now(), sha256('body'::bytea), 'Bob''s card', 'body')
                """,
                new { bobs, bob },
                cancellationToken: Ct));
        }

        // Each project's own queue order, high, none and low, each oldest first, and across the
        // two by priority and then age: 3 and 6 high, then 2 and 5, then the 46, and card 1, low,
        // the 51st, left for "and 1 more".
        var list = await List(jobs);
        var next = list["next"]!.AsArray();
        (Guid, int)[] expected =
        [
            (jobs.Project, 3), (fodr, 6), (jobs.Project, 2), (fodr, 5),
            .. Enumerable.Range(10, 46).Select(number => (jobs.Project, number)),
        ];
        Assert.Equal(expected, next.Select(Key));
        Assert.Equal(1, (int)list["next_more"]!);
        Assert.DoesNotContain(next, card => (Guid)card!["project_id"]! == bobs);

        // A card's facts: its project, its title and its state, and the job due for it.
        var first = next[0]!;
        Assert.Equal("Personal-platform", (string)first["project_name"]!);
        Assert.Equal("Card 3", (string)first["title"]!);
        Assert.Equal("queued", (string)first["state"]!);
        Assert.Equal("build", (string)first["job"]!);
        Assert.Equal("fodr", (string)next[1]!["project_name"]!);

        // The worker takes card 3: a card under way is a panel, not next, and card 1 is the 50th.
        Assert.Equal(3, (int)(await jobs.Job())["card"]!["number"]!);
        list = await List(jobs);
        next = list["next"]!.AsArray();
        Assert.Equal([.. expected.Skip(1), (jobs.Project, 1)], next.Select(Key));
        Assert.Equal(0, (int)list["next_more"]!);

        // Nothing waiting is an empty list.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("UPDATE cards SET state = 'done' WHERE state = 'queued'", cancellationToken: Ct));
        }

        list = await List(jobs);
        Assert.Empty(list["next"]!.AsArray());
        Assert.Equal(0, (int)list["next_more"]!);
    }

    [Fact]
    public async Task EachCardHasItsReason()
    {
        // Card 1 is blocked on GitHub by card 2; feature 10's second card follows one that needs
        // answers; cards 2 and 3 are free; and fodr, with no worker, has card 5.
        await using var jobs = await Jobs.Start([
            Jobs.Card(1) with { BlockedBy = [2] },
            Jobs.Card(2),
            Jobs.Card(3),
            FeatureJobs.Parent(10, "A feature", [11, 12]),
            FeatureJobs.Sub(11),
            FeatureJobs.Sub(12),
        ]);
        await FeatureJobs.SetState(jobs, 11, "needs_answers");
        var fodr = await SecondProject(jobs, [Jobs.Card(5)]);

        var cards = await Cards(jobs);
        Assert.Equal(new Shown("Waits for #2, which it depends on", "card", 2), cards[(jobs.Project, 1)]);
        Assert.Equal(new Shown("Waits for your answers on #11", "waiting_for_you", null), cards[(jobs.Project, 12)]);
        Assert.Equal(new Shown("No worker is running for fodr", "project_page", null), cards[(fodr, 5)]);
        // A card that runs has no reason not to.
        Assert.Equal(new Shown(null, null, null), cards[(jobs.Project, 2)]);
        Assert.Equal(new Shown(null, null, null), cards[(jobs.Project, 3)]);
        // A card that waits for the owner is on "Waiting for you", not here.
        Assert.False(cards.ContainsKey((jobs.Project, 11)));

        // The one worker takes card 2: card 3 is next for it, and card 1 still waits for card 2.
        Assert.Equal(2, (int)(await jobs.Job())["card"]!["number"]!);
        cards = await Cards(jobs);
        Assert.Equal(new Shown("Next for fodr-worker, after its current run", null, null), cards[(jobs.Project, 3)]);
        Assert.Equal(new Shown("Waits for #2, which it depends on", "card", 2), cards[(jobs.Project, 1)]);

        // A worker seen later that needs a rebuild does not speak for the project, since the one
        // that needs none takes its jobs.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.SayHello(Jobs.SecondWorker, "platform-hosted/devflow-worker:2026.01.01", name: "old-worker");
        Assert.NotEqual("Its worker needs building again", (await Cards(jobs))[(jobs.Project, 3)].Reason);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("DELETE FROM workers WHERE id = @worker", new { worker = Jobs.SecondWorker }, cancellationToken: Ct));
        }

        // An open issue that is not a card is said, with nothing to link.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET waits_for = ARRAY['#99'] WHERE project_id = @project AND number = 1",
                new { project = jobs.Project },
                cancellationToken: Ct));
        }

        Assert.Equal(new Shown("Waits for #99, which it depends on", null, null), (await Cards(jobs))[(jobs.Project, 1)]);

        // With no Claude login, no card of either project runs, and the settings is where it is given.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("DELETE FROM claude_logins WHERE user_id = @user", new { user = Jobs.User }, cancellationToken: Ct));
        }

        cards = await Cards(jobs);
        Assert.Equal(new Shown("No Claude login", "settings", null), cards[(jobs.Project, 3)]);
        Assert.Equal(new Shown("No Claude login", "settings", null), cards[(fodr, 5)]);

        // The project stopped comes first, on its page.
        await Merging.Merged.Stop(jobs);
        cards = await Cards(jobs);
        Assert.Equal(new Shown("The project is stopped: resume it", "project_page", null), cards[(jobs.Project, 1)]);
        Assert.Equal(new Shown("No Claude login", "settings", null), cards[(fodr, 5)]);
    }

    [Fact]
    public async Task AReasonChangeIsStreamed()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)]);
        await Merging.Merged.Stop(jobs);
        await using var stream = await EventStream.Open(jobs.Routes.App, Events, Contract.Token);
        Assert.Equal(HttpStatusCode.OK, stream.Status);

        // At once, the card waits for the project, stopped.
        var first = await stream.Expect();
        Assert.Equal("running", first.EventType);
        var card = Assert.Single(JsonNode.Parse(first.Data)!["next"]!.AsArray())!;
        Assert.Equal("The project is stopped: resume it", (string?)card["reason"]);

        // Nothing changes, so nothing is sent.
        Assert.Null(await stream.AfterChecks(jobs.Routes.Time, most: 2));

        // The owner resumes it: the card's reason goes, sent within a check.
        var resumed = await jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/resume", Contract.Token, $$"""{"commit": "{{new string('e', 40)}}"}""");
        Assert.Equal(StatusCodes.Status204NoContent, resumed.Response.StatusCode);
        var changed = await stream.AfterChecks(jobs.Routes.Time, most: 1);
        Assert.NotNull(changed);
        Assert.Equal("running", changed.Value.EventType);
        card = Assert.Single(JsonNode.Parse(changed.Value.Data)!["next"]!.AsArray())!;
        Assert.Equal(1, (int)card["number"]!);
        Assert.Null((string?)card["reason"]);
    }

    /// <summary>A card's reason as the list carries it: its sentence, where it is acted on, and the
    /// card it waits for when that is the link.</summary>
    private sealed record Shown(string? Reason, string? Link, int? LinkCard);

    private static (Guid, int) Key(JsonNode? card) => ((Guid)card!["project_id"]!, (int)card["number"]!);

    private static async Task<JsonObject> List(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get(Path, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    /// <summary>Each card next, by its project and number, as the list shows its reason.</summary>
    private static async Task<Dictionary<(Guid, int), Shown>> Cards(Jobs jobs) =>
        (await List(jobs))["next"]!.AsArray().ToDictionary(Key, card => new Shown((string?)card!["reason"], (string?)card["link"], (int?)card["link_card"]));

    /// <summary>fodr, a second project of the user's in the same installation, holding
    /// <paramref name="cards"/> and project.json, read once so its cards are picked; no worker has
    /// said hello in it.</summary>
    private static async Task<Guid> SecondProject(Jobs jobs, IReadOnlyList<FakeIssue> cards)
    {
        var world = jobs.Fake.World(Jobs.User);
        var installation = world.Installations[0];
        var fodr = new FakeRepository(Fodr, "Adamniels", "fodr", [], [], [],
            [new FakeFile(".devflow/project.json", "main", Encoding.UTF8.GetBytes(Jobs.ProjectFile()))],
            cards);
        jobs.Fake.Replace(Jobs.User, world with { Installations = [installation with { Repositories = [.. installation.Repositories, fodr] }] });
        var project = await jobs.Routes.Project(Jobs.User, Fodr, "fodr");
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(project, Jobs.User));
        return project;
    }
}
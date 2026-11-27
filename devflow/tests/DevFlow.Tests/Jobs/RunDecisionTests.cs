using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.Jobs;
using DevFlow.Stops;
using DevFlow.Watching;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// One rule decides what runs: <see cref="RunDecision"/> answers, for a project and its cards, run
/// or why not yet, with the project's gates in the order the dispatcher meets them, each card's own
/// reason read from the predicates handing out reads, and the dispatcher doing exactly what it
/// answers. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class RunDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => Jobs.Ct;

    /// <summary>A project with nothing in its way and a card to build.</summary>
    private static readonly ProjectSituation Free = new(
        "Personal-platform",
        NeedsRebuild: false,
        ProjectHold.None,
        Wait: null,
        SetupGate.Checked,
        SetupKind: null,
        ClaudeLoginState.Ok,
        WorkerSeen: true,
        NextKind: DevFlow.Protocol.Job.Build,
        Version: "1",
        Now);

    private static readonly ProjectWait Failing = new(2, "The network was away", Now.AddMinutes(-10), Now.AddMinutes(10), UntilReset: false);

    private static readonly ProjectWait AtLimit = new(1, "Claude's usage limit was reached", Now.AddMinutes(-1), Now.AddHours(3), UntilReset: true);

    /// <summary>Each project gate, in the order of the slice's Settled, with the reason it gives
    /// alone.</summary>
    private static readonly (string Gate, Func<ProjectSituation, ProjectSituation> Set, WaitReason Reason)[] Gates =
    [
        ("rebuild", s => s with { NeedsRebuild = true }, new WaitReason.NeedsRebuild()),
        ("stopped", s => s with { Hold = ProjectHold.Stopped }, new WaitReason.ProjectStopped()),
        ("unwatched", s => s with { Hold = ProjectHold.Unwatched }, new WaitReason.Unwatched()),
        ("cannot run", s => s with { Wait = Failing }, new WaitReason.CannotRun(Failing.WaitUntil, Failing.Reason)),
        ("setup", s => s with { Setup = SetupGate.CheckRunning }, new WaitReason.NeedsSetup(Checking: true)),
        ("login", s => s with { Login = ClaudeLoginState.None }, new WaitReason.NoClaudeLogin(Expired: false)),
        ("usage limit", s => s with { Wait = AtLimit }, new WaitReason.UsageLimit(AtLimit.WaitUntil)),
        ("no worker", s => s with { WorkerSeen = false }, new WaitReason.NoWorker("Personal-platform")),
    ];

    [Fact]
    public void EachProjectReasonInItsOrder()
    {
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), RunDecision.Of(Free));

        // Each gate alone gives its own reason.
        foreach (var (_, set, reason) in Gates)
        {
            Assert.Equal(new Decision.Wait(reason), RunDecision.Of(set(Free)));
        }

        // With a gate and every later one set, the first in the order decides. They are set from the
        // last, so a gate that shares a field with a later one, a stop and the default branch not
        // read, or a wait after failures and one at the usage limit, is the one left.
        for (var first = 0; first < Gates.Length; first++)
        {
            var situation = Free;
            for (var gate = Gates.Length - 1; gate >= first; gate--)
            {
                situation = Gates[gate].Set(situation);
            }

            Assert.Equal(new Decision.Wait(Gates[first].Reason), RunDecision.Of(situation));
        }

        // The other ways each gate reads: a login past its renewal, a check that failed, a worker
        // built from no recipe with no setup job to take, and a wait already over.
        Assert.Equal(new Decision.Wait(new WaitReason.NoClaudeLogin(Expired: true)), RunDecision.Of(Free with { Login = ClaudeLoginState.Expired }));
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), RunDecision.Of(Free with { Login = ClaudeLoginState.DueSoon }));
        Assert.Equal(new Decision.Wait(new WaitReason.NeedsSetup(Checking: false)), RunDecision.Of(Free with { Setup = SetupGate.CheckFailed }));
        Assert.Equal(new Decision.Wait(new WaitReason.NeedsSetup(Checking: false)), RunDecision.Of(Free with { Setup = SetupGate.NotSetUp }));
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), RunDecision.Of(Free with { Wait = Failing with { WaitUntil = Now } }));

        // What runs when nothing waits: a check due, which needs no Claude login and is held by no
        // usage limit, since it runs no agent; a setup's job for a worker built from no recipe; and
        // nothing when there is nothing to do.
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.RecipeCheck), RunDecision.Of(Free with { Setup = SetupGate.CheckDue, Login = ClaudeLoginState.None, Wait = AtLimit }));
        Assert.Equal(new Decision.Wait(new WaitReason.CannotRun(Failing.WaitUntil, Failing.Reason)), RunDecision.Of(Free with { Setup = SetupGate.CheckDue, Wait = Failing }));
        // A check runs on a worker too: with none seen lately, as the page reads a project, it waits
        // for one.
        Assert.Equal(new Decision.Wait(new WaitReason.NoWorker("Personal-platform")), RunDecision.Of(Free with { Setup = SetupGate.CheckDue, WorkerSeen = false }));
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Setup), RunDecision.Of(Free with { Setup = SetupGate.NotSetUp, SetupKind = DevFlow.Setup.SetupRunStore.SetupKind }));
        Assert.Equal(new Decision.Wait(new WaitReason.NoClaudeLogin(Expired: false)), RunDecision.Of(Free with { Setup = SetupGate.NotSetUp, SetupKind = DevFlow.Setup.SetupRunStore.SetupKind, Login = ClaudeLoginState.None }));
        Assert.Equal(new Decision.Idle(), RunDecision.Of(Free with { NextKind = null }));
    }

    [Fact]
    public async Task EachCardReason()
    {
        // Card 1 is blocked on GitHub by card 2; feature 10's second card follows one that needs
        // answers; cards 2 and 3 are free.
        await using var jobs = await Jobs.Start([
            Jobs.Card(1) with { BlockedBy = [2] },
            Jobs.Card(2),
            Jobs.Card(3),
            FeatureJobs.Parent(10, "A feature", [11, 12]),
            FeatureJobs.Sub(11),
            FeatureJobs.Sub(12),
        ]);
        await FeatureJobs.SetState(jobs, 11, "needs_answers");

        var cards = await CardDecisions(jobs);
        Assert.Equal(new Decision.Wait(new WaitReason.WaitsForCard("#2")), cards[1]);
        Assert.Equal(new Decision.Wait(new WaitReason.WaitsForAnswers(11)), cards[12]);
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), cards[2]);
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), cards[3]);
        // A card that needs answers is not waiting to run, so it has no decision.
        Assert.False(cards.ContainsKey(11));

        // The one worker takes card 2: card 3 could run, but waits for it.
        Assert.Equal(2, (int)(await jobs.Job())["card"]!["number"]!);
        cards = await CardDecisions(jobs);
        Assert.Equal(new Decision.Wait(new WaitReason.WorkersBusy("fodr-worker")), cards[3]);
        Assert.False(cards.ContainsKey(2));

        // A second worker that asked lately frees it again.
        await jobs.SayHello(Jobs.SecondWorker);
        Assert.Equal(new Decision.Run(DevFlow.Protocol.Job.Build), (await CardDecisions(jobs))[3]);

        // No worker of the project seen in 2 minutes: the project waits for one, and so does every
        // card.
        jobs.Routes.Time.Advance(RunStore.Silence + TimeSpan.FromSeconds(1));
        var situation = await Situation(jobs, Jobs.FirstWorker);
        var project = RunDecision.Of(situation);
        Assert.Equal(new Decision.Wait(new WaitReason.NoWorker("Personal-platform")), project);
        Assert.Equal(project, (await CardDecisions(jobs))[3]);
    }

    /// <summary>The worlds the dispatcher is held to the rule in.</summary>
    public static TheoryData<string> Worlds() =>
    [
        "free", "stopped", "failing", "usage limit", "no login", "unwatched", "check due", "check at usage limit", "check failed", "rebuild", "needs answers", "blocked",
    ];

    [Theory]
    [MemberData(nameof(Worlds))]
    public async Task TheDispatcherDoesWhatTheRuleSays(string world)
    {
        var worker = Jobs.FirstWorker;
        await using var jobs = world switch
        {
            "no login" => await Jobs.Start([Jobs.Card(1)], login: false),
            "check due" or "check at usage limit" or "check failed" => await Jobs.Start([Jobs.Card(1)], checkedRecipe: false),
            "needs answers" => await Jobs.Start([
                FeatureJobs.Parent(10, "A feature", [11, 12]), FeatureJobs.Sub(11), FeatureJobs.Sub(12), Jobs.Card(20)]),
            "blocked" => await Jobs.Start([Jobs.Card(1) with { BlockedBy = [2] }, Jobs.Card(2)]),
            // A low card written first and one with no priority: the second goes first.
            _ => await Jobs.Start([Jobs.Card(1, labels: ["devflow", "priority: low"]), Jobs.Card(2)]),
        };
        var now = jobs.Routes.Time.GetUtcNow();
        switch (world)
        {
            case "stopped":
                await Merging.Merged.Stop(jobs);
                break;
            case "failing" or "usage limit" or "check at usage limit":
                await Sql(jobs, "INSERT INTO project_waits (project_id, user_id, failures, reason, wait_until, since, until_reset) VALUES (@project, @user, 1, 'It failed outside', @until, @now, @reset)",
                    new { project = jobs.Project, user = Jobs.User, until = now.AddMinutes(30), now, reset = world != "failing" });
                break;
            case "unwatched":
                jobs.Routes.Time.Advance(BranchWatch.UnwatchedAfter + TimeSpan.FromSeconds(1));
                break;
            case "check failed":
                await Sql(jobs, "INSERT INTO recipe_checks (run_id, project_id, user_id, version, commit, passed, exit_code, output, checked_at) VALUES (gen_random_uuid(), @project, @user, '1', @commit, false, 1, 'It failed', @now)",
                    new { project = jobs.Project, user = Jobs.User, commit = new string('0', 40), now });
                break;
            case "rebuild":
                await jobs.SayHello(Jobs.SecondWorker, "platform-hosted/devflow-worker:2026.01.01");
                worker = Jobs.SecondWorker;
                break;
            case "needs answers":
                await FeatureJobs.SetState(jobs, 11, "needs_answers");
                break;
        }

        // The worker asking is seen first, as the dispatcher marks it, and then the rule is asked.
        await Sql(jobs, "UPDATE workers SET last_seen_at = @now WHERE id = @worker", new { now = jobs.Routes.Time.GetUtcNow(), worker });
        var rule = RunDecision.Of(await Situation(jobs, worker));
        var cards = await RunDecision.Cards(jobs.Routes.Door, Jobs.User, jobs.Project, rule, jobs.Routes.Time.GetUtcNow(), Ct);
        var firstToRun = cards.FirstOrDefault(card => card.Decision is Decision.Run);

        var response = await jobs.Next(worker);

        if (rule is Decision.Run { Kind: var kind })
        {
            Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
            var job = await Workers.Workers.Json(response);
            Assert.Equal(kind, (string)job["kind"]!);
            if (kind is DevFlow.Protocol.Job.Build)
            {
                Assert.NotNull(firstToRun);
                Assert.Equal(firstToRun.Number, (int)job["card"]!["number"]!);
            }
        }
        else
        {
            Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
            Assert.DoesNotContain(cards, card => card.Decision is Decision.Run);
        }

        // Each world answers what it was staged for.
        var expected = world switch
        {
            "free" or "needs answers" or "blocked" => (Decision)new Decision.Run(DevFlow.Protocol.Job.Build),
            "stopped" => new Decision.Wait(new WaitReason.ProjectStopped()),
            "failing" => new Decision.Wait(new WaitReason.CannotRun(now.AddMinutes(30), "It failed outside")),
            "usage limit" => new Decision.Wait(new WaitReason.UsageLimit(now.AddMinutes(30))),
            "no login" => new Decision.Wait(new WaitReason.NoClaudeLogin(Expired: false)),
            "unwatched" => new Decision.Wait(new WaitReason.Unwatched()),
            // A check runs no agent, so Claude's usage limit does not hold it.
            "check due" or "check at usage limit" => new Decision.Run(DevFlow.Protocol.Job.RecipeCheck),
            "check failed" => new Decision.Wait(new WaitReason.NeedsSetup(Checking: false)),
            "rebuild" => new Decision.Wait(new WaitReason.NeedsRebuild()),
            _ => throw new ArgumentOutOfRangeException(nameof(world), world, "not a world"),
        };
        Assert.Equal(expected, rule);
        var taken = world switch { "free" => 2, "needs answers" => 20, "blocked" => 2, _ => 0 };
        if (taken != 0)
        {
            Assert.Equal(taken, firstToRun?.Number);
        }

        if (world == "needs answers")
        {
            Assert.Equal(new Decision.Wait(new WaitReason.WaitsForAnswers(11)), cards.Single(card => card.Number == 12).Decision);
        }

        if (world == "blocked")
        {
            Assert.Equal(new Decision.Wait(new WaitReason.WaitsForCard("#2")), cards.Single(card => card.Number == 1).Decision);
        }
    }

    [Fact]
    public void EveryReasonHasItsSentence()
    {
        var reset = new DateTimeOffset(2026, 11, 17, 13, 0, 0, TimeSpan.Zero);
        var reasons = new (WaitReason Reason, string Sentence, ActedOn Where)[]
        {
            (new WaitReason.NeedsRebuild(), "Its worker needs building again", ActedOn.Nowhere),
            (new WaitReason.ProjectStopped(), "The project is stopped: resume it", ActedOn.ProjectPage),
            (new WaitReason.Unwatched(), "Its default branch has not been read for 5 minutes", ActedOn.Nowhere),
            (new WaitReason.CannotRun(reset, "The network was away"), "Dev flow cannot run its cards: The network was away", ActedOn.ProjectPage),
            (new WaitReason.NeedsSetup(Checking: true), "Its recipe is being checked", ActedOn.Nowhere),
            (new WaitReason.NeedsSetup(Checking: false), "It needs setting up", ActedOn.Nowhere),
            (new WaitReason.NoClaudeLogin(Expired: false), "No Claude login", ActedOn.Settings),
            (new WaitReason.NoClaudeLogin(Expired: true), "The Claude login has expired", ActedOn.Settings),
            (new WaitReason.UsageLimit(reset), "At Claude's usage limit until 2026-11-17 13:00 UTC", ActedOn.Nowhere),
            (new WaitReason.NoWorker("fodr"), "No worker is running for fodr", ActedOn.ProjectPage),
            (new WaitReason.WaitsForCard("#31"), "Waits for #31, which it depends on", ActedOn.Nowhere),
            (new WaitReason.WaitsForCard("Adamniels/elsewhere#5"), "Waits for Adamniels/elsewhere#5, which it depends on", ActedOn.Nowhere),
            (new WaitReason.WaitsForAnswers(31), "Waits for your answers on #31", ActedOn.WaitingForYou),
            (new WaitReason.WorkersBusy("fodr-worker"), "Next for fodr-worker, after its current run", ActedOn.Nowhere),
        };

        foreach (var (reason, sentence, where) in reasons)
        {
            Assert.Equal(sentence, reason.Sentence);
            Assert.Equal(where, reason.Where);
        }

        // Every kind of reason there is has its sentence here, so one added is added here too.
        var kinds = typeof(WaitReason).GetNestedTypes().Where(type => type.IsSubclassOf(typeof(WaitReason))).Select(type => type.Name).Order(StringComparer.Ordinal);
        Assert.Equal(kinds, reasons.Select(each => each.Reason.GetType().Name).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>The project's situation for <paramref name="worker"/>, as the dispatcher reads it.</summary>
    private static async Task<ProjectSituation> Situation(Jobs jobs, Guid worker)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.User, jobs.Project, worker, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return situation;
    }

    /// <summary>Each card's decision, by number, for the project as its first worker finds it.</summary>
    private static async Task<Dictionary<int, Decision>> CardDecisions(Jobs jobs)
    {
        var project = RunDecision.Of(await Situation(jobs, Jobs.FirstWorker));
        var cards = await RunDecision.Cards(jobs.Routes.Door, Jobs.User, jobs.Project, project, jobs.Routes.Time.GetUtcNow(), Ct);
        return cards.ToDictionary(card => card.Number, card => card.Decision);
    }

    private static async Task Sql(Jobs jobs, string sql, object parameters)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: Ct));
    }
}
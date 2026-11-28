using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Stops;
using DevFlow.Tests.Jobs;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Stops;

/// <summary>
/// Claude's usage limit is the user's, since the subscription is: a run failing outside its card
/// with a reset records the user's subscription limited until then, and until it every project of
/// theirs on the subscription hands out no job that runs a model, with the usage limit's reason; a
/// check, which runs none, still goes. Against real Postgres and the fake code host. See "Stops" in
/// devflow/README.md, and why the limit is the user's, "Stops" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class SubscriptionLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALimitIsTheUsers()
    {
        await using var jobs = await Jobs.Jobs.Start();
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");
        var now = jobs.Routes.Time.GetUtcNow();
        var reset = now + TimeSpan.FromMinutes(20);

        // The build fails outside its card at the usage limit: the user's subscription is limited
        // until its reset, recorded now, and Bob's is not.
        await Limit.Fail(jobs, await jobs.Job(), reset);
        Assert.Equal((reset, now), await Limit.Of(jobs, Jobs.Jobs.User));
        Assert.Null(await Limit.Of(jobs, bob));

        // The project whose run found it counts the failure as before, until the reset.
        var wait = await Stop.Wait(jobs);
        Assert.NotNull(wait);
        Assert.Equal((1, true, reset), (wait.Failures, wait.UntilReset, wait.WaitUntil));

        // Bob's project is not held by it.
        var bobsSituation = await RunDecision.ReadForPage(jobs.Routes.Door, bob, bobs, Jobs.Jobs.Image, now, Ct);
        Assert.NotNull(bobsSituation);
        Assert.IsNotType<WaitReason.UsageLimit>((RunDecision.Of(bobsSituation) as Decision.Wait)?.Reason);

        // Of two resets the later is kept, whichever is recorded last.
        var later = reset + TimeSpan.FromMinutes(5);
        await Record(jobs, later, now);
        await Record(jobs, reset, now);
        Assert.Equal(later, (await Limit.Of(jobs, Jobs.Jobs.User))!.Value.ResetsAt);
    }

    private static Task<bool> Record(Jobs.Jobs jobs, DateTimeOffset resetsAt, DateTimeOffset now) =>
        jobs.Routes.Door.InUserTransaction(Jobs.Jobs.User, async transaction =>
        {
            await SubscriptionLimits.Record(transaction, Jobs.Jobs.User, resetsAt, now, Ct);
            return true;
        }, Ct);

    [Fact]
    public async Task AResetNotBelievedOrNoneRecordsNothing()
    {
        // A reset more than 8 days off is a misreading: the doubling holds, and the user's
        // subscription is not limited.
        await using var far = await Jobs.Jobs.Start();
        await Limit.Fail(far, await far.Job(), far.Routes.Time.GetUtcNow() + TimeSpan.FromDays(9));
        Assert.Null(await Limit.Of(far, Jobs.Jobs.User));
        Assert.False((await Stop.Wait(far))!.UntilReset);

        // A failure outside the card that names no reset is the doubling, as before.
        await using var none = await Jobs.Jobs.Start();
        await Limit.Fail(none, await none.Job(), null);
        Assert.Null(await Limit.Of(none, Jobs.Jobs.User));
        Assert.Equal(none.Routes.Time.GetUtcNow() + ProjectWaits.First, (await Stop.Wait(none))!.WaitUntil);
    }

    [Fact]
    public async Task WaitingProjectsAllWait()
    {
        await using var jobs = await Jobs.Jobs.Start();
        var (fodr, fodrKey) = await PauseTests.SecondProject(jobs);
        // Read once its worker said hello, so its recipe is checked and its next job a build.
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(fodr, Jobs.Jobs.User));
        Assert.Equal(new Decision.Run(Job.Build), await PauseTests.Rule(jobs, fodr, PauseTests.FodrWorker));
        var now = jobs.Routes.Time.GetUtcNow();
        var reset = now + TimeSpan.FromMinutes(20);

        // The first project's build finds the limit.
        await Limit.Fail(jobs, await jobs.Job(), reset);

        // Neither project hands out a job that runs a model, and each answers the usage limit with
        // its reset, in the user's zone: fodr, whose run did not find it, as well.
        var limited = new Decision.Wait(new WaitReason.UsageLimit(Limit.InZone(reset)));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(PauseTests.FodrWorker, fodrKey)).Response.StatusCode);
        Assert.Equal(limited, await PauseTests.Rule(jobs));
        Assert.Equal(limited, await PauseTests.Rule(jobs, fodr, PauseTests.FodrWorker));
        var said = Assert.IsType<WaitReason.UsageLimit>(((Decision.Wait)await PauseTests.Rule(jobs, fodr, PauseTests.FodrWorker)).Reason);
        Assert.Equal(Limit.Stockholm.GetUtcOffset(reset), said.Until.Offset);
        Assert.Equal($"At the usage limit, until {Limit.InZone(reset):ddd HH:mm}", said.Sentence);
        Assert.Equal(ActedOn.RunsOn, said.Where);

        // fodr counts no failure of its own: it waits for the user's limit alone.
        Assert.Null(await ProjectWaits.Of(jobs.Routes.Door, Jobs.Jobs.User, fodr, Ct));

        // A minute before the reset both still wait; at the reset both run again.
        await Limit.MoveTo(jobs, reset - TimeSpan.FromMinutes(1), (fodr, PauseTests.FodrWorker, fodrKey));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(PauseTests.FodrWorker, fodrKey)).Response.StatusCode);
        await Limit.MoveTo(jobs, reset, (fodr, PauseTests.FodrWorker, fodrKey));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Next(PauseTests.FodrWorker, fodrKey)).Response.StatusCode);
    }

    [Fact]
    public async Task ACheckStillRuns()
    {
        // The user's subscription limited, a check of the worker's setup, which runs no model, is
        // handed out all the same.
        await using var checking = await Jobs.Jobs.Start(checkedRecipe: false);
        await Limit.Set(checking, checking.Routes.Time.GetUtcNow() + TimeSpan.FromMinutes(20));
        Assert.Equal(new Decision.Run(Job.RecipeCheck), await PauseTests.Rule(checking));
        Assert.Equal(Job.RecipeCheck, (string)(await checking.Job())["kind"]!);
    }
}
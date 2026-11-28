using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Stops;
using DevFlow.Tests.Jobs;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Stops;

/// <summary>
/// A project on <c>subscription_then_api_key</c> continues on the API key at the subscription's
/// usage limit: its jobs carry the subscription, and while the user's subscription is limited the
/// key; the usage limit's failure puts its card back and starts no project wait, so its next job
/// goes at once, on the key; and the first job handed out after the reset carries the subscription
/// again. Against real Postgres and the fake code host. See "The API key" and "Stops" in
/// devflow/README.md.
/// </summary>
public sealed class ContinueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItContinuesOnTheKey()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2)]);
        await jobs.RunOnTheKey(runsOn: "subscription_then_api_key");
        var reset = jobs.Routes.Time.GetUtcNow() + TimeSpan.FromMinutes(20);

        // Its first job carries the subscription, the Claude login's token.
        var first = await jobs.Job();
        Assert.Equal((JobCredential.Subscription, Jobs.Jobs.ClaudeToken), Credential(first));
        var card = (int)first["card"]!["number"]!;

        // It finds the usage limit: the user's subscription is limited, the card is put back, and
        // the project counts no wait of its own.
        await Limit.Fail(jobs, first, reset);
        Assert.Equal(reset, (await Limit.Of(jobs, Jobs.Jobs.User))!.Value.ResetsAt);
        Assert.Equal("queued", (await jobs.Card(card)).State);
        Assert.Null(await Stop.Wait(jobs));

        // So its next job goes at once, carrying the API key, and its run records the key.
        Assert.Equal(new Decision.Run(Job.Build), await PauseTests.Rule(jobs));
        var next = await jobs.Job();
        Assert.Equal((JobCredential.ApiKey, Jobs.Jobs.ApiKey), Credential(next));
        Assert.Equal(JobCredential.ApiKey, (await jobs.Runs()).Single(run => run.Id == (Guid)next["job_id"]!).Credential);

        // A project on the subscription alone waits for the same limit.
        await using var waiting = await Jobs.Jobs.Start();
        await Limit.Set(waiting, reset);
        Assert.Equal(StatusCodes.Status204NoContent, (await waiting.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task ItGoesBackAfterTheReset()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2), Jobs.Jobs.Card(3)]);
        await jobs.RunOnTheKey(runsOn: "subscription_then_api_key");
        var reset = jobs.Routes.Time.GetUtcNow() + TimeSpan.FromMinutes(20);
        await Limit.Fail(jobs, await jobs.Job(), reset);

        // On the key, a run that succeeds does not switch it back: nothing but the reset does.
        var onTheKey = await jobs.Job();
        Assert.Equal(JobCredential.ApiKey, Credential(onTheKey).Kind);
        var finished = await jobs.Post(WorkerApi.ResultPath((Guid)onTheKey["job_id"]!), Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)onTheKey["branch"]!));
        Assert.Equal(StatusCodes.Status204NoContent, finished.Response.StatusCode);
        await Limit.MoveTo(jobs, reset - TimeSpan.FromMinutes(1));
        var stillOnTheKey = await jobs.Job();
        Assert.Equal(JobCredential.ApiKey, Credential(stillOnTheKey).Kind);
        finished = await jobs.Post(WorkerApi.ResultPath((Guid)stillOnTheKey["job_id"]!), Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)stillOnTheKey["branch"]!));
        Assert.Equal(StatusCodes.Status204NoContent, finished.Response.StatusCode);

        // The first job handed out after the reset carries the subscription again.
        await Limit.MoveTo(jobs, reset);
        Assert.Equal((JobCredential.Subscription, Jobs.Jobs.ClaudeToken), Credential(await jobs.Job()));
    }

    [Fact]
    public async Task OnTheKeyItNeedsNoClaudeLogin()
    {
        // Without a Claude login, a project continuing on the key waits for one while its
        // subscription is not limited, and runs on the key while it is.
        await using var jobs = await Jobs.Jobs.Start(login: false);
        await jobs.RunOnTheKey(runsOn: "subscription_then_api_key");
        Assert.Equal(new Decision.Wait(new WaitReason.NoClaudeLogin(Expired: false)), await PauseTests.Rule(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        await Limit.Set(jobs, jobs.Routes.Time.GetUtcNow() + TimeSpan.FromMinutes(20));
        Assert.Equal(new Decision.Run(Job.Build), await PauseTests.Rule(jobs));
        Assert.Equal(JobCredential.ApiKey, Credential(await jobs.Job()).Kind);
    }

    [Fact]
    public async Task TheProjectsPageSaysIt()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await jobs.RunOnTheKey(runsOn: "subscription_then_api_key");

        // On the subscription, the workers list says nothing of the key.
        Assert.Null((await WorkerList(jobs))["on_the_key"]);

        // Limited, it runs on the key until the reset, with what the key cost today of its cap.
        var reset = jobs.Routes.Time.GetUtcNow() + TimeSpan.FromMinutes(20);
        await Limit.Set(jobs, reset);
        await Usage.ApiCapTests.Spent(jobs, jobs.Routes.Time.GetUtcNow(), JobCredential.ApiKey, 3.5m);
        var key = (await WorkerList(jobs))["on_the_key"]!.AsObject();
        Assert.Equal(reset, (DateTimeOffset)key["until"]!);
        Assert.Equal($"Running on the API key until {Limit.InZone(reset):ddd HH:mm}", (string)key["sentence"]!);
        Assert.Equal("3.50 of 20 USD today", (string)key["spend"]!);

        // On the key alone it says so, with no time.
        await jobs.RunOnTheKey(runsOn: "api_key");
        key = (await WorkerList(jobs))["on_the_key"]!.AsObject();
        Assert.Null(key["until"]);
        Assert.Equal("Running on the API key", (string)key["sentence"]!);
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> WorkerList(Jobs.Jobs jobs)
    {
        var answer = await jobs.Routes.App.Get(Workers.Workers.ListPath(jobs.Project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return System.Text.Json.Nodes.JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    private static (string Kind, string Secret) Credential(System.Text.Json.Nodes.JsonObject job) =>
        ((string)job["credential"]!["kind"]!, (string)job["credential"]!["secret"]!);
}
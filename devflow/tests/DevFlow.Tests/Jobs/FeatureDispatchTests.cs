using DevFlow.Cards;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A feature's cards are handed out one at a time, in their order: the first by position that is
/// queued and free to start, only while no card of the feature has a run under way and every card
/// before it is done, stopped or held; and different features side by side. Against the fake and a
/// real Postgres. See "Jobs" in devflow/README.md and "A feature's cards, one at a time" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class FeatureDispatchTests
{
    private static CancellationToken Ct => Jobs.Ct;

    [Fact]
    public async Task OneCardAtATimeInOrder()
    {
        // The sub-issue order is neither the numbers' nor the cards' age: 13 first, then 11, then 12.
        await using var jobs = await Jobs.Start([
            FeatureJobs.Parent(10, "Dev flow: features", [13, 11, 12]),
            FeatureJobs.Sub(11),
            FeatureJobs.Sub(12),
            FeatureJobs.Sub(13),
        ]);
        await jobs.SayHello(Jobs.SecondWorker);

        // Two workers ask: one job, for the card at position 1, and nothing for the other.
        Assert.Equal(13, (int)(await jobs.Job())["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Single(await jobs.Runs());

        // Its run over and its pull request under review, it still holds the next one back.
        await FeatureJobs.Finish(jobs, 13, "reviewing");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);

        // Done, the card at position 2 is handed out, and only it.
        await FeatureJobs.SetState(jobs, 13, "done");
        Assert.Equal(11, (int)(await jobs.Job(Jobs.SecondWorker))["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(12)).State);
    }

    [Fact]
    public async Task WhatAnEarlierCardHoldsBack()
    {
        // Card 33, the third, is blocked on GitHub by card 31, the first.
        await using var jobs = await Jobs.Start([
            FeatureJobs.Parent(30, "Dev flow: features", [31, 32, 33]),
            FeatureJobs.Sub(31),
            FeatureJobs.Sub(32),
            FeatureJobs.Sub(33, blockedBy: [31]),
        ]);

        // The first waiting for the owner holds the second back, and the page says why.
        await FeatureJobs.SetState(jobs, 31, "waiting_for_owner");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        var listed = await Listed(jobs);
        Assert.Equal("queued", listed[32].State);
        Assert.Equal("Waits for #31, earlier in the feature", listed[32].Reason);
        // The third already waits for the first on GitHub, which it keeps saying.
        Assert.Equal("Waits for #31", listed[33].Reason);

        // Held, the first holds back only itself: the second is handed out.
        await FeatureJobs.SetState(jobs, 31, "held");
        Assert.Equal(32, (int)(await jobs.Job())["card"]!["number"]!);

        // Stopped, the same: the second, back in the queue, is handed out again.
        await FeatureJobs.Finish(jobs, 32, "queued");
        await FeatureJobs.SetState(jobs, 31, "stopped");
        Assert.Equal(32, (int)(await jobs.Job())["card"]!["number"]!);

        // The second done, the third is still blocked by the first on GitHub, which is open.
        await FeatureJobs.Finish(jobs, 32, "done");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(33)).State);
        Assert.Equal("Waits for #31", (await Listed(jobs))[33].Reason);
    }

    [Fact]
    public async Task FeaturesRunSideBySide()
    {
        await using var jobs = await Jobs.Start([
            FeatureJobs.Parent(40, "The first feature", [41, 42]),
            FeatureJobs.Sub(41),
            FeatureJobs.Sub(42),
            FeatureJobs.Parent(50, "The second feature", [51, 52]),
            FeatureJobs.Sub(51),
            FeatureJobs.Sub(52),
        ]);
        await jobs.SayHello(Jobs.SecondWorker);

        // A job for each feature, its first card, and then nothing.
        var first = (int)(await jobs.Job())["card"]!["number"]!;
        var second = (int)(await jobs.Job(Jobs.SecondWorker))["card"]!["number"]!;

        Assert.Equal([41, 51], new[] { first, second }.Order());
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    /// <summary>The project's cards as its page lists them, features' and their own, by number.</summary>
    private static async Task<Dictionary<int, CardView>> Listed(Jobs jobs)
    {
        var list = await CardStore.List(jobs.Routes.Door, Jobs.User, jobs.Project, Ct);
        Assert.NotNull(list);
        return list.Features.SelectMany(feature => feature.Cards).Concat(list.Cards).ToDictionary(card => card.Number);
    }
}
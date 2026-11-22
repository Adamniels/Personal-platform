using DevFlow.Protocol;
using DevFlow.Tests.Jobs;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Inside a feature one card runs at a time, and a card before it holds it back unless it is done,
/// stopped, refused or held: a refused card holds back only itself and the cards blocked by it on
/// GitHub, as a stopped card does, so the rest of the feature goes on. Against the fake code host and
/// a real Postgres, with the fake worker. See "A feature's cards, one at a time" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class HeldBackTests
{
    private const int Parent = Features.FeaturePage.Parent;
    private const int First = Features.FeaturePage.First;
    private const int Second = Features.FeaturePage.Second;
    private const int Third = Features.FeaturePage.Third;

    [Fact]
    public async Task ARefusedCardHoldsBackOnlyItsDependents()
    {
        // Three cards, the third blocked by the first on GitHub.
        await using var jobs = await Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second, Third]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
            FeatureJobs.Sub(Third, "The feature's page", blockedBy: [First]),
        ], read: false);
        await FeatureJobs.FirstCommit(jobs);

        // The first read as too thin, the others ready.
        await Read(jobs, Prepared(ReadJobTests.Reading(Thin(First, "Where the sub-issues come from"), Ready(Second), Ready(Third))));
        Assert.Equal("refused", (await jobs.Card(First)).State);

        // The refused card does not hold back the card after it: the second is built.
        var build = await jobs.Job();
        Assert.Equal((Job.Build, Second), ((string)build["kind"]!, (int)build["card"]!["number"]!));

        // The card blocked by it waits for it, and nothing more is handed out while the second
        // is done; read as the project's minute would.
        await FeatureJobs.Finish(jobs, Second, "done");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var third = await jobs.Card(Third);
        Assert.Equal(("queued", $"Waits for #{First}"), (third.State, third.Reason));
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Build && run.CardNumber == Third);
    }
}
using DevFlow.Protocol;
using DevFlow.Tests.Jobs;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Inside a feature one card runs at a time, and a card before it holds it back unless it is done,
/// stopped, refused or held: a refused card holds back only itself and the cards blocked by it on
/// GitHub, as a stopped card does, so the rest of the feature goes on; a card that needs answers
/// holds back every later card, as a queued card does. Against the fake code host and
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

    [Fact]
    public async Task ACardNeedingAnswersHoldsBackLaterCards()
    {
        // Three cards, none blocked by another on GitHub.
        await using var jobs = await Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second, Third]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
            FeatureJobs.Sub(Third, "The feature's page"),
        ], read: false);
        await FeatureJobs.FirstCommit(jobs);

        // The second read with a question, with questions on, the others ready.
        await Read(jobs, Prepared(ReadJobTests.Reading(Ready(First), Asks(Second, ("Is the list sorted by date?", "Yes")), Ready(Third))));
        Assert.Equal("needs_answers", (await jobs.Card(Second)).State);

        // The earlier card, ready, is built meanwhile.
        var build = await jobs.Job();
        Assert.Equal((Job.Build, First), ((string)build["kind"]!, (int)build["card"]!["number"]!));

        // Once it is done, the card after the one needing answers waits for it, as it would for a
        // queued card, and nothing more is handed out.
        await FeatureJobs.Finish(jobs, First, "done");
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        var third = await jobs.Card(Third);
        Assert.Equal(("queued", $"Waits for #{Second}, earlier in the feature"), (third.State, await ShownReason(jobs, Third)));
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.DoesNotContain(await jobs.Runs(), run => run.Kind == Job.Build && run.CardNumber == Third);
    }

    /// <summary>A card's reason as the cards list shows it, past row level security.</summary>
    private static async Task<string?> ShownReason(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Jobs.Jobs.Ct);
        return await Dapper.SqlMapper.ExecuteScalarAsync<string?>(admin, new Dapper.CommandDefinition(
            $"SELECT {DevFlow.Cards.CardStore.ShownReason} FROM cards WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number },
            cancellationToken: Jobs.Jobs.Ct));
    }
}
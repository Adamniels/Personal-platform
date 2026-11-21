using System.Text.Json.Nodes;

using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Every job carries the model its kind reads from the project's settings, building for a build, a
/// fix, a conflict and a feature's fix, reviewing for a card's review and a feature's, and the
/// project's minutes and turns, with the 25 USD every run is given until part 8's budget. Against
/// real Postgres and the fake code host, each kind brought about with the fake worker and handed
/// out through the worker's own route. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class JobModelTests
{
    /// <summary>The project's building model sonnet, its reviewing model haiku, its reading model
    /// left at opus, 45 minutes and 300 turns, so each value read is told apart from the
    /// others and from the defaults.</summary>
    private static Task Configure(Jobs jobs) => Merged.Sql(
        jobs,
        """
        UPDATE project_settings
        SET model_building = 'sonnet', model_reviewing = 'haiku', run_minutes = 45, run_turns = 300
        WHERE project_id = @project
        """);

    /// <summary>The job handed out next, after checking it is of <paramref name="kind"/>.</summary>
    private static async Task<JsonObject> Next(Jobs jobs, string kind)
    {
        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(kind, (string)job["kind"]!);
        return job;
    }

    private static void Carries(JsonObject job, string model)
    {
        Assert.Equal(model, (string)job["model"]!);
        Assert.Equal((45, 300, 25m), ((int)job["limits"]!["minutes"]!, (int)job["limits"]!["turns"]!, (decimal)job["limits"]!["budget_usd"]!));
    }

    [Fact]
    public async Task EachKindCarriesItsModel()
    {
        // A build.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await Configure(jobs);
            Carries(await Next(jobs, "build"), "sonnet");
        }

        // A card's review.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await Configure(jobs);
            await Built.Run(jobs, Built.Prepared(Built.Report()));
            Carries(await Next(jobs, "review"), "haiku");
        }

        // A card's fix.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await Configure(jobs);
            await Fixed.BuiltAndReviewed(jobs);
            Carries(await Next(jobs, "fix"), "sonnet");
        }

        // A conflict between the default branch and a feature's branch.
        await using (var jobs = await Ending.Feature())
        {
            await Configure(jobs);
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            await Reviewing.ReadAgain(jobs);
            Carries(await Next(jobs, "conflict"), "sonnet");
        }

        // A whole feature's review.
        await using (var jobs = await Ending.Feature())
        {
            await Configure(jobs);
            await Reviewing.Published(jobs);
            Carries(await Next(jobs, "feature_review"), "haiku");
        }

        // A whole feature's fix.
        await using (var jobs = await Ending.Feature())
        {
            await Configure(jobs);
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            Carries(await Next(jobs, "feature_fix"), "sonnet");
        }
    }
}
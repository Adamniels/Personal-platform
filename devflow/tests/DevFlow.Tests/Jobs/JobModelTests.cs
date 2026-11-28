using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Setup;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Every job carries the model its kind reads from the project's settings, building for a build, a
/// fix, a conflict, a feature's fix and a setup, reviewing for a card's review and a feature's,
/// reading for a reading, and the project's minutes and turns. Against real Postgres and the fake
/// code host, each kind brought about with the fake worker and handed out through the worker's own
/// route, by <see cref="EachKind"/>, which RunSettingsTests walks for the spend as well. See "Jobs"
/// in devflow/README.md.
/// </summary>
public sealed class JobModelTests
{
    /// <summary>Every kind that runs a model, in the order <see cref="EachKind"/> hands them
    /// out.</summary>
    internal static readonly IReadOnlyList<string> Kinds =
        [Job.Build, Job.Review, Job.Fix, Job.Conflict, Job.FeatureReview, Job.FeatureFix, Job.Read, Job.Setup];

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

    /// <summary>The job handed out next, a minute on.</summary>
    private static async Task<JsonObject> Next(Jobs jobs)
    {
        Fixed.Later(jobs);
        return await jobs.Job();
    }

    /// <summary>
    /// Brings about each kind of <see cref="Kinds"/> on a project of its own, runs
    /// <paramref name="configure"/> on the project before anything is handed out, and hands
    /// <paramref name="check"/> the job of that kind, after checking it is of that kind.
    /// </summary>
    internal static async Task EachKind(Func<Jobs, Task> configure, Action<string, JsonObject> check)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(check);

        void Hand(string kind, JsonObject job)
        {
            Assert.Equal(kind, (string)job["kind"]!);
            check(kind, job);
        }

        // A build.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await configure(jobs);
            Hand(Job.Build, await Next(jobs));
        }

        // A card's review.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await configure(jobs);
            await Built.Run(jobs, Built.Prepared(Built.Report()));
            Hand(Job.Review, await Next(jobs));
        }

        // A card's fix.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await configure(jobs);
            await Fixed.BuiltAndReviewed(jobs);
            Hand(Job.Fix, await Next(jobs));
        }

        // A conflict between the default branch and a feature's branch.
        await using (var jobs = await Ending.Feature())
        {
            await configure(jobs);
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            await Reviewing.ReadAgain(jobs);
            Hand(Job.Conflict, await Next(jobs));
        }

        // A whole feature's review.
        await using (var jobs = await Ending.Feature())
        {
            await configure(jobs);
            await Reviewing.Published(jobs);
            Hand(Job.FeatureReview, await Next(jobs));
        }

        // A whole feature's fix.
        await using (var jobs = await Ending.Feature())
        {
            await configure(jobs);
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            Hand(Job.FeatureFix, await Next(jobs));
        }

        // A reading of a card not yet read.
        await using (var jobs = await Jobs.Start(read: false))
        {
            await configure(jobs);
            Hand(Job.Read, await jobs.Job());
        }

        // A setup, of a project with no .devflow/ on its default branch.
        await using (var jobs = await SetupRunTests.Unset())
        {
            await configure(jobs);
            Hand(Job.Setup, await SetupRunTests.Next(jobs));
        }
    }

    [Fact]
    public async Task EachKindCarriesItsModel()
    {
        var kinds = new List<string>();
        await EachKind(Configure, (kind, job) =>
        {
            kinds.Add(kind);
            var model = kind switch
            {
                Job.Review or Job.FeatureReview => "haiku",
                Job.Read => "opus",
                _ => "sonnet",
            };
            Assert.Equal(model, (string)job["model"]!);
            Assert.Equal((45, 300), ((int)job["limits"]!["minutes"]!, (int)job["limits"]!["turns"]!));
        });
        Assert.Equal(Kinds, kinds);
    }
}
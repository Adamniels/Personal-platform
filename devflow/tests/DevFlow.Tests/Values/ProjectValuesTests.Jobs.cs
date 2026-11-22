using System.Text.Json.Nodes;

using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Setup;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Values;

using Jobs = DevFlow.Tests.Jobs.Jobs;

public sealed partial class ProjectValuesTests
{
    private const string DatabaseUrl = "postgres://throwaway:throwaway-pass@localhost:5432/test";

    /// <summary>The project's two values given, as the owner gives them on its page.</summary>
    private static async Task GiveTwo(Jobs jobs)
    {
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(jobs.Routes.App, jobs.Project, "STRIPE_TEST_KEY", Value)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Give(jobs.Routes.App, jobs.Project, "DATABASE_URL", DatabaseUrl)).Response.StatusCode);
    }

    /// <summary>The job handed out next, after checking it is of <paramref name="kind"/>, as
    /// <c>JobModelTests</c> asks for one.</summary>
    private static async Task<JsonObject> Next(Jobs jobs, string kind)
    {
        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(kind, (string)job["kind"]!);
        return job;
    }

    private static List<(string Name, string Value)> Carried(JsonObject job) =>
        [.. job["values"]!.AsArray().Select(value => ((string)value!["name"]!, (string)value!["value"]!))];

    private static void CarriesThem(JsonObject job) =>
        Assert.Equal([("DATABASE_URL", DatabaseUrl), ("STRIPE_TEST_KEY", Value)], Carried(job));

    private static void CarriesNone(JsonObject job)
    {
        Assert.Empty(Carried(job));
        Assert.DoesNotContain(Value, job.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobsThatRunCodeCarryThem()
    {
        // A build.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await GiveTwo(jobs);
            CarriesThem(await Next(jobs, "build"));
        }

        // A card's review runs none of the code.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await GiveTwo(jobs);
            await Built.Run(jobs, Built.Prepared(Built.Report()));
            CarriesNone(await Next(jobs, "review"));
        }

        // A card's fix.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            await GiveTwo(jobs);
            await Fixed.BuiltAndReviewed(jobs);
            CarriesThem(await Next(jobs, "fix"));
        }

        // A conflict between the default branch and a feature's branch.
        await using (var jobs = await Ending.Feature())
        {
            await GiveTwo(jobs);
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            await Reviewing.ReadAgain(jobs);
            CarriesThem(await Next(jobs, "conflict"));
        }

        // A whole feature's review runs none of the code.
        await using (var jobs = await Ending.Feature())
        {
            await GiveTwo(jobs);
            await Reviewing.Published(jobs);
            CarriesNone(await Next(jobs, "feature_review"));
        }

        // A whole feature's fix.
        await using (var jobs = await Ending.Feature())
        {
            await GiveTwo(jobs);
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            CarriesThem(await Next(jobs, "feature_fix"));
        }

        // A reading runs none of the code.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)], read: false))
        {
            await GiveTwo(jobs);
            CarriesNone(await Next(jobs, "read"));
        }

        // A check of the setup's version.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)], checkedRecipe: false))
        {
            await GiveTwo(jobs);
            CarriesThem(await Next(jobs, "check"));
        }

        // A setup, and its review, which runs none of the code.
        await using (var jobs = await SetupRunTests.Unset())
        {
            await GiveTwo(jobs);
            var setup = await SetupRunTests.Next(jobs);
            Assert.Equal("setup", (string)setup["kind"]!);
            CarriesThem(setup);

            SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "devflow/setup", SetupRunTests.GoodRecipe), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
            var head = await SetupRunTests.Push(jobs);
            Assert.Equal(StatusCodes.Status204NoContent, (await SetupRunTests.Send(jobs, setup, SetupRunTests.Result(head))).Response.StatusCode);
            await SetupRunTests.Tend(jobs);
            var review = await SetupRunTests.Next(jobs);
            Assert.Equal("review", (string)review["kind"]!);
            CarriesNone(review);
        }
    }
}
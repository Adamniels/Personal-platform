using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// What the tests of merging a feature into the default branch share: the feature of
/// <see cref="Ending"/>, with the owner's GitHub linked all the way so Dev flow holds a token that
/// acts as him, carried to its whole review and to CI on its head; the routes the page calls; the
/// minute that tells its cards how it ended; and what the fake and the database hold afterwards.
/// Against real Postgres and the fake code host. See "Merging a feature" in devflow/README.md.
/// </summary>
internal static class FeatureMerging
{
    public static CancellationToken Ct => Ending.Ct;

    public static Guid User => Ending.User;

    /// <summary>The words a refusal says when the head is not the one shown.</summary>
    public const string Changed = "The pull request has changed since this was shown";

    /// <summary>The feature of <see cref="Ending"/>, its parent 10 and its cards 11 and 12, with the
    /// owner's GitHub linked as <see cref="Jobs.Jobs.Owner"/>.</summary>
    public static Task<Jobs.Jobs> Feature() =>
        Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Ending.Parent, "Dev flow: features", [Ending.First, Ending.Second]),
            FeatureJobs.Sub(Ending.First, "Read the sub-issues"),
            FeatureJobs.Sub(Ending.Second, "Show each feature"),
        ],
        signIn: true);

    /// <summary>The feature finished, its pull request opened and its whole review answered
    /// <paramref name="verdict"/>, or failed in the job when null; then CI staged on its head as
    /// <paramref name="checkRuns"/>, green when null, and the next minute. The head. A verdict other
    /// than approve comes with every fix round already used, so the review leaves the feature
    /// waiting for the owner, the one way a feature it did not approve reaches him; going through
    /// the rounds is <see cref="FeatureFixTests"/>'s.</summary>
    public static async Task<string> Reviewed(Jobs.Jobs jobs, string? verdict = "approve", IReadOnlyList<CheckRun>? checkRuns = null, params JsonObject[] findings)
    {
        var head = await Reviewing.Published(jobs);
        if (verdict is not (null or "approve"))
        {
            await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE features SET fix_round = @most WHERE project_id = @project AND number = @number",
                new { most = DevFlow.Projects.RunSettings.Defaults.FixRoundsFeature, project = jobs.Project, number = Ending.Parent },
                cancellationToken: Ct));
        }

        if (verdict is null)
        {
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            var failed = await Built.Run(jobs, Built.PreparedFailure(inCard: true, "The reviewer could not read the diff"));
            Assert.Equal("feature_review", (string)failed["kind"]!);
        }
        else
        {
            await Reviewing.Review(jobs, Built.Verdict(verdict, findings));
        }

        if (checkRuns is null)
        {
            Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
        }
        else if (checkRuns.Count > 0)
        {
            Merged.Ci(jobs, head, checkRuns);
        }

        await Reviewing.Minute(jobs);
        return head;
    }

    /// <summary>The feature's pull request's number, as stored.</summary>
    public static async Task<int> Pull(Jobs.Jobs jobs) => (await Ending.Row(jobs)).PullRequestNumber!.Value;

    /// <summary>The address the dialog reads and the merge is asked at.</summary>
    public static string MergePath(Guid project, int number = Ending.Parent) => $"{FeaturePage.PathOf(project, number)}/merge";

    /// <summary>Asks to merge the feature, as the page does, naming the commit it showed.</summary>
    public static Task<HttpContext> Merge(Jobs.Jobs jobs, string? headCommit) =>
        jobs.Routes.App.SendJson("POST", MergePath(jobs.Project), Contract.Token, JsonSerializer.Serialize(new { head_commit = headCommit }));

    /// <summary>The feature's story, as its page reads it.</summary>
    public static async Task<JsonNode> Story(Jobs.Jobs jobs)
    {
        var (status, story) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
        Assert.Equal(StatusCodes.Status200OK, status);
        return story!;
    }

    /// <summary>The feature's item under "Features ready for your review".</summary>
    public static async Task<JsonNode> Item(Jobs.Jobs jobs)
    {
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        return Assert.Single(list!["features"]!.AsArray())!;
    }

    /// <summary>The fake's merges of a pull request by a person, with their own token, oldest first;
    /// the app's merges of the feature's cards into its branch are not among them.</summary>
    public static List<FakeWrite> Merges(Jobs.Jobs jobs) => [.. jobs.Fake.Writes(User).Where(write => write.Kind == "merge" && write.Purpose is null)];

    /// <summary>How many times the code host was asked to merge as a person.</summary>
    public static int AskedToMerge(Jobs.Jobs jobs) => jobs.Routes.Host.Calls.Count(call => call.Operation == "Merge");

    /// <summary>The issue with the number in the user's world.</summary>
    public static FakeIssue Issue(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(User).Installations[0].Repositories[0].Issues.Single(issue => issue.Number == number);

    /// <summary>The pull request with the number in the user's world.</summary>
    public static PullRequest PullRequest(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(User).Installations[0].Repositories[0].PullRequests.Single(pull => pull.Number == number);

    /// <summary>The app's writes to the issue with the number, oldest first.</summary>
    public static List<FakeWrite> AppWritesTo(Jobs.Jobs jobs, int number) =>
        [.. jobs.Fake.Writes(User).Where(write => write.Number == number && write.Purpose is not null && write.Kind is "comment" or "issue_closed" or "issue_opened")];

    /// <summary>The minute that tells a merged or closed feature's cards how it ended, as its loop
    /// runs it.</summary>
    public static Task Tell(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<FeatureOutcomes>().TellPending(jobs.Project, User, Ct);

    /// <summary>When the feature's branch was last read by its watch.</summary>
    public static async Task<DateTime> WatchedAt(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<DateTime>(new CommandDefinition(
            """
            SELECT b.last_read_at FROM branch_watches b JOIN features f ON f.id = b.feature_id
            WHERE f.project_id = @project AND f.number = @parent
            """,
            new { project = jobs.Project, parent = Ending.Parent },
            cancellationToken: Ct));
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// A card built by the fake worker on top of <see cref="Jobs.Jobs"/>: the project ready to hand out
/// work, the fake worker run with a prepared result, and what the fake and the database hold
/// afterwards. See "The pull request Dev flow opens" in devflow/README.md.
/// </summary>
internal static class Built
{
    public const string FakeWorkerPath = "/devflow/api/fake/worker/run";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A prepared result, a worker's but for its id, branch and head commit, which the
    /// fake worker fills: succeeded with <paramref name="report"/> and a passing check unless
    /// given otherwise.</summary>
    public static JsonObject Prepared(JsonNode? report = null, int? checkExit = 0)
    {
        var result = Jobs.Jobs.Result(Guid.Empty, "", report, checkExit);
        result.Remove("worker_id");
        result.Remove("branch");
        result.Remove("head_commit");
        return result;
    }

    /// <summary>A prepared result that took the pictures <paramref name="names"/>, with
    /// <paramref name="note"/>.</summary>
    public static JsonObject WithPictures(string? note, params string[] names)
    {
        var result = Prepared();
        result["screenshots"] = new JsonObject { ["names"] = new JsonArray([.. names.Select(name => (JsonNode)name)]), ["note"] = note };
        return result;
    }

    /// <summary>A prepared failure, in the card or outside it.</summary>
    public static JsonObject PreparedFailure(bool inCard, string reason)
    {
        var result = Jobs.Jobs.Failure(Guid.Empty, "", inCard, reason);
        result.Remove("worker_id");
        result.Remove("branch");
        result.Remove("head_commit");
        return result;
    }

    /// <summary>The fake worker takes the project's next job and ends it with
    /// <paramref name="result"/>, putting <paramref name="pictures"/> on pr-screenshots under the
    /// commit's folder as a worker pushes them; its answer, which names the job, the branch and the
    /// head.</summary>
    public static async Task<JsonObject> Run(Jobs.Jobs jobs, JsonObject result, IReadOnlyDictionary<string, byte[]>? pictures = null)
    {
        var request = new JsonObject
        {
            ["project_id"] = jobs.Project,
            ["result"] = result,
        };
        if (pictures is not null)
        {
            request["pictures"] = new JsonObject([.. pictures.Select(picture => KeyValuePair.Create(picture.Key, (JsonNode?)Convert.ToBase64String(picture.Value)))]);
        }

        var response = await jobs.Routes.App.SendJson("POST", FakeWorkerPath, Contract.Token, request.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Workers.Workers.Json(response)).AsObject();
    }

    /// <summary>A verdict in its form: <paramref name="verdict"/>, with <paramref name="findings"/>,
    /// and one blocking finding when it is changes and none are given.</summary>
    public static JsonObject Verdict(string verdict = "approve", params JsonObject[] findings) => new()
    {
        ["verdict"] = verdict,
        ["summary"] = verdict == "approve" ? "The empty state is there and tested." : "The empty state shows on an empty list only after a reload.",
        ["findings"] = new JsonArray([.. (findings.Length > 0 || verdict == "approve" ? findings : [Finding()]).Select(finding => (JsonNode)finding)]),
        ["for_owner"] = new JsonArray(new JsonObject { ["decision"] = "Show it on every list", ["view"] = "Agreed: the lists share one component." }),
    };

    /// <summary>A finding, blocking unless said.</summary>
    public static JsonObject Finding(bool blocking = true, string file = "web/src/list.tsx", int line = 42, string what = "The empty state is read once", string why = "A list emptied later shows nothing") => new()
    {
        ["blocking"] = blocking,
        ["file"] = file,
        ["line"] = line,
        ["what"] = what,
        ["why"] = why,
    };

    /// <summary>A prepared review result: succeeded with <paramref name="verdict"/> as its report
    /// and no check, since a review runs none.</summary>
    public static JsonObject PreparedVerdict(JsonNode? verdict = null) => Prepared(verdict ?? Verdict(), checkExit: null);

    /// <summary>The fake worker takes the project's next job, a review, and ends it with
    /// <paramref name="verdict"/>; its answer.</summary>
    public static async Task<JsonObject> Review(Jobs.Jobs jobs, JsonNode? verdict = null)
    {
        // A minute after the build, so the runs' order is theirs and not a frozen clock's.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        var ran = await Run(jobs, PreparedVerdict(verdict));
        Assert.Equal("review", (string)ran["kind"]!);
        return ran;
    }

    /// <summary>The pull requests the app opened in the user's world, oldest first.</summary>
    public static List<FakeWrite> Opened(Jobs.Jobs jobs) =>
        [.. jobs.Fake.Writes(Jobs.Jobs.User).Where(write => write.Kind == "pull_request")];

    /// <summary>A report with a decision for the owner beside one that is not, a task not done and
    /// one done, a test and a follow up.</summary>
    public static JsonObject Report()
    {
        var report = Jobs.Jobs.GoodReport();
        report["tasks"] = new JsonArray(
            new JsonObject { ["text"] = "Add the empty state", ["done"] = true },
            new JsonObject { ["text"] = "Count the habits", ["done"] = false });
        report["decisions"] = new JsonArray(
            new JsonObject { ["chosen"] = "A plain sentence", ["alternative"] = "An illustration", ["why"] = "Nothing else on the page is drawn", ["for_owner"] = false },
            new JsonObject { ["chosen"] = "Show it on every list", ["alternative"] = "Only on habits", ["why"] = "The lists look the same", ["for_owner"] = true });
        return report;
    }

    /// <summary>The card's pull request as the database records it: its number, its address and
    /// the card's state.</summary>
    public static async Task<CardPull> Card(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<CardPull>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, pull_request_number AS PullRequestNumber, pull_request_url AS PullRequestUrl
            FROM cards WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    /// <summary>Changes the pull request with the number in the user's world, as someone acting on
    /// GitHub would, and closes the card's issue when <paramref name="closeIssue"/> is given, as a
    /// merge with its <c>Closes</c> line does.</summary>
    public static void OnGitHub(Jobs.Jobs jobs, int pull, PullRequestState state, int? closeIssue = null)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository => repository with
                {
                    PullRequests = [.. repository.PullRequests.Select(each => each.Number == pull ? each with { State = state } : each)],
                    Issues = [.. repository.Issues.Select(each => each.Number == closeIssue ? each with { Open = false } : each)],
                })],
            })],
        });
    }

    /// <summary>Adds <paramref name="pull"/> to the user's world, as if it were already open.</summary>
    public static void Stage(Jobs.Jobs jobs, PullRequest pull)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository => repository with
                {
                    PullRequests = [.. repository.PullRequests, pull],
                })],
            })],
        });
    }

    /// <summary>A GET as the fixture token's user, and its JSON.</summary>
    public static async Task<(int Status, JsonNode? Body)> Get(Jobs.Jobs jobs, string path)
    {
        var response = await jobs.Routes.App.Send("GET", path, Contract.Token);
        var body = await Harness.Body(response);
        return (response.Response.StatusCode, body.Length == 0 ? null : JsonNode.Parse(body));
    }

    /// <summary>Asks to merge the card from the list, as the page does, naming the commit it
    /// showed.</summary>
    public static Task<HttpContext> Merge(Jobs.Jobs jobs, int card, string? headCommit, Guid? project = null) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{project ?? jobs.Project}/cards/{card}/merge", Contract.Token, JsonSerializer.Serialize(new { head_commit = headCommit }));

    internal sealed class CardPull
    {
        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public int? PullRequestNumber { get; init; }

        public string? PullRequestUrl { get; init; }
    }
}
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Fixing;

/// <summary>
/// A card taken through its rounds by the fake worker on top of <see cref="Built"/>: a fix run with
/// a prepared result, the owner's comment staged on a pull request as GitHub would hold it, and what
/// the database holds of the rounds. See "Fixing" in devflow/README.md.
/// </summary>
internal static class Fixed
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A minute later, so the next run starts after the last and the runs' order is
    /// theirs, not a frozen clock's.</summary>
    public static void Later(Jobs.Jobs jobs) => jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));

    /// <summary>A fix's report: done, with a reply to each finding.</summary>
    public static JsonObject Report(params (string Finding, string Answer)[] replies)
    {
        var report = Jobs.Jobs.GoodReport();
        report["summary"] = "Read the empty state on every change, with a test that empties the list.";
        report["replies"] = new JsonArray([.. (replies.Length > 0 ? replies : [("The empty state is read once", "Fixed: it is read on every change, and list.test.tsx empties the list.")])
            .Select(reply => (JsonNode)new JsonObject { ["finding"] = reply.Finding, ["answer"] = reply.Answer })]);
        return report;
    }

    /// <summary>The fake worker takes the project's next job, a fix, and ends it with
    /// <paramref name="result"/>, a passing fix unless given; with <paramref name="leaveNoCommit"/>
    /// it makes no commit, as an agent that changed nothing. The project is read first, as its
    /// minute would. Its answer.</summary>
    public static async Task<JsonObject> Fix(Jobs.Jobs jobs, JsonObject? result = null, bool leaveNoCommit = false)
    {
        Later(jobs);
        // The project is read on its minute, as it would be, so its default branch stays watched
        // and its work is not held.
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        var response = await jobs.Routes.App.SendJson("POST", Built.FakeWorkerPath, Contract.Token, new JsonObject
        {
            ["project_id"] = jobs.Project,
            ["result"] = result ?? Built.Prepared(Report()),
            ["leave_no_commit"] = leaveNoCommit,
        }.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var ran = (await Workers.Workers.Json(response)).AsObject();
        Assert.Equal("fix", (string)ran["kind"]!);
        return ran;
    }

    /// <summary>Card 4 built with <see cref="Built.Report"/> and reviewed with
    /// <paramref name="verdict"/>, changes with one blocking finding unless given. What the build
    /// answered.</summary>
    public static async Task<JsonObject> BuiltAndReviewed(Jobs.Jobs jobs, JsonObject? verdict = null)
    {
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        await Built.Review(jobs, verdict ?? Built.Verdict("changes"));
        return built;
    }

    /// <summary>A comment by <paramref name="author"/> on the conversation with the number, as
    /// GitHub would hold it, written at the fake clock's now unless <paramref name="at"/> is given,
    /// with an id after every other.</summary>
    public static void Comment(Jobs.Jobs jobs, int number, string author, string body, DateTimeOffset? at = null)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        var when = at ?? jobs.Routes.Time.GetUtcNow();
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository =>
                {
                    var id = repository.Conversations.SelectMany(conversation => conversation.Comments).Select(comment => comment.Id).DefaultIfEmpty(0).Max() + 1;
                    var comment = new Comment(id, author, body, when, when);
                    var conversations = repository.Conversations.Any(conversation => conversation.Number == number)
                        ? repository.Conversations.Select(conversation => conversation.Number == number ? conversation with { Comments = [.. conversation.Comments, comment] } : conversation)
                        : [.. repository.Conversations, new FakeConversation(number, [comment])];
                    return repository with { Conversations = [.. conversations] };
                })],
            })],
        });
    }

    /// <summary>The issue with the number given <paramref name="labels"/> in place of its own, as
    /// the owner taking it off the board, with none, or putting it back would leave it.</summary>
    public static void Label(Jobs.Jobs jobs, int number, IReadOnlyList<string> labels)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository => repository with
                {
                    Issues = [.. repository.Issues.Select(issue => issue.Number == number ? issue with { Labels = labels } : issue)],
                })],
            })],
        });
    }

    /// <summary>How many fix rounds the card has used, started by reviews.</summary>
    public static async Task<int> RoundsUsed(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT fix_rounds FROM cards WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    /// <summary>What a fix run records of its round: the commit it was handed and the round, null
    /// for the owner's comment.</summary>
    public static async Task<(string? FromCommit, int? Round)> FixRun(Jobs.Jobs jobs, Guid run)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string?, int?)>(new CommandDefinition(
            "SELECT from_commit, fix_round FROM runs WHERE id = @run",
            new { run },
            cancellationToken: Ct));
    }

    /// <summary>The head of the pull request with the number, as the fake's world holds it.</summary>
    public static string PullHead(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(Jobs.Jobs.User).Installations.SelectMany(installation => installation.Repositories)
            .SelectMany(repository => repository.PullRequests).Single(pull => pull.Number == number).HeadCommit;
}
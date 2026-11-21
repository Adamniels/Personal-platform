using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// Sending a card back: from a feature's page the owner sends back a card that has already landed in
/// the feature's branch, or one of its decisions, with a comment posted as him on the card's issue;
/// the next read opens the card again with his comment as its input, a fresh agent builds it on a new
/// branch from the feature's branch with what was built before, and once it lands again the feature
/// goes through its end again. Reopening the card's issue on GitHub and commenting there does the
/// same. Against real Postgres and the fake code host, with the fake worker. See "Sending a card
/// back" in devflow/README.md.
/// </summary>
public sealed class SendBackTests
{
    private const string Said = "Read the sub-issues a hundred at a time, not all at once.";

    private const string FirstBranch = "devflow/11-read-the-sub-issues";

    private static CancellationToken Ct => Ending.Ct;

    private static Guid User => Ending.User;

    [Fact]
    public async Task ACardIsOpenedAgainAndQueued()
    {
        await using var jobs = await Ending.Feature(signIn: true);
        await FeaturePage.Landed(jobs);
        await Tell(jobs);
        Assert.False(Issue(jobs, Ending.First).Open);
        var landedIn = (await Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;

        // Offered on the landed card, and on no other.
        var story = await Story(jobs);
        Assert.True((bool)CardOf(story, Ending.First)["can_send_back"]!);
        Assert.False((bool)CardOf(story, Ending.Second)["can_send_back"]!);
        Assert.Null(CardOf(story, Ending.First)["sent_back"]);

        // Sent back from the page: my comment, trimmed, on the card's issue, as me with my linked
        // token and never as the app.
        var sent = await SendBack(jobs, Ending.First, $"  {Said}\n");
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);
        var mine = Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
        Assert.Equal((Ending.First, Said, (string?)null), (mine.Number, mine.Body, mine.Purpose));

        // The read that followed queued the card with my comment, and the feature builds again.
        var card = await Built.Card(jobs, Ending.First);
        Assert.Equal(("queued", SendingBack.Queued, (int?)null), (card.State, card.Reason, card.PullRequestNumber));
        Assert.Equal("building", (await Ending.Row(jobs)).State);

        // The app opens its issue again, once, with a key to post.
        await Tell(jobs);
        Assert.True(Issue(jobs, Ending.First).Open);
        var opened = Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "issue_opened");
        Assert.Equal((Ending.First, FakeCodeHost.AppLogin, "post"), (opened.Number, opened.By, opened.Purpose));
        await Tell(jobs);
        Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "issue_opened");

        // Read again, with its issue closed or open, it stays queued, and my comment is taken once.
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("queued", (await Built.Card(jobs, Ending.First)).State);
        var shown = CardOf(await Story(jobs), Ending.First);
        Assert.False((bool)shown["can_send_back"]!);
        var sentBack = shown["sent_back"]!;
        Assert.Equal(1, (int)sentBack["times"]!);
        Assert.Equal(landedIn, (int)sentBack["pull_request"]!["number"]!);
        Assert.Equal([Said], sentBack["comments"]!.AsArray().Select(comment => (string)comment!));
        Assert.Equal(1, await SentBacks(jobs, Ending.First));
    }

    [Fact]
    public async Task TheBriefCarriesWhatWasBuilt()
    {
        await using var jobs = await Ending.Feature(signIn: true);
        await FeaturePage.Landed(jobs);
        await Tell(jobs);
        var landedIn = (await Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, Ending.First, Said)).Response.StatusCode);

        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal((Job.Build, Ending.First), ((string)job["kind"]!, (int)job["card"]!["number"]!));
        var brief = (string)job["brief"]!;

        // The card as picked, then, after it, what was built and my comment as my input.
        var card = brief.IndexOf("#11 Read the sub-issues", StringComparison.Ordinal);
        var section = brief.IndexOf("## Sent back", StringComparison.Ordinal);
        Assert.True(card >= 0 && section > card, brief);
        var sentBack = brief[section..];
        Assert.Contains($"pull request #{landedIn}", sentBack, StringComparison.Ordinal);
        Assert.Contains("### The report it landed with", sentBack, StringComparison.Ordinal);
        Assert.Contains("\"chosen\": \"Show it on every list\"", sentBack, StringComparison.Ordinal);
        Assert.Contains("### The owner's comment", sentBack, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Said}\n```", sentBack, StringComparison.Ordinal);
        Assert.DoesNotContain("## Run again", brief, StringComparison.Ordinal);

        // The run keeps my comment as its input.
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var comments = await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT owner_comments::text FROM runs WHERE project_id = @project AND id = @id",
            new { project = jobs.Project, id = (Guid)job["job_id"]! },
            cancellationToken: Ct));
        Assert.Equal([Said], JsonSerializer.Deserialize<List<string>>(comments));
    }

    [Fact]
    public async Task ItIsBuiltOnANewBranch()
    {
        await using var jobs = await Ending.Feature(signIn: true);
        await FeaturePage.Landed(jobs);
        await Tell(jobs);
        var first = (await Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;

        // The first time it is sent back, -2, from the feature's branch at its head.
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, Ending.First, Said)).Response.StatusCode);
        var second = await BuildAndLand(jobs, $"{FirstBranch}-2");
        Assert.NotEqual(first, second);

        // The second time, -3: a branch that merged is never used again.
        await Tell(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, Ending.First, "And keep the order.")).Response.StatusCode);
        var third = await BuildAndLand(jobs, $"{FirstBranch}-3");
        Assert.NotEqual(second, third);
        Assert.Equal(2, await SentBacks(jobs, Ending.First));
    }

    [Fact]
    public async Task SendingBackADecision()
    {
        await using var jobs = await Ending.Feature(signIn: true);
        await FeaturePage.Landed(jobs);
        await Tell(jobs);

        // Built.Report marks "Show it on every list", its second decision, for me.
        var decision = Assert.Single(CardOf(await Story(jobs), Ending.First)["decisions"]!.AsArray())!;
        var run = (Guid)decision["run_id"]!;
        var position = (int)decision["position"]!;
        var sent = await SendBackDecision(jobs, run, position, Said);
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);

        // My comment on the card's issue, as me, begins with the decision quoted.
        var mine = Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
        var expected = $"> Chosen: Show it on every list\n> Instead of: Only on habits\n\n{Said}";
        Assert.Equal((Ending.First, expected), (mine.Number, mine.Body));

        // The decision is recorded sent back, and the card queued with the whole comment.
        var answer = Assert.Single(await FeaturePage.Answers(jobs));
        Assert.Equal((Ending.First, run, position, "sent_back"), (answer.CardNumber, answer.RunId, answer.Position, answer.Answer));
        Assert.Equal(("queued", SendingBack.Queued), ((await Built.Card(jobs, Ending.First)).State, (await Built.Card(jobs, Ending.First)).Reason));
        var shown = CardOf(await Story(jobs), Ending.First);
        Assert.Equal("sent_back", (string)shown["decisions"]![0]!["answer"]!["answer"]!);
        Assert.Equal([expected], shown["sent_back"]!["comments"]!.AsArray().Select(comment => (string)comment!));

        // Answered, it is not sent back again, nor answered "Fine as it is".
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBackDecision(jobs, run, position, Said)).Response.StatusCode);
        Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
    }

    [Fact]
    public async Task TheFeatureEndsAgain()
    {
        await using var jobs = await Ending.Feature(signIn: true);
        var head = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("approve"));
        Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        var ended = await Ending.Row(jobs);
        Assert.Equal("ready", ended.State);
        var featurePull = ended.PullRequestNumber!.Value;
        await Tell(jobs);

        // Sent back, the feature builds again, its pull request open.
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, Ending.First, Said)).Response.StatusCode);
        Assert.Equal("building", (await Ending.Row(jobs)).State);
        Assert.Equal(PullRequestState.Open, Merged.Pull(jobs, featurePull).State);

        // Built and landed again, the feature's end runs again: nothing new from main to bring in,
        // its pull request kept, its body listing the card's new pull request.
        var landedAgain = await BuildAndLand(jobs, $"{FirstBranch}-2");
        await Ending.ReadAndEnd(jobs);
        var row = await Ending.Row(jobs);
        Assert.Equal(("reviewing", featurePull), (row.State, row.PullRequestNumber));
        Assert.Contains($"- #{Ending.First} Read the sub-issues, landed in #{landedAgain}", Merged.Pull(jobs, featurePull).Body, StringComparison.Ordinal);
        Assert.Equal(PullRequestState.Open, Merged.Pull(jobs, featurePull).State);

        // Reviewed as a whole at its new head, with CI green there, it is ready again.
        await Reviewing.Minute(jobs);
        var newHead = await Reviewing.Head(jobs);
        Assert.NotEqual(head, newHead);
        var review = await Reviewing.Review(jobs, Built.Verdict("approve"));
        Assert.Equal(newHead, (string)review["head_commit"]!);
        Merged.Ci(jobs, newHead, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        row = await Ending.Row(jobs);
        Assert.Equal(("ready", (string?)null, featurePull), (row.State, row.Reason, row.PullRequestNumber));
        Assert.Equal(2, (await Reviewing.Reviews(jobs)).Count);
        Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "pull_request" && write.Base == "main");
    }

    [Fact]
    public async Task ReopeningOnGitHubWithACommentSendsItBack()
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        await Tell(jobs);
        Fixed.Later(jobs);

        // Opened again on GitHub, by anyone, with no comment of mine: not queued, and its page says
        // why.
        Merged.Issue(jobs, Ending.First, issue => issue with { Open = true });
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal(("done", SendingBack.OpenAgain), ((await Built.Card(jobs, Ending.First)).State, (await Built.Card(jobs, Ending.First)).Reason));
        Assert.Equal(SendingBack.OpenAgain, (string?)CardOf(await Story(jobs), Ending.First)["reason"]);

        // Anyone else's comment queues nothing either.
        Fixed.Comment(jobs, Ending.First, "someone-else", "Send it back, please.");
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("done", (await Built.Card(jobs, Ending.First)).State);

        // My comment does, the same way, with mine alone as its input.
        Fixed.Comment(jobs, Ending.First, Jobs.Jobs.Owner, Said);
        await jobs.Routes.Read(jobs.Project, User);
        var card = await Built.Card(jobs, Ending.First);
        Assert.Equal(("queued", SendingBack.Queued), (card.State, card.Reason));
        Assert.Equal("building", (await Ending.Row(jobs)).State);
        Fixed.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(($"{FirstBranch}-2", Ending.Branch), ((string)job["branch"]!, (string)job["base"]!));
        Assert.Contains($"```text\n{Said}\n```", (string)job["brief"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("Send it back, please.", (string)job["brief"]!, StringComparison.Ordinal);

        // Closed again with nothing sent back, a landed card says nothing more.
        await using var closed = await Ending.Feature();
        await FeaturePage.Landed(closed);
        await Tell(closed);
        Merged.Issue(closed, Ending.First, issue => issue with { Open = true });
        await closed.Routes.Read(closed.Project, User);
        Merged.Issue(closed, Ending.First, issue => issue with { Open = false });
        await closed.Routes.Read(closed.Project, User);
        Assert.Equal(("done", (string?)null), ((await Built.Card(closed, Ending.First)).State, (await Built.Card(closed, Ending.First)).Reason));
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("closed")]
    public async Task OnlyForALandedCardOfAnOpenFeature(string ended)
    {
        await using var jobs = await Ending.Feature(signIn: true);
        await FeaturePage.Landed(jobs);
        await Tell(jobs);

        // A card that has not landed: not offered, and refused.
        Assert.False((bool)CardOf(await Story(jobs), Ending.Second)["can_send_back"]!);
        var refused = await SendBack(jobs, Ending.Second, Said);
        Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
        Assert.Equal("This card cannot be sent back now", (string)(await Workers.Workers.Json(refused))["title"]!);

        // A landed card of a feature that is merged or closed: not offered, and refused, its
        // decision too.
        await Merged.Sql(jobs, $"UPDATE features SET state = '{ended}' WHERE project_id = @project");
        var shown = CardOf(await Story(jobs), Ending.First);
        Assert.False((bool)shown["can_send_back"]!);
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(jobs, Ending.First, Said)).Response.StatusCode);
        var decision = Assert.Single(shown["decisions"]!.AsArray())!;
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBackDecision(jobs, (Guid)decision["run_id"]!, (int)decision["position"]!, Said)).Response.StatusCode);
        Assert.DoesNotContain(jobs.Fake.Writes(User), write => write.Kind == "comment" && write.By == Jobs.Jobs.Owner);
        Assert.Empty(await FeaturePage.Answers(jobs));

        // My comment on its issue on GitHub queues nothing.
        Fixed.Later(jobs);
        Fixed.Comment(jobs, Ending.First, Jobs.Jobs.Owner, Said);
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Equal("done", (await Built.Card(jobs, Ending.First)).State);
        Assert.Equal(0, await SentBacks(jobs, Ending.First));
    }

    /// <summary>The card sent back, its issue opened again on the minute, built by the fake worker on
    /// <paramref name="branch"/>, from the feature's branch at its head, its pull request opened into the feature's branch, CI green and
    /// approved, so the merge check lands it there again; then its issue told and the project read.
    /// The pull request it landed in.</summary>
    private static async Task<int> BuildAndLand(Jobs.Jobs jobs, string branch)
    {
        // The minute after the send back opens the card's issue again, which the merge check needs:
        // it lands only an open sub-issue of its feature.
        await Tell(jobs);
        Assert.True(Issue(jobs, Ending.First).Open);
        await jobs.Routes.Read(jobs.Project, User);
        var featureHead = await Reviewing.Head(jobs);
        Fixed.Later(jobs);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal((Job.Build, Ending.First, branch), ((string)built["kind"]!, (int)built["card"]!, (string)built["branch"]!));
        var run = (await jobs.Runs()).Where(each => each.CardNumber == Ending.First && each.Kind == Job.Build).OrderBy(each => each.StartedAt).Last();
        Assert.Equal((branch, Ending.Branch), (run.Branch, run.Base));

        // Its commit is made on the feature's branch as it was.
        var commit = (string)built["head_commit"]!;
        var gitDir = Assert.IsType<CodeHostResult<Uri>.Found>(await jobs.Fake.CloneAddress(User, FeatureJobs.Repo, Ct)).Value.LocalPath;
        Assert.Equal($"{commit} {featureHead}", await Git(gitDir, "rev-list", "--parents", "-n", "1", commit));

        // Its pull request goes into the feature's branch, from the new branch.
        var opened = Built.Opened(jobs).Last();
        Assert.Equal((branch, Ending.Branch), (opened.Head, opened.Base));
        var pull = (await Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;
        await jobs.Routes.Read(jobs.Project, User);
        Merged.Ci(jobs, commit, Merged.GreenRuns, Merged.GreenStatuses);
        await Built.Review(jobs, Built.Verdict("approve"));
        var landed = await Built.Card(jobs, Ending.First);
        Assert.Equal(("done", (string?)null), (landed.State, landed.Reason));
        Assert.Equal(PullRequestState.Merged, Merged.Pull(jobs, pull).State);
        await Tell(jobs);
        Assert.False(Issue(jobs, Ending.First).Open);
        await jobs.Routes.Read(jobs.Project, User);
        return pull;
    }

    /// <summary>The owner's send back of the card with the number, from the feature's page, a minute
    /// after what came before, as a person's click comes after the run it answers.</summary>
    private static Task<HttpContext> SendBack(Jobs.Jobs jobs, int card, string comment)
    {
        Fixed.Later(jobs);
        return jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/cards/{card}/send-back", Contract.Token, JsonSerializer.Serialize(new { comment }));
    }

    /// <summary>The owner's send back of the decision at <paramref name="position"/> of the report of
    /// <paramref name="run"/>, from the feature's page, a minute after what came before.</summary>
    private static Task<HttpContext> SendBackDecision(Jobs.Jobs jobs, Guid run, int position, string comment)
    {
        Fixed.Later(jobs);
        return jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/features/{Ending.Parent}/decisions/{run}/{position}/send-back", Contract.Token, JsonSerializer.Serialize(new { comment }));
    }

    /// <summary>The feature's story, as its page reads it.</summary>
    private static async Task<JsonNode> Story(Jobs.Jobs jobs)
    {
        var (status, story) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
        Assert.Equal(StatusCodes.Status200OK, status);
        return story!;
    }

    private static JsonNode CardOf(JsonNode story, int number) =>
        story["cards"]!.AsArray().Single(card => (int)card!["number"]! == number)!;

    /// <summary>The card's issue in the fake world.</summary>
    private static FakeIssue Issue(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(User).Installations.Single().Repositories.Single().Issues.Single(issue => issue.Number == number);

    /// <summary>How many times the card was sent back, read past row level security.</summary>
    private static async Task<int> SentBacks(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT sent_backs FROM cards WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    /// <summary>What git printed, trimmed; fails the test when it fails.</summary>
    private static async Task<string> Git(string gitDir, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(gitDir);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)!;
        var output = await git.StandardOutput.ReadToEndAsync(Ct);
        var error = await git.StandardError.ReadToEndAsync(Ct);
        await git.WaitForExitAsync(Ct);
        Assert.True(git.ExitCode == 0, error);
        return output.Trim();
    }

    /// <summary>The landed cards' issues told and the sent back cards' issues opened, as their
    /// minute does it.</summary>
    private static Task Tell(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<LandedCards>().TellPending(jobs.Project, User, Ct);
}
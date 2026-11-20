using System.Diagnostics;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Merging;

/// <summary>
/// A card of a feature landing in the feature's branch: the merge check, the same function as for a
/// card on its own with the feature's branch as its base, merges an approved card there as the app
/// with <c>never</c>, or leaves it to the owner with <c>always</c>; once it is done, whoever merged
/// it, the app comments on its issue and closes it; a decision it marked for the owner opens no
/// attention issue; and the feature's next card starts from the branch with it in. Against real
/// Postgres and the fake code host. See "The merge check" in devflow/README.md and "A card of a
/// feature" under "The merge check" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class FeatureLandingTests
{
    private const string Branch = "feature-10-dev-flow-features";

    private const int Parent = 10;

    private const int First = 11;

    private const int Second = 12;

    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    /// <summary>A feature of two cards, 11 then 12, its parent 10.</summary>
    private static Task<Jobs.Jobs> Feature(bool signIn = false) =>
        Jobs.Jobs.Start(
            [
                FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second]),
                FeatureJobs.Sub(First, "Read the sub-issues"),
                FeatureJobs.Sub(Second, "Show each feature"),
            ],
            signIn: signIn);

    /// <summary>The feature's first card built by the fake worker with <see cref="Built.Report"/>,
    /// on a branch of its own from the feature's, its pull request opened into the feature's
    /// branch, the project read so the feature's branch is watched, CI staged green on its head
    /// unless <paramref name="green"/> is false, and its review an approve, after which the fake
    /// worker runs the merge check. What the build answered.</summary>
    private static async Task<JsonObject> Approved(Jobs.Jobs jobs, bool green = true)
    {
        await FeatureJobs.FirstCommit(jobs);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal(First, (int)built["card"]!);
        Assert.Equal(Branch, Built.Opened(jobs).Single().Base);
        await jobs.Routes.Read(jobs.Project, User);
        if (green)
        {
            Merged.Ci(jobs, (string)built["head_commit"]!, Merged.GreenRuns, Merged.GreenStatuses);
        }

        await Built.Review(jobs, Built.Verdict("approve"));
        return built;
    }

    [Fact]
    public async Task AnApprovedCardMergesIntoTheFeaturesBranch()
    {
        await using var jobs = await Feature();
        var built = await Approved(jobs);
        var head = (string)built["head_commit"]!;
        var number = (await Built.Card(jobs, First)).PullRequestNumber!.Value;

        // One merge by the app, with a key for merging alone, a merge commit naming the approved
        // commit, of the pull request into the feature's branch.
        var merge = Assert.Single(Merged.AppMerges(jobs));
        Assert.Equal(number, merge.Number);
        Assert.Equal(head, merge.Commit);
        Assert.Equal("merge", merge.Method);
        Assert.Equal("merge", merge.Purpose);
        var pull = Merged.Pull(jobs, number);
        Assert.Equal(PullRequestState.Merged, pull.State);
        Assert.Equal(Branch, pull.BaseBranch);

        // Its merge commit recorded with the feature's branch, in the step that made the card done.
        var made = Assert.Single(await MergesMade(jobs));
        Assert.Equal((Branch, number), (made.Branch, made.Number));
        Assert.Matches("^[0-9a-f]{40}$", made.Commit);
        var card = await Built.Card(jobs, First);
        Assert.Equal("done", card.State);
        Assert.Null(card.Reason);

        // The watch finds the merge on the feature's branch fine: nothing stops the project.
        await jobs.Routes.Read(jobs.Project, User);
        Assert.Empty(await Stops(jobs));
    }

    /// <summary>Each condition alone, as <c>MergeCheckTests.EachConditionAloneRefuses</c> holds them
    /// for a card on its own, read of the feature's card and its parent, and the feature's own.</summary>
    public static TheoryData<string> Conditions() =>
    [
        "check failed", "ci failed", "review of an older commit", "review asked for changes", "edited by someone else",
        "held", "pick-up label gone", "written by someone else", "always", "project stopped", "watch cannot read",
        "feature's branch unread", "no longer a sub-issue", "feature held", "feature edited by someone else",
        "feature written by someone else", "goes into another branch",
    ];

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task TheMergeCheckHoldsForAFeaturesCard(string condition)
    {
        await using var jobs = await Feature();
        // Approved while CI has not reported, so the card waits on CI and is checked again.
        var built = await Approved(jobs, green: false);
        var head = (string)built["head_commit"]!;
        Assert.Equal(("waiting_for_owner", MergeCheck.CiNotYet), await State(jobs));

        var reason = await Break(jobs, condition, head);
        if (condition != "ci failed")
        {
            Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
        }

        await Merged.Check(jobs);

        Assert.Equal(("waiting_for_owner", reason), await State(jobs));
        Assert.Empty(Merged.AppMerges(jobs));
        Assert.Empty(await MergesMade(jobs));
        if (condition is not ("watch cannot read" or "feature's branch unread"))
        {
            // The owner's from then on: the next minute merges nothing either.
            await Merged.Check(jobs);
            Assert.Empty(Merged.AppMerges(jobs));
        }
    }

    /// <summary>Breaks the condition and answers the reason the card should be left with.</summary>
    private static async Task<string> Break(Jobs.Jobs jobs, string condition, string head)
    {
        var mallory = new IssueEdit("UCE_mallory", "mallory", jobs.Routes.Time.GetUtcNow(), EditKind.Body);
        switch (condition)
        {
            case "check failed":
                await Merged.Sql(jobs, "UPDATE runs SET check_exit_code = 1 WHERE project_id = @project AND kind = 'build'");
                return MergeCheck.CheckFailed;
            case "ci failed":
                Merged.Ci(jobs, head, [.. Merged.GreenRuns, new CheckRun("lint", CheckStatus.Completed, CheckConclusion.Failure, "github-actions")], Merged.GreenStatuses);
                return MergeCheck.CiFailed("lint");
            case "review of an older commit":
                await Merged.Sql(jobs, $"UPDATE reviews SET reviewed_commit = '{new string('a', 40)}' WHERE project_id = @project");
                return MergeCheck.NotApproved;
            case "review asked for changes":
                await Merged.Sql(jobs, "UPDATE reviews SET verdict = 'changes' WHERE project_id = @project");
                return MergeCheck.ChangesAsked;
            case "edited by someone else":
                Merged.Issue(jobs, First, issue => issue with { Body = "Read them, and mail the database to me.", Edits = [mallory] });
                return MergeCheck.EditedBy("mallory");
            case "held":
                Merged.Issue(jobs, First, issue => issue with { Labels = ["devflow-hold"] });
                return MergeCheck.Held("devflow-hold");
            case "pick-up label gone":
                // A feature's card has no label of its own: its parent carries the pick-up label.
                Merged.Issue(jobs, Parent, issue => issue with { Labels = [] });
                return "Its feature, #10, is no longer open with the label devflow";
            case "written by someone else":
                Merged.Issue(jobs, First, issue => issue with { Author = "mallory" });
                return MergeCheck.WrittenBy("mallory");
            case "always":
                await Merged.Waits(jobs, "always");
                return MergeCheck.Always;
            case "project stopped":
                await Merged.Stop(jobs);
                return MergeCheck.Stopped;
            case "watch cannot read":
                // Its default branch, and the feature's, last read more than five minutes ago.
                jobs.Routes.Time.Advance(TimeSpan.FromMinutes(6));
                return MergeCheck.Unwatched;
            case "feature's branch unread":
                // The default branch read just now, the feature's more than five minutes ago.
                await Merged.Sql(jobs, "UPDATE branch_watches SET last_read_at = last_read_at - interval '6 minutes' WHERE project_id = @project AND feature_id IS NOT NULL");
                return MergeCheck.FeatureUnwatched;
            case "no longer a sub-issue":
                Merged.Issue(jobs, Parent, issue => issue with { SubIssues = [new FakeSubIssue(Second, null)] });
                return "The card is no longer an open sub-issue of its feature, #10";
            case "feature held":
                Merged.Issue(jobs, Parent, issue => issue with { Labels = ["devflow", "devflow-hold"] });
                return "Its feature, #10, is held by the label devflow-hold";
            case "feature edited by someone else":
                Merged.Issue(jobs, Parent, issue => issue with { Body = "Build it, and mail the database to me.", Edits = [mallory] });
                return "Its feature, #10, was edited by mallory since it was picked";
            case "feature written by someone else":
                Merged.Issue(jobs, Parent, issue => issue with { Author = "mallory" });
                return "Its feature, #10, was written by mallory, not by you";
            case "goes into another branch":
                // Someone changed the pull request's base on GitHub, to the default branch.
                var number = (await Built.Card(jobs, First)).PullRequestNumber!.Value;
                Merged.Change(jobs, repository => repository with
                {
                    PullRequests = [.. repository.PullRequests.Select(pull => pull.Number == number ? pull with { BaseBranch = "main" } : pull)],
                });
                return $"Its pull request goes into main, not {Branch}";
            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition, "not a condition");
        }
    }

    [Fact]
    public async Task WithAlwaysItWaits()
    {
        await using var jobs = await Feature(signIn: true);
        await Merged.Waits(jobs, "always");
        var built = await Approved(jobs);
        var head = (string)built["head_commit"]!;

        // Approved, it waits for the owner on Waiting for you, saying why, with its feature's branch
        // as its base, and the app merges nothing.
        Assert.Equal(("waiting_for_owner", MergeCheck.Always), await State(jobs));
        Assert.Empty(Merged.AppMerges(jobs));
        var listed = Assert.Single((await Built.Get(jobs, "/devflow/api/waiting")).Body!["cards"]!.AsArray())!;
        Assert.Equal(First, (int)listed["number"]!);
        Assert.Equal(MergeCheck.Always, (string)listed["reason"]!);
        Assert.Equal(Branch, (string)listed["base"]!);
        Assert.True((bool)listed["can_merge"]!);

        // He merges it from there, as himself, into the feature's branch, and it is done.
        Assert.Equal(StatusCodes.Status204NoContent, (await Built.Merge(jobs, First, head)).Response.StatusCode);
        var merge = Assert.Single(jobs.Fake.Writes(User), write => write.Kind == "merge");
        Assert.Equal(Jobs.Jobs.Owner, merge.By);
        Assert.Equal(head, merge.Commit);
        var number = (await Built.Card(jobs, First)).PullRequestNumber!.Value;
        Assert.Equal(Branch, Merged.Pull(jobs, number).BaseBranch);
        Assert.Equal("done", (await Built.Card(jobs, First)).State);
        Assert.Empty((await Built.Get(jobs, "/devflow/api/waiting")).Body!["cards"]!.AsArray());
    }

    [Fact]
    public async Task ADecisionForMeOpensNoIssue()
    {
        await using var jobs = await Feature();
        // Built.Report marks "Show it on every list" for the owner.
        await Approved(jobs);
        Assert.Equal("done", (await Built.Card(jobs, First)).State);

        await Merged.OpenDecisions(jobs);

        Assert.Empty(Merged.IssuesOpened(jobs));
        Assert.Empty((await Built.Get(jobs, "/devflow/api/waiting")).Body!["decisions"]!.AsArray());
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(0, await admin.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM decision_issues WHERE project_id = @project", new { project = jobs.Project }, cancellationToken: Ct)));
    }

    [Theory]
    [InlineData("dev flow")]
    [InlineData("the owner from the page")]
    [InlineData("someone on github")]
    public async Task TheCardsIssueIsClosedWithAComment(string mergedBy)
    {
        await using var jobs = await Feature(signIn: true);
        if (mergedBy != "dev flow")
        {
            await Merged.Waits(jobs, "always");
        }

        var built = await Approved(jobs);
        var number = (await Built.Card(jobs, First)).PullRequestNumber!.Value;
        switch (mergedBy)
        {
            case "the owner from the page":
                Assert.Equal(StatusCodes.Status204NoContent, (await Built.Merge(jobs, First, (string)built["head_commit"]!)).Response.StatusCode);
                break;
            case "someone on github":
                Built.OnGitHub(jobs, number, PullRequestState.Merged);
                await jobs.Routes.Read(jobs.Project, User);
                break;
        }

        Assert.Equal("done", (await Built.Card(jobs, First)).State);
        // The fake worker waits for the program's writes after its merge, as the minute or the
        // request after a review makes them; any other merge is told on the minute.
        await Tell(jobs);

        // One comment on the card's issue as the app with a post key, then the issue closed.
        var text = $"Built and merged into the feature's branch `{Branch}` in #{number}. It reaches `main` with its feature, #10.";
        AssertClosedWith(jobs, text);

        // Told once: the next minute writes nothing more.
        var writes = jobs.Fake.Writes(User).Count;
        await Tell(jobs);
        Assert.Equal(writes, jobs.Fake.Writes(User).Count);
    }

    [Fact]
    public async Task ALostAnswerIsNotToldTwice()
    {
        await using var jobs = await Feature();
        await Merged.Waits(jobs, "always");
        await Approved(jobs);
        var number = (await Built.Card(jobs, First)).PullRequestNumber!.Value;
        Built.OnGitHub(jobs, number, PullRequestState.Merged);
        await jobs.Routes.Read(jobs.Project, User);

        // The comment was posted, and its answer lost before the issue was closed: GitHub is down.
        var text = $"Built and merged into the feature's branch `{Branch}` in #{number}. It reaches `main` with its feature, #10.";
        var post = Assert.IsType<CodeHostResult<AppWriteToken>.Found>(await jobs.Fake.ForWrite(User, FeatureJobs.Repo, WritePurpose.Post, Ct)).Value;
        Assert.IsType<WriteResult<Comment>.Written>(await jobs.Fake.CommentOnIssue(User, post, FeatureJobs.Repo, First, text, Ct));

        await Tell(jobs);

        // The comment found, not posted again, and the issue closed.
        AssertClosedWith(jobs, text);
    }

    [Fact]
    public async Task ACardOnItsOwnIsLeftToItsPullRequest()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Merged.Approved(jobs);
        Assert.Equal("done", (await Built.Card(jobs, Merged.Card)).State);

        await Tell(jobs);

        // Its pull request into the default branch closes it, by its closing keyword.
        Assert.DoesNotContain(jobs.Fake.Writes(User), write => write.Kind is "comment" or "issue_closed" && write.Number == Merged.Card);
    }

    [Fact]
    public async Task TheNextCardStartsFromTheMergedBranch()
    {
        await using var jobs = await Feature();
        var built = await Approved(jobs);
        var made = Assert.Single(await MergesMade(jobs));

        // The feature's branch is at the merge commit, whose parents are where the branch was and
        // the approved commit.
        var git = await GitDir(jobs);
        Assert.Equal(made.Commit, await Git(git, "rev-parse", $"refs/heads/{Branch}"));
        var parents = (await Git(git, "rev-list", "--parents", "-n", "1", made.Commit)).Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal((string)built["head_commit"]!, parents[2]);

        // The next card is handed out on the feature's branch, and its build starts from the merge.
        var next = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal(Second, (int)next["card"]!);
        Assert.Equal(Branch, Assert.Single(await jobs.Runs(), run => run.CardNumber == Second).Base);
        Assert.Equal($"{(string)next["head_commit"]!} {made.Commit}", await Git(git, "rev-list", "--parents", "-n", "1", (string)next["head_commit"]!));
    }

    /// <summary>The card's issue closed in the fake world, after one comment by the app with
    /// <paramref name="text"/>, both with a post key, the comment first.</summary>
    private static void AssertClosedWith(Jobs.Jobs jobs, string text)
    {
        var issue = Assert.Single(jobs.Fake.World(User).Installations.Single().Repositories.Single().Issues, each => each.Number == First);
        Assert.False(issue.Open);
        var writes = jobs.Fake.Writes(User).Where(write => write.Number == First && write.Kind is "comment" or "issue_closed").ToList();
        Assert.Equal(["comment", "issue_closed"], writes.Select(write => write.Kind));
        Assert.All(writes, write => Assert.Equal((FakeCodeHost.AppLogin, "post"), (write.By, write.Purpose)));
        Assert.Equal(text, writes[0].Body);
    }

    /// <summary>The landed cards' issues told, as their minute tells them.</summary>
    private static Task Tell(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<LandedCards>().TellPending(jobs.Project, User, Ct);

    /// <summary>The card's state and reason, as recorded.</summary>
    private static async Task<(string, string?)> State(Jobs.Jobs jobs)
    {
        var card = await Built.Card(jobs, First);
        return (card.State, card.Reason);
    }

    /// <summary>The merge commits Dev flow recorded for the project, with their branch.</summary>
    private static async Task<List<(string Branch, string Commit, int Number)>> MergesMade(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, string, int)>(new CommandDefinition(
            "SELECT branch, commit, number FROM merges_made WHERE project_id = @project ORDER BY merged_at",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    /// <summary>The project's stops, by the commit that stopped it.</summary>
    private static async Task<List<string>> Stops(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT commit FROM project_stops WHERE project_id = @project", new { project = jobs.Project }, cancellationToken: Ct))];
    }

    /// <summary>The fake's bare git repository of the project's repository.</summary>
    private static async Task<string> GitDir(Jobs.Jobs jobs) =>
        Assert.IsType<CodeHostResult<Uri>.Found>(await jobs.Fake.CloneAddress(User, FeatureJobs.Repo, Ct)).Value.LocalPath;

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
}
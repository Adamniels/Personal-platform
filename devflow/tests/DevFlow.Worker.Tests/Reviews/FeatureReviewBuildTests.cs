using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Reviews;

using FeatureJobs = DevFlow.Tests.Jobs.FeatureJobs;
using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A whole feature's review on the worker, in process against Dev flow with the fake code host's
/// real git: the reviewer gets a copy at the feature's head and, beside it as files, the feature's
/// own changes from the merge base with the default branch, its parent, its cards, the check's
/// result, and, apart, the remerge diff of each conflict an agent resolved since the feature's last
/// review. It runs read only, and nothing is committed or pushed. See "Reviewing a feature" in
/// devflow/README.md.
/// </summary>
public sealed class FeatureReviewBuildTests
{
    private const int Parent = 10;

    private const string Branch = "feature-10-greet-everyone";

    private const string ParentBody = "Everyone is greeted on the front page.";

    /// <summary>The feature and its one card, as the owner wrote them.</summary>
    private static IReadOnlyList<CodeHost.FakeIssue> Feature() =>
        [FeatureJobs.Parent(Parent, "Greet everyone", [11], ParentBody), FeatureJobs.Sub(11, "Greet the feature")];

    /// <summary>A verdict in its form, approve with nothing found.</summary>
    private static JsonObject Approve() => new()
    {
        ["verdict"] = "approve",
        ["summary"] = "The feature greets everyone, as its parent asks.",
        ["findings"] = new JsonArray(),
        ["for_owner"] = new JsonArray(),
    };

    /// <summary>
    /// The feature's branch made from <c>main</c> and given the card's file; then <c>main</c> moved
    /// on with a file of its own, and brought into the feature's branch as a merge, as its end does;
    /// the card done, and the feature reviewing at the merge, its pull request open and its branch
    /// watched there, as the minute that decides its review leaves it. The merge's id.
    /// </summary>
    private static async Task<string> Reviewable(Bench bench)
    {
        var first = (await bench.RemoteHead("main"))!;
        await Bench.Git(bench.Remote, "branch", Branch, "main");
        await bench.PushAsSomeoneElse(Branch, Branch, "Greet the feature", ("feature.txt", "the feature's own\n"));
        var main = await bench.PushAsSomeoneElse("main", "main", "Main moves on", ("main.txt", "main's own\n"));

        // Bringing main in, a merge commit on the feature's branch.
        var clone = Path.Combine(bench.Root, "bring-in");
        await Bench.Git(bench.Root, "clone", "--quiet", "--branch", Branch, bench.Remote, clone);
        await Bench.Git(clone, [.. Bench.Someone, "merge", "--quiet", "--no-edit", "origin/main"]);
        await Bench.Git(clone, "push", "--quiet", "origin", $"HEAD:refs/heads/{Branch}");
        var merge = await Bench.Git(clone, "rev-parse", "HEAD");

        var jobs = bench.Jobs;
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'reviewing', reason = NULL, branch = @branch, branch_base_commit = @first, brought_in_commit = @main,
                                pull_request_number = 5, pull_request_url = 'https://github.com/Adamniels/Personal-platform/pull/5',
                                review_commit = @merge, changed_at = @now
            WHERE project_id = @project AND number = @parent;
            UPDATE cards SET state = 'done' WHERE project_id = @project AND number = 11;
            """,
            new { project = jobs.Project, parent = Parent, branch = Branch, first, main, merge, now = jobs.Routes.Time.GetUtcNow() },
            cancellationToken: Ct));
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE branch_watches SET last_commit = @merge
            WHERE project_id = @project AND feature_id = (SELECT id FROM features WHERE project_id = @project AND number = @parent);
            UPDATE branch_watches SET last_commit = @main WHERE project_id = @project AND feature_id IS NULL;
            """,
            new { project = jobs.Project, parent = Parent, merge, main },
            cancellationToken: Ct));
        return merge;
    }

    /// <summary>A conflict run of the feature that succeeded <paramref name="minutesAgo"/> minutes
    /// ago, with <paramref name="remerge"/> as its remerge diff.</summary>
    private static async Task ConflictRun(Bench bench, string head, int minutesAgo, string remerge)
    {
        await using var admin = await bench.Jobs.Routes.Database.OpenAdmin(Ct);
        var at = bench.Jobs.Routes.Time.GetUtcNow().AddMinutes(-minutesAgo);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, check_command, from_commit, default_commit,
                              head_commit, started_at, finished_at, result, remerge_diff)
            VALUES (@id, @project, @user, @parent, @worker, 'conflict', @branch, 'main', 'true', @head, @head, @head, @at, @at, 'succeeded', @remerge)
            """,
            new { id = Guid.CreateVersion7(at), project = bench.Jobs.Project, user = Jobs.User, parent = Parent, worker = Jobs.FirstWorker, branch = Branch, head, at, remerge },
            cancellationToken: Ct));
    }

    /// <summary>An earlier review of the feature, <paramref name="minutesAgo"/> minutes ago, of
    /// <paramref name="commit"/>.</summary>
    private static async Task EarlierReview(Bench bench, string commit, int minutesAgo)
    {
        await using var admin = await bench.Jobs.Routes.Database.OpenAdmin(Ct);
        var at = bench.Jobs.Routes.Time.GetUtcNow().AddMinutes(-minutesAgo);
        var id = Guid.CreateVersion7(at);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, head_commit, started_at, finished_at, result)
            VALUES (@id, @project, @user, @parent, @worker, 'feature_review', @branch, 'main', @commit, @at, @at, 'succeeded');
            INSERT INTO feature_reviews (run_id, project_id, user_id, feature_number, reviewed_commit, verdict, summary, findings, for_owner,
                                         pull_request_number, reviewed_at, posted_at)
            VALUES (@id, @project, @user, @parent, @commit, 'approve', 'Fine.', '[]', '[]', 5, @at, @at);
            """,
            new { id, project = bench.Jobs.Project, user = Jobs.User, parent = Parent, worker = Jobs.FirstWorker, branch = Branch, commit, at },
            cancellationToken: Ct));
    }

    /// <summary>The review's folder as the agent finds it: each file's name and text.</summary>
    private static ScriptedAgent Reading(Dictionary<string, string> files, Action<string>? copyHead = null) => new(
        async (run, _) =>
        {
            foreach (var file in Directory.GetFiles(Path.Combine(run.WorkingDirectory, "review")))
            {
                files[Path.GetFileName(file)] = await File.ReadAllTextAsync(file, Ct);
            }

            copyHead?.Invoke(await Bench.Git(Path.Combine(run.WorkingDirectory, "copy"), "rev-parse", "HEAD"));
        },
        () => AgentResult.Success(Approve(), [], "", "oauth_token"));

    [Fact]
    public async Task TheDiffIsTheFeaturesOwn()
    {
        await using var bench = await Bench.Start(check: "echo all 3 checks passed", cards: Feature());
        var merge = await Reviewable(bench);
        var job = await bench.NextJob();
        Assert.Equal(Job.FeatureReview, job.GetProperty("kind").GetString());
        Assert.Equal(merge, job.GetProperty("to_review_feature").GetProperty("commit").GetString());

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string? copyHead = null;
        var agent = Reading(files, head => copyHead = head);
        await bench.Review(agent).Run(job, Ct);

        // The copy is at the feature's head, and the folder beside it holds what Dev flow gives,
        // with no remerge diff, since no conflict was resolved.
        Assert.Equal(merge, copyHead);
        Assert.Equal(["cards.md", "check.txt", "diff.patch", "feature.md"], files.Keys.Order(StringComparer.Ordinal));

        // The diff is the feature's own, from where it parted from main: the card's file, and none
        // of main's change that was brought in.
        var diff = files["diff.patch"];
        Assert.Contains("+++ b/feature.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("main.txt", diff, StringComparison.Ordinal);

        // The parent as picked, and the card with its handover note.
        Assert.Equal($"#{Parent} Greet everyone\n\n{ParentBody}\n", files["feature.md"]);
        Assert.Contains("#11 Greet the feature", files["cards.md"], StringComparison.Ordinal);

        // Read only, with the reviewer's form; the verdict went back for the commit the worker
        // checked out, and is kept for it.
        Assert.Equal(AgentAccess.ReadOnly, agent.Given!.Access);
        Assert.True(JsonNode.DeepEquals(ReviewVerdict.Schema(), JsonNode.Parse(agent.Given.ReportSchema)));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal(("succeeded", merge), ((string)sent["outcome"]!, (string)sent["head_commit"]!));
        Assert.Null(sent["check"]);
        await using var admin = await bench.Jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(merge, await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT reviewed_commit FROM feature_reviews WHERE project_id = @project", new { project = bench.Jobs.Project }, cancellationToken: Ct)));

        // Nothing was pushed, and nothing of the job is left on the machine.
        Assert.Equal(merge, await bench.RemoteHead(Branch));
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Handover));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task AConflictsResolutionIsGivenApart()
    {
        await using var bench = await Bench.Start(cards: Feature());
        var merge = await Reviewable(bench);

        // A conflict resolved before the feature's last review was read by it; the one since was
        // not.
        await ConflictRun(bench, merge, minutesAgo: 30, "diff --git a/old.txt b/old.txt\nremerge the older resolution\n");
        await EarlierReview(bench, (await bench.RemoteHead("main"))!, minutesAgo: 20);
        const string Since = "diff --git a/README.md b/README.md\nremerge CONFLICT (content): Merge conflict in README.md\n-<<<<<<< 4f2a\n+Hello from both\n";
        await ConflictRun(bench, merge, minutesAgo: 10, Since);

        var job = await bench.NextJob();
        Assert.Equal(Job.FeatureReview, job.GetProperty("kind").GetString());
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        await bench.Review(Reading(files)).Run(job, Ct);

        // Beside the diff, the resolution since the last review, and only that one.
        Assert.Contains("diff.patch", files.Keys);
        var remerge = files["remerge.patch"];
        Assert.Contains(Since, remerge, StringComparison.Ordinal);
        Assert.DoesNotContain("the older resolution", remerge, StringComparison.Ordinal);
    }
}
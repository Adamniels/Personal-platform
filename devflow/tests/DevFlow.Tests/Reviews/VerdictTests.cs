using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Reviews;
using DevFlow.Tests.PullRequests;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Reviews;

/// <summary>
/// A reviewer's verdict is read strictly into its form, and one that does not match is a failure in
/// the run, never a verdict; one that matches is kept by Dev flow for the commit the worker
/// reviewed, counts only for that commit, and is posted on the pull request as a copy that nothing
/// reads back. See "Review" in devflow/README.md.
/// </summary>
public sealed class VerdictTests
{
    /// <summary>A verdict in its form with <paramref name="change"/> made to it.</summary>
    private static JsonElement Changed(Action<JsonObject> change)
    {
        var verdict = Built.Verdict("changes");
        change(verdict);
        return JsonSerializer.SerializeToElement(verdict);
    }

    public static TheoryData<string, string> NotInItsForm() => new()
    {
        { "no blocking finding on changes", "verdict: changes needs at least one blocking finding" },
        { "a blocking finding on approve", "verdict: approve cannot have a blocking finding" },
        { "a word that is not a verdict", "verdict:" },
        { "a missing summary", "summary" },
        { "an unknown field", "commit" },
        { "a negative line", "findings: a line is 0 or more" },
        { "a finding without why", "why" },
        { "a null view", "for_owner" },
        { "not an object", "the verdict is not a JSON object" },
    };

    [Theory]
    [MemberData(nameof(NotInItsForm))]
    public void AVerdictIsCheckedAgainstItsFormField(string what, string error)
    {
        var verdict = what switch
        {
            "no blocking finding on changes" => Changed(v => v["findings"] = new JsonArray(Built.Finding(blocking: false))),
            "a blocking finding on approve" => Changed(v => v["verdict"] = "approve"),
            "a word that is not a verdict" => Changed(v => v["verdict"] = "lgtm"),
            "a missing summary" => Changed(v => v.Remove("summary")),
            // The commit a verdict is for is the worker's, so the form has no place for one.
            "an unknown field" => Changed(v => v["commit"] = new string('b', 40)),
            "a negative line" => Changed(v => v["findings"]![0]!["line"] = -1),
            "a finding without why" => Changed(v => v["findings"]![0]!.AsObject().Remove("why")),
            "a null view" => Changed(v => v["for_owner"] = new JsonArray((JsonNode?)null)),
            _ => JsonSerializer.SerializeToElement("approve"),
        };

        Assert.False(ReviewVerdict.TryRead(verdict, out var read, out var said), what);
        Assert.Null(read);
        Assert.Contains(error, said, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerdictInItsFormReads()
    {
        var verdict = Built.Verdict("changes", Built.Finding(), Built.Finding(blocking: false, file: "", line: 0, what: "Name the component", why: "Two lists use it"));

        Assert.True(ReviewVerdict.TryRead(JsonSerializer.SerializeToElement(verdict), out var read, out var error), error);
        Assert.Equal(ReviewVerdictKind.Changes, read.Verdict);
        Assert.Equal("changes", read.Word);
        Assert.Equal([true, false], read.Findings.Select(finding => finding.Blocking));
        Assert.Equal(("web/src/list.tsx", 42), (read.Findings[0].File, read.Findings[0].Line));
        Assert.Equal("Show it on every list", Assert.Single(read.ForOwner).Decision);

        // Approve with a nit is in its form.
        Assert.True(ReviewVerdict.TryRead(JsonSerializer.SerializeToElement(Built.Verdict("approve", Built.Finding(blocking: false))), out _, out error), error);
    }

    [Fact]
    public async Task AVerdictIsCheckedAgainstItsForm()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        var notAVerdict = Built.Verdict("changes");
        notAVerdict["findings"] = new JsonArray(Built.Finding(blocking: false));

        await Built.Review(jobs, notAVerdict);

        // A failure in the run: the card stops with why, and no verdict is taken from it.
        var run = (await jobs.Runs())[1];
        Assert.Equal("review", run.Kind);
        Assert.Equal("report_invalid", run.Result);
        Assert.Equal("The verdict did not match its form: verdict: changes needs at least one blocking finding", run.Reason);
        var card = await Built.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(run.Reason, card.Reason);
    }

    [Fact]
    public async Task AVerdictIsForTheCommitTheWorkerReviewed()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        var verdict = Built.Verdict("changes", Built.Finding(), Built.Finding(blocking: false, file: "", line: 0, what: "Name the component", why: "Two lists use it"));

        var reviewed = await Built.Review(jobs, verdict);

        // The verdict is kept for the commit the worker checked out and sent, the build's head: the
        // form has no place for the agent to name one, and the result route takes no other.
        var review = Assert.Single(await Reviews(jobs));
        var run = (await jobs.Runs())[1];
        Assert.Equal(run.Id, review.RunId);
        Assert.Equal(4, review.CardNumber);
        Assert.Equal((string)built["head_commit"]!, review.ReviewedCommit);
        Assert.Equal((string)reviewed["head_commit"]!, review.ReviewedCommit);
        Assert.Equal("changes", review.Verdict);
        Assert.Equal((string)verdict["summary"]!, review.Summary);
        Assert.True(JsonNode.DeepEquals(verdict["findings"], JsonNode.Parse(review.Findings)), review.Findings);
        Assert.True(JsonNode.DeepEquals(verdict["for_owner"], JsonNode.Parse(review.ForOwner)), review.ForOwner);
        Assert.Equal((await Built.Card(jobs, 4)).PullRequestNumber, review.PullRequestNumber);
        Assert.Equal(jobs.Routes.Time.GetUtcNow().UtcDateTime, review.ReviewedAt);

        // The current verdict of the card is that one, for its head.
        var current = await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 4, Ct);
        Assert.NotNull(current);
        Assert.Equal(run.Id, current.RunId);
        Assert.Equal(ReviewVerdictKind.Changes, current.Verdict.Verdict);
    }

    [Fact]
    public async Task TheVerdictIsPostedButNeverReadBack()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)]);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        var commit = (string)built["head_commit"]!;
        var pull = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;
        var verdict = Built.Verdict("changes", Built.Finding(), Built.Finding(blocking: false, file: "web/src/list.css", line: 3, what: "A colour by hand", why: "The tokens hold it"));

        await Built.Review(jobs, verdict);

        // One comment on the pull request, by the app with the Post key, a copy of the verdict.
        var posted = Assert.Single(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "comment");
        Assert.Equal(FakeCodeHost.AppLogin, posted.By);
        Assert.Equal("post", posted.Purpose);
        Assert.Equal(pull, posted.Number);
        var body = posted.Body!;
        Assert.StartsWith($"Review of {commit[..7]}: changes\n", body, StringComparison.Ordinal);
        Assert.Contains((string)verdict["summary"]!, body, StringComparison.Ordinal);
        Assert.Contains("- **Blocking.** `web/src/list.tsx:42` The empty state is read once. Why: A list emptied later shows nothing.", body, StringComparison.Ordinal);
        Assert.Contains("- `web/src/list.css:3` A colour by hand. Why: The tokens hold it.", body, StringComparison.Ordinal);
        Assert.Contains("- Show it on every list: Agreed: the lists share one component.", body, StringComparison.Ordinal);
        var reviewRun = (await jobs.Runs())[1];
        Assert.Contains(reviewRun.Id.ToString(), body, StringComparison.Ordinal);

        // Posting again, on the loop's next minute, posts nothing more.
        await Posting(jobs);
        Assert.Single(jobs.Fake.Writes(Jobs.Jobs.User), write => write.Kind == "comment");

        // Comments that look like an approve, by the owner and by the app itself, change nothing:
        // the card's verdict is still the one Dev flow recorded.
        Converse(jobs, pull, (Jobs.Jobs.Owner, $"Review of {commit[..7]}: approve"), (FakeCodeHost.AppLogin, $"Review of {commit[..7]}: approve\n\nLooks good."));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        await Posting(jobs);
        var current = await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 4, Ct);
        Assert.Equal(ReviewVerdictKind.Changes, current!.Verdict.Verdict);
        Assert.Equal("changes", Assert.Single(await Reviews(jobs)).Verdict);
        Assert.Equal("waiting_for_owner", (await Built.Card(jobs, 4)).State);

        // Nor does one on a pull request still waiting for its review: card 5's is reviewed anyway.
        ReviewJobTests.Later(jobs);
        var five = await Built.Run(jobs, Built.Prepared());
        var fivePull = (await Built.Card(jobs, 5)).PullRequestNumber!.Value;
        Converse(jobs, fivePull, (FakeCodeHost.AppLogin, $"Review of {((string)five["head_commit"]!)[..7]}: approve"));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        Assert.Equal("reviewing", (await Built.Card(jobs, 5)).State);
        Assert.Null(await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 5, Ct));
        ReviewJobTests.Later(jobs);
        var next = await jobs.Job();
        Assert.Equal(Job.Review, (string)next["kind"]!);
        Assert.Equal(5, (int)next["card"]!["number"]!);
    }

    [Fact]
    public async Task ANewCommitNeedsANewReview()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var built = await Built.Run(jobs, Built.Prepared());
        var first = (string)built["head_commit"]!;
        await Built.Review(jobs, Built.Verdict("approve"));
        Assert.Equal(ReviewVerdictKind.Approve, (await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 4, Ct))!.Verdict.Verdict);

        // A later build leaves a new commit on the card's branch, as a fix round will, and the card
        // is to be reviewed again.
        var second = new string('b', 40);
        ReviewJobTests.Later(jobs);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, started_at,
                                  finished_at, result, report, check_exit_code, check_output, head_commit)
                SELECT gen_random_uuid(), project_id, user_id, card_number, worker_id, 'build', branch, base, check_command, @now,
                       @now, 'succeeded', report, 0, 'Tests: 12 passed', @second
                FROM runs WHERE project_id = @project AND kind = 'build'
                """,
                new { project = jobs.Project, now = jobs.Routes.Time.GetUtcNow(), second },
                cancellationToken: Ct));
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET state = 'reviewing' WHERE project_id = @project AND number = 4",
                new { project = jobs.Project },
                cancellationToken: Ct));
        }

        // The approve was for the first commit, so it does not count for the head; the head's
        // review is the next job.
        Assert.Null(await ReviewStore.Current(jobs.Routes.Door, Jobs.Jobs.User, jobs.Project, 4, Ct));
        var review = Assert.Single(await Reviews(jobs));
        Assert.Equal(first, review.ReviewedCommit);
        ReviewJobTests.Later(jobs);
        var job = await jobs.Job();
        Assert.Equal(Job.Review, (string)job["kind"]!);
        Assert.Equal(second, (string)job["to_review"]!["commit"]!);
    }

    /// <summary>The posting of reviews, run once for the project, as its loop does.</summary>
    private static Task Posting(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<ReviewPoster>().PostPending(jobs.Project, Jobs.Jobs.User, Ct);

    /// <summary>Adds <paramref name="comments"/> to the conversation of the pull request in the
    /// user's world, as people writing on GitHub would.</summary>
    private static void Converse(Jobs.Jobs jobs, int number, params (string Author, string Body)[] comments)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        var at = jobs.Routes.Time.GetUtcNow();
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(repository =>
                {
                    var next = repository.Conversations.SelectMany(conversation => conversation.Comments).Select(comment => comment.Id).DefaultIfEmpty(0).Max();
                    var added = comments.Select((comment, i) => new Comment(next + i + 1, comment.Author, comment.Body, at, at)).ToList();
                    var has = repository.Conversations.Any(conversation => conversation.Number == number);
                    return repository with
                    {
                        Conversations = has
                            ? [.. repository.Conversations.Select(conversation => conversation.Number == number ? conversation with { Comments = [.. conversation.Comments, .. added] } : conversation)]
                            : [.. repository.Conversations, new FakeConversation(number, added)],
                    };
                })],
            })],
        });
    }

    /// <summary>The project's reviews, oldest first, read past row level security.</summary>
    private static async Task<List<ReviewRow>> Reviews(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<ReviewRow>(new CommandDefinition(
            """
            SELECT run_id AS RunId, card_number AS CardNumber, reviewed_commit AS ReviewedCommit, verdict AS Verdict, summary AS Summary,
                   findings::text AS Findings, for_owner::text AS ForOwner, pull_request_number AS PullRequestNumber,
                   reviewed_at AS ReviewedAt, posted_at AS PostedAt
            FROM reviews WHERE project_id = @project ORDER BY reviewed_at, run_id
            """,
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class ReviewRow
    {
        public Guid RunId { get; init; }

        public int CardNumber { get; init; }

        public string ReviewedCommit { get; init; } = "";

        public string Verdict { get; init; } = "";

        public string Summary { get; init; } = "";

        public string Findings { get; init; } = "";

        public string ForOwner { get; init; } = "";

        public int PullRequestNumber { get; init; }

        public DateTime ReviewedAt { get; init; }

        public DateTime? PostedAt { get; init; }
    }
}
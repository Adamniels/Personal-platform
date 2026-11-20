using System.Globalization;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

using static DevFlow.Tests.Features.FeaturePage;

namespace DevFlow.Tests.Features;

/// <summary>
/// <c>POST /devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/fine</c>:
/// answering a decision a card of a feature marked for the owner "Fine as it is", kept in
/// <c>decision_answers</c> and never written to the code host. See "A feature's page" in
/// devflow/README.md.
/// </summary>
public sealed class DecisionAnswerTests
{
    private static Task<HttpContext> Fine(Jobs.Jobs jobs, Guid project, Guid run, int position, int feature = Parent) =>
        jobs.Routes.App.SendJson("POST", FinePath(project, run, position, feature), Contract.Token, "{}");

    private static async Task<JsonNode> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response))!;

    /// <summary>The one decision the first card's latest report marked for the owner, as the page
    /// shows it.</summary>
    private static async Task<JsonNode> Decision(Jobs.Jobs jobs)
    {
        var (_, story) = await Built.Get(jobs, PathOf(jobs.Project));
        return Assert.Single(story!["cards"]![0]!["decisions"]!.AsArray())!;
    }

    [Fact]
    public async Task FineAsItIsIsKept()
    {
        await using var jobs = await Feature();
        await Landed(jobs);
        var decision = await Decision(jobs);
        var run = Guid.Parse((string)decision["run_id"]!);
        var position = (int)decision["position"]!;
        var answeredAt = jobs.Routes.Time.GetUtcNow();

        var answered = await Fine(jobs, jobs.Project, run, position);

        Assert.Equal(StatusCodes.Status200OK, answered.Response.StatusCode);
        var answer = await Json(answered);
        Assert.Equal("fine", (string)answer["answer"]!);
        Assert.Equal(answeredAt, DateTimeOffset.Parse((string)answer["answered_at"]!, CultureInfo.InvariantCulture));
        var kept = Assert.Single(await Answers(jobs));
        Assert.Equal((jobs.Project, User, First, run, position, "fine"), (kept.ProjectId, kept.UserId, kept.CardNumber, kept.RunId, kept.Position, kept.Answer));

        // The page shows it answered.
        var shown = (await Decision(jobs))["answer"]!;
        Assert.Equal("fine", (string)shown["answer"]!);
        Assert.Equal(answeredAt, DateTimeOffset.Parse((string)shown["answered_at"]!, CultureInfo.InvariantCulture));

        // Answering again changes nothing: the answer is kept as first given.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(10));
        var again = await Fine(jobs, jobs.Project, run, position);
        Assert.Equal(StatusCodes.Status200OK, again.Response.StatusCode);
        Assert.Equal(answeredAt, DateTimeOffset.Parse((string)(await Json(again))["answered_at"]!, CultureInfo.InvariantCulture));
        Assert.Equal(answeredAt, new DateTimeOffset(DateTime.SpecifyKind(Assert.Single(await Answers(jobs)).AnsweredAt, DateTimeKind.Utc)));

        // A decision of another user's feature is 404, and nothing is kept for it.
        var bobs = await BobsFeature(jobs);
        Assert.Equal(StatusCodes.Status404NotFound, (await Fine(jobs, bobs, run, position)).Response.StatusCode);
        Assert.Single(await Answers(jobs));
    }

    [Fact]
    public async Task OnlyADecisionThePageShowsIsAnswered()
    {
        await using var jobs = await Feature();
        await Landed(jobs);
        var decision = await Decision(jobs);
        var run = Guid.Parse((string)decision["run_id"]!);

        // The report's decision that is not marked for the owner, one past its last, a run that is
        // not there, another feature's number and a project that is not there: each 404.
        foreach (var (project, at, position, feature) in new[]
        {
            (jobs.Project, run, 0, Parent), (jobs.Project, run, 2, Parent), (jobs.Project, Guid.CreateVersion7(), 1, Parent),
            (jobs.Project, run, 1, 99), (jobs.Project, run, 1, First), (Guid.CreateVersion7(), run, 1, Parent),
        })
        {
            Assert.Equal(StatusCodes.Status404NotFound, (await Fine(jobs, project, at, position, feature)).Response.StatusCode);
        }

        // A run whose report is no longer the card's latest is 409: the page showed an older one.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                CREATE TEMP TABLE newer AS SELECT * FROM runs WHERE id = @run;
                UPDATE newer SET id = gen_random_uuid(), started_at = started_at + interval '1 hour', finished_at = finished_at + interval '1 hour';
                INSERT INTO runs SELECT * FROM newer;
                """,
                new { run },
                cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status409Conflict, (await Fine(jobs, jobs.Project, run, 1)).Response.StatusCode);
        Assert.Empty(await Answers(jobs));

        // Without a token, 401.
        var anonymous = await jobs.Routes.App.SendJson("POST", FinePath(jobs.Project, run, 1), null, "{}");
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
    }

    [Fact]
    public async Task AnAnswerIsNeverWrittenToTheCodeHost()
    {
        await using var jobs = await Feature();
        await Landed(jobs);
        var decision = await Decision(jobs);
        var world = jobs.Fake.World(User);
        var writes = jobs.Fake.Writes(User).Count;
        var calls = jobs.Routes.Host.Calls.Count;

        var answered = await Fine(jobs, jobs.Project, Guid.Parse((string)decision["run_id"]!), (int)decision["position"]!);

        Assert.Equal(StatusCodes.Status200OK, answered.Response.StatusCode);
        Assert.Same(world, jobs.Fake.World(User));
        Assert.Equal(writes, jobs.Fake.Writes(User).Count);
        Assert.Equal(calls, jobs.Routes.Host.Calls.Count);
    }
}
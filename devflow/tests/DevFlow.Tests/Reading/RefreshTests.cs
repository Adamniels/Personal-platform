using System.Text.Json.Nodes;

using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <c>POST /devflow/api/projects/{id}/refresh</c>: asks for the caller's project to be read now,
/// through the same seam the loop uses, and answers once it is done, or that it is under way. See
/// "What waits" in devflow/README.md.
/// </summary>
public sealed class RefreshTests
{
    private static readonly World Waiting = new();

    [Fact]
    public async Task RefreshingReadsNowAndAnswersWhenItIsDone()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));

        var response = await routes.Refresh(project);

        // Done by the time it answers: read as the caller, and stored.
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal(1, routes.Host.Lists(Routes.User));
        Assert.Equal(42, Assert.Single(await Reading.StoredItems(routes.Database, project)).Number);
        var waited = JsonNode.Parse(await Harness.Body(await routes.App.Get(Routes.WaitingPath, Contract.Token)))!;
        Assert.Equal(42, (int)waited["items"]![0]!["number"]!);

        // A read that fails is still done: what failed is in the list, not in this answer.
        routes.Fake.SetUnavailable(Routes.User, true);
        Assert.Equal(StatusCodes.Status204NoContent, (await routes.Refresh(project)).Response.StatusCode);
        Assert.Equal(2, routes.Host.Lists(Routes.User));
    }

    [Fact]
    public async Task AnotherUsersProjectIs404()
    {
        await using var routes = await Routes.Start();
        var bobs = await routes.Project(Routes.Bob);
        routes.Fake.Replace(Routes.Bob, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));

        foreach (var project in new[] { bobs, Guid.NewGuid() })
        {
            var response = await routes.Refresh(project);

            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
            Assert.StartsWith("application/problem+json", response.Response.ContentType, StringComparison.Ordinal);
            Assert.Equal("No such project", (string)JsonNode.Parse(await Harness.Body(response))!["title"]!);
        }

        // Nothing was read, for anyone.
        Assert.Empty(routes.Host.Calls);
        Assert.Null(await Reading.StoredRead(routes.Database, bobs));
    }

    [Fact]
    public async Task ASlowReadIsAcceptedAfterTenSeconds()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        routes.Host.Hold = hold;

        var answer = routes.Refresh(project);
        await routes.Host.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Reading.Ct);
        // Under ten seconds on the app's clock it waits; the route may not be waiting yet when the
        // clock first moves, so it moves until it answers.
        routes.Time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(200, Reading.Ct);
        Assert.False(answer.IsCompleted);
        for (var tries = 0; !answer.IsCompleted && tries < 50; tries++)
        {
            routes.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(50, Reading.Ct);
        }

        Assert.Equal(StatusCodes.Status202Accepted, (await answer).Response.StatusCode);

        // The read goes on, and is stored when the host answers.
        routes.Host.Hold = null;
        hold.SetResult();
        await Reading.Until(async () => (await Reading.StoredItems(routes.Database, project)).Count == 1, "the read being stored");
    }

    [Fact]
    public async Task FromAnotherOriginItIs403()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        var response = await routes.App.Send("POST", $"/devflow/api/projects/{project}/refresh", Contract.Token, ("Origin", "https://evil.test"));

        Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
        Assert.Empty(routes.Host.Calls);
    }
}
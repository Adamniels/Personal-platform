using DevFlow.Decisions;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevFlow.Tests.Merging;

/// <summary>
/// Checking a card right after its review approves it: the result route asks for the project's
/// merge check without waiting for it, and the loop behind <see cref="MergeRequests"/> runs it. See
/// "The merge check" in devflow/README.md.
/// </summary>
public sealed class MergeRequestTests
{
    [Fact]
    public async Task TheResultRouteAsksForACheck()
    {
        var requests = new MergeRequests(enabled: true);
        await using var jobs = await Jobs.Jobs.Start(configure: builder => builder.Services.AddSingleton(requests));
        var job = await jobs.Job();

        var response = await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)job["branch"]!));

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.True(requests.Reader.TryRead(out var asked));
        Assert.Equal((jobs.Project, Jobs.Jobs.User), asked);
        Assert.False(requests.Reader.TryRead(out _));
    }

    [Fact]
    public void WithTheLoopsOffARequestIsDropped()
    {
        var requests = new MergeRequests(enabled: false);
        requests.Request(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(requests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task TheLoopChecksWhatIsAsked()
    {
        await using var jobs = await Jobs.Jobs.Start();
        var built = await Merged.Approved(jobs, green: false);
        Merged.Ci(jobs, (string)built["head_commit"]!, Merged.GreenRuns);
        var requests = new MergeRequests(enabled: true);
        var services = jobs.Routes.App.Services;
        using var loop = new MergeRequestLoop(requests, services.GetRequiredService<MergeChecker>(), services.GetRequiredService<DecisionIssues>(), NullLogger<MergeRequestLoop>.Instance);
        await loop.StartAsync(Merged.Ct);
        try
        {
            requests.Request(jobs.Project, Jobs.Jobs.User);
            for (var i = 0; i < 200 && (await Built.Card(jobs, Merged.Card)).State != "done"; i++)
            {
                await Task.Delay(50, Merged.Ct);
            }
        }
        finally
        {
            await loop.StopAsync(Merged.Ct);
        }

        Assert.Equal("done", (await Built.Card(jobs, Merged.Card)).State);
        Assert.Single(Merged.AppMerges(jobs));
    }
}
using System.Text.Json;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;
using DevFlow.Worker.Pages;
using DevFlow.Worker.Review;

namespace DevFlow.Worker;

/// <summary>
/// The worker, from its settings to the end of its loop: a wrong or missing setting ends it with 2
/// and a line naming the setting, before anything is sent. See "The worker" in devflow/README.md.
/// </summary>
internal static class WorkerProgram
{
    /// <param name="handler">Dev flow's handler, which tests give; a real connection
    /// otherwise.</param>
    /// <param name="time">The clock, which tests move; the system's otherwise.</param>
    /// <param name="name">The worker's name; the machine's, its container's hostname,
    /// otherwise.</param>
    public static async Task<int> Run(
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken stopping,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (WorkerSettings.Load(environment, out var problem) is not { } settings)
        {
            await error.WriteLineAsync(problem);
            return WorkerLoop.WrongSetting;
        }

        time ??= TimeProvider.System;
        var log = new WorkerLog(output, time);
        var identity = new WorkerIdentity(Guid.CreateVersion7(time.GetUtcNow()), name ?? Environment.MachineName, settings.RecipeVersion, settings.BaseImage);
        log.Write($"Dev flow worker {identity.Id} ({identity.Name}), recipe {identity.RecipeVersion} on {identity.BaseImage}, for {settings.DevFlowUrl}.");
        using var http = DevFlowClient.CreateHttpClient(settings, handler);
        var devFlow = new DevFlowClient(http);
        var tools = Tools(time, log);
        var build = new BuildJob(devFlow, identity.Id, tools, time, log);
        var review = new ReviewJob(devFlow, identity.Id, tools, time, log);
        var loop = new WorkerLoop(devFlow, identity, (job, stopping) => RunJob(job, build, review, log, stopping), time, log, error);
        return await loop.Run(stopping);
    }

    /// <summary>Runs the job by its kind: a build, or a fix, which is carried as one, or a review;
    /// a job of any other kind is left, and Dev flow finds its run lost.</summary>
    internal static Task RunJob(JsonElement job, BuildJob build, ReviewJob review, WorkerLog log, CancellationToken stopping)
    {
        var kind = job.ValueKind == JsonValueKind.Object && job.TryGetProperty("kind", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;
        switch (kind)
        {
            case Job.Build or Job.Fix:
                return build.Run(job, stopping);
            case Job.Review:
                return review.Run(job, stopping);
            default:
                log.Write($"The job is of a kind this worker does not run, {kind ?? "none"}; it is left.");
                return Task.CompletedTask;
        }
    }

    /// <summary>What a job runs with in the base image: Claude Code as <c>agent</c>, root's
    /// processes as the worker's own, the image's folders, and the project's start as
    /// <c>agent</c>.</summary>
    internal static JobTools Tools(TimeProvider time, WorkerLog log)
    {
        var user = AgentUser.Default;
        var asAgent = new SystemProcessStarter(user.Name, time);
        var pages = PageTools.For(user.Name, time);
        var claude = ClaudeCodeOptions.Default with { Home = user.Home, Path = user.Path, Lang = user.Lang, BrowsersPath = pages.BrowsersPath };
        return new JobTools(new ClaudeCodeAgent(asAgent, claude, log), new SystemProcessStarter(null, time), asAgent, JobFolders.Default, user, pages);
    }
}
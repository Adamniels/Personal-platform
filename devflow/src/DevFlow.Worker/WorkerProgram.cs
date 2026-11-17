using System.Text.Json;

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
        var loop = new WorkerLoop(new DevFlowClient(http), identity, NoJobsYet(log), time, log, error);
        return await loop.Run(stopping);
    }

    /// <summary>
    /// What a job does until the slices that build and push define one: Dev flow hands none out
    /// yet, so one arriving is logged and left, and the worker asks for the next.
    /// </summary>
    private static JobHandler NoJobsYet(WorkerLog log) => (JsonElement _, CancellationToken _) =>
    {
        log.Write("This worker cannot run jobs yet; the job is left.");
        return Task.CompletedTask;
    };
}
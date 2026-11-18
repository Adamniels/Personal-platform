using System.Text.Json;

using DevFlow.Protocol;

namespace DevFlow.Worker;

/// <summary>Runs a job Dev flow handed out, its answer as it came. What a job holds is defined
/// with handing out work; until then Dev flow hands none out.</summary>
internal delegate Task JobHandler(JsonElement job, CancellationToken cancellationToken);

/// <summary>Who the worker is, as it says hello: the id it made when it started, its container's
/// hostname, and its image's versions.</summary>
internal sealed record WorkerIdentity(Guid Id, string Name, string RecipeVersion, string BaseImage)
{
    public HelloRequest Hello => new(Id, Name, RecipeVersion, BaseImage);

    public NextJobRequest Next => new(Id);
}

/// <summary>
/// The worker's loop. It says hello, then asks for work; on <c>204</c> it waits and asks again,
/// 5 seconds doubling to at most 60, and after a job it asks at once and the wait starts again from
/// 5. Dev flow away, by no answer, a timeout or a <c>5xx</c>, is a wait of the same length and
/// another try, never an exit, and the next answer from it begins with hello again. A wrong key,
/// <c>401</c>, ends it with 2 and a line naming <c>DEVFLOW_PROJECT_KEY</c>; stopping ends it with
/// 0. Time is the <see cref="TimeProvider"/> given, so tests move it. See "The worker" in
/// devflow/README.md.
/// </summary>
internal sealed class WorkerLoop(DevFlowClient devFlow, WorkerIdentity identity, JobHandler runJob, TimeProvider time, WorkerLog log, TextWriter error)
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    /// <summary>Exit code for a setting or a key that is wrong.</summary>
    public const int WrongSetting = 2;

    private TimeSpan _wait = FirstWait;

    /// <summary>Runs until <paramref name="stopping"/> or a wrong key, and answers the exit
    /// code.</summary>
    public async Task<int> Run(CancellationToken stopping)
    {
        try
        {
            return await Loop(stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            log.Write("Stopped.");
            return 0;
        }
    }

    private async Task<int> Loop(CancellationToken stopping)
    {
        var helloNeeded = true;
        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            var justSaidHello = false;
            if (helloNeeded)
            {
                var hello = await devFlow.Hello(identity.Hello, stopping);
                switch (hello.Kind)
                {
                    case AnswerKind.Done:
                        log.Write($"Said hello to Dev flow as {identity.Id} ({identity.Name}).");
                        helloNeeded = false;
                        justSaidHello = true;
                        break;
                    case AnswerKind.WrongKey:
                        return WrongKey();
                    case AnswerKind.Invalid:
                        // What the worker reports breaks Dev flow's rules, and will on every try.
                        await error.WriteLineAsync(
                            $"Dev flow refused this worker's hello: {hello.Detail}. Check {WorkerSettings.RecipeVersionVariable} and {WorkerSettings.BaseImageVariable}.");
                        return WrongSetting;
                    default:
                        await Wait($"{hello.Detail}", stopping);
                        continue;
                }
            }

            var next = await devFlow.NextJob(identity.Next, stopping);
            switch (next.Kind)
            {
                case AnswerKind.Done:
                    // Nothing to do, which needs no line in the log.
                    await Wait(null, stopping);
                    break;
                case AnswerKind.Job:
                    log.Write("Got a job.");
                    await runJob(next.Body, stopping);
                    _wait = FirstWait;
                    break;
                case AnswerKind.WrongKey:
                    return WrongKey();
                case AnswerKind.SayHelloFirst:
                    helloNeeded = true;
                    // At once, unless it was just said: then Dev flow disagrees with itself, and
                    // asking again at once would never stop.
                    if (justSaidHello)
                    {
                        await Wait("Dev flow asked for hello right after it", stopping);
                    }

                    break;
                default:
                    helloNeeded = true;
                    await Wait(next.Detail, stopping);
                    break;
            }
        }
    }

    /// <summary>Waits the current wait, after logging why when there is a reason, and doubles it
    /// for next time, up to <see cref="LongestWait"/>.</summary>
    private async Task Wait(string? reason, CancellationToken stopping)
    {
        if (reason is not null)
        {
            log.Write($"{reason}; trying again in {_wait.TotalSeconds:0} seconds.");
        }

        await Task.Delay(_wait, time, stopping);
        _wait = TimeSpan.FromTicks(Math.Min(_wait.Ticks * 2, LongestWait.Ticks));
    }

    private int WrongKey()
    {
        // The key itself is never printed, only the setting it came from.
        error.WriteLine(
            $"Dev flow refused the project's key (401). {WorkerSettings.KeyVariable} must be the project's current worker key: make a new one on the project's page in Dev flow and start the worker with it.");
        return WrongSetting;
    }
}
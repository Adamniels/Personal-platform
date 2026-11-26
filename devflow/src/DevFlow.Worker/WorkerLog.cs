using System.Globalization;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker;

/// <summary>
/// The worker's log: one line per event, its time in UTC first, to standard output, where
/// <c>docker logs</c> reads it. Nothing secret is ever written to it: no key, no token and no
/// header; and while a job carries the project's values, <see cref="Hiding"/>, each is scrubbed
/// from every line, as from what the worker sends. While a job's log is sent, <see cref="Sending"/>,
/// every line is a <see cref="RunLineKind.System"/> line of it too, so the job's log holds what the
/// worker did as well as what the agent and the check did.
/// </summary>
internal sealed class WorkerLog(TextWriter output, TimeProvider time)
{
    private ValueScrub _scrub = ValueScrub.None;
    private IRunLog? _run;

    public void Write(string message)
    {
        var line = Note(message);
        Volatile.Read(ref _run)?.Add(RunLineKind.System, line);
    }

    /// <summary>Writes a line to the worker's own log only, never to the job's: one about sending
    /// the job's log itself. The line as written, scrubbed.</summary>
    public string Note(string message)
    {
        var line = Volatile.Read(ref _scrub).Scrub(message);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{time.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ} {line}"));
        return line;
    }

    /// <summary>The log of the job that runs, where its agent's lines and its check's go; null
    /// between jobs.</summary>
    public IRunLog? Run => Volatile.Read(ref _run);

    /// <summary>Makes <paramref name="run"/> the job's log, <see cref="Run"/>, and adds every line
    /// written to it, until the answer is disposed, once the job's result is sent.</summary>
    public IDisposable Sending(IRunLog run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var before = Interlocked.Exchange(ref _run, run);
        return new Unsend(this, before);
    }

    private sealed class Unsend(WorkerLog log, IRunLog? before) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref log._run, before);
    }

    /// <summary>Scrubs <paramref name="values"/> from every line written until the answer is
    /// disposed, at the end of the job that carried them, which forgets them.</summary>
    public IDisposable Hiding(IReadOnlyList<JobValue>? values)
    {
        var before = Volatile.Read(ref _scrub);
        Volatile.Write(ref _scrub, new ValueScrub(values ?? []));
        return new Restore(this, before);
    }

    private sealed class Restore(WorkerLog log, ValueScrub before) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref log._scrub, before);
    }
}
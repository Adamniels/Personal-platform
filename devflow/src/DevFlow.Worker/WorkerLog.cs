using System.Globalization;

using DevFlow.Protocol;

namespace DevFlow.Worker;

/// <summary>
/// The worker's log: one line per event, its time in UTC first, to standard output, where
/// <c>docker logs</c> reads it. Nothing secret is ever written to it: no key, no token and no
/// header; and while a job carries the project's values, <see cref="Hiding"/>, each is scrubbed
/// from every line, as from what the worker sends.
/// </summary>
internal sealed class WorkerLog(TextWriter output, TimeProvider time)
{
    private ValueScrub _scrub = ValueScrub.None;

    public void Write(string message) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{time.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ} {Volatile.Read(ref _scrub).Scrub(message)}"));

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
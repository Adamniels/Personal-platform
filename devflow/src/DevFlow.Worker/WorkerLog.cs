using System.Globalization;

namespace DevFlow.Worker;

/// <summary>
/// The worker's log: one line per event, its time in UTC first, to standard output, where
/// <c>docker logs</c> reads it. Nothing secret is ever written to it: no key, no token and no
/// header.
/// </summary>
internal sealed class WorkerLog(TextWriter output, TimeProvider time)
{
    public void Write(string message) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{time.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ} {message}"));
}
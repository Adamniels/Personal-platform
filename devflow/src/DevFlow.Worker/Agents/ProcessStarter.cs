using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DevFlow.Worker.Agents;

/// <summary>A program to start: its arguments as a list, never through a shell, and exactly the
/// environment given, nothing of the worker's own.</summary>
/// <param name="Bytes">Whether its standard output is bytes rather than text: read as Latin-1, one
/// character for each byte, so <c>Encoding.Latin1.GetBytes</c> gives them back exactly.</param>
/// <param name="Lines">Where each line it writes goes as it is written, standard output's and
/// standard error's each with its stream named, without its line ending; null for none. Called
/// from the reader of each stream, so from two threads at once, and the outcome holds all of both
/// either way.</param>
/// <param name="Stop">Cancelled when the owner stopped the run the program is part of: the program
/// and everything it started get <c>SIGTERM</c>, and <c>SIGKILL</c> after
/// <see cref="SystemProcessStarter.StopGrace"/> whatever of them still runs, and the outcome says it
/// was stopped. Unlike the cancellation token, which ends it at once and throws.</param>
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, bool Bytes = false, Action<ProcessStream, string>? Lines = null, CancellationToken Stop = default);

/// <summary>Which of a program's two output streams a line came from.</summary>
internal enum ProcessStream
{
    Stdout,
    Stderr,
}

/// <summary>How a program ended, and what it wrote.</summary>
/// <param name="TimedOut">Whether it ran past its time limit and was ended.</param>
/// <param name="Stopped">Whether the owner stopped it, by its spec's <see cref="ProcessSpec.Stop"/>,
/// before it ended on its own; never also <paramref name="TimedOut"/>.</param>
internal sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Stopped = false);

/// <summary>Starts a program and waits for it, at most its time limit. The seam
/// <see cref="ClaudeCodeAgent"/>'s tests replace.</summary>
internal interface IProcessStarter
{
    Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken);
}

/// <summary>
/// Starts real processes. The environment is cleared and holds only what the spec gives, so a
/// variable the worker holds, such as an <c>ANTHROPIC_API_KEY</c>, never reaches the program.
/// Standard input is closed at once. Both output streams are read as they are written, so a spec's
/// line sink has each line while the program runs. Past the time limit the process and everything
/// it started are ended, and the outcome says so. Stopped by the owner, through the spec's
/// <see cref="ProcessSpec.Stop"/>, they are ended gently: <c>SIGTERM</c> to the process and every
/// process it started, as <see cref="ProcessTable"/> reads them then, and <c>SIGKILL</c>, after
/// <see cref="StopGrace"/>, to each of those still running and whatever it started since; the
/// outcome says it was stopped.
///
/// With <paramref name="runAs"/>, the program runs as that user through <c>setpriv</c>, from
/// util-linux, which keeps the environment as given; the worker runs as root and Claude Code
/// refuses <c>bypassPermissions</c> as root. How the image provides the user is "The base image
/// and the start commands".
/// </summary>
internal sealed class SystemProcessStarter(string? runAs, TimeProvider time) : IProcessStarter
{
    /// <summary>How long a stopped program and what it started have after <c>SIGTERM</c> before
    /// <c>SIGKILL</c>.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>How often, within <see cref="StopGrace"/>, the worker looks whether a stopped
    /// program has ended.</summary>
    private static readonly TimeSpan StopPoll = TimeSpan.FromMilliseconds(100);

    public async Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var start = new ProcessStartInfo
        {
            FileName = runAs is null ? spec.FileName : "setpriv",
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = spec.Bytes ? Encoding.Latin1 : null,
        };
        if (runAs is not null)
        {
            foreach (var argument in new[] { "--reuid", runAs, "--regid", runAs, "--init-groups", "--", spec.FileName })
            {
                start.ArgumentList.Add(argument);
            }
        }

        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{spec.FileName} did not start");
        process.StandardInput.Close();
        // Read to the end whatever happens, so a full pipe never stalls the program.
        var stdout = Read(process.StandardOutput, ProcessStream.Stdout, spec.Lines);
        var stderr = Read(process.StandardError, ProcessStream.Stderr, spec.Lines);

        using var limit = new CancellationTokenSource(timeLimit, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token, spec.Stop);
        var timedOut = false;
        var stopped = false;
        try
        {
            await process.WaitForExitAsync(either.Token);
        }
        catch (OperationCanceledException) when (spec.Stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The owner's stop, which wins over a time limit reached at the same moment.
            await EndGently(process);
            stopped = true;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        return new ProcessOutcome(process.ExitCode, await stdout, await stderr, timedOut, stopped);
    }

    /// <summary>
    /// Ends <paramref name="process"/> as a stop does: <c>SIGTERM</c> to it and every process it
    /// started; then, as soon as none of them runs or once <see cref="StopGrace"/> has passed,
    /// <c>SIGKILL</c> to each still running and whatever it started meanwhile, and the process
    /// waited for. Signalled as the worker itself, so a process that runs as the agent is reached.
    /// Without <c>ps</c> to read the processes by, the process tree is killed at once, as at a time
    /// limit.
    /// </summary>
    private async Task EndGently(Process process)
    {
        try
        {
            var signalled = (await ProcessTable.Read()).TreeOf([process.Id]);
            await ProcessTable.Signal("TERM", signalled);
            var until = time.GetUtcNow() + StopGrace;
            var table = await ProcessTable.Read();
            while (signalled.Any(table.Lives) && time.GetUtcNow() < until)
            {
                await Task.Delay(StopPoll, time);
                table = await ProcessTable.Read();
            }

            var left = table.TreeOf(signalled.Where(table.Lives));
            if (left.Count > 0)
            {
                await ProcessTable.Signal("KILL", left);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    /// <summary>
    /// The whole of <paramref name="reader"/>, exactly as written, read as it comes, with each line
    /// handed to <paramref name="lines"/> by a <see cref="LineSplitter"/>. A sink that throws is
    /// called no more, the stream is still read to its end, so the program never waits on a full
    /// pipe, and the exception is thrown then.
    /// </summary>
    private static async Task<string> Read(StreamReader reader, ProcessStream stream, Action<ProcessStream, string>? lines)
    {
        var whole = new StringBuilder();
        var splitter = lines is null ? null : new LineSplitter(stream, lines);
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            whole.Append(buffer, 0, read);
            splitter?.Append(buffer, read);
        }

        splitter?.End();
        if (splitter?.Failed is { } failed)
        {
            throw new InvalidOperationException($"the line sink of the program's {stream} failed", failed);
        }

        return whole.ToString();
    }
}

/// <summary>
/// The machine's processes as <c>ps</c> lists them at one moment, each with its parent and whether
/// it is a zombie, which has ended and only waits to be reaped: the worker runs as the container's
/// first process and reaps no process it did not start, so an orphan that ended stays listed so.
/// The same <c>ps</c> and <c>kill</c> run on Linux, in the image, from procps, and on the machines
/// the tests run on. A process id read here may be reused by the time it is signalled; within a
/// stop's 10 seconds, in a container whose ids only grow, that is not expected.
/// </summary>
internal sealed class ProcessTable
{
    private readonly Dictionary<int, (int Parent, bool Zombie)> _processes;

    private ProcessTable(Dictionary<int, (int Parent, bool Zombie)> processes) => _processes = processes;

    /// <summary>The processes running now, by <c>ps -A -o pid=,ppid=,stat=</c>.</summary>
    public static async Task<ProcessTable> Read()
    {
        var ps = new ProcessStartInfo("ps") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-A", "-o", "pid=,ppid=,stat=" })
        {
            ps.ArgumentList.Add(argument);
        }

        using var running = Process.Start(ps) ?? throw new InvalidOperationException("ps did not start");
        var errors = running.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = await running.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await running.WaitForExitAsync(CancellationToken.None);
        await errors;
        if (running.ExitCode != 0)
        {
            throw new InvalidOperationException($"ps exited with {running.ExitCode}");
        }

        var processes = new Dictionary<int, (int Parent, bool Zombie)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 3
                && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
            {
                processes[pid] = (parent, fields[2].StartsWith('Z'));
            }
        }

        return new ProcessTable(processes);
    }

    /// <summary>Whether the process runs: it is listed and is not a zombie.</summary>
    public bool Lives(int pid) => _processes.TryGetValue(pid, out var process) && !process.Zombie;

    /// <summary>Each of <paramref name="roots"/> that runs and every running process below it, by
    /// parent, each once.</summary>
    public IReadOnlyList<int> TreeOf(IEnumerable<int> roots)
    {
        var children = _processes.Where(pair => !pair.Value.Zombie).ToLookup(pair => pair.Value.Parent, pair => pair.Key);
        var tree = new List<int>();
        var seen = new HashSet<int>();
        var next = new Queue<int>(roots.Where(Lives));
        while (next.TryDequeue(out var pid))
        {
            if (!seen.Add(pid))
            {
                continue;
            }

            tree.Add(pid);
            foreach (var child in children[pid])
            {
                next.Enqueue(child);
            }
        }

        return tree;
    }

    /// <summary><c>kill -s &lt;signal&gt;</c> to each of <paramref name="pids"/>, as the worker; one
    /// that has ended meanwhile is passed over.</summary>
    public static async Task Signal(string signal, IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0)
        {
            return;
        }

        var kill = new ProcessStartInfo("kill") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        kill.ArgumentList.Add("-s");
        kill.ArgumentList.Add(signal);
        foreach (var pid in pids)
        {
            kill.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        using var running = Process.Start(kill) ?? throw new InvalidOperationException("kill did not start");
        var errors = running.StandardError.ReadToEndAsync(CancellationToken.None);
        await running.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await running.WaitForExitAsync(CancellationToken.None);
        await errors;
    }
}

/// <summary>
/// Hands each line of one of a program's streams to a sink, as its end is read: without its
/// <c>\n</c> or the <c>\r</c> before it, and the last one, when the stream ends without one, at
/// <see cref="End"/>. A sink that throws is called no more, and its exception is kept in
/// <see cref="Failed"/>. Used by every starter that reads a program's output, so a line is cut the
/// same way wherever it was written.
/// </summary>
internal sealed class LineSplitter(ProcessStream stream, Action<ProcessStream, string> lines)
{
    private readonly StringBuilder _line = new();

    /// <summary>What the sink threw, which stopped it being called; null while it has not.</summary>
    public Exception? Failed { get; private set; }

    /// <summary>The next <paramref name="count"/> characters of the stream, from
    /// <paramref name="buffer"/>.</summary>
    public void Append(char[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            if (buffer[i] == '\n')
            {
                _line.Append(buffer, start, i - start);
                Hand();
                _line.Clear();
                start = i + 1;
            }
        }

        _line.Append(buffer, start, count - start);
    }

    /// <summary>The stream has ended: its last line, when it had no line end.</summary>
    public void End()
    {
        if (_line.Length > 0)
        {
            Hand();
            _line.Clear();
        }
    }

    private void Hand()
    {
        if (Failed is not null)
        {
            return;
        }

        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        try
        {
            lines(stream, _line.ToString());
        }
        catch (Exception error)
        {
            Failed = error;
        }
    }
}
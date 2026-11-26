using System.Diagnostics;
using System.Globalization;
using System.Text;

using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Pages;

/// <summary>Starts a program that keeps running, the project's <c>start</c>, in a process group of
/// its own. The seam the screenshot tests replace.</summary>
internal interface IBackgroundStarter
{
    Task<IBackgroundProcess> Start(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>A program started in the background, and every process it started.</summary>
internal interface IBackgroundProcess : IAsyncDisposable
{
    /// <summary>Its exit code once it has ended, null while it runs. Processes it started may
    /// still run.</summary>
    int? ExitCode { get; }

    /// <summary>What it has written so far, standard output and standard error as they came, the
    /// last <see cref="SystemBackgroundStarter.KeptCharacters"/> characters.</summary>
    string Output { get; }

    /// <summary>Ends its process group: <c>SIGTERM</c>, and <c>SIGKILL</c> to whatever is left after
    /// <see cref="SystemBackgroundStarter.Grace"/>. Safe to call twice.</summary>
    Task End();
}

/// <summary>
/// Starts the project's processes for real: through <c>setsid</c>, from util-linux, so the program
/// leads a process group of its own and everything it starts is in it, and with
/// <paramref name="runAs"/> through <c>setpriv</c> first, as <see cref="SystemProcessStarter"/>
/// does, keeping the environment exactly as given. Ending it signals the group, with
/// <c>kill -- -&lt;group&gt;</c>, so a server the start command left running in the background ends
/// too, which ending the process tree by parent would miss once it was detached. Standard input is
/// closed at once. Each line it writes goes to the spec's line sink as it is written, as
/// <see cref="SystemProcessStarter"/> hands it; a sink that throws is called no more. See
/// "Screenshots" in devflow/README.md.
/// </summary>
internal sealed class SystemBackgroundStarter(string? runAs, TimeProvider time) : IBackgroundStarter
{
    /// <summary>How long the group has after <c>SIGTERM</c> before <c>SIGKILL</c>.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>How much of what it writes is kept, the last characters.</summary>
    public const int KeptCharacters = 16 * 1024;

    /// <summary>The program and arguments that start <paramref name="spec"/> in a group of its own,
    /// as <paramref name="user"/> when one is given.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) Command(string? user, ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        List<string> arguments = user is null ? [] : ["--reuid", user, "--regid", user, "--init-groups", "--", "setsid"];
        arguments.AddRange(["--", spec.FileName, .. spec.Arguments]);
        return (user is null ? "setsid" : "setpriv", arguments);
    }

    /// <summary>Who the programs run as, null for the worker's own user.</summary>
    public string? RunAs => runAs;

    public Task<IBackgroundProcess> Start(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (fileName, arguments) = Command(runAs, spec);
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{spec.FileName} did not start");
        process.StandardInput.Close();
        return Task.FromResult<IBackgroundProcess>(new Running(process, time, spec.Lines));
    }

    /// <summary>
    /// One started program. The process the worker started is <c>setpriv</c>, which becomes
    /// <c>setsid</c>, which becomes the program, each by <c>exec</c>, so its id is the program's and,
    /// since a child the worker starts never leads a group, the group's that <c>setsid</c> made.
    /// </summary>
    private sealed class Running : IBackgroundProcess
    {
        private readonly Process _process;
        private readonly TimeProvider _time;
        private readonly StringBuilder _output = new();
        private readonly Task _reading;
        private readonly int _group;
        private int _ended;

        public Running(Process process, TimeProvider time, Action<ProcessStream, string>? lines)
        {
            _process = process;
            _time = time;
            _group = process.Id;
            _reading = Task.WhenAll(
                Read(process.StandardOutput, lines is null ? null : new LineSplitter(ProcessStream.Stdout, lines)),
                Read(process.StandardError, lines is null ? null : new LineSplitter(ProcessStream.Stderr, lines)));
        }

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public async Task End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return;
            }

            await Signal("TERM");
            var until = _time.GetUtcNow() + Grace;
            while (await GroupLives() && _time.GetUtcNow() < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time);
            }

            if (await GroupLives())
            {
                await Signal("KILL");
            }

            try
            {
                // The group is gone, so the program with it: this reaps it, and reads what it wrote
                // last. A process that left the group holding its pipes is not waited for.
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(Grace, _time);
                await _reading.WaitAsync(Grace, _time);
            }
            catch (TimeoutException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await End();
            }
            finally
            {
                _process.Dispose();
            }
        }

        /// <summary>Whether any process of the group is left, by <c>kill -0</c>.</summary>
        private async Task<bool> GroupLives() => await Kill("-0") == 0;

        private async Task Signal(string signal) => await Kill($"-{signal}");

        /// <summary><c>kill</c> with <paramref name="signal"/> to the group, as the worker itself,
        /// so a process of the group that changed its user is reached too; its exit code.</summary>
        private async Task<int> Kill(string signal)
        {
            var kill = new ProcessStartInfo("kill") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { signal, "--", string.Create(CultureInfo.InvariantCulture, $"-{_group}") })
            {
                kill.ArgumentList.Add(argument);
            }

            using var running = Process.Start(kill) ?? throw new InvalidOperationException("kill did not start");
            await running.WaitForExitAsync(CancellationToken.None);
            return running.ExitCode;
        }

        private async Task Read(StreamReader reader, LineSplitter? lines)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                lock (_output)
                {
                    _output.Append(buffer, 0, read);
                    if (_output.Length > KeptCharacters)
                    {
                        _output.Remove(0, _output.Length - KeptCharacters);
                    }
                }

                lines?.Append(buffer, read);
            }

            lines?.End();
        }
    }
}
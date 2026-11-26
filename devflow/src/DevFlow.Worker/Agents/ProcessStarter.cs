using System.Diagnostics;
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
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, bool Bytes = false, Action<ProcessStream, string>? Lines = null);

/// <summary>Which of a program's two output streams a line came from.</summary>
internal enum ProcessStream
{
    Stdout,
    Stderr,
}

/// <summary>How a program ended, and what it wrote.</summary>
/// <param name="TimedOut">Whether it ran past its time limit and was ended.</param>
internal sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, bool TimedOut);

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
/// it started are ended, and the outcome says so.
///
/// With <paramref name="runAs"/>, the program runs as that user through <c>setpriv</c>, from
/// util-linux, which keeps the environment as given; the worker runs as root and Claude Code
/// refuses <c>bypassPermissions</c> as root. How the image provides the user is "The base image
/// and the start commands".
/// </summary>
internal sealed class SystemProcessStarter(string? runAs, TimeProvider time) : IProcessStarter
{
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
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(either.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        return new ProcessOutcome(process.ExitCode, await stdout, await stderr, timedOut);
    }

    /// <summary>
    /// The whole of <paramref name="reader"/>, exactly as written, read as it comes, with each line
    /// handed to <paramref name="lines"/> once its end is read, without its <c>\n</c> or the
    /// <c>\r</c> before it, and the last one when the stream ends without one. A sink that throws
    /// is called no more, the stream is still read to its end, so the program never waits on a
    /// full pipe, and the exception is thrown then.
    /// </summary>
    private static async Task<string> Read(StreamReader reader, ProcessStream stream, Action<ProcessStream, string>? lines)
    {
        var whole = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[8192];
        Exception? failed = null;

        void Hand()
        {
            if (lines is null || failed is not null)
            {
                return;
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line.Length--;
            }

            try
            {
                lines(stream, line.ToString());
            }
            catch (Exception error)
            {
                failed = error;
            }
        }

        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            whole.Append(buffer, 0, read);
            if (lines is null)
            {
                continue;
            }

            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    line.Append(buffer, start, i - start);
                    Hand();
                    line.Clear();
                    start = i + 1;
                }
            }

            line.Append(buffer, start, read - start);
        }

        if (line.Length > 0)
        {
            Hand();
        }

        if (failed is not null)
        {
            throw new InvalidOperationException($"the line sink of the program's {stream} failed", failed);
        }

        return whole.ToString();
    }
}
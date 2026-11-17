using System.Diagnostics;

namespace DevFlow.Worker.Agents;

/// <summary>A program to start: its arguments as a list, never through a shell, and exactly the
/// environment given, nothing of the worker's own.</summary>
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

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
/// Standard input is closed at once. Past the time limit the process and everything it started
/// are ended, and the outcome says so.
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
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

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
}
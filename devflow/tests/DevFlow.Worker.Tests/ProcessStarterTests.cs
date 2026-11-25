using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>
/// The real starter: the process gets exactly the environment given and nothing of the worker's,
/// so an <c>ANTHROPIC_API_KEY</c> the worker happens to hold cannot reach Claude Code; and a
/// process past its time limit is ended. Runs <c>/usr/bin/env</c> and <c>/bin/sh</c>, which every
/// machine the tests run on has.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class ProcessStarterTests
{
    [Fact]
    public async Task TheProcessGetsOnlyTheEnvironmentGiven()
    {
        System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api03-must-not-reach-the-run");
        System.Environment.SetEnvironmentVariable("CLAUDE_CODE_SIMPLE", "1");
        try
        {
            var given = new Dictionary<string, string> { ["HOME"] = "/tmp", ["LANG"] = "C.UTF-8", ["ONLY_THIS"] = "yes" };

            var outcome = await new SystemProcessStarter(runAs: null, TimeProvider.System)
                .Run(new ProcessSpec("/usr/bin/env", [], Path.GetTempPath(), given), TimeSpan.FromSeconds(30), Ct);

            Assert.Equal(0, outcome.ExitCode);
            Assert.False(outcome.TimedOut);
            Assert.Equal(
                ["HOME=/tmp", "LANG=C.UTF-8", "ONLY_THIS=yes"],
                outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            System.Environment.SetEnvironmentVariable("CLAUDE_CODE_SIMPLE", null);
        }
    }

    [Fact]
    public async Task ItKeepsStdoutStderrAndTheExitCode()
    {
        var outcome = await new SystemProcessStarter(runAs: null, TimeProvider.System)
            .Run(new ProcessSpec("/bin/sh", ["-c", "echo out; echo err >&2; exit 3"], Path.GetTempPath(), new Dictionary<string, string>()), TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(new ProcessOutcome(3, "out\n", "err\n", false), outcome);
    }

    [Fact]
    public async Task LinesReachTheSinkAsTheyAreWritten()
    {
        // The program writes each line only once the sink has answered the one before, by a file it
        // waits for: were the lines handed over only when it ended, it would wait until its time
        // limit, and the outcome would say it timed out.
        var folder = Directory.CreateTempSubdirectory("devflow-lines-");
        try
        {
            var lines = new List<(ProcessStream Stream, string Text)>();
            var script = """
                echo one
                while [ ! -f go1 ]; do sleep 0.05; done
                echo two >&2
                while [ ! -f go2 ]; do sleep 0.05; done
                printf 'three'
                """;
            void Sink(ProcessStream stream, string text)
            {
                lock (lines)
                {
                    lines.Add((stream, text));
                    File.WriteAllText(Path.Combine(folder.FullName, $"go{lines.Count}"), "");
                }
            }

            var outcome = await new SystemProcessStarter(runAs: null, TimeProvider.System)
                .Run(new ProcessSpec("/bin/sh", ["-c", script], folder.FullName, new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }, Lines: Sink), TimeSpan.FromSeconds(30), Ct);

            Assert.False(outcome.TimedOut);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal([(ProcessStream.Stdout, "one"), (ProcessStream.Stderr, "two"), (ProcessStream.Stdout, "three")], lines);
            // The outcome still holds all of both, exactly as written.
            Assert.Equal("one\nthree", outcome.Stdout);
            Assert.Equal("two\n", outcome.Stderr);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ALineSplitAcrossReadsIsOneLine()
    {
        // A line longer than any one read, with a carriage return before its end, is handed over
        // whole and without the return, and the outcome keeps both.
        var lines = new List<string>();
        var outcome = await new SystemProcessStarter(runAs: null, TimeProvider.System)
            .Run(
                new ProcessSpec("/bin/sh", ["-c", "head -c 20000 /dev/zero | tr '\\0' x; printf '\\r\\nend\\n'"], Path.GetTempPath(), new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }, Lines: (_, text) => lines.Add(text)),
                TimeSpan.FromSeconds(30),
                Ct);

        Assert.Equal([new string('x', 20000), "end"], lines);
        Assert.Equal(new string('x', 20000) + "\r\nend\n", outcome.Stdout);
    }

    [Fact]
    public async Task AProcessPastItsTimeLimitIsEnded()
    {
        var started = DateTimeOffset.UtcNow;

        var outcome = await new SystemProcessStarter(runAs: null, TimeProvider.System)
            .Run(new ProcessSpec("/bin/sh", ["-c", "echo started; sleep 60"], Path.GetTempPath(), new Dictionary<string, string>()), TimeSpan.FromSeconds(1), Ct);

        Assert.True(outcome.TimedOut);
        Assert.Equal("started\n", outcome.Stdout);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30));
    }
}

/// <summary>The tests that change the test process's own environment, run one at a time.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;
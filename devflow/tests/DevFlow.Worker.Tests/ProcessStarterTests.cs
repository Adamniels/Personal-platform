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
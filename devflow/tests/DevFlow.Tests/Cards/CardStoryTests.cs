using System.Globalization;

using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Cards;

/// <summary>
/// What a card's story carries for its page beyond its runs, reviews and rounds: a failed run's
/// error output, the tail of what its worker sent, and the repository it is a card of. See "A
/// card's page" in devflow/README.md.
/// </summary>
public sealed class CardStoryTests
{
    [Fact]
    public async Task AFailedRunCarriesItsStderrTail()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        // A failure outside the card, whose worker sent 120 lines of stderr, then, once the
        // project's wait after it is over, a build that succeeded with its one line.
        var failure = Built.PreparedFailure(inCard: false, "Claude Code exited with 1");
        failure["stderr"] = string.Join('\n', Enumerable.Range(1, 120).Select(line => string.Create(CultureInfo.InvariantCulture, $"line {line}")));
        await Built.Run(jobs, failure);
        jobs.Routes.Time.Advance(DevFlow.Stops.ProjectWaits.First);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        await Built.Run(jobs, Built.Prepared(Built.Report()));

        var (status, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal(StatusCodes.Status200OK, status);
        var runs = card!["runs"]!.AsArray();
        Assert.Equal(["succeeded", "failed_outside_card"], runs.Select(run => (string)run!["result"]!));
        // The last 50 lines, in order, of the 200 the run stores.
        var tail = ((string)runs[1]!["stderr_tail"]!).Split('\n');
        Assert.Equal(50, tail.Length);
        Assert.Equal("line 71", tail[0]);
        Assert.Equal("line 120", tail[^1]);
        // A run that succeeded carries none, whatever its worker sent.
        Assert.True(runs[0]!.AsObject().ContainsKey("stderr_tail"));
        Assert.Null(runs[0]!["stderr_tail"]);
    }

    [Fact]
    public async Task AFailedRunWithNoStderrCarriesItEmpty()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.PreparedFailure(inCard: true, "Claude Code exited with 1"));

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal("", (string)card!["runs"]![0]!["stderr_tail"]!);
    }

    [Fact]
    public async Task TheStoryNamesItsRepository()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

        Assert.Equal("Adamniels/Personal-platform", (string)card!["repository"]!);
    }
}

using DevFlow.Fixing;
using DevFlow.Tests.Merging;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Fixing;

/// <summary>
/// A card's fix rounds are its project's <c>fix_rounds_card</c>, read when a verdict is judged:
/// a <c>changes</c> verdict with every round used stops the card, and with none the first does.
/// Against real Postgres and the fake code host, with the fake worker. See "Fixing" in
/// devflow/README.md.
/// </summary>
public sealed class FixRoundTests
{
    [Fact]
    public async Task ACardStopsAfterItsProjectsRounds()
    {
        // One round: the first changes starts it, the second stops the card.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Merged.Sql(jobs, "UPDATE project_settings SET fix_rounds_card = 1 WHERE project_id = @project");
            await Fixed.BuiltAndReviewed(jobs);
            Assert.Equal(("fixing", FixRounds.Asked(1, 1)), ((await jobs.Card(4)).State, (await jobs.Card(4)).Reason));
            Assert.Equal("Round 1 of 1: the reviewer asked for changes; a fix runs next", (await jobs.Card(4)).Reason);
            await Fixed.Fix(jobs);
            await Built.Review(jobs, Built.Verdict("changes"));
            Assert.Equal(("stopped", FixRounds.UsedUp), ((await jobs.Card(4)).State, (await jobs.Card(4)).Reason));
            Assert.Equal(1, await Fixed.RoundsUsed(jobs, 4));
        }

        // None: the first changes stops it, and no fix runs.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Merged.Sql(jobs, "UPDATE project_settings SET fix_rounds_card = 0 WHERE project_id = @project");
            await Fixed.BuiltAndReviewed(jobs);
            Assert.Equal(("stopped", FixRounds.UsedUp), ((await jobs.Card(4)).State, (await jobs.Card(4)).Reason));
            Assert.Equal(0, await Fixed.RoundsUsed(jobs, 4));
            Assert.Equal(["build", "review"], (await jobs.Runs()).Select(run => run.Kind));
        }

        // Ten: the most the setting takes, past the three the table once held.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Merged.Sql(jobs, "UPDATE project_settings SET fix_rounds_card = 10 WHERE project_id = @project");
            await Fixed.BuiltAndReviewed(jobs);
            for (var round = 1; round <= 4; round++)
            {
                Assert.Equal(("fixing", FixRounds.Asked(round, 10)), ((await jobs.Card(4)).State, (await jobs.Card(4)).Reason));
                await Fixed.Fix(jobs);
                await Built.Review(jobs, Built.Verdict("changes"));
            }

            Assert.Equal(("fixing", FixRounds.Asked(5, 10), 5), ((await jobs.Card(4)).State, (await jobs.Card(4)).Reason, await Fixed.RoundsUsed(jobs, 4)));
        }
    }
}
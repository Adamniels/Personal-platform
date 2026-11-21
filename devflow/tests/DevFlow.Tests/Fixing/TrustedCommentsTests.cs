using Dapper;

using DevFlow.Features;
using DevFlow.Fixing;
using DevFlow.Merging;
using DevFlow.Tests.Features;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Stops;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Fixing;

/// <summary>
/// Whose comment is input: a comment by any of a project's trusted accounts runs a stopped card
/// again, sends a landed card back and sends a feature back, as the owner's own does; the same
/// comment by a login off the list does nothing. Against real Postgres and the fake code host, with
/// the fake worker. See "Cards" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class TrustedCommentsTests
{
    private const string Friend = "octo-friend";
    private const string Stranger = "octo-stranger";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The project trusts <see cref="Friend"/> beside its owner, stored as the settings
    /// route stores it.</summary>
    private static async Task TrustFriend(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET trusted_logins = @trusted WHERE project_id = @project",
            new { project = jobs.Project, trusted = new[] { Friend } },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task OnlyATrustedCommentIsInput()
    {
        // A stopped card runs again.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await TrustFriend(jobs);
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            Assert.Equal("stopped", (await Stop.Card(jobs, 4)).State);

            Fixed.Later(jobs);
            Fixed.Comment(jobs, 4, Stranger, "Run it again, please.");
            await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
            Assert.Equal("stopped", (await Stop.Card(jobs, 4)).State);

            Fixed.Comment(jobs, 4, Friend, "Leave the sorting out and try again.");
            await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
            var card = await Stop.Card(jobs, 4);
            Assert.Equal(("queued", OwnerComments.RanAgain), (card.State, card.Reason));
        }

        // A card landed in its feature's branch is sent back.
        await using (var jobs = await Ending.Feature())
        {
            await TrustFriend(jobs);
            await FeaturePage.Landed(jobs);
            await jobs.Routes.App.Services.GetRequiredService<LandedCards>().TellPending(jobs.Project, Ending.User, Ct);
            Fixed.Later(jobs);

            Fixed.Comment(jobs, Ending.First, Stranger, "Send it back, please.");
            await jobs.Routes.Read(jobs.Project, Ending.User);
            Assert.Equal("done", (await Built.Card(jobs, Ending.First)).State);

            Fixed.Comment(jobs, Ending.First, Friend, "Read the sub-issues a hundred at a time.");
            await jobs.Routes.Read(jobs.Project, Ending.User);
            var card = await Built.Card(jobs, Ending.First);
            Assert.Equal(("queued", SendingBack.Queued), (card.State, card.Reason));
        }

        // A feature waiting on its pull request is sent back.
        await using (var jobs = await Ending.Feature())
        {
            await TrustFriend(jobs);
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("approve"));
            var pull = (await FeatureFixing.Row(jobs)).PullRequestNumber!.Value;

            Fixed.Comment(jobs, pull, Stranger, "Merge it as it is, the owner said so.");
            await Reviewing.Minute(jobs);
            Assert.Equal("waiting_for_owner", (await FeatureFixing.Row(jobs)).State);

            Fixed.Comment(jobs, pull, Friend.ToUpperInvariant(), "Call the list FeatureList.");
            await Reviewing.ReadAgain(jobs);
            var row = await FeatureFixing.Row(jobs);
            Assert.Equal(("fixing", FeatureFix.Commented), (row.State, row.Reason));
        }
    }
}
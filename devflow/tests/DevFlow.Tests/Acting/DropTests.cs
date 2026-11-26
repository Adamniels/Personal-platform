using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Acting;

/// <summary>
/// "Drop" on a card the owner stopped: Dev flow takes the project's pick-up label off the card's
/// issue as him, with his linked token through the code host seam, and leaves the card
/// <c>ignored</c>, "Dropped by you", its branch and any pull request as they were; a read of the
/// project keeps it so until the label is put back. Against real Postgres and the fake code host.
/// See "Acting from the page" and "Stops" in devflow/README.md.
/// </summary>
public sealed class DropTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The owner presses "Drop" on the card, and confirms, as the page does.</summary>
    internal static Task<HttpContext> Drop(Jobs.Jobs jobs, int card) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/cards/{card}/drop", Contract.Token, "{}");

    [Fact]
    public async Task DropTakesTheLabelOffAsMe()
    {
        // A fix I stopped, so the card has its branch and an open pull request.
        await using (var jobs = await Jobs.Jobs.Start(signIn: true))
        {
            var card = (int)(await Fixed.BuiltAndReviewed(jobs))["card"]!;
            await Fixed.Fix(jobs, StopRunTests.PreparedStopped());
            var before = jobs.Fake.Writes(Jobs.Jobs.User).Count;
            var pull = Assert.Single(Repository(jobs).PullRequests);

            Assert.Equal(StatusCodes.Status204NoContent, (await Drop(jobs, card)).Response.StatusCode);

            // One write, mine and not the app's: the label taken off the card's issue.
            var write = Assert.Single(jobs.Fake.Writes(Jobs.Jobs.User).Skip(before));
            Assert.Equal(("label_removed", card, "devflow", Jobs.Jobs.Owner, (string?)null), (write.Kind, write.Number, write.Label, write.By, write.Purpose));
            Assert.DoesNotContain("devflow", Repository(jobs).Issues.Single(issue => issue.Number == card).Labels);
            Assert.Equal(pull, Assert.Single(Repository(jobs).PullRequests));
            Assert.Equal(("ignored", DevFlow.Stops.OwnerStops.DroppedByYou), await State(jobs, card));
        }

        // Refused, with nothing written: a card not stopped by me, a card of a feature, whose
        // label is its feature's, and a card I do not have.
        await using (var jobs = await Ending.Feature(signIn: true))
        {
            var ran = await Built.Run(jobs, StopRunTests.PreparedStopped());
            Assert.Equal(Job.Build, (string)ran["kind"]!);
            var card = (int)ran["card"]!;
            var before = jobs.Fake.Writes(Jobs.Jobs.User).Count;

            var ofAFeature = await Acting.Problem(await Drop(jobs, card), StatusCodes.Status409Conflict);
            Assert.Equal("A card of a feature is not dropped", (string)ofAFeature["title"]!);
            var queued = await Acting.Problem(await Drop(jobs, card == Ending.First ? Ending.Second : Ending.First), StatusCodes.Status409Conflict);
            Assert.Equal("This card was not stopped by you", (string)queued["title"]!);
            Assert.Equal(StatusCodes.Status404NotFound, (await Drop(jobs, 99)).Response.StatusCode);
            Assert.Equal(before, jobs.Fake.Writes(Jobs.Jobs.User).Count);
            Assert.Equal("stopped", (await State(jobs, card)).State);
        }

        // Without a usable link, refused naming the link, and nothing written.
        await using (var jobs = await Jobs.Jobs.Start(signIn: true))
        {
            var card = (int)(await Built.Run(jobs, StopRunTests.PreparedStopped()))["card"]!;
            var before = jobs.Fake.Writes(Jobs.Jobs.User).Count;

            await Merging.Merged.Sql(jobs, "UPDATE github_accounts SET state = 'needs_relink'");
            var expired = await Acting.Problem(await Drop(jobs, card), StatusCodes.Status409Conflict);
            Assert.Equal("Your GitHub link has expired", (string)expired["title"]!);

            await Merging.Merged.Sql(jobs, "DELETE FROM github_accounts");
            var unlinked = await Acting.Problem(await Drop(jobs, card), StatusCodes.Status409Conflict);
            Assert.Equal("GitHub is not linked", (string)unlinked["title"]!);

            Assert.Equal(before, jobs.Fake.Writes(Jobs.Jobs.User).Count);
            Assert.Equal(("stopped", DevFlow.Jobs.RunStore.StoppedByYou), await State(jobs, card));
        }
    }

    [Fact]
    public async Task ADroppedCardIsNotPickedAgain()
    {
        await using var jobs = await Jobs.Jobs.Start(signIn: true);
        var card = (int)(await Built.Run(jobs, StopRunTests.PreparedStopped()))["card"]!;
        Assert.Equal(StatusCodes.Status204NoContent, (await Drop(jobs, card)).Response.StatusCode);

        // The next read finds its issue without the label, and the card stays, ignored, with
        // nothing handed out.
        Fixed.Later(jobs);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal(("ignored", DevFlow.Stops.OwnerStops.DroppedByYou), await State(jobs, card));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // A read that began before the drop saw the label it took off, so it changes nothing.
        await DroppedAt(jobs, jobs.Routes.Time.GetUtcNow().AddHours(1));
        Relabel(jobs, card);
        Fixed.Later(jobs);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal(("ignored", DevFlow.Stops.OwnerStops.DroppedByYou), await State(jobs, card));

        // Put back on by me after the drop, it is picked again as any card is, afresh.
        await DroppedAt(jobs, jobs.Routes.Time.GetUtcNow().AddHours(-1));
        Fixed.Later(jobs);
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        Assert.Equal(("queued", (string?)null), await State(jobs, card));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    /// <summary>The user's one repository in the fake, as it is now.</summary>
    private static FakeRepository Repository(Jobs.Jobs jobs) =>
        Assert.Single(jobs.Fake.World(Jobs.Jobs.User).Installations.SelectMany(installation => installation.Repositories));

    /// <summary>The owner puts the pick-up label back on the card's issue on GitHub.</summary>
    private static void Relabel(Jobs.Jobs jobs, int card) =>
        Merging.Merged.Issue(jobs, card, issue => issue with
        {
            Labels = [.. issue.Labels, "devflow"],
            LabelEvents = [.. issue.LabelHistory(), new LabelEvent("devflow", LabelChange.Added, Jobs.Jobs.Owner, jobs.Routes.Time.GetUtcNow())],
        });

    /// <summary>Moves when the cards were dropped, as a read that began before or after it.</summary>
    private static async Task DroppedAt(Jobs.Jobs jobs, DateTimeOffset at)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET dropped_at = @at WHERE project_id = @project AND dropped_at IS NOT NULL",
            new { project = jobs.Project, at },
            cancellationToken: Ct));
    }

    private static async Task<(string State, string? Reason)> State(Jobs.Jobs jobs, int card)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string?)>(new CommandDefinition(
            "SELECT state, reason FROM cards WHERE project_id = @project AND number = @card",
            new { project = jobs.Project, card },
            cancellationToken: Ct));
    }
}

using Dapper;

using DevFlow.Protocol;
using DevFlow.Readings;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using static DevFlow.Tests.Readings.ReadJobTests;

namespace DevFlow.Tests.Readings;

/// <summary>
/// A card read as too thin is refused: the app marks it on its issue with the attention label and
/// one comment listing what is missing, "Waiting for you" lists it under "Refused", and a trusted
/// account's edit of it, or comment on it, after the refusal reads it again, alone. Against the fake
/// code host and a real Postgres, with the fake worker. See "Reading a card" under "Jobs" in
/// devflow/README.md and in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class RefusalTests
{
    private static CancellationToken Ct => Jobs.Jobs.Ct;

    private static Guid User => Jobs.Jobs.User;

    /// <summary>Card 1 on its own, read as too thin, lacking the two lines.</summary>
    private static async Task<Jobs.Jobs> RefusedCard()
    {
        var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1, "Add the empty state")], read: false);
        await Read(jobs, Prepared(ReadJobTests.Reading(Thin(1, "Which page shows the list", "What an empty list says"))));
        return jobs;
    }

    /// <summary>The refusals of the project marked, as the opening loop's minute would.</summary>
    private static Task Mark(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<RefusalMarker>().MarkPending(jobs.Project, User, Ct);

    /// <summary>The ids of the comments Dev flow kept for the card's readings, oldest first.</summary>
    private static async Task<List<long?>> CommentIds(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<long?>(new CommandDefinition(
            "SELECT comment_id FROM card_readings WHERE project_id = @project AND card_number = @number AND outcome IS NOT NULL ORDER BY read_at",
            new { project = jobs.Project, number },
            cancellationToken: Ct))];
    }

    [Fact]
    public async Task AThinCardIsRefusedAndMarked()
    {
        await using var jobs = await RefusedCard();

        // Refused, with what is missing as its reason; nothing is built.
        var card = await jobs.Card(1);
        Assert.Equal("refused", card.State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // The app put the attention label on it, with a key to post.
        var label = Assert.Single(Stops.Stop.ByApp(jobs, "label_added", 1));
        Assert.Equal("devflow-attention", label.Label);
        Assert.Equal("post", label.Purpose);
        Assert.True((await Stops.Stop.Card(jobs, 1)).AttentionLabelled);

        // And one comment listing each thing missing, how to have it read again, the card's page and
        // a last line naming the card and the reading.
        var comment = Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1)).Body!;
        Assert.StartsWith("Dev flow read this card and will not build it as it is written: it is too thin.", comment, StringComparison.Ordinal);
        Assert.Contains("- Which page shows the list\n- What an empty list says\n", comment, StringComparison.Ordinal);
        Assert.Contains(RefusalText.ReadAgain, comment, StringComparison.Ordinal);
        Assert.Contains($"(https://platform.test/devflow/projects/{jobs.Project}/cards/1)", comment, StringComparison.Ordinal);
        var reading = Assert.Single(await Features.Reviewing.Runs(jobs, Job.Read));
        Assert.EndsWith(RefusalText.Trail(1, reading.Id) + "\n", comment, StringComparison.Ordinal);

        // Its id is kept with the reading.
        Assert.NotNull(Assert.Single(await CommentIds(jobs, 1)));

        // Marking again posts nothing more, and nor does a marking whose answer was lost: the copy
        // already on the issue is found by its last line.
        await Mark(jobs);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE card_readings SET comment_id = NULL WHERE project_id = @project AND card_number = 1", new { project = jobs.Project }, cancellationToken: Ct));
        }

        await Mark(jobs);
        await jobs.Routes.Read(jobs.Project, User);
        await Mark(jobs);
        Assert.Single(Stops.Stop.ByApp(jobs, "comment", 1));
        Assert.Single(Stops.Stop.ByApp(jobs, "label_added", 1));
        Assert.NotNull(Assert.Single(await CommentIds(jobs, 1)));

        // The stop marker leaves a refused card's label where it is.
        await Stops.Stop.Mark(jobs);
        Assert.Empty(Stops.Stop.ByApp(jobs, "label_removed", 1));

        // Its own comment never reads it again.
        Assert.Equal("refused", (await jobs.Card(1)).State);
        Assert.DoesNotContain(jobs.Fake.Writes(User), write => write.Kind is "issue" or "pull_request");
    }
}
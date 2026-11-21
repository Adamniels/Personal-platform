using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Reading;

using static DevFlow.Tests.Cards.FeatureSupport;

namespace DevFlow.Tests.Cards;

/// <summary>
/// Picking by who changed a label, read from the issue's timeline: the pick-up label counts only
/// when the newest change of it by a trusted account added it, and the hold label counts whoever
/// added it, and while only someone untrusted took it off since a trusted account added it. For a
/// card on its own, a feature's parent and a feature's card. Against the fake and a real Postgres.
/// See "How a card is picked" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class LabelActorTests
{
    private const string Friend = "octo-friend";
    private const string Hold = "devflow-hold";

    private static LabelEvent Added(string label, string actor, int minute) => new(label, LabelChange.Added, actor, At.AddHours(1).AddMinutes(minute));

    private static LabelEvent Removed(string label, string actor, int minute) => new(label, LabelChange.Removed, actor, At.AddHours(1).AddMinutes(minute));

    /// <summary>A card of the owner's with <paramref name="labels"/> on it and exactly
    /// <paramref name="events"/> on its timeline.</summary>
    private static FakeIssue Card(int number, IReadOnlyList<string> labels, params LabelEvent[] events) =>
        Issue(number, labels: labels) with { LabelEvents = events };

    private static async Task<FeatureCardRow> Stored(Reading.Reading reading, Guid project, int number) =>
        (await StoredCards(reading, project)).Single(card => card.Number == number);

    [Fact]
    public async Task APickUpLabelByAStrangerIgnoresTheCard()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading,
        [
            Card(12, ["devflow"], Added("devflow", Stranger, 0)),
            // I took it off, and a stranger put it back on.
            Card(13, ["devflow"], Added("devflow", Owner, 0), Removed("devflow", Owner, 1), Added("devflow", Stranger, 2)),
            // On the issue with nothing on its timeline that says who put it there.
            Card(14, ["devflow"]),
        ]);

        await Read(reading, project);

        var twelve = await Stored(reading, project, 12);
        Assert.Equal(("ignored", $"The pick-up label devflow was added by {Stranger}, who is not a trusted account of this project"), (twelve.State, twelve.Reason));
        Assert.False(twelve.Picked);
        var thirteen = await Stored(reading, project, 13);
        Assert.Equal(("ignored", $"The pick-up label devflow was added by {Stranger}, who is not a trusted account of this project"), (thirteen.State, thirteen.Reason));
        var fourteen = await Stored(reading, project, 14);
        Assert.Equal(("ignored", "The pick-up label devflow was not added by a trusted account of this project"), (fourteen.State, fourteen.Reason));

        // An ignored card costs nothing more: what blocks it is never read.
        Assert.DoesNotContain(reading.Host.Calls, call => call.Operation == "BlockedBy");
    }

    [Fact]
    public async Task APickUpLabelByATrustedAccountPicksIt()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        await using (var admin = await reading.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET trusted_logins = @trusted WHERE project_id = @project", new { project, trusted = new[] { Friend } }, cancellationToken: Ct));
        }

        Stage(reading,
        [
            Card(12, ["devflow"], Added("devflow", Owner, 0)),
            // Labelled by a stranger, and then again by me.
            Card(13, ["devflow"], Added("devflow", Stranger, 0), Removed("devflow", Owner, 1), Added("devflow", Owner, 2)),
            // Labelled by an account I trust, its login in another case.
            Card(14, ["devflow"], Added("devflow", "Octo-Friend", 0)),
        ]);

        await Read(reading, project);

        foreach (var number in new[] { 12, 13, 14 })
        {
            var card = await Stored(reading, project, number);
            Assert.Equal(("queued", null), (card.State, card.Reason));
            Assert.True(card.Picked);
        }
    }

    [Fact]
    public async Task AHoldByAnyoneHolds()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, [Card(12, ["devflow", Hold], Added("devflow", Owner, 0), Added(Hold, Stranger, 1))]);

        await Read(reading, project);

        var card = await Stored(reading, project, 12);
        Assert.Equal(("held", $"Held by the label {Hold}"), (card.State, card.Reason));
    }

    [Fact]
    public async Task AHoldTakenOffByAStrangerStillHolds()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        var stillHeld = $"Held by the label {Hold}, taken off by {Stranger}, who is not a trusted account of this project";
        Stage(reading,
        [
            Card(12, ["devflow"], Added("devflow", Owner, 0), Added(Hold, Owner, 1), Removed(Hold, Stranger, 2)),
            // The same for a card of a feature, which carries no pick-up label of its own.
            Card(20, ["devflow"], Added("devflow", Owner, 0)) with { SubIssues = [new FakeSubIssue(21, null)] },
            Card(21, [], Added(Hold, Owner, 1), Removed(Hold, Stranger, 2)),
        ]);

        await Read(reading, project);

        var twelve = await Stored(reading, project, 12);
        Assert.Equal(("held", stillHeld), (twelve.State, twelve.Reason));
        var twentyOne = await Stored(reading, project, 21);
        Assert.Equal(("held", stillHeld), (twentyOne.State, twentyOne.Reason));

        // Taken off by me, it is released, even after a stranger put it back and took it off again.
        Stage(reading,
        [
            Card(12, ["devflow"], Added("devflow", Owner, 0), Added(Hold, Owner, 1), Removed(Hold, Owner, 2)),
            Card(20, ["devflow"], Added("devflow", Owner, 0)) with { SubIssues = [new FakeSubIssue(21, null)] },
            Card(21, [], Added(Hold, Owner, 1), Removed(Hold, Stranger, 2), Added(Hold, Stranger, 3), Removed(Hold, Owner, 4)),
        ]);

        await Read(reading, project);

        twelve = await Stored(reading, project, 12);
        Assert.Equal(("queued", null), (twelve.State, twelve.Reason));
        twentyOne = await Stored(reading, project, 21);
        Assert.Equal(("queued", null), (twentyOne.State, twentyOne.Reason));
    }

    [Fact]
    public async Task TheParentsLabelIsJudgedTheSame()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading,
        [
            Card(20, ["devflow"], Added("devflow", Stranger, 0)) with { SubIssues = [new FakeSubIssue(21, null), new FakeSubIssue(22, null)] },
            Issue(21),
            Issue(22),
        ]);

        await Read(reading, project);

        var feature = Assert.Single(await StoredFeatures(reading, project));
        Assert.Equal(("ignored", $"The pick-up label devflow was added by {Stranger}, who is not a trusted account of this project"), (feature.State, feature.Reason));
        Assert.All(await StoredCards(reading, project), card =>
            Assert.Equal(("ignored", CardPicker.FeatureNotPicked(20)), (card.State, card.Reason)));

        // A hold I put on the parent and a stranger took off still holds every card of it.
        Stage(reading,
        [
            Card(20, ["devflow"], Added("devflow", Owner, 0), Added(Hold, Owner, 1), Removed(Hold, Stranger, 2)) with { SubIssues = [new FakeSubIssue(21, null), new FakeSubIssue(22, null)] },
            Issue(21),
            Issue(22),
        ]);

        await Read(reading, project);

        feature = Assert.Single(await StoredFeatures(reading, project));
        Assert.Equal(("held", $"Held by the label {Hold}, taken off by {Stranger}, who is not a trusted account of this project"), (feature.State, feature.Reason));
        Assert.All(await StoredCards(reading, project), card => Assert.Equal("held", card.State));
    }

    [Fact]
    public async Task ATimelineThatCannotBeReadPicksNothing()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, [Card(12, ["devflow"], Added("devflow", Owner, 0))]);
        await Read(reading, project);
        Assert.Equal("queued", (await Stored(reading, project, 12)).State);
        var read = (await reading.ReadOf(project))!;

        // I take the label off and a stranger puts it back on, and the timeline cannot be read: the
        // card stays as it was, and the read is recorded as failing.
        Stage(reading, [Card(12, ["devflow"], Added("devflow", Owner, 0), Removed("devflow", Owner, 1), Added("devflow", Stranger, 2))]);
        reading.Host.Unavailable["LabelEvents"] = true;

        Assert.Equal(ReadResult.Failed, await reading.Reader.Read(project, Reading.Reading.Alice, Ct));

        var card = await Stored(reading, project, 12);
        Assert.Equal(("queued", null), (card.State, card.Reason));
        var failed = (await reading.ReadOf(project))!;
        Assert.Equal(ProjectReader.HostUnavailable, failed.LastError);
        Assert.Equal(read.LastSuccessAt, failed.LastSuccessAt);

        // It answers again, and the stranger's label ignores it.
        reading.Host.Unavailable.Clear();
        await Read(reading, project);
        Assert.Equal("ignored", (await Stored(reading, project, 12)).State);
    }
}
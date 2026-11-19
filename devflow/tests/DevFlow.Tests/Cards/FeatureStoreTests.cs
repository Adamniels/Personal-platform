using DevFlow.Reading;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Runner;

using static DevFlow.Tests.Cards.FeatureSupport;

namespace DevFlow.Tests.Cards;

/// <summary>
/// A feature and its cards are stored by the read that found them, in its one transaction with the
/// waiting items, so the three always describe the same read; a read that fails changes none of
/// them. Against the fake and a real Postgres. See "Features" in devflow/README.md.
/// </summary>
public sealed class FeatureStoreTests
{
    [Fact]
    public async Task AFeatureAndItsCardsAreOneRead()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        // A pull request of the runner's that waits for the owner, beside a feature with two cards.
        var waiting = new World();
        void StageAll(IReadOnlyList<int> subIssues, string title)
        {
            var world = Reading.Reading.Staging([Reading.Reading.From(waiting)], [Reading.Reading.SliceOf(waiting)]);
            var repository = world.Installations[0].Repositories[0];
            var feature = Labelled(110, subIssues: subIssues) with { Title = title };
            world = world with
            {
                Installations = [world.Installations[0] with { Repositories = [repository with { Issues = [feature, Issue(111), Issue(112), Issue(113)] }] }],
            };
            reading.Fake.Replace(Reading.Reading.Alice, world);
        }

        StageAll([111, 112], "The feature");
        await Read(reading, project);

        Assert.Single(await reading.Items(project));
        Assert.Equal([(110, "The feature")], (await StoredFeatures(reading, project)).Select(feature => (feature.Number, feature.Title)));
        Assert.Equal([(111, 1), (112, 2)], (await StoredCards(reading, project)).Select(card => (card.Number, card.Position!.Value)));
        var read = (await reading.ReadOf(project))!;

        // Everything changes, and then the sub-issues cannot be read: the read fails, and nothing
        // of it is stored, the waiting items included.
        StageAll([113, 111], "Renamed");
        reading.Host.Unavailable["SubIssues"] = true;
        Assert.Equal(ReadResult.Failed, await reading.Reader.Read(project, Reading.Reading.Alice, Ct));

        Assert.Single(await reading.Items(project));
        Assert.Equal([(110, "The feature")], (await StoredFeatures(reading, project)).Select(feature => (feature.Number, feature.Title)));
        Assert.Equal([(111, 1), (112, 2)], (await StoredCards(reading, project)).Select(card => (card.Number, card.Position!.Value)));
        var failed = (await reading.ReadOf(project))!;
        Assert.Equal(ProjectReader.HostUnavailable, failed.LastError);
        Assert.Equal(read.LastSuccessAt, failed.LastSuccessAt);

        // GitHub answers again: the next read stores all of it.
        reading.Host.Unavailable.Clear();
        await Read(reading, project);
        Assert.Equal([(110, "Renamed")], (await StoredFeatures(reading, project)).Select(feature => (feature.Number, feature.Title)));
        Assert.Equal([(111, 2), (113, 1)], (await StoredCards(reading, project)).Select(card => (card.Number, card.Position!.Value)));
    }
}
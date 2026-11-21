using System.Security.Cryptography;
using System.Text;

using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;

using static DevFlow.Tests.Cards.FeatureSupport;

namespace DevFlow.Tests.Cards;

/// <summary>
/// Picking features in each project's read: an open issue carrying the pick-up label with at least
/// one sub-issue is a feature, checked as a card is, and its direct sub-issues in the same
/// repository are its cards, in their order, each checked by the card rules and needing no label
/// of its own. Against the fake and a real Postgres. See "Features and their cards" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class FeaturePickerTests
{
    [Fact]
    public async Task ALabelledParentIsAFeature()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        // The sub-issues in an order that is not their numbers'; 12 waits for 14, which is open.
        Stage(reading,
        [
            Labelled(10, subIssues: [13, 11, 12]),
            Issue(11),
            Issue(12, blockedBy: [14]),
            Issue(13),
            Issue(14),
        ]);

        await Read(reading, project);

        var feature = Assert.Single(await StoredFeatures(reading, project));
        Assert.Equal(Reading.Reading.Alice, feature.UserId);
        Assert.Equal((10, "Issue 10", "queued", (string?)null, "none"), (feature.Number, feature.Title, feature.State, feature.Reason, feature.Priority));
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/10", feature.WebUrl);
        Assert.Equal(At.AddMinutes(10).UtcDateTime, feature.IssueCreatedAt);
        // The parent's record of what was picked, as a card's, since later slices quote its body.
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("Build the thing of issue 10.")), feature.PickedBodySha256);
        Assert.Equal("Issue 10", feature.PickedTitle);
        Assert.Null(feature.PickedEdit);
        Assert.Equal("Build the thing of issue 10.", feature.PickedBody);
        Assert.Equal((false, 0), (feature.Unlabelled, feature.FixRound));

        // Three cards, at 1, 2 and 3 in sub-issue order, each queued or waiting by the card rules;
        // the parent is not a card itself.
        var cards = await StoredCards(reading, project);
        Assert.Equal(
        [
            (11, "queued", (string?)null, (int?)10, (int?)2),
            (12, "queued", "Waits for #14", 10, 3),
            (13, "queued", null, 10, 1),
        ], cards.Select(card => (card.Number, card.State, card.Reason, card.Feature, card.Position)));
        Assert.All(cards, card => Assert.True(card.Picked));

        // None is queued as a card on its own; the feature's cards are read first, together, and its
        // first card is then its next build, by the feature's own rule, see FeatureDispatchTests
        // and ReadJobTests.
        Assert.Empty(await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));
        Assert.Equal("read", await DevFlow.Jobs.JobStore.NextKind(reading.Door, Reading.Reading.Alice, project, DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task EachCardIsCheckedLikeAnyCard()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading,
        [
            Labelled(20, subIssues: [21, 22, 23, 24]),
            Issue(21, author: Stranger),
            Issue(22, edits: [new("UCE_22", Stranger, At, EditKind.Body)]),
            Issue(23),
            Issue(24, labels: ["devflow-hold"]),
        ]);

        await Read(reading, project);

        Assert.Equal(
        [
            (21, "ignored", $"{Stranger} is not a trusted account of this project", false),
            (22, "ignored", $"Edited by {Stranger}, who is not a trusted account of this project", false),
            (23, "queued", (string?)null, true),
            (24, "held", "Held by the label devflow-hold", true),
        ], (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Reason, card.Picked)));
        Assert.Equal("queued", Assert.Single(await StoredFeatures(reading, project)).State);
        // One by someone else costs nothing beyond the list of sub-issues.
        Assert.DoesNotContain(reading.Host.Calls, call => call.Operation is "Edits" or "BlockedBy" && call.Argument == "21");
    }

    [Fact]
    public async Task AParentNotTheOwnersIgnoresTheFeature()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading,
        [
            Labelled(30, subIssues: [31, 32], author: Stranger),
            Issue(31),
            Issue(32),
            Labelled(40, subIssues: [41], edits: [new("RTE_40", Stranger, At, EditKind.Title)]),
            Issue(41),
        ]);

        await Read(reading, project);

        var features = await StoredFeatures(reading, project);
        Assert.Equal(
        [
            (30, "ignored", $"{Stranger} is not a trusted account of this project"),
            (40, "ignored", $"Edited by {Stranger}, who is not a trusted account of this project"),
        ], features.Select(feature => (feature.Number, feature.State, feature.Reason)));
        Assert.All(features, feature => Assert.Null(feature.PickedBodySha256));
        Assert.Equal(
        [
            (31, "ignored", "Its feature, #30, is not picked", (int?)30),
            (32, "ignored", "Its feature, #30, is not picked", 30),
            (41, "ignored", "Its feature, #40, is not picked", 40),
        ], (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Reason, card.Feature)));
        // Nothing of a feature that is not picked is read past its sub-issues.
        Assert.DoesNotContain(reading.Host.Calls, call => call.Operation is "Edits" or "BlockedBy" && call.Argument is "31" or "32" or "41");
    }

    [Fact]
    public async Task WhatIsNotACardOfTheFeature()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        var pull = new PullRequest(57, "A pull request among them", Owner, [], new string('a', 40), "57-x", "main", false, "",
            new Uri("https://github.com/Adamniels/Personal-platform/pull/57"), At, PullRequestState.Open);
        // 51 and 56 are cards; 52 is in another repository; 53 is a card that has sub-issues of its
        // own, and 54 is one of them; 55 is closed; 57 is a pull request.
        static IReadOnlyList<FakeIssue> World(bool fiftySixOpen) =>
        [
            Labelled(50, subIssues: [51, 53, 55, 56, 57], elsewhere: [new(5, Elsewhere)]),
            Issue(51),
            Labelled(53, subIssues: [54]),
            Issue(54),
            Issue(55, open: false),
            Issue(56, open: fiftySixOpen),
        ];
        Stage(reading, World(fiftySixOpen: true), [pull], [Issue(5)]);
        await Read(reading, project);

        var cards = await StoredCards(reading, project);
        Assert.Equal(
        [
            (51, "queued", (string?)null, (int?)50, (int?)1),
            (53, "queued", null, 50, 2),
            (54, "ignored", "Only a feature's direct sub-issues in this repository are its cards", null, null),
            (56, "queued", null, 50, 3),
        ], cards.Select(card => (card.Number, card.State, card.Reason, card.Feature, card.Position)));
        // 53 is one card of the feature, not a feature of its own.
        Assert.Equal([50], (await StoredFeatures(reading, project)).Select(feature => feature.Number));
        // The one elsewhere is not stored, its number being another repository's, and the feature
        // names it.
        Assert.Equal(
            "Only a feature's direct sub-issues in this repository are its cards: Adamniels/elsewhere#5 is not one",
            Assert.Single(await StoredFeatures(reading, project)).Reason);

        // Dev flow built 56, and it was closed: it stays a card of the feature, where it is. 55,
        // closed and never built, is not one.
        await SetState(reading, project, 56, "done");
        Stage(reading, World(fiftySixOpen: false), [pull], [Issue(5)]);
        await Read(reading, project);

        Assert.Equal(
            [(51, (int?)1, "queued"), (53, 2, "queued"), (56, 3, "done")],
            (await StoredCards(reading, project)).Where(card => card.Feature == 50).Select(card => (card.Number, card.Position, card.State)));
    }

    [Fact]
    public async Task HoldOnTheParentHoldsTheFeature()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading,
        [
            Labelled(60, subIssues: [61, 62, 63], more: ["DevFlow-Hold"]),
            Issue(61),
            Issue(62, blockedBy: [64]),
            Issue(63, author: Stranger),
            Issue(64),
        ]);

        await Read(reading, project);

        var feature = Assert.Single(await StoredFeatures(reading, project));
        Assert.Equal(("held", "Held by the label devflow-hold"), (feature.State, feature.Reason));
        Assert.NotNull(feature.PickedBodySha256);
        // Every card that is the owner's is held with the feature; one by someone else is still
        // ignored for that.
        Assert.Equal(
        [
            (61, "held", "The feature is held by the label devflow-hold"),
            (62, "held", "The feature is held by the label devflow-hold"),
            (63, "ignored", $"{Stranger} is not a trusted account of this project"),
        ], (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Reason)));

        // The hold taken off, the feature and its cards are queued again.
        Stage(reading, [Labelled(60, subIssues: [61, 62, 63]), Issue(61), Issue(62, blockedBy: [64]), Issue(63, author: Stranger), Issue(64)]);
        await Read(reading, project);

        Assert.Equal("queued", Assert.Single(await StoredFeatures(reading, project)).State);
        Assert.Equal(
            [(61, "queued", (string?)null), (62, "queued", "Waits for #64")],
            (await StoredCards(reading, project)).Take(2).Select(card => (card.Number, card.State, card.Reason)));
    }

    [Fact]
    public async Task ALabelledSubIssueIsTheFeaturesCard()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, [Labelled(70, subIssues: [71, 72]), Labelled(71), Issue(72)]);

        await Read(reading, project);

        Assert.Equal(
            [(71, (int?)70, (int?)1), (72, 70, 2)],
            (await StoredCards(reading, project)).Select(card => (card.Number, card.Feature, card.Position)));
        Assert.Equal([70], (await StoredFeatures(reading, project)).Select(feature => feature.Number));
    }

    [Fact]
    public async Task AParentWithoutSubIssuesIsACard()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, [Labelled(80), Labelled(90)]);
        await Read(reading, project);

        Assert.Empty(await StoredFeatures(reading, project));
        Assert.Equal(
            [(80, "queued", (int?)null), (90, "queued", null)],
            (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Feature)));
        Assert.Equal([80, 90], await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));

        // 80 has started as a card, and then both get sub-issues: 80 stays a card, and its
        // sub-issues are not cards; 90 had not started, and is a feature now.
        await SetState(reading, project, 80, "building");
        Stage(reading, [Labelled(80, subIssues: [81, 82]), Issue(81), Issue(82), Labelled(90, subIssues: [91]), Issue(91)]);
        await Read(reading, project);

        Assert.Equal([90], (await StoredFeatures(reading, project)).Select(feature => feature.Number));
        Assert.Equal(
        [
            (80, "building", (string?)null, (int?)null, false),
            (81, "ignored", "Its parent, #80, is being built as a card", null, false),
            (82, "ignored", "Its parent, #80, is being built as a card", null, false),
            (91, "queued", null, 90, false),
        ], (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Reason, card.Feature, card.Unlabelled)));
    }

    [Fact]
    public async Task TakingTheLabelOffTheParent()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, [Labelled(100, subIssues: [101, 102, 103]), Issue(101), Issue(102), Issue(103)]);
        await Read(reading, project);
        // 102 is under way, as later slices will set it.
        await SetState(reading, project, 102, "building");

        Stage(reading, [Issue(100, subIssues: [101, 102, 103]), Issue(101), Issue(102), Issue(103)]);
        await Read(reading, project);

        // The cards that had not started are gone with the feature; the one under way finishes,
        // marked unlabelled, as a card's own label does, and its feature is kept for it.
        Assert.Equal(
            [(102, "building", (int?)100, true)],
            (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Feature, card.Unlabelled)));
        Assert.True(Assert.Single(await StoredFeatures(reading, project)).Unlabelled);

        // The label put back, it is the owner's feature again, and its cards return.
        Stage(reading, [Labelled(100, subIssues: [101, 102, 103]), Issue(101), Issue(102), Issue(103)]);
        await Read(reading, project);
        Assert.False(Assert.Single(await StoredFeatures(reading, project)).Unlabelled);
        Assert.Equal(
            [(101, "queued", false), (102, "building", false), (103, "queued", false)],
            (await StoredCards(reading, project)).Select(card => (card.Number, card.State, card.Unlabelled)));

        // Off again, and once its run has ended and removed the card, nothing of the feature is
        // left.
        Stage(reading, [Issue(100, subIssues: [101, 102, 103]), Issue(101), Issue(102), Issue(103)]);
        await Read(reading, project);
        await using (var admin = await reading.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "DELETE FROM cards WHERE project_id = @project AND unlabelled", new { project }, cancellationToken: Ct));
        }

        await Read(reading, project);
        Assert.Empty(await StoredCards(reading, project));
        Assert.Empty(await StoredFeatures(reading, project));
    }
}
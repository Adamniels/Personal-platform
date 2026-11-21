using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Projects;

namespace DevFlow.Tests.Cards;

/// <summary>
/// <see cref="LabelActors.Judge"/> alone, the one rule every label is read by: the newest change of
/// it by a trusted account decides, and it fails closed. See "How a card is picked" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class LabelJudgeTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private static readonly TrustedAccounts Trusted = TrustedAccounts.Of("octo-owner", ["octo-friend"]);

    private static LabelEvent Added(string label, string actor, int minute) => new(label, LabelChange.Added, actor, At.AddMinutes(minute));

    private static LabelEvent Removed(string label, string actor, int minute) => new(label, LabelChange.Removed, actor, At.AddMinutes(minute));

    [Fact]
    public void LabelsAndLoginsAreOneWhateverTheirCase()
    {
        Assert.Equal(new LabelCount(true, null), LabelActors.Judge("devflow", LabelKind.SwitchesOn, ["DevFlow"], [Added("DEVFLOW", "Octo-Friend", 0)], Trusted));
        Assert.Equal(new LabelCount(true, "octo-stranger"), LabelActors.Judge("devflow-hold", LabelKind.HoldsBack, [], [Added("Devflow-Hold", "OCTO-OWNER", 0), Removed("devflow-hold", "octo-stranger", 1)], Trusted));
    }

    [Fact]
    public void TheNewestByTimeDecidesHoweverTheyAreListed()
    {
        // I took it off after a stranger put it back: listed out of order, it is still off for me.
        Assert.Equal(new LabelCount(false, "octo-stranger"), LabelActors.Judge("devflow", LabelKind.SwitchesOn, ["devflow"],
            [Added("devflow", "octo-stranger", 2), Removed("devflow", "octo-owner", 1), Added("devflow", "octo-owner", 0)], Trusted));
    }

    [Fact]
    public void ADeletedAccountIsNotTrusted() =>
        Assert.Equal(new LabelCount(false, "ghost"), LabelActors.Judge("devflow", LabelKind.SwitchesOn, ["devflow"], [Added("devflow", "ghost", 0)], Trusted));

    [Fact]
    public void ALabelThatIsOffSwitchesNothingOn() =>
        Assert.Equal(new LabelCount(false, null), LabelActors.Judge("devflow", LabelKind.SwitchesOn, [], [Added("devflow", "octo-owner", 0)], Trusted));

    [Fact]
    public void WithoutALinkNobodyIsTrustedAndAHoldOnStillHolds()
    {
        var nobody = TrustedAccounts.Of(null, ["octo-friend"]);
        Assert.False(LabelActors.Judge("devflow", LabelKind.SwitchesOn, ["devflow"], [Added("devflow", "octo-friend", 0)], nobody).Counts);
        Assert.True(LabelActors.Judge("devflow-hold", LabelKind.HoldsBack, ["devflow-hold"], [], nobody).Counts);
        Assert.False(LabelActors.Judge("devflow-hold", LabelKind.HoldsBack, [], [Added("devflow-hold", "octo-friend", 0), Removed("devflow-hold", "octo-stranger", 1)], nobody).Counts);
    }

    [Fact]
    public void AHoldNobodyTrustedPutOnIsReleasedByAnyone() =>
        Assert.Equal(new LabelCount(false, null), LabelActors.Judge("devflow-hold", LabelKind.HoldsBack, [], [Added("devflow-hold", "octo-stranger", 0), Removed("devflow-hold", "octo-stranger", 1)], Trusted));
}
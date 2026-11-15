using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// The pull request body as <c>.claude/skills/pick-up-slice/templates.md</c> lays it out: the
/// summary, the slice it closes, and the decisions the agent made.
/// </summary>
public sealed class BodyTests
{
    private static IReadOnlyList<BodyDecision> Decisions(string section) =>
        PullRequestBody.Decisions($"## Summary\n\nText.\n\nCloses #7\n\n## Decisions the agent made\n\n{section}\n\n## Verification\n\nNothing.\n");

    private static OwnerDecision Only(string section) => Assert.Single(Decisions(section)).Text;

    [Fact]
    public void TheSummaryAndTheSliceAreRead()
    {
        const string body = "## Summary\n\nThe first line\nand its second line.\n\nBest read in this order:\n\n1. One.\n\nCloses #277\n\n## Tasks\n\n- [ ] One.\n";
        Assert.Equal("The first line and its second line.", PullRequestBody.Summary(body));
        Assert.Equal(277, RunnerReader.Slice(body));

        // The Closes line is never the summary, wherever it is.
        const string closesFirst = "## Summary\n\nCloses #12\n\nWhat it does.\n";
        Assert.Equal("What it does.", PullRequestBody.Summary(closesFirst));
        Assert.Equal(12, RunnerReader.Slice(closesFirst));
        Assert.Equal("What it does.", PullRequestBody.Summary("## Summary\n\nCloses #12\nWhat it does.\n"));

        // Line endings as a browser sends them.
        Assert.Equal("One. Two.", PullRequestBody.Summary("## Summary\r\n\r\nOne.\r\nTwo.\r\n\r\nCloses #3\r\n"));
        Assert.Equal(3, RunnerReader.Slice("## Summary\r\n\r\nOne.\r\n\r\nCloses #3\r\n"));

        // One line, at most 300 characters.
        var longSummary = PullRequestBody.Summary("## Summary\n\n" + new string('x', 1000) + "\n");
        Assert.Equal(300, longSummary.Length);
        Assert.EndsWith("…", longSummary, StringComparison.Ordinal);
        Assert.StartsWith(new string('x', 299), longSummary, StringComparison.Ordinal);
        // A character outside the basic plane is never cut in half.
        var emoji = PullRequestBody.Summary("## Summary\n\n" + new string('x', 298) + "😀😀\n");
        Assert.Equal(new string('x', 298) + "…", emoji);

        // No summary, no slice.
        Assert.Equal("", PullRequestBody.Summary(""));
        Assert.Null(RunnerReader.Slice(""));
        Assert.Equal("", PullRequestBody.Summary("## Summary\n\nCloses #3\n"));
        Assert.Null(RunnerReader.Slice("## Summary\n\nThe text.\n\n## Tasks\n\nCloses #3\n"));
        Assert.Null(RunnerReader.Slice("## Summary\n\nCloses #3 and #4\n"));
        Assert.Null(RunnerReader.Slice("## Summary\n\nCloses #99999999999\n"));
        Assert.Equal("", PullRequestBody.Summary("### Summary\n\nThe text.\n"));
    }

    [Fact]
    public void EachDecisionIsSplitIntoWhatWasChosenTheAlternativeAndWhy()
    {
        Assert.Equal(new OwnerDecision("Chose A.", "B.", "C."), Only("- Chose A. Alternative: B. Why: C."));
        Assert.Equal(new OwnerDecision("Chose A.", "B or D.", "C."), Only("- Chose A. Alternatives: B or D. Why: C."));

        // Across lines.
        Assert.Equal(
            new OwnerDecision("Chose A, for the reason below.", "B, which costs more.", "C, the stricter one."),
            Only("- Chose A, for the reason\n  below. Alternative: B, which costs\n  more.\n  Why: C, the stricter\n  one."));

        // With nested bullets under it, joined to it.
        Assert.Equal(
            new OwnerDecision("Chose A.", "B.", "C, for these: - one case - another case"),
            Only("- Chose A. Alternative: B. Why: C, for these:\n  - one case\n  - another case"));

        // A Why before the Alternative is part of what was chosen; the first after it splits.
        Assert.Equal(new OwnerDecision("Why: not here. Chose A.", "B.", "C. Why: also C."), Only("- Why: not here. Chose A. Alternative: B. Why: C. Why: also C."));

        // Several, in order, each with its own lines.
        var several = Decisions("- First. Alternative: one. Why: two.\n- Second. Alternative: three. Why: four.\n  More of four.\n* Third. Alternative: five. Why: six.");
        Assert.Equal(
            [new("First.", "one.", "two."), new("Second.", "three.", "four. More of four."), new("Third.", "five.", "six.")],
            several.Select(decision => decision.Text));
    }

    [Fact]
    public void ADecisionThatDoesNotFitTheFormIsKeptWhole()
    {
        Assert.Equal(new OwnerDecision("Only what was chosen.", null, null), Only("- Only what was chosen."));
        Assert.Equal(new OwnerDecision("Chose A. Why: C.", null, null), Only("- Chose A. Why: C."));
        Assert.Equal(new OwnerDecision("Chose A.", "B.", null), Only("- Chose A. Alternative: B."));
        Assert.Equal(new OwnerDecision("Chose A. Alternative:B.", null, null), Only("- Chose A. Alternative:B."));

        // Text outside a bullet is a decision of its own, never dropped.
        var mixed = Decisions("A sentence before the list.\n\n- Chose A. Alternative: B. Why: C.\n\nA paragraph after it.\n\n  An indented one.");
        Assert.Equal(
            ["A sentence before the list.", "Chose A.", "A paragraph after it. An indented one."],
            mixed.Select(decision => decision.Text.Chosen));

        // A line straight after a bullet, without indent, continues it.
        Assert.Equal(new OwnerDecision("Chose A.", "B.", "C, still."), Only("- Chose A. Alternative: B.\nWhy: C, still."));
    }

    [Fact]
    public void OnlyDecisionsMarkedForTheOwnerAreHis()
    {
        var read = Decisions(string.Join('\n',
            "- **owner-decision** Chose A. Alternative: B. Why: C.",
            "- **owner-decision.** Chose D. Alternative: E. Why: F.",
            "- **owner-decision:** Chose G.",
            "- **owner-decision: chose H.** Alternative: I. Why: J.",
            "- Chose K, marked **owner-decision** in the middle.",
            "- Chose L.",
            "- Not marked owner-decision, since it narrows what is accepted.",
            "- Chose M, an `owner-decision` in code.",
            "- **Owner-decision** in another case."));

        Assert.Equal(
            [
                new OwnerDecision("Chose A.", "B.", "C."),
                new OwnerDecision("Chose D.", "E.", "F."),
                new OwnerDecision("Chose G.", null, null),
                new OwnerDecision("**chose H.**", "I.", "J."),
                new OwnerDecision("Chose K, marked in the middle.", null, null),
            ],
            read.Where(decision => decision.IsOwners).Select(decision => decision.Text));
        Assert.Equal(
            ["Chose L.", "Not marked owner-decision, since it narrows what is accepted.", "Chose M, an `owner-decision` in code.", "**Owner-decision** in another case."],
            read.Where(decision => !decision.IsOwners).Select(decision => decision.Text.Chosen));

        // Through the reader: the owner's decisions, and how many others there are.
        var item = new World { Pull = new World().Pull with { Body = World.Body.Replace("- Chose D.", "- Chose X.\n- Chose D.", StringComparison.Ordinal) } }.Read();
        Assert.NotNull(item);
        Assert.Equal([new OwnerDecision("Chose A.", "B.", "C.")], item.OwnerDecisions);
        Assert.Equal(2, item.OtherDecisions);
    }

    [Fact]
    public void ABodyWithoutTheHeadingHasNoDecisions()
    {
        Assert.Empty(PullRequestBody.Decisions(""));
        Assert.Empty(PullRequestBody.Decisions("## Summary\n\nText.\n\n- **owner-decision** Chose A.\n"));
        foreach (var heading in new[] { "### Decisions the agent made", "## Decisions the agent made:", " ## Decisions the agent made", "## decisions the agent made", "Decisions the agent made" })
        {
            Assert.Empty(PullRequestBody.Decisions($"## Summary\n\nText.\n\n{heading}\n\n- **owner-decision** Chose A.\n"));
        }

        // The section ends at the next heading of its level or above, and a heading in a fence
        // is not one.
        var read = PullRequestBody.Decisions(
            "## Decisions the agent made\n\n- Chose A.\n\n```\n## Not a heading\n```\n\n### A sub heading\n\n# Verification\n\n- Not a decision.\n");
        Assert.Equal(["Chose A.", "``` ## Not a heading ```", "### A sub heading"], read.Select(decision => decision.Text.Chosen));

        // Through the reader.
        var item = new World { Pull = new World().Pull with { Body = "## Summary\n\nText.\n\nCloses #7\n" } }.Read();
        Assert.NotNull(item);
        Assert.Empty(item.OwnerDecisions);
        Assert.Equal(0, item.OtherDecisions);
    }

    [Fact]
    public void TheReviewersViewIsTheLatestVerdictsForTheOwnerEntries()
    {
        var two = "- **The first.** I agree.\n  It holds.\n- The second.";
        var item = (new World() with
        {
            Comments =
            [
                World.Verdict(1, "changes", World.Older, World.At, "- From an older round."),
                World.Verdict(2, "approve", World.Head, World.At.AddMinutes(1), two),
            ],
        }).Read();
        Assert.NotNull(item);
        Assert.Equal(["**The first.** I agree. It holds.", "The second."], item.ReviewerView);

        Assert.Empty(PullRequestBody.ReviewerView("## For the owner\n\nNone. Every decision is within Settled.\n\n## Replies\n\nNone.\n"));
        Assert.Empty(PullRequestBody.ReviewerView("## Blocking\n\n- A finding.\n"));
        Assert.Empty(PullRequestBody.ReviewerView(""));
        Assert.Empty(new World().Read()!.ReviewerView);
    }

    [Fact]
    public void NoneMeansNoDecisions()
    {
        Assert.Empty(Decisions("None."));
        Assert.Empty(Decisions("None. Settled and the docs answered everything."));
        Assert.Empty(Decisions("\n\n  None.\n"));
        Assert.Empty(Decisions(""));
        Assert.Single(Decisions("Nothing to say."));
    }
}
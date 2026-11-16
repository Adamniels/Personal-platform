using System.Text.Json.Nodes;

using DevFlow.CodeHost;
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
    /// <summary>An image line as <c>scripts/agent/screenshots.sh</c> prints one, for
    /// <paramref name="address"/>.</summary>
    private static string Image(string name, string address) => $"![{name}]({address})";

    private const string Shots = "https://github.com/Adamniels/Personal-platform/blob/pr-screenshots";

    [Fact]
    public void TheScreenshotsNamedInTheBodyAreRead()
    {
        // Pull request 305's body as GitHub answered it, recorded in contracts/github/pull.json: a
        // before and an after pair of the shell, and a pair of the banner.
        var recorded = JsonNode.Parse(File.ReadAllText(Tests.Repository.Path("contracts", "github", "pull.json")))!;
        var body = (string)recorded["body"]!;

        var names = PullRequestBody.Screenshots(body, "Adamniels", "Personal-platform", 305);

        Assert.Equal(
            [
                "devflow-shell-desktop-before", "devflow-shell-phone-before",
                "devflow-shell-desktop", "devflow-shell-phone",
                "devflow-fake-banner-desktop", "devflow-fake-banner-phone",
            ],
            names);

        // Paired by the part before -desktop and -phone, in the order each pair first appears,
        // desktop first; a before pair is a pair of its own.
        Assert.Equal(
            ["devflow-shell-before: devflow-shell-desktop-before devflow-shell-phone-before", "devflow-shell: devflow-shell-desktop devflow-shell-phone", "devflow-fake-banner: devflow-fake-banner-desktop devflow-fake-banner-phone"],
            Screenshots.Group(names).Select(Describe));

        // Each name once, however often the body shows it; a name with one of the two is a pair
        // of one; a name with neither is its own; phone written first is still shown second.
        var again = string.Join('\n',
            Image("a-phone", $"{Shots}/42/a-phone.png?raw=true"),
            Image("a-desktop", $"{Shots}/42/a-desktop.png?raw=true"),
            Image("a-desktop again", $"{Shots}/42/a-desktop.png?raw=true"),
            Image("b-desktop", $"{Shots}/42/b-desktop.png?raw=true"),
            Image("diagram", $"{Shots}/42/diagram.png?raw=true"),
            Image("c-phone", $"{Shots}/42/c-phone.png?raw=true"));
        var read = PullRequestBody.Screenshots(again, "Adamniels", "Personal-platform", 42);
        Assert.Equal(["a-phone", "a-desktop", "b-desktop", "diagram", "c-phone"], read);
        Assert.Equal(["a: a-desktop a-phone", "b: b-desktop", "diagram: diagram", "c: c-phone"], Screenshots.Group(read).Select(Describe));

        // Through the reader: the item carries the names, in the body's order.
        var world = new World();
        var item = (world with { Pull = world.Pull with { Body = World.Body + "\n" + again } }).Read();
        Assert.NotNull(item);
        Assert.Equal(["a-phone", "a-desktop", "b-desktop", "diagram", "c-phone"], item.Screenshots);
        Assert.Empty(world.Read()!.Screenshots);
    }

    [Fact]
    public void OnlyThisPullRequestsOwnScreenshotsCount()
    {
        var body = string.Join('\n',
            "## Verification",
            "",
            Image("kept-desktop", $"{Shots}/42/kept-desktop.png?raw=true"),
            // Another repository, another owner, the owner or name in another case.
            Image("x", "https://github.com/Adamniels/other-repository/blob/pr-screenshots/42/x.png?raw=true"),
            Image("x", "https://github.com/someone/Personal-platform/blob/pr-screenshots/42/x.png?raw=true"),
            Image("x", "https://github.com/adamniels/personal-platform/blob/pr-screenshots/42/x.png?raw=true"),
            Image("x", "https://github.com.evil.test/Adamniels/Personal-platform/blob/pr-screenshots/42/x.png?raw=true"),
            Image("x", "http://github.com/Adamniels/Personal-platform/blob/pr-screenshots/42/x.png?raw=true"),
            // Another branch.
            Image("x", "https://github.com/Adamniels/Personal-platform/blob/main/42/x.png?raw=true"),
            Image("x", "https://github.com/Adamniels/Personal-platform/blob/pr-screenshots-2/42/x.png?raw=true"),
            // Another pull request's folder, or none.
            Image("x", $"{Shots}/41/x.png?raw=true"),
            Image("x", $"{Shots}/420/x.png?raw=true"),
            Image("x", $"{Shots}/x.png?raw=true"),
            // A path that climbs, or goes deeper.
            Image("x", $"{Shots}/42/../41/x.png?raw=true"),
            Image("x", $"{Shots}/42/%2e%2e/x.png?raw=true"),
            Image("x", $"{Shots}/42/..png?raw=true"),
            Image("x", $"{Shots}/42/deeper/x.png?raw=true"),
            // A name with anything but lowercase letters, digits and hyphens, or none.
            Image("x", $"{Shots}/42/Upper-desktop.png?raw=true"),
            Image("x", $"{Shots}/42/under_score.png?raw=true"),
            Image("x", $"{Shots}/42/dot.ted.png?raw=true"),
            Image("x", $"{Shots}/42/sp%20ace.png?raw=true"),
            Image("x", $"{Shots}/42/caf\u00e9.png?raw=true"),
            Image("x", $"{Shots}/42/.png?raw=true"),
            // Not exactly the address: another ending, a query or fragment more, or not a png.
            Image("x", $"{Shots}/42/x.png"),
            Image("x", $"{Shots}/42/x.png?raw=true&y=1"),
            Image("x", $"{Shots}/42/x.png?raw=true#y"),
            Image("x", $"{Shots}/42/x.jpg?raw=true"),
            // A link and not an image, and an image inside a fenced block.
            $"[x]({Shots}/42/link-desktop.png?raw=true)",
            "```",
            Image("x", $"{Shots}/42/fenced-desktop.png?raw=true"),
            "```");

        Assert.Equal(["kept-desktop"], PullRequestBody.Screenshots(body, "Adamniels", "Personal-platform", 42));
        // The same body read for pull request 41, or for the other repository, finds theirs.
        Assert.Equal(["x"], PullRequestBody.Screenshots(body, "Adamniels", "Personal-platform", 41));
        Assert.Equal(["x"], PullRequestBody.Screenshots(body, "Adamniels", "other-repository", 42));
        Assert.Empty(PullRequestBody.Screenshots(body, "Adamniels", "another", 42));
        Assert.Empty(PullRequestBody.Screenshots("", "Adamniels", "Personal-platform", 42));
        Assert.Empty(PullRequestBody.Screenshots(null, "Adamniels", "Personal-platform", 42));

        // The reader reads them for the pull request's own repository and number only, and none
        // when it is not told the repository.
        var world = new World();
        var other = world with { Pull = world.Pull with { Body = body } };
        Assert.Equal(["kept-desktop"], other.Read()!.Screenshots);
        Assert.Equal(["x"], (other with { Repository = new CodeHost.Repository(41, 9002, "Adamniels", "other-repository") }).Read()!.Screenshots);
        Assert.Empty(RunnerReader.Read(other.Input with { Repository = null! })!.Screenshots);
    }

    private static string Describe(ScreenshotGroup group) => $"{group.Name}: {string.Join(' ', group.Images)}";
}
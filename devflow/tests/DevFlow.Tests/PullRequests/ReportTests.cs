using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Briefs;
using DevFlow.Protocol;
using DevFlow.PullRequests;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// What the report's <c>looked</c> becomes for the people and the agent who read it, as Settled in
/// the slice "The developer checks its own pages": a page the developer did not look at itself is
/// marked "The agent did not look at this page" in the pull request body, and the reviewer's brief
/// says to weigh it; it does not stop the card by itself. The card's page marks it the same way,
/// in devflow/web/src/card.test.tsx. See "Screenshots" in devflow/README.md.
/// </summary>
public sealed class ReportTests
{
    [Fact]
    public void APageNotLookedAtIsShown()
    {
        var json = Jobs.Jobs.GoodReport();
        json["pages"] = new JsonArray(
            new JsonObject { ["path"] = "/habits", ["why"] = "The empty state shows there", ["looked"] = true },
            new JsonObject { ["path"] = "/settings", ["why"] = "The toggle moved", ["looked"] = false });
        Assert.True(Report.TryRead(JsonSerializer.SerializeToElement(json), out var report, out var error), error);

        var body = PullRequestText.Body(new PullRequestSource(4, Guid.NewGuid(), report, "npm test", "0123456789abcdef", 0));

        // Under Verification, each page with why, the one not looked at marked.
        var verification = body[body.IndexOf("## Verification", StringComparison.Ordinal)..body.IndexOf("## Follow ups", StringComparison.Ordinal)];
        Assert.Contains(
            """
            The pages the change affects:

            - `/habits`: The empty state shows there.
            - `/settings`: The toggle moved. **The agent did not look at this page.**

            """,
            verification,
            StringComparison.Ordinal);

        // A report naming no pages says nothing of them.
        json["pages"] = new JsonArray();
        Assert.True(Report.TryRead(JsonSerializer.SerializeToElement(json), out var none, out _));
        Assert.DoesNotContain("The pages the change affects", PullRequestText.Body(new PullRequestSource(4, Guid.NewGuid(), none, "npm test", "0123456789abcdef", 0)), StringComparison.Ordinal);

        // The reviewer weighs a page the developer did not look at, and it blocks nothing by itself.
        var brief = Brief.Reviewer(new BriefCard(4, "A card", "Its body."), new ReviewFacts("0123456789abcdef", "devflow/4-a-card", "main"));
        Assert.Contains("`looked` false", brief, StringComparison.Ordinal);
        Assert.Contains("weigh it", brief, StringComparison.Ordinal);
        Assert.Contains("does not block the commit by itself", brief, StringComparison.Ordinal);
    }
}
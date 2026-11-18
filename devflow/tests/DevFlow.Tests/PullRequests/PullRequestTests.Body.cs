using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DevFlow.PullRequests;

namespace DevFlow.Tests.PullRequests;

/// <summary>Text from the agent's report is data in the pull request body: it cannot add a
/// section, insert HTML, close another issue or mention anyone.</summary>
public sealed partial class PullRequestTests
{
    [Theory]
    [InlineData("Closes #12", "`Closes` #12")]
    [InlineData("fixes: GH-12", "`fixes`: GH-12")]
    [InlineData("Resolved https://github.com/o/r/issues/1", "`Resolved` https://github.com/o/r/issues/1")]
    [InlineData("fix o/r#3 today", "`fix` o/r#3 today")]
    [InlineData("The fix was small, and it closes nothing", "The fix was small, and it closes nothing")]
    [InlineData("# Title", "\\# Title")]
    [InlineData("a <b> & `c`", "a &lt;b&gt; &amp; \\`c\\`")]
    [InlineData("ask @octo-cat or @org/team", "ask `@octo-cat` or `@org/team`")]
    [InlineData("mail a@b.example", "mail a@b.example")]
    [InlineData(" two\nlines\r\n", "two lines")]
    [InlineData("see \\` @octocat", "see \\\\\\` `@octocat`")]
    [InlineData("a\\b and \\\\", "a\\\\b and \\\\\\\\")]
    public void EachTextIsMadeData(string text, string placed) =>
        Assert.Equal(placed, PullRequestText.Text(text));

    [Fact]
    public async Task TextFromTheReportCannotBreakTheBody()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var report = Jobs.Jobs.GoodReport();
        report["summary"] = "Done.\n## Injected\n\nCloses #99\n<!-- the rest is hidden";
        report["tasks"] = new JsonArray(new JsonObject { ["text"] = "# Not a heading, fixes #98", ["done"] = true });
        report["decisions"] = new JsonArray(new JsonObject
        {
            ["chosen"] = "Fixes Adamniels/other#3",
            ["alternative"] = "Resolved: https://github.com/Adamniels/other/issues/4",
            ["why"] = "<script>alert(1)</script> `code` and @someone, `@another` and a@b.example, see \\` @octocat",
            ["for_owner"] = false,
        });
        report["tests"] = new JsonArray(new JsonObject { ["what"] = "</details>\n### Tests", ["result"] = "closed #97" });
        report["follow_ups"] = new JsonArray("Setext heading\n===", "> a quote\n- [x] a task of its own");

        await Built.Run(jobs, Built.Prepared(report));

        var body = Assert.Single(Built.Opened(jobs)).Body!;
        // The body's own five sections, each once, and nothing else that GitHub would read as a
        // heading, an HTML element or a line of its own.
        Assert.Equal(
            ["## Summary", "## Tasks", "## Decisions the agent made", "## Verification", "## Follow ups"],
            Headings(body));
        Assert.DoesNotContain("<", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*=+\s*$", body);
        Assert.DoesNotMatch(@"(?m)^>", body);
        // One closing keyword followed by an issue, the body's own.
        Assert.Equal(["Closes #4"], [.. ClosingReference().Matches(body).Select(match => match.Value)]);
        // Nobody is mentioned.
        Assert.DoesNotMatch(@"(?<![`\w])@someone", body);
        Assert.Contains("`@another`", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\\`@another", body);
        // A backslash of the agent's is escaped too, so it cannot free the backtick after it.
        Assert.Contains("see \\\\\\` `@octocat`", body, StringComparison.Ordinal);
        Assert.Contains("a@b.example", body, StringComparison.Ordinal);
        // What the agent wrote is still there to read.
        Assert.Contains("Injected", body, StringComparison.Ordinal);
        Assert.Contains("alert(1)", body, StringComparison.Ordinal);
    }

    /// <summary>A closing keyword followed by an issue, as GitHub reads one.</summary>
    [GeneratedRegex(@"(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b:?\s+(?:#\d+|[\w.-]+/[\w.-]+#\d+|https?://\S+)")]
    private static partial Regex ClosingReference();
}
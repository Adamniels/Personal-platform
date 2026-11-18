using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Reviews;

/// <summary>
/// A reviewer's verdict is read strictly into its form, and one that does not match is a failure in
/// the run, never a verdict. See "Review" in devflow/README.md.
/// </summary>
public sealed class VerdictTests
{
    /// <summary>A verdict in its form with <paramref name="change"/> made to it.</summary>
    private static JsonElement Changed(Action<JsonObject> change)
    {
        var verdict = Built.Verdict("changes");
        change(verdict);
        return JsonSerializer.SerializeToElement(verdict);
    }

    public static TheoryData<string, string> NotInItsForm() => new()
    {
        { "no blocking finding on changes", "verdict: changes needs at least one blocking finding" },
        { "a blocking finding on approve", "verdict: approve cannot have a blocking finding" },
        { "a word that is not a verdict", "verdict:" },
        { "a missing summary", "summary" },
        { "an unknown field", "commit" },
        { "a negative line", "findings: a line is 0 or more" },
        { "a finding without why", "why" },
        { "a null view", "for_owner" },
        { "not an object", "the verdict is not a JSON object" },
    };

    [Theory]
    [MemberData(nameof(NotInItsForm))]
    public void AVerdictIsCheckedAgainstItsFormField(string what, string error)
    {
        var verdict = what switch
        {
            "no blocking finding on changes" => Changed(v => v["findings"] = new JsonArray(Built.Finding(blocking: false))),
            "a blocking finding on approve" => Changed(v => v["verdict"] = "approve"),
            "a word that is not a verdict" => Changed(v => v["verdict"] = "lgtm"),
            "a missing summary" => Changed(v => v.Remove("summary")),
            // The commit a verdict is for is the worker's, so the form has no place for one.
            "an unknown field" => Changed(v => v["commit"] = new string('b', 40)),
            "a negative line" => Changed(v => v["findings"]![0]!["line"] = -1),
            "a finding without why" => Changed(v => v["findings"]![0]!.AsObject().Remove("why")),
            "a null view" => Changed(v => v["for_owner"] = new JsonArray((JsonNode?)null)),
            _ => JsonSerializer.SerializeToElement("approve"),
        };

        Assert.False(ReviewVerdict.TryRead(verdict, out var read, out var said), what);
        Assert.Null(read);
        Assert.Contains(error, said, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerdictInItsFormReads()
    {
        var verdict = Built.Verdict("changes", Built.Finding(), Built.Finding(blocking: false, file: "", line: 0, what: "Name the component", why: "Two lists use it"));

        Assert.True(ReviewVerdict.TryRead(JsonSerializer.SerializeToElement(verdict), out var read, out var error), error);
        Assert.Equal(ReviewVerdictKind.Changes, read.Verdict);
        Assert.Equal("changes", read.Word);
        Assert.Equal([true, false], read.Findings.Select(finding => finding.Blocking));
        Assert.Equal(("web/src/list.tsx", 42), (read.Findings[0].File, read.Findings[0].Line));
        Assert.Equal("Show it on every list", Assert.Single(read.ForOwner).Decision);

        // Approve with a nit is in its form.
        Assert.True(ReviewVerdict.TryRead(JsonSerializer.SerializeToElement(Built.Verdict("approve", Built.Finding(blocking: false))), out _, out error), error);
    }

    [Fact]
    public async Task AVerdictIsCheckedAgainstItsForm()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Built.Run(jobs, Built.Prepared());
        var notAVerdict = Built.Verdict("changes");
        notAVerdict["findings"] = new JsonArray(Built.Finding(blocking: false));

        await Built.Review(jobs, notAVerdict);

        // A failure in the run: the card stops with why, and no verdict is taken from it.
        var run = (await jobs.Runs())[1];
        Assert.Equal("review", run.Kind);
        Assert.Equal("report_invalid", run.Result);
        Assert.Equal("The verdict did not match its form: verdict: changes needs at least one blocking finding", run.Reason);
        var card = await Built.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(run.Reason, card.Reason);
    }
}
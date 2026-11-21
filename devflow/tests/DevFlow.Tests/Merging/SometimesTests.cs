using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Tests.PullRequests;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Merging;

/// <summary>
/// "Sometimes" through the merge check as it runs, against a real Postgres and the fake: what it
/// reads of the project's setting, the card's review label, its pull request's files and the
/// decisions its reports marked for the owner, and that a setting changed since the check read is
/// never merged past. <see cref="DecideTests"/> holds each condition alone. See "The merge check"
/// in devflow/README.md.
/// </summary>
public sealed class SometimesTests
{
    private static CancellationToken Ct => Merged.Ct;

    private static Task Sometimes(Jobs.Jobs jobs, string set = "") =>
        Merged.Sql(jobs, $"UPDATE project_settings SET waits_for_owner = 'sometimes'{set} WHERE project_id = @project");

    /// <summary>Built, its pull request's files staged, CI green and approved, the merge check
    /// running after the approve as the fake worker runs it.</summary>
    private static async Task Approved(Jobs.Jobs jobs, params ChangedFile[] files)
    {
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Merged.Ci(jobs, (string)built["head_commit"]!, Merged.GreenRuns, Merged.GreenStatuses);
        var number = await Merged.PullNumber(jobs);
        Merged.Change(jobs, repository => repository with { PullRequestFiles = [new FakePullRequestFiles(number, files)] });
        await Built.Review(jobs, Built.Verdict("approve"));
    }

    [Fact]
    public async Task ACardMarkedForReviewWaits()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs);
        Merged.Issue(jobs, Merged.Card, issue => issue with { Labels = [.. issue.Labels, "devflow-review"] });

        await Approved(jobs);
        await Merged.Check(jobs);

        var card = await Built.Card(jobs, Merged.Card);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(MergeCheck.Marked, card.Reason);
        Assert.Empty(Merged.AppMerges(jobs));
    }

    [Fact]
    public async Task AListedPathWaitsAndNoListReadsNoFiles()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs, ", wait_when_decision = false, wait_paths = ARRAY['*.sql', 'src/auth/**']");

        await Approved(jobs, new ChangedFile("web/list.tsx", null), new ChangedFile("lib/old.rs", "src/auth/old.rs"));

        var card = await Built.Card(jobs, Merged.Card);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(MergeCheck.PathListed("src/auth/old.rs", "src/auth/**"), card.Reason);
        Assert.Empty(Merged.AppMerges(jobs));
    }

    [Fact]
    public async Task WithNothingListedOrTickedItMerges()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs, ", wait_when_decision = false");

        // No paths listed: a change anywhere merges.
        await Approved(jobs, new ChangedFile("src/auth/login.rs", null));

        Assert.Equal("done", (await Built.Card(jobs, Merged.Card)).State);
        Assert.Single(Merged.AppMerges(jobs));
    }

    [Fact]
    public async Task ADecisionWaitsWhenTicked()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs);

        // Built.Report marks one decision for the owner.
        await Approved(jobs);

        var card = await Built.Card(jobs, Merged.Card);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(MergeCheck.Decision, card.Reason);
        Assert.Empty(Merged.AppMerges(jobs));
        await Merged.OpenDecisions(jobs);
        Assert.Empty(Merged.IssuesOpened(jobs));
    }

    [Fact]
    public async Task ADecisionNotTickedMergesAndOpensItsIssue()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs, ", wait_when_decision = false");

        await Approved(jobs);
        await Merged.OpenDecisions(jobs);

        Assert.Equal("done", (await Built.Card(jobs, Merged.Card)).State);
        Assert.Single(Merged.AppMerges(jobs));
        var issue = Assert.Single(Merged.IssuesOpened(jobs));
        Assert.Equal($"Decision in #{await Merged.PullNumber(jobs)}: Show it on every list", issue.Title);
    }

    /// <summary>The merge reads the setting again under the project's lock: one changed since the
    /// check decided is not merged past, and the card stays due for the next minute.</summary>
    [Fact]
    public async Task ASettingChangedSinceTheCheckIsNotMergedPast()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Sometimes(jobs, ", wait_when_decision = false");
        // CI not reported yet keeps the card due.
        var built = await Merged.Approved(jobs, green: false);
        var head = (string)built["head_commit"]!;
        var number = await Merged.PullNumber(jobs);
        var merges = jobs.Routes.App.Services.GetRequiredService<AppMerges>();
        var decided = new WaitSetting("sometimes", WaitConditions.Defaults with { WhenDecision = false });

        var changed = new[]
        {
            new WaitSetting("never", WaitConditions.Defaults),
            new WaitSetting("always", WaitConditions.Defaults),
            decided with { Conditions = decided.Conditions with { WhenDecision = true } },
            decided with { Conditions = decided.Conditions with { WhenMarked = false } },
            decided with { Conditions = decided.Conditions with { ReviewLabel = "look at me" } },
            decided with { Conditions = decided.Conditions with { Paths = ["*.sql"] } },
        };
        foreach (var stale in changed)
        {
            Assert.IsType<AppMergeOutcome.CardMoved>(await merges.MergeCard(Jobs.Jobs.User, jobs.Project, Merged.Card, number, head, stale, Ct));
        }

        Assert.Empty(Merged.AppMerges(jobs));
        Assert.Equal("waiting_for_owner", (await Built.Card(jobs, Merged.Card)).State);

        // As it is, it merges.
        Assert.IsType<AppMergeOutcome.Done>(await merges.MergeCard(Jobs.Jobs.User, jobs.Project, Merged.Card, number, head, decided, Ct));
        Assert.Single(Merged.AppMerges(jobs));
    }
}
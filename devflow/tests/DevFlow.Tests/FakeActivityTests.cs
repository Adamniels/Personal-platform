using System.Text.Json;

using DevFlow.CodeHost;
using DevFlow.Tests.GitHub;

using Microsoft.Extensions.Time.Testing;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests;

/// <summary>
/// The fake's side of watching a branch: a repository's default branch, <c>main</c> unless its world
/// names another, its branches' changes as staged, newest first, and every merge it takes, the
/// owner's or the app's, landing on the default branch as a change by whoever merged, with a merge
/// commit the merge answers. See "The fake" in devflow/README.md.
/// </summary>
public sealed class FakeActivityTests
{
    private static readonly Guid Owner = Guid.Parse("4f3c7a1e-9b2d-4c8e-a1f0-6d5e4c3b2a19");

    private static readonly CodeRepository Platform = new(Installation: 41, Id: 9001, Owner: "Adamniels", Name: "Personal-platform");

    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeActivity Push(long id, string branch = "main", string actor = "someone") =>
        new(id, branch, new string('a', 40), new string('b', 40), At.AddMinutes(id), ActivityKind.Push, actor);

    private static FakeWorld World(PullRequest? pull = null, string? defaultBranch = null, params FakeActivity[] activity) => new(
    [
        new FakeInstallation(41, "Adamniels",
        [
            new FakeRepository(Platform.Id, Platform.Owner, Platform.Name, pull is null ? [] : [pull], [], [], [], [], defaultBranch, activity.Length == 0 ? null : activity),
        ]),
    ]);

    [Fact]
    public async Task TheDefaultBranchIsMainUnlessTheWorldSaysAnother()
    {
        var host = new FakeCodeHost(new FakeTimeProvider(At));
        host.Replace(Owner, World());
        Assert.Equal("main", Results.Value(await host.DefaultBranch(Owner, Platform, Ct)));

        host.Replace(Owner, World(defaultBranch: "trunk"));
        Assert.Equal("trunk", Results.Value(await host.DefaultBranch(Owner, Platform, Ct)));
    }

    [Fact]
    public async Task ABranchsChangesAreAnsweredNewestFirst()
    {
        var host = new FakeCodeHost(new FakeTimeProvider(At));
        host.Replace(Owner, World(null, null, Push(1), Push(2, "other"), Push(3), Push(4)));

        Assert.Equal([4L, 3, 1], Results.Value(await host.Activity(Owner, Platform, "main", null, Ct)).Select(activity => activity.Id));
        Assert.Equal([4L], Results.Value(await host.Activity(Owner, Platform, "main", 3, Ct)).Select(activity => activity.Id));
        Assert.Equal([4L, 3, 1], Results.Value(await host.Activity(Owner, Platform, "main", 0, Ct)).Select(activity => activity.Id));
        Assert.Equal([2L], Results.Value(await host.Activity(Owner, Platform, "other", null, Ct)).Select(activity => activity.Id));

        host.SetUnavailable(Owner, true);
        Assert.IsType<CodeHostResult<IReadOnlyList<BranchActivity>>.Unavailable>(await host.Activity(Owner, Platform, "main", null, Ct));
    }

    [Fact]
    public async Task TheAppsMergeLandsOnTheDefaultBranchWithItsCommit()
    {
        var time = new FakeTimeProvider(At);
        var host = new FakeCodeHost(time);
        var head = new string('c', 40);
        var pull = new PullRequest(304, "The vault", "devflow-fake[bot]", [], head, "304-vault", "trunk", false, "",
            new Uri("https://github.com/Adamniels/Personal-platform/pull/304"), At, PullRequestState.Open);
        host.Replace(Owner, World(pull, "trunk", Push(7, "trunk")));
        var key = Results.Value(await host.ForWrite(Owner, Platform, WritePurpose.Merge, Ct));

        var merged = Assert.IsType<MergeResult.Merged>(await host.MergeAsApp(Owner, key, Platform, 304, head, Ct));

        var landed = Results.Value(await host.Activity(Owner, Platform, "trunk", 7, Ct));
        var change = Assert.Single(landed);
        Assert.Equal(8, change.Id);
        Assert.Equal(ActivityKind.PullRequestMerge, change.Kind);
        Assert.Equal(FakeCodeHost.AppLogin, change.Actor);
        Assert.Equal(new string('b', 40), change.Before);
        Assert.Equal(merged.Commit, change.After);
        Assert.Matches("^[0-9a-f]{40}$", merged.Commit);
        Assert.Equal(At, change.At);
    }

    [Fact]
    public void AWorldWithoutTheNewFieldsReadsAndWritesAsBefore()
    {
        var json = JsonSerializer.Serialize(World(), FakeJson.Options);

        Assert.DoesNotContain("default_branch", json, StringComparison.Ordinal);
        Assert.DoesNotContain("activity", json, StringComparison.Ordinal);
        var staged = JsonSerializer.Serialize(World(null, "trunk", Push(1)), FakeJson.Options);
        Assert.Contains("\"default_branch\":\"trunk\"", staged, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"push\"", staged, StringComparison.Ordinal);

        var duplicate = World(null, null, Push(1), Push(1));
        Assert.Equal("installations[0].repositories[0].activity[1].id", FakeJson.Check(duplicate)!.Field);
    }
}
using DevFlow.CodeHost;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests;

/// <summary>
/// <see cref="FakeCodeHost"/>, through <see cref="ICodeHost"/>: what is staged in an account's world
/// is what the interface answers, for that account alone, and the two failures every caller
/// handles. See "The code host" in devflow/README.md.
/// </summary>
public sealed class FakeCodeHostTests
{
    private static readonly Guid Owner = Guid.Parse("0b6c1f0e-4f6a-4a39-9d43-6f1c8f2f7a11");
    private static readonly Guid Stranger = Guid.Parse("7d1e9c55-2b8a-4f3e-a0c4-3e9f5b1d2c88");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CodeRepository Platform = new(Installation: 41, Id: 9001, Owner: "Adamniels", Name: "Personal-platform");

    private const string Head = "3f2c9a8b7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b";

    private static readonly DateTimeOffset At = new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly PullRequest Waiting = new(
        Number: 304,
        Title: "The vault",
        Author: "mac-mini-agent",
        Labels: ["owner-merge", "in-review"],
        HeadCommit: Head,
        HeadBranch: "272-the-vault",
        BaseBranch: "main",
        Draft: false,
        Body: "## What changed\n\nThe vault.\n\nCloses #272",
        Url: new Uri("https://github.com/Adamniels/Personal-platform/pull/304"),
        UpdatedAt: At,
        State: PullRequestState.Open);

    private static readonly PullRequest Merged = Waiting with
    {
        Number = 303,
        Title = "Dev flow's database",
        Labels = [],
        HeadCommit = "e6ebe07aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        HeadBranch = "271-dev-flow-s-database",
        Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/303"),
        State = PullRequestState.Merged,
    };

    private static readonly Comment Verdict = new(Id: 1001, Author: "mac-mini-agent", Body: "approve, at the head", CreatedAt: At, UpdatedAt: At);

    private static readonly Comment Edited = new(Id: 1002, Author: "Adamniels", Body: "Why AES-GCM?", CreatedAt: At.AddMinutes(5), UpdatedAt: At.AddMinutes(9));

    private static readonly CommitStatus ReviewOk = new("review-ok", StatusState.Success, "approved at the head");

    private static readonly CheckRun CiOk = new("ci-ok", CheckStatus.Completed, CheckConclusion.Success, "github-actions");

    private static readonly CheckRun E2e = new("e2e", CheckStatus.InProgress, Conclusion: null, "github-actions");

    private static readonly byte[] Agents = "# Working in this repository\n"u8.ToArray();

    /// <summary>One installation holding the platform's repository, with one pull request waiting,
    /// one merged, the waiting one's conversation, its head's statuses and checks, and a file.</summary>
    private static FakeWorld World => new(
    [
        new FakeInstallation(41, "Adamniels",
        [
            new FakeRepository(
                Platform.Id,
                Platform.Owner,
                Platform.Name,
                PullRequests: [Waiting, Merged],
                Conversations: [new FakeConversation(304, [Verdict, Edited]), new FakeConversation(273, [])],
                Commits: [new FakeCommit(Head, [ReviewOk], [CiOk, E2e])],
                Files: [new FakeFile("AGENTS.md", "main", Agents)]),
        ]),
    ]);

    /// <summary>The fake as callers see it, so the test reads through the interface.</summary>
#pragma warning disable CA1859 // The interface is the point: callers only ever hold an ICodeHost.
    private static ICodeHost Through(FakeCodeHost host) => host;
#pragma warning restore CA1859

    private static T Found<T>(CodeHostResult<T> result) =>
        Assert.IsType<CodeHostResult<T>.Found>(result).Value;

    [Fact]
    public async Task AStagedPullRequestIsReadBack()
    {
        var host = new FakeCodeHost();
        host.Replace(Owner, World);
        var code = Through(host);

        // Open ones only, each with everything the waiting list reads.
        var open = Found(await code.OpenPullRequests(Owner, Platform, Ct));
        var only = Assert.Single(open);
        Assert.Equal(Waiting, only with { Labels = Waiting.Labels });
        Assert.Equal(["owner-merge", "in-review"], only.Labels);
        Assert.Equal("mac-mini-agent", only.Author);
        Assert.Equal(Head, only.HeadCommit);
        Assert.Equal(Waiting.Body, only.Body);

        // One by number, whatever its state.
        Assert.Equal(PullRequestState.Merged, Found(await code.PullRequest(Owner, Platform, 303, Ct)).State);
        Assert.Equal(304, Found(await code.PullRequest(Owner, Platform, 304, Ct)).Number);

        // The conversation, oldest first, and an issue's, which has none yet.
        Assert.Equal([Verdict, Edited], Found(await code.Comments(Owner, Platform, 304, Ct)));
        Assert.Empty(Found(await code.Comments(Owner, Platform, 273, Ct)));
        // A pull request's number reaches its conversation even when nothing staged one.
        Assert.Empty(Found(await code.Comments(Owner, Platform, 303, Ct)));

        // What is posted on the head commit.
        Assert.Equal([ReviewOk], Found(await code.Statuses(Owner, Platform, Head, Ct)));
        Assert.Equal([CiOk, E2e], Found(await code.CheckRuns(Owner, Platform, Head, Ct)));

        // A file on a branch, byte for byte.
        Assert.Equal(Agents, Found(await code.File(Owner, Platform, "AGENTS.md", "main", Ct)));

        // A repository is its installation and id: renamed, it is the same one.
        var renamed = Platform with { Owner = "someone-else", Name = "platform" };
        Assert.Single(Found(await code.OpenPullRequests(Owner, renamed, Ct)));
    }

    [Fact]
    public async Task OneAccountsWorldIsNotAnothers()
    {
        var host = new FakeCodeHost();
        host.Replace(Owner, World);

        // The stranger has an empty world, and the owner's repository is not in it.
        Assert.Equal(FakeWorld.Empty, host.World(Stranger));
        Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.NotFound>(await host.OpenPullRequests(Stranger, Platform, Ct));

        // A world the stranger stages replaces theirs alone.
        host.Replace(Stranger, FakeWorld.Empty with
        {
            Installations = [new FakeInstallation(41, "Adamniels", [new FakeRepository(Platform.Id, "x", "y", [], [], [], [])])],
        });
        Assert.Empty(Found(await host.OpenPullRequests(Stranger, Platform, Ct)));
        Assert.Single(Found(await host.OpenPullRequests(Owner, Platform, Ct)));
        Assert.Equal(World.Installations[0].Repositories[0].PullRequests, host.World(Owner).Installations[0].Repositories[0].PullRequests);

        // And so does being unavailable.
        host.SetUnavailable(Stranger, unavailable: true);
        Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.Unavailable>(await host.OpenPullRequests(Stranger, Platform, Ct));
        Assert.Single(Found(await host.OpenPullRequests(Owner, Platform, Ct)));
    }

    [Fact]
    public async Task AnUnknownRepositoryIsNotFound()
    {
        var host = new FakeCodeHost();
        host.Replace(Owner, World);

        // An empty world, another installation, and the same id reached through another one.
        Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.NotFound>(await new FakeCodeHost().OpenPullRequests(Owner, Platform, Ct));
        foreach (var unknown in new[] { Platform with { Installation = 42 }, Platform with { Id = 9002 } })
        {
            Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.NotFound>(await host.OpenPullRequests(Owner, unknown, Ct));
            Assert.IsType<CodeHostResult<PullRequest>.NotFound>(await host.PullRequest(Owner, unknown, 304, Ct));
            Assert.IsType<CodeHostResult<IReadOnlyList<Comment>>.NotFound>(await host.Comments(Owner, unknown, 304, Ct));
            Assert.IsType<CodeHostResult<IReadOnlyList<CommitStatus>>.NotFound>(await host.Statuses(Owner, unknown, Head, Ct));
            Assert.IsType<CodeHostResult<IReadOnlyList<CheckRun>>.NotFound>(await host.CheckRuns(Owner, unknown, Head, Ct));
            Assert.IsType<CodeHostResult<byte[]>.NotFound>(await host.File(Owner, unknown, "AGENTS.md", "main", Ct));
        }

        // In a repository it knows, what it does not hold is not found either.
        Assert.IsType<CodeHostResult<PullRequest>.NotFound>(await host.PullRequest(Owner, Platform, 999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Comment>>.NotFound>(await host.Comments(Owner, Platform, 999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CommitStatus>>.NotFound>(await host.Statuses(Owner, Platform, "0000000000000000000000000000000000000000", Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CheckRun>>.NotFound>(await host.CheckRuns(Owner, Platform, "0000000000000000000000000000000000000000", Ct));
        Assert.IsType<CodeHostResult<byte[]>.NotFound>(await host.File(Owner, Platform, "AGENTS.md", "272-the-vault", Ct));
        Assert.IsType<CodeHostResult<byte[]>.NotFound>(await host.File(Owner, Platform, "agents.md", "main", Ct));
    }

    [Fact]
    public async Task WhatAPersonCanConnectIsTheirWorldsInstallationsReadWithTheirToken()
    {
        var host = new FakeCodeHost();
        var thesis = new FakeRepository(9101, "uppsala-lab", "thesis", [], [], [], []);
        host.Replace(Owner, World with { Installations = [.. World.Installations, new FakeInstallation(52, "uppsala-lab", [thesis])] });
        var token = await SignIn(host, Owner);
        var code = Through(host);

        Assert.Equal([new Installation(41, "Adamniels"), new Installation(52, "uppsala-lab")], Found(await code.Installations(Owner, token, Ct)));
        Assert.Equal([Platform], Found(await code.Repositories(Owner, token, 41, Ct)));
        Assert.Equal([new CodeRepository(52, 9101, "uppsala-lab", "thesis")], Found(await code.Repositories(Owner, token, 52, Ct)));

        // An installation the world does not hold is not found.
        Assert.IsType<CodeHostResult<IReadOnlyList<CodeRepository>>.NotFound>(await code.Repositories(Owner, token, 77, Ct));

        // Only with the token the account's sign in gave, as GitHub takes only a person's own:
        // another's, or none, is not taken, which the adapter answers as unavailable.
        var strangers = await SignIn(host, Stranger);
        Assert.IsType<CodeHostResult<IReadOnlyList<Installation>>.Unavailable>(await code.Installations(Owner, strangers, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CodeRepository>>.Unavailable>(await code.Repositories(Owner, "not-a-token", 41, Ct));

        // And not at all while the account is unavailable.
        host.SetUnavailable(Owner, unavailable: true);
        Assert.IsType<CodeHostResult<IReadOnlyList<Installation>>.Unavailable>(await code.Installations(Owner, token, Ct));
    }

    [Fact]
    public void TheInstallAddressIsTheFakesOwnPage()
    {
        Assert.Equal(
            new Uri("https://platform.test/devflow/fake-github/install"),
            Through(new FakeCodeHost()).InstallAddress(new Uri("https://platform.test")));
    }

    /// <summary>An access token for the account, through the fake's sign in as Dev flow gets one.</summary>
    private static async Task<string> SignIn(FakeCodeHost host, Guid account)
    {
        var redirect = new Uri("https://platform.test/devflow/github/callback");
        var verifier = Pkce.Random();
        var code = host.Grant(account, Pkce.Challenge(verifier), redirect);
        return Assert.IsType<SignInResult<UserTokenPair>.Granted>(await host.Exchange(account, code, verifier, redirect, Ct)).Value.AccessToken;
    }

    [Fact]
    public async Task TheFakeCanBeToldToBeUnavailable()
    {
        var host = new FakeCodeHost();
        host.Replace(Owner, World);

        host.SetUnavailable(Owner, unavailable: true);

        // Every operation, on what exists and on what does not, without a time to try again.
        Assert.Null(Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.Unavailable>(await host.OpenPullRequests(Owner, Platform, Ct)).RetryAt);
        Assert.IsType<CodeHostResult<PullRequest>.Unavailable>(await host.PullRequest(Owner, Platform, 304, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Comment>>.Unavailable>(await host.Comments(Owner, Platform, 304, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CommitStatus>>.Unavailable>(await host.Statuses(Owner, Platform, Head, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<CheckRun>>.Unavailable>(await host.CheckRuns(Owner, Platform, Head, Ct));
        Assert.IsType<CodeHostResult<byte[]>.Unavailable>(await host.File(Owner, Platform, "AGENTS.md", "main", Ct));
        Assert.IsType<CodeHostResult<byte[]>.Unavailable>(await host.File(Owner, Platform with { Id = 1 }, "AGENTS.md", "main", Ct));

        // The world is kept, and answers again once the host is back.
        Assert.Equal(World.Installations[0].Id, host.World(Owner).Installations[0].Id);
        host.SetUnavailable(Owner, unavailable: false);
        Assert.Single(Found(await host.OpenPullRequests(Owner, Platform, Ct)));
    }
}
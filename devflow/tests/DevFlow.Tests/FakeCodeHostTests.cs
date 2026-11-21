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
                Files: [new FakeFile("AGENTS.md", "main", Agents)],
                Issues: []),
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
            Installations = [new FakeInstallation(41, "Adamniels", [new FakeRepository(Platform.Id, "x", "y", [], [], [], [], [])])],
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
        var thesis = new FakeRepository(9101, "uppsala-lab", "thesis", [], [], [], [], []);
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

    [Fact]
    public async Task IssuesAreListedByLabelWithWhatBlocksThemAndWhoEditedThem()
    {
        var host = new FakeCodeHost();
        var title = new IssueEdit("RTE_1", "Adamniels", At.AddHours(2), EditKind.Title);
        var body = new IssueEdit("UCE_1", "octo-stranger", At.AddHours(1), EditKind.Body);
        static FakeIssue Issue(int number, bool open, IReadOnlyList<string> labels, IReadOnlyList<int> blockedBy, IReadOnlyList<IssueEdit> edits) => new(
            number, $"Card {number}", "Adamniels", labels, $"Body of {number}",
            new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"), At.AddDays(-number), open, blockedBy, edits);
        var world = World;
        var repository = world.Installations[0].Repositories[0] with
        {
            PullRequests = [Waiting with { Labels = ["devflow"] }, Merged with { Labels = ["devflow"] }],
            Issues =
            [
                Issue(338, open: true, ["DevFlow"], [339], [title, body]),
                Issue(339, open: false, ["devflow"], [], []),
                Issue(340, open: true, [], [], []),
            ],
        };
        host.Replace(Owner, world with { Installations = [world.Installations[0] with { Repositories = [repository] }] });

        // The open issues with the label, whatever its case, and the open pull request with it,
        // marked; never a closed one.
        var listed = Found(await host.LabelledIssues(Owner, Platform, "devflow", Ct));
        Assert.Equal([(338, false), (304, true)], listed.Select(issue => (issue.Number, issue.IsPullRequest)));
        // A record compares its list of labels by reference, so they are compared on their own.
        Assert.Equal(["DevFlow"], listed[0].Labels);
        Assert.Equal(new Issue(338, "Card 338", "Adamniels", [], "Body of 338",
            new Uri("https://github.com/Adamniels/Personal-platform/issues/338"), At.AddDays(-338), IsPullRequest: false), listed[0] with { Labels = [] });
        Assert.Empty(Found(await host.LabelledIssues(Owner, Platform, "devflow-hold", Ct)));

        // What blocks it, closed here; and its edits, the body's first.
        Assert.Equal([new Dependency("Adamniels", "Personal-platform", 339, Open: false, new Uri("https://github.com/Adamniels/Personal-platform/issues/339"))],
            Found(await host.BlockedBy(Owner, Platform, 338, Ct)));
        Assert.Equal([body, title], Found(await host.Edits(Owner, Platform, 338, Ct)));
        Assert.Empty(Found(await host.Edits(Owner, Platform, 340, Ct)));

        // An issue that is not there is not found; another account sees nothing of it.
        Assert.IsType<CodeHostResult<IReadOnlyList<Dependency>>.NotFound>(await host.BlockedBy(Owner, Platform, 999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound>(await host.Edits(Owner, Platform, 304, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Issue>>.NotFound>(await host.LabelledIssues(Stranger, Platform, "devflow", Ct));

        host.SetUnavailable(Owner, unavailable: true);
        Assert.IsType<CodeHostResult<IReadOnlyList<Issue>>.Unavailable>(await host.LabelledIssues(Owner, Platform, "devflow", Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Dependency>>.Unavailable>(await host.BlockedBy(Owner, Platform, 338, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable>(await host.Edits(Owner, Platform, 338, Ct));
    }

    [Fact]
    public async Task LabelEventsAreAnsweredAsStagedOrAsTheAuthorsOwn()
    {
        var host = new FakeCodeHost();
        var added = new LabelEvent("devflow", LabelChange.Added, "octo-stranger", At.AddHours(1));
        var held = new LabelEvent("devflow-hold", LabelChange.Added, "Adamniels", At.AddHours(2));
        var released = new LabelEvent("devflow-hold", LabelChange.Removed, "octo-stranger", At.AddHours(3));
        static FakeIssue Issue(int number, IReadOnlyList<string> labels, IReadOnlyList<LabelEvent>? events) => new(
            number, $"Card {number}", "Adamniels", labels, $"Body of {number}",
            new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"), At, Open: true, [], [], LabelEvents: events);
        var world = World;
        var repository = world.Installations[0].Repositories[0] with
        {
            Issues =
            [
                // Staged out of order: answered oldest first.
                Issue(338, ["devflow"], [released, added, held]),
                // None staged: each label it carries, put on by its author as it was written.
                Issue(339, ["devflow", "priority: high"], null),
                Issue(340, [], []),
            ],
        };
        host.Replace(Owner, world with { Installations = [world.Installations[0] with { Repositories = [repository] }] });

        Assert.Equal([added, held, released], Found(await host.LabelEvents(Owner, Platform, 338, Ct)));
        Assert.Equal(
            [new LabelEvent("devflow", LabelChange.Added, "Adamniels", At), new LabelEvent("priority: high", LabelChange.Added, "Adamniels", At)],
            Found(await host.LabelEvents(Owner, Platform, 339, Ct)));
        Assert.Empty(Found(await host.LabelEvents(Owner, Platform, 340, Ct)));

        // A pull request's number, or one the world does not hold, is not found; another account
        // sees nothing of it.
        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound>(await host.LabelEvents(Owner, Platform, 304, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound>(await host.LabelEvents(Owner, Platform, 999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound>(await host.LabelEvents(Stranger, Platform, 338, Ct));

        host.SetUnavailable(Owner, unavailable: true);
        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable>(await host.LabelEvents(Owner, Platform, 338, Ct));
    }

    [Fact]
    public async Task SubIssuesAreAnsweredInTheirOrderFromWhereverTheyAre()
    {
        var host = new FakeCodeHost();
        static FakeIssue Issue(int number, bool open = true, IReadOnlyList<FakeSubIssue>? subIssues = null, string repository = "Personal-platform") => new(
            number, $"Issue {number}", "Adamniels", [], $"Body of {number}",
            new Uri($"https://github.com/Adamniels/{repository}/issues/{number}"), At.AddDays(-number), open, [], [], subIssues);
        var world = World;
        var platform = world.Installations[0].Repositories[0] with
        {
            PullRequests = [Waiting],
            Issues = [Issue(330, subIssues: [new(332, null), new(5, 9002), new(331, null), new(304, null)]), Issue(331, open: false), Issue(332), Issue(333)],
        };
        var elsewhere = new FakeRepository(9002, "Adamniels", "elsewhere", [], [], [], [], [Issue(5, repository: "elsewhere")]);
        host.Replace(Owner, world with { Installations = [world.Installations[0] with { Repositories = [platform, elsewhere] }] });

        var subIssues = Found(await host.SubIssues(Owner, Platform, 330, Ct));

        // In the order staged, not by number, each with its own repository; a pull request marked.
        Assert.Equal(
            [(332, "Personal-platform", true, false), (5, "elsewhere", true, false), (331, "Personal-platform", false, false), (304, "Personal-platform", true, true)],
            subIssues.Select(sub => (sub.Number, sub.Name, sub.Open, sub.IsPullRequest)));
        Assert.Equal(new SubIssue(332, "Issue 332", "Body of 332", true, "Adamniels", [], At.AddDays(-332), "Adamniels", "Personal-platform",
            new Uri("https://github.com/Adamniels/Personal-platform/issues/332"), IsPullRequest: false), subIssues[0] with { Labels = [] });

        // None for an issue that has none, not found for one that is not there.
        Assert.Empty(Found(await host.SubIssues(Owner, Platform, 333, Ct)));
        Assert.IsType<CodeHostResult<IReadOnlyList<SubIssue>>.NotFound>(await host.SubIssues(Owner, Platform, 999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<SubIssue>>.NotFound>(await host.SubIssues(Stranger, Platform, 330, Ct));
        host.SetUnavailable(Owner, unavailable: true);
        Assert.IsType<CodeHostResult<IReadOnlyList<SubIssue>>.Unavailable>(await host.SubIssues(Owner, Platform, 330, Ct));
    }
}
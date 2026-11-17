using System.Diagnostics;

using DevFlow.CodeHost;

using Microsoft.Extensions.Time.Testing;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests;

/// <summary>
/// The fake's side of the app's writes: it issues its own keys, takes a write only with a key it
/// issued for that account, repository and purpose, records each write as the app's with its
/// purpose, apart from the owner's, and keeps a bare git repository per repository that a worker
/// in a test fetches from and pushes to for real. See "The fake" in devflow/README.md.
/// </summary>
public sealed class FakeAppWriteTests
{
    private static readonly Guid Owner = Guid.Parse("4f3c7a1e-9b2d-4c8e-a1f0-6d5e4c3b2a19");

    private static readonly CodeRepository Platform = new(Installation: 41, Id: 9001, Owner: "Adamniels", Name: "Personal-platform");

    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Head = new('c', 40);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PullRequest Waiting = new(
        Number: 304,
        Title: "The vault",
        Author: "mac-mini-agent",
        Labels: ["owner-merge"],
        HeadCommit: Head,
        HeadBranch: "272-the-vault",
        BaseBranch: "main",
        Draft: false,
        Body: "Closes #272",
        Url: new Uri("https://github.com/Adamniels/Personal-platform/pull/304"),
        UpdatedAt: At,
        State: PullRequestState.Open);

    private static FakeWorld World => new(
    [
        new FakeInstallation(41, "Adamniels",
        [
            new FakeRepository(Platform.Id, Platform.Owner, Platform.Name, [Waiting], [new FakeConversation(310, [])], [], []),
        ]),
    ]);

    private static async Task<AppWriteToken> Key(FakeCodeHost host, WritePurpose purpose, CodeRepository? repository = null) =>
        Assert.IsType<CodeHostResult<AppWriteToken>.Found>(await host.ForWrite(Owner, repository ?? Platform, purpose, Ct)).Value;

    /// <summary>An access token for the account, through the fake's sign in as Dev flow gets one.</summary>
    private static async Task<UserAccessToken> SignIn(FakeCodeHost host)
    {
        var redirect = new Uri("https://platform.test/devflow/github/callback");
        var verifier = Pkce.Random();
        var code = host.Grant(Owner, Pkce.Challenge(verifier), redirect);
        var pair = Assert.IsType<SignInResult<UserTokenPair>.Granted>(await host.Exchange(Owner, code, verifier, redirect, Ct)).Value;
        return new UserAccessToken(pair.AccessToken);
    }

    [Fact]
    public async Task TheFakeRecordsWhoWroteWhat()
    {
        using var fake = new FakeCodeHost(new FakeTimeProvider(At));
        fake.Replace(Owner, World);
        var host = fake;
        var post = await Key(host, WritePurpose.Post);
        var merge = await Key(host, WritePurpose.Merge);
        var branchHead = await Git.PushBranch(Assert.IsType<CodeHostResult<Uri>.Found>(await host.CloneAddress(Owner, Platform, Ct)).Value, "336-the-write-path");

        var opened = Assert.IsType<WriteResult<PullRequest>.Written>(await host.OpenPullRequest(Owner, post, Platform, "336-the-write-path", "main", "The write path", "Closes #336", Ct)).Value;
        var comment = Assert.IsType<WriteResult<Comment>.Written>(await host.CommentOnIssue(Owner, post, Platform, opened.Number, "Picked up.", Ct)).Value;
        Assert.Equal(["has-decision"], Assert.IsType<WriteResult<IReadOnlyList<string>>.Written>(await host.AddLabel(Owner, post, Platform, opened.Number, "has-decision", Ct)).Value);
        Assert.Equal(["owner-merge", "in-review"], Assert.IsType<WriteResult<IReadOnlyList<string>>.Written>(await host.AddLabel(Owner, post, Platform, 304, "in-review", Ct)).Value);
        Assert.Equal(["in-review"], Assert.IsType<WriteResult<IReadOnlyList<string>>.Written>(await host.RemoveLabel(Owner, post, Platform, 304, "owner-merge", Ct)).Value);
        var issue = Assert.IsType<WriteResult<Issue>.Written>(await host.OpenIssue(Owner, post, Platform, "Main moved", "Something landed.", ["devflow"], Ct)).Value;
        Assert.IsType<MergeResult.Merged>(await host.MergeAsApp(Owner, merge, Platform, 304, Head, Ct));
        // And one by the owner, from the page.
        Assert.IsType<CommentResult.Posted>(await host.Comment(Owner, await SignIn(fake), Platform, opened.Number, "Looks right.", Ct));

        // What the app opened is the host's next numbers, its head the commit pushed, by the app.
        Assert.Equal((311, 312), (opened.Number, issue.Number));
        Assert.Equal((branchHead, "336-the-write-path", "main", FakeCodeHost.AppLogin, PullRequestState.Open),
            (opened.HeadCommit, opened.HeadBranch, opened.BaseBranch, opened.Author, opened.State));
        Assert.Equal((FakeCodeHost.AppLogin, "Picked up."), (comment.Author, comment.Body));
        Assert.Equal(["devflow"], issue.Labels);

        // Each write recorded with who made it: the app with its key's purpose, or the person.
        var by = FakeCodeHost.AppLogin;
        Assert.Equal(
            [
                new FakeWrite("pull_request", Platform.Id, 311, branchHead, null, "Closes #336", by, Purpose: "post", Title: "The write path", Head: "336-the-write-path", Base: "main"),
                new FakeWrite("comment", Platform.Id, 311, null, null, "Picked up.", by, Purpose: "post"),
                new FakeWrite("label_added", Platform.Id, 311, null, null, null, by, Purpose: "post", Label: "has-decision"),
                new FakeWrite("label_added", Platform.Id, 304, null, null, null, by, Purpose: "post", Label: "in-review"),
                new FakeWrite("label_removed", Platform.Id, 304, null, null, null, by, Purpose: "post", Label: "owner-merge"),
                new FakeWrite("issue", Platform.Id, 312, null, null, "Something landed.", by, Purpose: "post", Title: "Main moved"),
                new FakeWrite("merge", Platform.Id, 304, Head, "merge", null, by, Purpose: "merge"),
                new FakeWrite("comment", Platform.Id, 311, null, null, "Looks right.", FakeUser.Default.Login),
            ],
            fake.Writes(Owner));
        Assert.Equal(7, fake.Writes(Owner).Count(write => write.Purpose is not null));

        // And the world holds what was written, so the next read sees it.
        var repository = fake.World(Owner).Installations[0].Repositories[0];
        Assert.Equal(PullRequestState.Merged, repository.PullRequests.Single(pull => pull.Number == 304).State);
        Assert.Equal(["in-review"], repository.PullRequests.Single(pull => pull.Number == 304).Labels);
        Assert.Equal(["has-decision"], repository.PullRequests.Single(pull => pull.Number == 311).Labels);
        Assert.Equal([FakeCodeHost.AppLogin, FakeUser.Default.Login], Assert.IsType<CodeHostResult<IReadOnlyList<Comment>>.Found>(await host.Comments(Owner, Platform, 311, Ct)).Value.Select(each => each.Author));
        var stored = Assert.Single(repository.Issues!);
        Assert.Equal((312, "Main moved", "Something landed.", "devflow"), (stored.Number, stored.Title, stored.Body, Assert.Single(stored.Labels)));
    }

    [Fact]
    public async Task AWorkerFetchesAndPushesThroughTheCloneAddress()
    {
        using var fake = new FakeCodeHost(new FakeTimeProvider(At));
        fake.Replace(Owner, World);

        var address = Assert.IsType<CodeHostResult<Uri>.Found>(await fake.CloneAddress(Owner, Platform, Ct)).Value;
        var pushed = await Git.PushBranch(address, "feature");

        // The same address each time, a bare repository holding what was pushed.
        Assert.Equal("file", address.Scheme);
        Assert.Equal(address, Assert.IsType<CodeHostResult<Uri>.Found>(await fake.CloneAddress(Owner, Platform, Ct)).Value);
        Assert.Equal(pushed, (await Git.Run(null, "ls-remote", address.AbsoluteUri, "refs/heads/feature")).Split('\t')[0]);

        // Not found for a repository the world does not hold, and unavailable when said.
        Assert.IsType<CodeHostResult<Uri>.NotFound>(await fake.CloneAddress(Owner, Platform with { Id = 7 }, Ct));
        fake.SetUnavailable(Owner, true);
        Assert.IsType<CodeHostResult<Uri>.Unavailable>(await fake.CloneAddress(Owner, Platform, Ct));
    }

    [Fact]
    public async Task OnlyAKeyTheFakeIssuedForTheAccountIsTaken()
    {
        var time = new FakeTimeProvider(At);
        using var fake = new FakeCodeHost(time);
        fake.Replace(Owner, World);
        var post = await Key(fake, WritePurpose.Post);

        // Another account's key, even for the same repository, is not taken.
        var stranger = Guid.Parse("b2c1d0e9-8f7a-4b6c-9d5e-4f3a2b1c0d9e");
        fake.Replace(stranger, World);
        var theirs = Assert.IsType<CodeHostResult<AppWriteToken>.Found>(await fake.ForWrite(stranger, Platform, WritePurpose.Post, Ct)).Value;
        Assert.Equal(new WriteResult<Comment>.Refused(401, FakeCodeHost.BadCredentials), await fake.CommentOnIssue(Owner, theirs, Platform, 304, "x", Ct));

        // A key past its hour is not taken.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new WriteResult<Comment>.Refused(401, FakeCodeHost.BadCredentials), await fake.CommentOnIssue(Owner, post, Platform, 304, "x", Ct));
        Assert.Empty(fake.Writes(Owner));

        // Each key is new, and a key for the wrong write is refused before anything is done.
        var fresh = await Key(fake, WritePurpose.Post);
        Assert.NotEqual(post.Value, fresh.Value);
        Assert.Equal(At.AddHours(2), fresh.ExpiresAt);
        await Assert.ThrowsAsync<ArgumentException>(() => fake.MergeAsApp(Owner, fresh, Platform, 304, Head, Ct));
        var push = await Key(fake, WritePurpose.Push);
        await Assert.ThrowsAsync<ArgumentException>(() => fake.OpenIssue(Owner, push, Platform, "t", "b", [], Ct));

        // A repository the world does not hold has no key.
        Assert.IsType<CodeHostResult<AppWriteToken>.NotFound>(await fake.ForWrite(Owner, Platform with { Id = 7 }, WritePurpose.Post, Ct));
    }

    [Fact]
    public async Task TheFakeRefusesAsGitHubDoes()
    {
        using var fake = new FakeCodeHost(new FakeTimeProvider(At));
        fake.Replace(Owner, World);
        var post = await Key(fake, WritePurpose.Post);

        // A head branch that was never pushed, and a label the issue does not carry.
        Assert.Equal(422, Assert.IsType<WriteResult<PullRequest>.Refused>(await fake.OpenPullRequest(Owner, post, Platform, "nowhere", "main", "t", "b", Ct)).Status);
        Assert.Equal(new WriteResult<IReadOnlyList<string>>.Refused(404, FakeCodeHost.LabelMissing), await fake.RemoveLabel(Owner, post, Platform, 304, "absent", Ct));
        // A number with neither a pull request, an issue nor a conversation.
        Assert.Equal(404, Assert.IsType<WriteResult<Comment>.Refused>(await fake.CommentOnIssue(Owner, post, Platform, 999, "x", Ct)).Status);
        Assert.Equal(404, Assert.IsType<WriteResult<IReadOnlyList<string>>.Refused>(await fake.AddLabel(Owner, post, Platform, 999, "x", Ct)).Status);

        // An open pull request from the same branch already exists.
        await Git.PushBranch(Assert.IsType<CodeHostResult<Uri>.Found>(await fake.CloneAddress(Owner, Platform, Ct)).Value, "twice");
        Assert.IsType<WriteResult<PullRequest>.Written>(await fake.OpenPullRequest(Owner, post, Platform, "twice", "main", "t", "b", Ct));
        Assert.Equal(422, Assert.IsType<WriteResult<PullRequest>.Refused>(await fake.OpenPullRequest(Owner, post, Platform, "twice", "main", "t", "b", Ct)).Status);

        fake.SetUnavailable(Owner, true);
        Assert.IsType<WriteResult<Issue>.Unavailable>(await fake.OpenIssue(Owner, post, Platform, "t", "b", [], Ct));
        Assert.Single(fake.Writes(Owner));
    }

    /// <summary>git, as a worker runs it, in a folder of its own.</summary>
    private static class Git
    {
        /// <summary>Who commits, set for each command, so no git configuration is needed.</summary>
        private static readonly string[] Identity = ["-c", "user.name=Worker", "-c", "user.email=worker@example.invalid", "-c", "commit.gpgsign=false"];

        /// <summary>Clones <paramref name="address"/>, commits on <c>main</c> and on
        /// <paramref name="branch"/>, pushes both, and answers the branch's commit.</summary>
        public static async Task<string> PushBranch(Uri address, string branch)
        {
            var work = Directory.CreateTempSubdirectory("devflow-fake-worker-").FullName;
            try
            {
                await Run(null, "clone", "--quiet", address.AbsoluteUri, work);
                await Run(work, "switch", "--quiet", "--create", "main");
                await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "# Project\n", Ct);
                await Run(work, "add", "README.md");
                await Run(work, "commit", "--quiet", "--message", "Start");
                await Run(work, "push", "--quiet", "origin", "main");
                await Run(work, "switch", "--quiet", "--create", branch);
                await File.WriteAllTextAsync(Path.Combine(work, "CHANGE.md"), "A change.\n", Ct);
                await Run(work, "add", "CHANGE.md");
                await Run(work, "commit", "--quiet", "--message", "Change");
                await Run(work, "push", "--quiet", "origin", branch);
                return (await Run(work, "rev-parse", "HEAD")).Trim();
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }

        public static async Task<string> Run(string? folder, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            if (folder is not null)
            {
                start.WorkingDirectory = folder;
            }

            foreach (var argument in Identity.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEndAsync(Ct);
            var error = git.StandardError.ReadToEndAsync(Ct);
            await git.WaitForExitAsync(Ct);
            Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)}: {await error}");
            return await output;
        }
    }
}
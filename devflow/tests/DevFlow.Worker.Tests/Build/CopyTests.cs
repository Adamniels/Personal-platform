using DevFlow.CodeHost;

namespace DevFlow.Worker.Tests.Build;

/// <summary>
/// The clean working copy a build job gives the agent, seen from inside the agent's run: the job's
/// branch, from the base or from the remote, the app's bot as its committer, a remote with no
/// credentials, and the worker's own repository beside it, root's alone. See "The worker" in
/// devflow/README.md.
/// </summary>
public sealed class CopyTests
{
    private const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public async Task TheAgentsCopyIsTheJobsBranchFromTheBase()
    {
        await using var bench = await Bench.Start();
        var main = await Bench.Git(bench.Remote, "rev-parse", "main");
        var looked = false;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            var copy = run.WorkingDirectory;
            Assert.Equal(Bench.Branch, await Bench.Git(copy, "branch", "--show-current"));
            Assert.Equal(main, await Bench.Git(copy, "rev-parse", "HEAD"));
            Assert.Equal("# The project\n", await File.ReadAllTextAsync(Path.Combine(copy, "README.md"), Support.Ct));
            Assert.Equal(FakeCodeHost.AppLogin, await Bench.Git(copy, "config", "user.name"));
            Assert.Equal(FakeCodeHost.AppEmail, await Bench.Git(copy, "config", "user.email"));

            // Its remote is the bundle it was cloned from, removed since, which holds no credentials.
            var origin = await Bench.Git(copy, "remote", "get-url", "origin");
            Assert.EndsWith("/copy.bundle", origin, StringComparison.Ordinal);
            Assert.False(File.Exists(origin));

            // The worker's own repository is a bare one in the job's folder of secrets, mode 700,
            // with the branch made from the base and checked out.
            var secret = Assert.Single(Directory.GetDirectories(bench.Folders.Secrets));
            Assert.Equal(RootOnly, Bench.Mode(secret));
            Assert.Equal(Path.Combine(bench.Folders.Work, Path.GetFileName(secret), "copy"), copy);
            var repository = Path.Combine(secret, "repo");
            Assert.Equal("true", await Bench.Git(repository, "rev-parse", "--is-bare-repository"));
            Assert.Equal($"refs/heads/{Bench.Branch}", await Bench.Git(repository, "symbolic-ref", "HEAD"));
            Assert.Equal(main, await Bench.Git(repository, "rev-parse", Bench.Branch));
            looked = true;
        });

        await bench.RunJob(agent);

        Assert.True(looked);
    }

    [Fact]
    public async Task ARemoteBranchIsTheCopysStart()
    {
        string? earlier = null;
        await using var bench = await Bench.Start(before: async bench =>
            earlier = await bench.PushAsSomeoneElse(Bench.Branch, "main", "Work from a lost run", ("earlier.txt", "from before\n")));
        var looked = false;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            Assert.Equal(Bench.Branch, await Bench.Git(run.WorkingDirectory, "branch", "--show-current"));
            Assert.Equal(earlier, await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD"));
            Assert.Equal("from before\n", await File.ReadAllTextAsync(Path.Combine(run.WorkingDirectory, "earlier.txt"), Support.Ct));
            looked = true;
        });

        await bench.RunJob(agent);

        Assert.True(looked);
    }
}
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;
using DevFlow.Worker.Pages;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Screenshots;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A build's pictures, on the bench, in process against Dev flow with the fake code host's real
/// git: after the check passed and the branch was pushed, the project is started from a clean copy
/// of the pushed commit, the report's pages are taken at two sizes, and the pictures go to
/// <c>pr-screenshots</c>. The project's start and its url are faked, and Playwright is a script on
/// the agent's PATH that records what it was asked. See "Screenshots" in devflow/README.md.
/// </summary>
public sealed class ScreenshotJobTests
{
    private const string Start = "npm ci && npm run dev";
    private const string Url = "http://localhost:5173";

    /// <summary>A report that passes, naming <paramref name="paths"/> as its pages.</summary>
    private static JsonObject Report(params string[] paths)
    {
        var report = Jobs.GoodReport();
        report["pages"] = new JsonArray([.. paths.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["why"] = "It changed", ["looked"] = false })]);
        return report;
    }

    /// <summary>An agent that commits the greeting and reports <paramref name="report"/>.</summary>
    private static ScriptedAgent Agent(JsonObject report) => new(
        async (run, _) => await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"),
        () => AgentResult.Success(report, new JsonObject(), "", "oauth_token"));

    private static JsonObject? Sent(Bench bench) => bench.SentResult()["screenshots"]?.AsObject();

    private static string[] Names(JsonObject? screenshots) =>
        [.. screenshots!["names"]!.AsArray().Select(name => name!.GetValue<string>())];

    [Fact]
    public async Task ThePagesTheReportNamesAreTaken()
    {
        await using var bench = await Bench.Start(check: "echo checked", start: Start, url: Url);
        string? copyHead = null;
        string[] copyFiles = [];
        bench.Starter.OnStart = async spec =>
        {
            copyHead = await Bench.Git(spec.WorkingDirectory, "rev-parse", "HEAD");
            copyFiles = [.. Directory.EnumerateFileSystemEntries(spec.WorkingDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        };
        var job = await bench.NextJob();
        var jobId = job.GetProperty("job_id").GetGuid();
        var paths = bench.Folders.For(jobId);

        await bench.Build(Agent(Report("/", "/habits", "/Habits"))).Run(job, Ct);

        // From a clean copy of the commit pushed: the branch's head, holding only what was
        // committed, started once and ended.
        var head = await bench.RemoteHead();
        Assert.Equal(head, copyHead);
        Assert.Equal([".git", ".gitignore", "README.md", "greeting.txt"], copyFiles);
        var project = Assert.Single(bench.Starter.Started);
        Assert.Equal(paths.Pages, project.Spec.WorkingDirectory);
        Assert.Equal(1, project.Ends);
        Assert.Equal(new Uri(Url), Assert.Single(bench.Probe.Asked.Distinct()));

        // After the check passed: the check ran first, then the pictures.
        var agentRuns = bench.AsAgent.Started.Select(started => started.Spec).ToList();
        var check = agentRuns.FindIndex(spec => spec is { FileName: "sh" } && spec.Arguments[1].Contains("echo checked", StringComparison.Ordinal));
        var firstPicture = agentRuns.FindIndex(spec => spec.FileName == bench.Pages.Playwright);
        Assert.InRange(check, 0, firstPicture - 1);

        // Each page once, in the report's order, the second /habits left out, full page at the
        // desktop's 1280 by 800 and a phone's 390 by 844.
        Assert.Equal(
            [
                $"screenshot --full-page --viewport-size 1280,800 {Url}/ {paths.Pictures}/home-desktop.png browsers={bench.Pages.BrowsersPath}",
                $"screenshot --full-page --viewport-size 390,844 {Url}/ {paths.Pictures}/home-phone.png browsers={bench.Pages.BrowsersPath}",
                $"screenshot --full-page --viewport-size 1280,800 {Url}/habits {paths.Pictures}/habits-desktop.png browsers={bench.Pages.BrowsersPath}",
                $"screenshot --full-page --viewport-size 390,844 {Url}/habits {paths.Pictures}/habits-phone.png browsers={bench.Pages.BrowsersPath}",
            ],
            bench.Pictures);
        Assert.All(agentRuns.Where(spec => spec.FileName == bench.Pages.Playwright), spec => Assert.Equal(paths.Pictures, spec.WorkingDirectory));

        // The result names them, and has nothing to say besides.
        var sent = Sent(bench);
        Assert.Equal(["home-desktop", "home-phone", "habits-desktop", "habits-phone"], Names(sent));
        Assert.Null(sent!["note"]);
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task TheyGoToPrScreenshots()
    {
        // pr-screenshots holds another card's pictures; while this job's are taken, someone pushes
        // more there, so the first push is refused and the worker fetches it and tries once more.
        await using var bench = await Bench.Start(start: Start, url: Url, before: bench => Pictures(bench, "devflow/7/1234567/home-desktop.png"));
        bench.Starter.OnStart = _ => Pictures(bench, "devflow/8/89abcde/home-phone.png");
        var mainBefore = await bench.RemoteHead("main");

        var job = await bench.RunJob(Agent(Report("/habits")));

        var head = (await bench.RemoteHead())!;
        var folder = $"devflow/1/{head[..7]}";
        var tip = (await bench.RemoteHead(PageShots.Branch))!;
        var files = (await Bench.Git(bench.Remote, "ls-tree", "-r", "--name-only", tip)).Split('\n');
        Assert.Equal([$"{folder}/habits-desktop.png", $"{folder}/habits-phone.png", "devflow/7/1234567/home-desktop.png", "devflow/8/89abcde/home-phone.png"], files.Order(StringComparer.Ordinal));

        // Each picture as Playwright wrote it.
        Assert.Equal(Camera.Png, Blob(bench, $"{tip}:{folder}/habits-desktop.png"));
        Assert.Equal(Camera.Png, Blob(bench, $"{tip}:{folder}/habits-phone.png"));

        // On top of what was there, never forced: the commit someone else pushed is its parent, and
        // its parent the first.
        var parents = (await Bench.Git(bench.Remote, "rev-list", "--parents", "-n", "1", tip)).Split(' ');
        Assert.Equal(2, parents.Length);
        Assert.Contains("devflow/8/89abcde/home-phone.png", await Bench.Git(bench.Remote, "ls-tree", "-r", "--name-only", parents[1]), StringComparison.Ordinal);
        // By the app's bot, as the job's other commits.
        var committer = job.GetProperty("committer");
        Assert.Equal(
            $"{committer.GetProperty("name").GetString()} <{committer.GetProperty("email").GetString()}> Screenshots of #1 at {head[..7]}",
            await Bench.Git(bench.Remote, "log", "-1", "--format=%cn <%ce> %s", tip));

        // Never to the default branch.
        Assert.Equal(mainBefore, await bench.RemoteHead("main"));
        Assert.Equal(["habits-desktop", "habits-phone"], Names(Sent(bench)));
        Assert.Contains($"The push to {PageShots.Branch} was refused, fetching it again", bench.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBranchIsMadeWhenTheRepositoryHasNone()
    {
        await using var bench = await Bench.Start(start: Start, url: Url);

        await bench.RunJob(Agent(Report("/")));

        var tip = (await bench.RemoteHead(PageShots.Branch))!;
        // An orphan: one commit, with no parent, holding the pictures alone.
        Assert.Equal(tip, await Bench.Git(bench.Remote, "rev-list", tip));
        var head = (await bench.RemoteHead())!;
        Assert.Equal($"devflow/1/{head[..7]}/home-desktop.png\ndevflow/1/{head[..7]}/home-phone.png", await Bench.Git(bench.Remote, "ls-tree", "-r", "--name-only", tip));
    }

    [Fact]
    public async Task NoStartMeansNoPictures()
    {
        await using var bench = await Bench.Start(start: "", url: "");

        await bench.RunJob(Agent(Report("/habits")));

        var sent = Sent(bench);
        Assert.Empty(Names(sent));
        Assert.Equal("This project has no pages to show", sent!["note"]!.GetValue<string>());
        Assert.Empty(bench.Starter.Started);
        Assert.Empty(bench.Pictures);
        Assert.Null(await bench.RemoteHead(PageShots.Branch));
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task APageThatDoesNotComeUpIsSaidNotHidden()
    {
        // The url never answers: after 3 minutes of the fake clock the wait ends, with why and what
        // the start printed, and the project is ended.
        await using (var bench = await Bench.Start(start: Start, url: Url))
        {
            bench.Probe.Answer = _ => throw new HttpRequestException("Connection refused");
            bench.Starter.Output = "> vite\nError: Cannot find module 'vite'\n";
            var run = bench.RunJob(Agent(Report("/habits")));
            await Until(() => bench.Probe.Asks > 0 || run.IsCompleted);
            while (!run.IsCompleted)
            {
                bench.Clock.Advance(BuildJob.AskEvery);
                await Task.WhenAny(run, Task.Delay(5, Ct));
            }

            await run;
            var sent = Sent(bench);
            Assert.Empty(Names(sent));
            Assert.Equal(
                $"Screenshots could not be taken: the project did not answer at {Url} within 3 minutes; its start said: > vite; Error: Cannot find module 'vite'",
                sent!["note"]!.GetValue<string>());
            Assert.InRange(bench.Probe.Asks, 150, 200);
            Assert.Equal(1, Assert.Single(bench.Starter.Started).Ends);
            Assert.Empty(bench.Pictures);

            // The card is not stopped for it: the run succeeded, and the note is kept.
            Assert.Equal("succeeded", (await bench.Run()).Result);
            Assert.NotEqual("stopped", (await bench.Jobs.Card(1)).State);
        }

        // A page that fails is said by its path and size; the others are still taken and pushed. A
        // 500 is not up yet; a redirect to a login page is.
        await using (var bench = await Bench.Start(start: Start, url: Url))
        {
            var asks = 0;
            bench.Probe.Answer = _ => new HttpResponseMessage(Interlocked.Increment(ref asks) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.Found);
            var run = bench.RunJob(Agent(Report("/broken", "/habits", "/big")));
            await Until(() => bench.Probe.Asks > 0 || run.IsCompleted);
            while (!run.IsCompleted)
            {
                bench.Clock.Advance(BuildJob.AskEvery);
                await Task.WhenAny(run, Task.Delay(5, Ct));
            }

            await run;
            var sent = Sent(bench);
            Assert.Equal(["habits-desktop", "habits-phone"], Names(sent));
            Assert.Equal(
                $"Screenshots could not be taken: /broken at desktop size: {Camera.Broken}; /broken at phone size: {Camera.Broken}; /big at desktop size: the picture is not a PNG of at most 5 MB; /big at phone size: the picture is not a PNG of at most 5 MB",
                sent!["note"]!.GetValue<string>());
            Assert.Equal(2, bench.Probe.Asks);
            Assert.NotNull(await bench.RemoteHead(PageShots.Branch));
            Assert.Equal("succeeded", (await bench.Run()).Result);
        }
    }

    [Fact]
    public async Task TheProjectRunsAsAgentWithItsTestMode()
    {
        await using var bench = await Bench.Start(start: Start, url: Url);
        var job = await bench.NextJob();
        var paths = bench.Folders.For(job.GetProperty("job_id").GetGuid());

        await bench.Build(Agent(Report("/"))).Run(job, Ct);

        // The start command through sh, in the clean copy, in an environment built from nothing:
        // the agent's home, PATH and LANG, and DEVFLOW=1, the one thing Dev flow sets.
        var project = Assert.Single(bench.Starter.Started);
        Assert.Equal("sh", project.Spec.FileName);
        Assert.Equal(["-c", Start], project.Spec.Arguments);
        Assert.Equal(paths.Pages, project.Spec.WorkingDirectory);
        Assert.Equal(
            new Dictionary<string, string> { ["HOME"] = bench.User.Home, ["PATH"] = bench.User.Path, ["LANG"] = "C.UTF-8", ["DEVFLOW"] = "1" },
            project.Spec.Environment);

        // Ended once, after its pictures and before its folder was removed.
        Assert.Equal(1, project.Ends);
        Assert.True(project.CopyThereAtEnd);
        Assert.False(Directory.Exists(paths.Work));

        // In the image, the starter is the agent's, in a process group of its own, through setpriv
        // and setsid.
        var tools = WorkerProgram.Tools(TimeProvider.System, new WorkerLog(TextWriter.Null, TimeProvider.System));
        var starter = Assert.IsType<SystemBackgroundStarter>(tools.Pages.Starter);
        Assert.Equal("agent", starter.RunAs);
        Assert.Equal(PageTools.ImageBrowsers, tools.Pages.BrowsersPath);
        Assert.Equal("playwright", tools.Pages.Playwright);
        var (fileName, arguments) = SystemBackgroundStarter.Command("agent", project.Spec);
        Assert.Equal("setpriv", fileName);
        Assert.Equal(["--reuid", "agent", "--regid", "agent", "--init-groups", "--", "setsid", "--", "sh", "-c", Start], arguments);
    }

    [Fact]
    public async Task EndingTheProjectEndsItsWholeGroup()
    {
        // setsid is util-linux's, in the image and on Linux; macOS has none.
        Assert.SkipUnless(File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid"), "setsid is not on this machine; CI runs this on Linux");
        var folder = Directory.CreateTempSubdirectory("devflow-group-").FullName;
        try
        {
            var pids = Path.Combine(folder, "pids");
            var starter = new SystemBackgroundStarter(null, TimeProvider.System);
            // A server left in the background by its start, as `npm run dev &` leaves one, and one
            // detached from its parent, which ending the tree by parent would miss.
            var spec = new ProcessSpec("sh", ["-c", $"sleep 300 & echo $! >> {pids}; (sleep 300 & echo $! >> {pids}) ; echo up; wait"], folder, new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" });
            await using var project = await starter.Start(spec, Ct);
            await Until(() => File.Exists(pids) && File.ReadAllLines(pids).Length == 2);
            var started = File.ReadAllLines(pids).Select(int.Parse).ToList();
            Assert.All(started, pid => Assert.True(Alive(pid), $"{pid} should run"));

            await project.End();

            Assert.All(started, pid => Assert.False(Alive(pid), $"{pid} should have ended"));
            Assert.NotNull(project.ExitCode);
            Assert.Contains("up", project.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ANameIsAlwaysSafe()
    {
        Assert.Equal("home", PageShots.NameOf("/"));
        Assert.Equal("home", PageShots.NameOf(""));
        Assert.Equal("habits", PageShots.NameOf("/habits/"));
        Assert.Equal("habits-today-week-2", PageShots.NameOf("/Habits/Today?week=2"));
        Assert.Equal("etc-passwd", PageShots.NameOf("../../etc/passwd"));
        Assert.Equal("caf-menu", PageShots.NameOf("/café/menu"));
        Assert.Equal("home", PageShots.NameOf("/ü/ë"));
        var name = PageShots.NameOf("/" + string.Join('/', Enumerable.Repeat("abcdefghi", 12)));
        Assert.Equal(PageShots.MostNameLength - 1, name.Length);
        Assert.False(name.EndsWith('-'));
        foreach (var path in new[] { "/", "/a b/<script>", "/\u0000x", "/" + new string('-', 80) + "x", "//evil.example/x", "/%2e%2e/x" })
        {
            var page = PageShots.NameOf(path);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", page);
            Assert.InRange(page.Length, 1, PageShots.MostNameLength);
            Assert.True(PageShots.IsName($"{page}-desktop"), page);
            Assert.True(PageShots.IsName($"{page}-phone"), page);
        }

        Assert.False(PageShots.IsName("home"));
        Assert.False(PageShots.IsName("../home-desktop"));
        Assert.False(PageShots.IsName("Home-desktop"));
        Assert.False(PageShots.IsName(new string('a', 61) + "-phone"));

        // At most 10 pages, in the report's order, a page whose name an earlier one took left out.
        var paths = Enumerable.Range(1, 12).Select(i => $"/page/{i}").Prepend("/page/1").ToArray();
        var pages = BuildJob.Pages(Report(paths));
        Assert.Equal([.. Enumerable.Range(1, 9).Select(i => $"page-{i}")], pages.Select(page => page.Name));

        // A page's address stays on the project's own.
        Assert.Equal("http://localhost:5173/habits", BuildJob.Address(Url, "/habits"));
        Assert.Equal("http://localhost:5173/habits", BuildJob.Address(Url + "/", "habits"));
        Assert.Equal("http://localhost:5173/app/habits", BuildJob.Address(Url + "/app", "/habits"));
        Assert.Equal("http://localhost:5173//evil.example/x", BuildJob.Address(Url, "//evil.example/x"));
        Assert.Equal("localhost", new Uri(BuildJob.Address(Url, "@evil.example/x")!).Host);
        Assert.Null(BuildJob.Address(Url, "/a b"));
        Assert.Null(BuildJob.Address(Url, "/a\nb"));
        Assert.Null(BuildJob.Address("http://example.com", "/"));
        Assert.Null(BuildJob.Address("file:///etc/passwd", "/"));
    }

    /// <summary>Pushes a commit to pr-screenshots on the remote adding a file at
    /// <paramref name="path"/>, as another job's pictures would.</summary>
    private static async Task Pictures(Bench bench, string path)
    {
        var copy = Path.Combine(bench.Root, $"pictures-{Guid.NewGuid():N}");
        var exists = await bench.RemoteHead(PageShots.Branch) is not null;
        if (exists)
        {
            await Bench.Git(bench.Root, "clone", "--quiet", "--branch", PageShots.Branch, bench.Remote, copy);
        }
        else
        {
            await Bench.Git(bench.Root, "init", "--quiet", $"--initial-branch={PageShots.Branch}", copy);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, path))!);
        await File.WriteAllBytesAsync(Path.Combine(copy, path), Camera.Png, Ct);
        await Bench.Git(copy, "add", "--", path);
        await Bench.Git(copy, [.. Bench.Someone, "commit", "--quiet", "-m", $"Pictures {path}"]);
        await Bench.Git(copy, "push", "--quiet", bench.Remote, PageShots.Branch);
    }

    /// <summary>The bytes of the blob <paramref name="name"/> names in the remote.</summary>
    private static byte[] Blob(Bench bench, string name)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = bench.Remote, RedirectStandardOutput = true };
        foreach (var argument in new[] { "cat-file", "blob", name })
        {
            start.ArgumentList.Add(argument);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        using var memory = new MemoryStream();
        git.StandardOutput.BaseStream.CopyTo(memory);
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return memory.ToArray();
    }

    private static bool Alive(int pid)
    {
        using var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("kill", ["-0", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { RedirectStandardError = true })!;
        kill.WaitForExit();
        return kill.ExitCode == 0;
    }

    /// <summary>Waits, in real time, at most ten seconds, until <paramref name="done"/>.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 1000 && !done(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(done(), "it did not happen within ten seconds");
    }
}
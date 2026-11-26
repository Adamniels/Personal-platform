using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;
using DevFlow.Worker.Pages;
using DevFlow.Worker.Read;
using DevFlow.Worker.Review;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;
using Reading = DevFlow.Tests.Reading.Reading;

/// <summary>
/// A workbench for one build job: Dev flow in process with one project ready to hand out card 1,
/// the fake code host's real bare git repository holding a first commit on <c>main</c>, folders of
/// the test's own for the job's secrets and work, both users' processes recorded, and a clock the
/// test moves. Both users are the test's own, since the test machine has no second user; the
/// difference the image makes is in "BaseImageTests". See "The worker" in devflow/README.md.
/// </summary>
internal sealed class Bench : IAsyncDisposable
{
    /// <summary>The card's title, so its branch is <see cref="Branch"/>.</summary>
    public const string Title = "Add the greeting";

    public const string Branch = "devflow/1-add-the-greeting";

    /// <summary>Stands in, in a check, for a file under the bench's folder the check can write.</summary>
    public const string Out = "{out}";

    private Bench(Jobs jobs, string root, string remote, RecordingHandler handler)
    {
        Jobs = jobs;
        Root = root;
        Remote = remote;
        Handler = handler;
        Folders = new JobFolders(RootsFolder(root, "run"), RootsFolder(root, "handover"), Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        Camera.Install(bin, root);
        User = new AgentUser(null, Directory.CreateDirectory(Path.Combine(root, "home")).FullName, $"{bin}:{System.Environment.GetEnvironmentVariable("PATH")}", "C.UTF-8");
        Pages = new PageTools(Starter, Probe, Path.Combine(root, "browsers"), Path.Combine(bin, "playwright"));
        var settings = WorkerSettings.Load(Environment(("DEVFLOW_URL", jobs.Routes.App.Server.BaseAddress.ToString()), ("DEVFLOW_PROJECT_KEY", jobs.Key)), out _)!;
        _http = DevFlowClient.CreateHttpClient(settings, handler);
        Client = new DevFlowClient(_http);
    }

    private readonly HttpClient _http;

    public Jobs Jobs { get; }

    /// <summary>The bench's own folder, removed with it.</summary>
    public string Root { get; }

    /// <summary>The fake code host's bare repository for the project.</summary>
    public string Remote { get; }

    public RecordingHandler Handler { get; }

    public JobFolders Folders { get; }

    public AgentUser User { get; }

    public DevFlowClient Client { get; }

    public RecordingStarter AsRoot { get; } = new("root");

    public RecordingStarter AsAgent { get; } = new("agent");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Stands in for the project's start: records each, and ends nothing real.</summary>
    public FakePageStarter Starter { get; } = new();

    /// <summary>Stands in for the project answering at its url: 200 unless a test says otherwise.</summary>
    public FakeProbe Probe { get; } = new();

    /// <summary>What a job starts the project and takes its pictures with on the bench.</summary>
    public PageTools Pages { get; }

    /// <summary>What the stand in for Playwright on the agent's PATH was asked, one line each.</summary>
    public IReadOnlyList<string> Pictures => File.Exists(Camera.LogOf(Root)) ? File.ReadAllLines(Camera.LogOf(Root)) : [];

    public StringBuilder Log { get; } = new();

    /// <summary>The file <see cref="Out"/> names.</summary>
    public string OutFile => Path.Combine(Root, "out");

    /// <summary>
    /// The bench, its project's check <paramref name="check"/>, with <see cref="Out"/> standing in
    /// for <see cref="OutFile"/>. <c>main</c> holds a README and a <c>.gitignore</c> ignoring
    /// <c>build/</c>; <paramref name="before"/> may push more to the remote before the job is
    /// handed out. <paramref name="cards"/> are the repository's issues, card 1 unless given. With
    /// <paramref name="read"/> false, the cards are not taken as read, so a reading is the first job.
    /// </summary>
    public static async Task<Bench> Start(string check = "true", Func<Bench, Task>? before = null, string start = "", string url = "", IReadOnlyList<FakeIssue>? cards = null, bool read = true)
    {
        var root = Directory.CreateTempSubdirectory("devflow-build-").FullName;
        var outFile = Path.Combine(root, "out");
        var jobs = await Jobs.Start(cards ?? [Jobs.Card(1, Title)], projectFile: Jobs.ProjectFile(check.Replace(Out, outFile, StringComparison.Ordinal), start, url), read: read);
        try
        {
            var repository = new Repository(Reading.Installation, Reading.RepositoryId, "Adamniels", "Personal-platform");
            var address = Assert.IsType<CodeHostResult<Uri>.Found>(await jobs.Fake.CloneAddress(Jobs.User, repository, Ct)).Value;
            var bench = new Bench(jobs, root, address.LocalPath, new RecordingHandler(jobs.Routes.App.Server.CreateHandler()));
            var seed = Path.Combine(root, "seed");
            await Git(root, "init", "--quiet", "--initial-branch=main", seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# The project\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(seed, ".gitignore"), "build/\n", Ct);
            await Git(seed, "add", "-A");
            await Git(seed, [.. Someone, "commit", "--quiet", "-m", "The first commit"]);
            await Git(seed, "push", "--quiet", bench.Remote, "main");
            if (before is not null)
            {
                await before(bench);
            }

            return bench;
        }
        catch
        {
            await jobs.DisposeAsync();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    /// <summary>The job Dev flow hands the worker, as it came.</summary>
    public async Task<JsonElement> NextJob()
    {
        var next = await Client.NextJob(new NextJobRequest(Jobs.FirstWorker), Ct);
        Assert.Equal(AnswerKind.Job, next.Kind);
        return next.Body;
    }

    /// <summary>A build job for the bench's worker, with <paramref name="agent"/>.</summary>
    public BuildJob Build(IAgent agent) =>
        new(Client, Jobs.FirstWorker, new JobTools(agent, AsRoot, AsAgent, Folders, User, Pages), Clock, new WorkerLog(TextWriter.Synchronized(new StringWriter(Log)), Clock));

    /// <summary>A review job for the bench's worker, with <paramref name="agent"/>.</summary>
    public ReviewJob Review(IAgent agent) =>
        new(Client, Jobs.FirstWorker, new JobTools(agent, AsRoot, AsAgent, Folders, User, Pages), Clock, new WorkerLog(TextWriter.Synchronized(new StringWriter(Log)), Clock));

    /// <summary>A reading job for the bench's worker, with <paramref name="agent"/>.</summary>
    public ReadJob Read(IAgent agent) =>
        new(Client, Jobs.FirstWorker, new JobTools(agent, AsRoot, AsAgent, Folders, User, Pages), Clock, new WorkerLog(TextWriter.Synchronized(new StringWriter(Log)), Clock));

    /// <summary>
    /// Card 1 built, by an agent that commits <c>greeting.txt</c>, its check run and its branch
    /// pushed, and its pull request opened as Dev flow's loop opens it, so its review is the next
    /// job; a minute later, so the review's run starts after the build's. The commit the build
    /// left.
    /// </summary>
    public async Task<string> BuiltAndOpened()
    {
        await RunJob(new ScriptedAgent(async (run, _) => await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting")));
        var head = await RemoteHead() ?? throw new InvalidOperationException("the build pushed nothing");
        await Jobs.Routes.App.Services.GetRequiredService<PullRequestOpener>().OpenPending(Jobs.Project, Jobs.User, Ct);
        Jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return head;
    }

    /// <summary>Hands out the next job and runs it to its end with <paramref name="agent"/>.</summary>
    public async Task<JsonElement> RunJob(IAgent agent)
    {
        var job = await NextJob();
        await Build(agent).Run(job, Ct);
        return job;
    }

    /// <summary>The owner stops the project's run under way, the one there is, through the route
    /// the page's "Stop now" calls.</summary>
    public async Task StopTheRun()
    {
        var run = Assert.Single(await Jobs.Runs(), run => run.FinishedAt is null);
        var stop = await DevFlow.Tests.Jobs.StopRunTests.StopRun(Jobs, run.Id);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status202Accepted, stop.Response.StatusCode);
    }

    /// <summary>Moves the worker's clock a second at a time, so its log posts its heartbeat, until
    /// <paramref name="done"/> holds, as Dev flow's answer stop or a job ending makes it; at most a
    /// few hundred seconds of the clock, and of real time.</summary>
    public async Task MoveUntil(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(25), Ct);
        }

        Assert.True(done(), "the worker's clock moved 400 seconds and it did not happen");
    }

    /// <summary>The remote's refs, each with its commit.</summary>
    public async Task<string> Refs() => await Git(Remote, "for-each-ref", "--format=%(refname) %(objectname)");

    /// <summary>The commit the branch is at on the remote, or null when it is not there.</summary>
    public async Task<string?> RemoteHead(string branch = Branch)
    {
        var (exit, output) = await TryGit(Remote, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
        return exit == 0 ? output.Trim() : null;
    }

    /// <summary>The run Dev flow recorded, the one there is.</summary>
    public async Task<Jobs.RunRow> Run() => Assert.Single(await Jobs.Runs());

    /// <summary>The body of the result the worker sent, the one there is, or the one for the job
    /// <paramref name="jobId"/>.</summary>
    public JsonObject SentResult(Guid? jobId = null) =>
        JsonNode.Parse(Assert.Single(Handler.Calls, call => call.Path.EndsWith("/result", StringComparison.Ordinal)
            && (jobId is null || call.Path == WorkerApi.ResultPath(jobId.Value))).Body)!.AsObject();

    /// <summary>The texts of the lines of <paramref name="kind"/> Dev flow took from the worker's
    /// log posts, in the order they were sent; of the job <paramref name="jobId"/> alone when one is
    /// given.</summary>
    public List<string> Logged(string kind, Guid? jobId = null) =>
        [.. Handler.Calls
            .Where(call => (jobId is null ? call.Path.EndsWith("/log", StringComparison.Ordinal) : call.Path == WorkerApi.LogPath(jobId.Value)) && call.Answer == "200")
            .SelectMany(call => JsonNode.Parse(call.Body)!["lines"]!.AsArray())
            .Where(line => (string)line!["kind"]! == kind)
            .Select(line => (string)line!["text"]!)];

    /// <summary>The identity the test commits as, where it commits as someone other than the
    /// worker or the agent.</summary>
    public static readonly string[] Someone = ["-c", "user.name=Someone else", "-c", "user.email=someone@example.com"];

    /// <summary>git in <paramref name="directory"/> as the test, which must succeed; what it
    /// printed.</summary>
    public static async Task<string> Git(string directory, params string[] arguments)
    {
        var (exit, output) = await TryGit(directory, arguments);
        Assert.True(exit == 0, $"git {string.Join(' ', arguments)} exited {exit}: {output}");
        return output.Trim();
    }

    public static async Task<(int Exit, string Output)> TryGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEndAsync(Ct);
        var error = git.StandardError.ReadToEndAsync(Ct);
        await git.WaitForExitAsync(Ct);
        return (git.ExitCode, await output + await error);
    }

    /// <summary>Commits <paramref name="files"/> on a clone of the remote's <paramref name="from"/>
    /// and pushes it to <paramref name="branch"/> there, as someone other than the worker; the
    /// commit's id.</summary>
    public async Task<string> PushAsSomeoneElse(string branch, string from, string message, params (string Path, string Text)[] files)
    {
        var clone = Path.Combine(Root, $"other-{Guid.NewGuid():N}");
        await Git(Root, "clone", "--quiet", "--branch", from, Remote, clone);
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(clone, path))!);
            await File.WriteAllTextAsync(Path.Combine(clone, path), text, Ct);
        }

        await Git(clone, "add", "-A");
        await Git(clone, [.. Someone, "commit", "--quiet", "-m", message]);
        await Git(clone, "push", "--quiet", "origin", $"HEAD:refs/heads/{branch}");
        return await Git(clone, "rev-parse", "HEAD");
    }

    /// <summary>A folder of the bench's for root's job folders, which only its owner can write, as
    /// in the image, whatever the test's umask.</summary>
    private static string RootsFolder(string root, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    /// <summary>The folder's mode.</summary>
    public static UnixFileMode Mode(string folder) =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException("the worker runs on Linux") : File.GetUnixFileMode(folder);

    /// <summary>Makes the file executable by its owner, as a planted hook or script is.</summary>
    public static void Executable(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the worker runs on Linux, and its tests on Linux or macOS");
        }

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Every file under <paramref name="folder"/>, its path and its bytes as text.</summary>
    public static IEnumerable<(string Path, string Text)> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Select(file => (file, Encoding.Latin1.GetString(File.ReadAllBytes(file))))
            : [];

    public async ValueTask DisposeAsync()
    {
        // The worker's log, in the test's output, so a failure says what the job did.
        TestContext.Current.TestOutputHelper?.WriteLine(Log.ToString());
        _http.Dispose();
        Handler.Dispose();
        await Jobs.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>A process the worker started, and as which of its two users.</summary>
internal sealed record Started(string As, ProcessSpec Spec);

/// <summary>Starts real processes as the test, and records each, as the user the worker meant it
/// for.</summary>
internal sealed class RecordingStarter(string user) : IProcessStarter
{
    private readonly SystemProcessStarter _inner = new(null, TimeProvider.System);
    private readonly List<Started> _started = [];

    public IReadOnlyList<Started> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        lock (_started)
        {
            _started.Add(new Started(user, spec));
        }

        return _inner.Run(spec, timeLimit, cancellationToken);
    }
}

/// <summary>An agent that does what the test says in its working copy, then answers a report that
/// passes, unless the test answers otherwise; it keeps the run it was given.</summary>
internal sealed class ScriptedAgent(Func<AgentRun, CancellationToken, Task> work, Func<AgentResult>? answer = null) : IAgent
{
    public AgentRun? Given { get; private set; }

    public async Task<AgentResult> Run(AgentRun run, CancellationToken cancellationToken)
    {
        Given = run;
        await work(run, cancellationToken);
        return answer?.Invoke() ?? AgentResult.Success(Jobs.GoodReport(), new JsonObject(), "a warning\n", "oauth_token");
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> in the copy.</summary>
    public static async Task Write(AgentRun run, string path, string text)
    {
        var file = Path.Combine(run.WorkingDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text, Ct);
    }

    /// <summary>Writes the file and commits it in the copy, as the agent would, with the identity
    /// the copy's config gives; the commit's id.</summary>
    public static async Task<string> Commit(AgentRun run, string path, string text, string message)
    {
        await Write(run, path, text);
        await Bench.Git(run.WorkingDirectory, "add", "--", path);
        await Bench.Git(run.WorkingDirectory, "commit", "--quiet", "-m", message);
        return await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD");
    }
}
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

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
        Folders = new JobFolders(Directory.CreateDirectory(Path.Combine(root, "run")).FullName, Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
        User = new AgentUser(null, Directory.CreateDirectory(Path.Combine(root, "home")).FullName, System.Environment.GetEnvironmentVariable("PATH")!, "C.UTF-8");
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

    public StringBuilder Log { get; } = new();

    /// <summary>The file <see cref="Out"/> names.</summary>
    public string OutFile => Path.Combine(Root, "out");

    /// <summary>
    /// The bench, its project's check <paramref name="check"/>, with <see cref="Out"/> standing in
    /// for <see cref="OutFile"/>. <c>main</c> holds a README and a <c>.gitignore</c> ignoring
    /// <c>build/</c>; <paramref name="before"/> may push more to the remote before the job is
    /// handed out.
    /// </summary>
    public static async Task<Bench> Start(string check = "true", Func<Bench, Task>? before = null)
    {
        var root = Directory.CreateTempSubdirectory("devflow-build-").FullName;
        var outFile = Path.Combine(root, "out");
        var jobs = await Jobs.Start([Jobs.Card(1, Title)], projectFile: Jobs.ProjectFile(check.Replace(Out, outFile, StringComparison.Ordinal)));
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
        return next.Job;
    }

    /// <summary>A build job for the bench's worker, with <paramref name="agent"/>.</summary>
    public BuildJob Build(IAgent agent) =>
        new(new BuildTools(agent, AsRoot, AsAgent, Folders, User), Clock, new WorkerLog(TextWriter.Synchronized(new StringWriter(Log)), Clock));

    /// <summary>Hands out the next job and runs it to its end with <paramref name="agent"/>.</summary>
    public async Task<JsonElement> RunJob(IAgent agent)
    {
        var job = await NextJob();
        await Build(agent).Run(job, Ct);
        return job;
    }

    /// <summary>The commit the branch is at on the remote, or null when it is not there.</summary>
    public async Task<string?> RemoteHead(string branch = Branch)
    {
        var (exit, output) = await TryGit(Remote, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
        return exit == 0 ? output.Trim() : null;
    }

    /// <summary>The run Dev flow recorded, the one there is.</summary>
    public async Task<Jobs.RunRow> Run() => Assert.Single(await Jobs.Runs());

    /// <summary>The body of the result the worker sent, the one there is.</summary>
    public JsonObject SentResult() =>
        JsonNode.Parse(Assert.Single(Handler.Calls, call => call.Path.EndsWith("/result", StringComparison.Ordinal)).Body)!.AsObject();

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
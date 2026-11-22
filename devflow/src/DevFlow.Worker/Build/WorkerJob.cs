using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Pages;

namespace DevFlow.Worker.Build;

/// <summary>What a job runs with: the agent, a starter for root's processes and one for the
/// agent's, where the job's files go, who the agent's processes run as, and what a build starts the
/// project and takes its pictures with.</summary>
internal sealed record JobTools(IAgent Agent, IProcessStarter Root, IProcessStarter AsAgent, JobFolders Folders, AgentUser User, PageTools Pages);

/// <summary>
/// What every job a worker runs shares, a build's and a review's: root's folders for the job, made
/// new where only root can write; the bundles root hands the agent, so root never writes where the
/// agent can; git as root and as the agent; the job's folders removed whatever happened; and the
/// result sent, patiently, while Dev flow is away. The steps of each kind of job are in
/// <see cref="BuildJob"/> and <see cref="Review.ReviewJob"/>. See "The worker" in
/// devflow/README.md, and "Workers" in devflow/docs/devflow-architecture.md for why.
/// </summary>
internal abstract partial class WorkerJob(DevFlowClient client, Guid workerId, JobTools tools, TimeProvider time, WorkerLog log)
{
    /// <summary>How many lines of the check's output and the agent's standard error are kept.</summary>
    public const int TailLines = 200;

    /// <summary>The longest the worker keeps trying to reach Dev flow for a push key or the
    /// result, while it is away.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>Dev flow, as the worker calls it.</summary>
    protected DevFlowClient Client { get; } = client;

    /// <summary>The id the worker said hello with.</summary>
    protected Guid WorkerId { get; } = workerId;

    protected JobTools Tools { get; } = tools;

    protected TimeProvider Time { get; } = time;

    protected WorkerLog Log { get; } = log;

    /// <summary>git, as root and as the agent.</summary>
    protected WorkerGit Git { get; } = new(tools.Root, tools.AsAgent, tools.User);

    /// <summary>
    /// Sends the job's result, trying again while Dev flow is away, for at most
    /// <see cref="Patience"/>: succeeded with the agent's report when there is no
    /// <paramref name="failure"/>, failed with it otherwise; the check's result when one ran, the
    /// commit, the agent's usage, standard error and refused actions as they came, and a build's
    /// pictures when they were tried, and a conflict's remerge diff; for a check, its pictures'
    /// bytes, since it pushes nothing.
    /// </summary>
    protected async Task Send(Job job, AgentFailure? failure, AgentResult? agent, CheckResult? check, string? head, TimeSpan duration, CancellationToken stopping, ResultScreenshots? screenshots = null, string? remergeDiff = null, IReadOnlyList<ResultPicture>? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var request = new ResultRequest(
            WorkerId,
            failure is null ? ResultRequest.Succeeded : ResultRequest.Failed,
            failure is null && agent?.Report is { } report ? JsonSerializer.SerializeToElement(report, WorkerApi.Json) : null,
            failure is { } failed ? new RunFailure(failed.Reason, failed.Side == FailureSide.Card, failed.TimedOut, failed.ResetsAt) : null,
            check,
            job.Branch,
            head,
            agent?.AuthMethod,
            JsonSerializer.SerializeToElement(agent?.Usage ?? new JsonObject(), WorkerApi.Json),
            (long)duration.TotalMilliseconds,
            Last(agent?.Stderr ?? "", TailLines),
            JsonSerializer.SerializeToElement(agent?.Denials ?? new JsonArray(), WorkerApi.Json),
            screenshots,
            remergeDiff,
            pictures);
        var answer = await Patiently(() => Client.Result(job.JobId, request, stopping), "the result", stopping);
        Log.Write(answer.Kind == AnswerKind.Done
            ? $"Sent the result: {request.Outcome}{(request.Failure is { } sent ? $", {sent.Reason}" : "")}."
            : $"Dev flow did not take the result: {answer.Detail}.");
    }

    /// <summary>The agent, as <c>agent</c>, in <paramref name="workingDirectory"/> with
    /// <paramref name="access"/>, with the job's brief, schema, model, limits, Claude token and the
    /// project's values, ended
    /// at the job's time limit whatever it is doing; and a failure in the card if it was.</summary>
    protected async Task<AgentResult> RunAgent(Job job, string workingDirectory, AgentAccess access, CancellationToken stopping)
    {
        var limits = new AgentLimits(job.Limits.Turns, job.Limits.BudgetUsd, TimeSpan.FromMinutes(job.Limits.Minutes));
        var run = new AgentRun(workingDirectory, job.Brief, job.ReportSchema.GetRawText(), job.Model, limits, new AgentCredential(job.ClaudeToken), access, job.Values);
        using var limit = new CancellationTokenSource(limits.Time, Time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stopping, limit.Token);
        Log.Write($"Starting the agent, for at most {limits.Time.TotalMinutes:0} minutes.");
        AgentResult result;
        try
        {
            result = await Tools.Agent.Run(run, either.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            result = AgentResult.TimedOut(limits, [], "", null);
        }

        Log.Write(result.Failure is { } failure ? $"The agent failed: {failure.Reason}." : "The agent finished with a report.");
        return result;
    }

    /// <summary>
    /// Why the job breaks the protocol, or null when it does not: it carries no model, or one that
    /// is not a model's name, lowercase letters and digits with single dots or hyphens between,
    /// which Claude Code's aliases and its full model ids are, so no value given as
    /// <c>--model</c> is read as another option. Dev flow always sends one; a job without it comes
    /// from a Dev flow that does not speak this worker's protocol, which is no fault of the card.
    /// </summary>
    internal static string? ProtocolError(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Model is { } model && ModelName().IsMatch(model)
            ? null
            : "The job carries no model the worker can pass to Claude Code, so Dev flow and the worker disagree on the protocol";
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();

    /// <summary>A call to Dev flow, made again while Dev flow is away, waiting as the loop waits,
    /// until <see cref="Patience"/> has passed.</summary>
    protected async Task<Answer> Patiently(Func<Task<Answer>> call, string what, CancellationToken stopping)
    {
        var until = Time.GetUtcNow() + Patience;
        var wait = WorkerLoop.FirstWait;
        while (true)
        {
            var answer = await call();
            if (answer.Kind != AnswerKind.Away || Time.GetUtcNow() + wait > until)
            {
                return answer;
            }

            Log.Write($"{answer.Detail} for {what}; trying again in {wait.TotalSeconds:0} seconds.");
            await Task.Delay(wait, Time, stopping);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, WorkerLoop.LongestWait.Ticks));
        }
    }

    /// <summary>
    /// A clone the agent makes of <paramref name="refs"/> of root's repository, in
    /// <paramref name="target"/>, through a bundle root writes in the job's handover folder and
    /// removes after; the clone's remote is that bundle's path, which holds no credentials. Root
    /// never writes in the agent's folder: a link the agent left there, or the folder itself
    /// replaced with one, would turn root's write into one at a path of the agent's choosing.
    /// </summary>
    protected async Task CloneForAgent(Job job, JobPaths paths, IReadOnlyList<string> refs, string target, IReadOnlyList<string> config, string what, CancellationToken stopping)
    {
        var bundle = Path.Combine(paths.Handover, $"{Path.GetFileName(target)}.bundle");
        try
        {
            await Root(paths, ["bundle", "create", "--quiet", bundle, .. refs], FailureSide.OutsideCard, what, stopping);
            if (!OperatingSystem.IsWindows())
            {
                // Readable by the agent whatever root's umask is.
                File.SetUnixFileMode(bundle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            await AgentGit(paths.Work, ["clone", "--quiet", "--branch", job.Branch, .. config, "--", bundle, target], what, stopping);
        }
        finally
        {
            File.Delete(bundle);
        }
    }

    /// <summary>Root's folder for the job at <paramref name="folder"/>, made new with
    /// <paramref name="mode"/>, in a parent no one but its owner can write, so the agent cannot
    /// have put anything there or swap the folder.</summary>
    protected static void RootsOwn(string folder, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the worker runs on Linux");
        }

        var parent = Path.GetDirectoryName(folder)!;
        if ((File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"{parent} can be written by others than root, so the worker does not use it");
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder, mode);
        File.SetUnixFileMode(folder, mode);
    }

    /// <summary>The commit a branch of root's repository is at, or null when it has none.</summary>
    protected async Task<string?> Commit(JobPaths paths, string branch, CancellationToken stopping)
    {
        var found = await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"], stopping);
        return found.ExitCode == 0 ? found.Stdout.Trim() : null;
    }

    /// <summary>A root git command that must succeed, or the step fails with
    /// <paramref name="what"/> on <paramref name="side"/>, and what git said, the key scrubbed.</summary>
    protected async Task Root(JobPaths paths, IReadOnlyList<string> arguments, FailureSide side, string what, CancellationToken stopping, PushCredential? credential = null, bool inSecret = false, string? index = null)
    {
        var outcome = await Git.AsRoot(paths, arguments, stopping, credential, inSecret, index);
        if (outcome.ExitCode != 0)
        {
            var said = Said(outcome.Stderr);
            throw new StepFailed(side, $"{what}: {credential?.Scrub(said) ?? said}");
        }
    }

    protected async Task AgentGit(string directory, IReadOnlyList<string> arguments, string what, CancellationToken stopping)
    {
        var outcome = await Git.AsAgent(directory, arguments, stopping);
        if (outcome.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"{what}: {Said(outcome.Stderr)}");
        }
    }

    /// <summary>A program, not git, as the agent, which must succeed.</summary>
    protected async Task Agent(string program, IReadOnlyList<string> arguments, string directory, FailureSide side, string what, CancellationToken stopping)
    {
        var outcome = await Tools.AsAgent.Run(new ProcessSpec(program, arguments, directory, Tools.User.Environment()), WorkerGit.Limit, stopping);
        if (outcome.ExitCode != 0)
        {
            throw new StepFailed(side, $"{what}: {Said(outcome.Stderr)}");
        }
    }

    /// <summary>
    /// Removes the job's folders: root's two by root, and the agent's by the agent, with
    /// <c>rm -rf</c>, so whatever links the agent left there, or a process of its own still
    /// changing it, the most it removes is what the agent could remove itself. Runs whether or not
    /// the worker is stopping.
    /// </summary>
    protected async Task Remove(JobPaths paths)
    {
        try
        {
            var removed = await Tools.AsAgent.Run(new ProcessSpec("rm", ["-rf", "--", paths.Work], Tools.Folders.Work, Tools.User.Environment()), WorkerGit.Limit, CancellationToken.None);
            if (removed.ExitCode != 0)
            {
                Log.Write($"Could not remove {paths.Work}: {Said(removed.Stderr)}");
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Could not remove {paths.Work}: {e.Message}");
        }

        foreach (var folder in new[] { paths.Handover, paths.Secret })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not remove {folder}: {e.Message}");
            }
        }
    }

    /// <summary>What a program said, for a reason: its last five lines that are not git's hints,
    /// on one line.</summary>
    protected static string Said(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("hint:", StringComparison.Ordinal))
            .TakeLast(5)
            .ToList();
        return lines.Count == 0 ? "it said nothing" : string.Join("; ", lines);
    }

    /// <summary>The last <paramref name="lines"/> lines of <paramref name="text"/>.</summary>
    internal static string Last(string text, int lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = text.EndsWith('\n') ? text.Length - 1 : text.Length;
        var at = end;
        for (var i = 0; i < lines; i++)
        {
            at = at > 0 ? text.LastIndexOf('\n', at - 1) : -1;
            if (at < 0)
            {
                return text;
            }
        }

        return text[(at + 1)..];
    }

    /// <summary>A branch name of letters, digits, <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>,
    /// which git takes as a branch and nothing else: not beginning with <c>-</c> or <c>/</c>, with
    /// no <c>..</c>, <c>//</c> or <c>@{</c>, and not ending in <c>/</c>, <c>.</c> or
    /// <c>.lock</c>.</summary>
    protected static bool SafeBranch(string branch) =>
        BranchName().IsMatch(branch)
        && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("//", StringComparison.Ordinal)
        && !branch.EndsWith('/') && !branch.EndsWith('.')
        && !branch.EndsWith(".lock", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]{0,254}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchName();

    protected const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    protected const UnixFileMode RootWritesAgentReads = RootOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>A step that cannot go on, with why and on which side.</summary>
    protected sealed class StepFailed(FailureSide side, string message) : Exception(message)
    {
        public FailureSide Side { get; } = side;
    }
}
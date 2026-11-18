using System.Text.Json;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Build;

/// <summary>What a build job runs with: the agent, a starter for root's processes and one for the
/// agent's, where the job's files go, and who the agent's processes run as.</summary>
internal sealed record BuildTools(IAgent Agent, IProcessStarter Root, IProcessStarter AsAgent, JobFolders Folders, AgentUser User);

/// <summary>
/// One build job, as Settled in the slice "The worker builds and pushes". Root clones the
/// repository with the push key into a bare repository of its own; the agent clones that, through
/// a bundle, into its working copy, and works there as <c>agent</c> within the time limit; and what
/// it left uncommitted is committed in its copy, as it. See "The worker" in devflow/README.md, and "Workers" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed class BuildJob(BuildTools tools, TimeProvider time, WorkerLog log)
{
    /// <summary>The message of the commit the worker makes of what the agent left uncommitted.</summary>
    public const string LeftUncommitted = "Work the agent left uncommitted";

    private readonly WorkerGit _git = new(tools.Root, tools.AsAgent, tools.User);

    /// <summary>Runs the job <paramref name="answer"/> holds.</summary>
    public async Task Run(JsonElement answer, CancellationToken stopping)
    {
        Job? job;
        try
        {
            job = answer.Deserialize<Job>(WorkerApi.Json);
        }
        catch (JsonException)
        {
            job = null;
        }

        if (job is null || job.Kind != Job.Build)
        {
            log.Write("The job is not a build job this worker can read; it is left.");
            return;
        }

        log.Write($"Building card #{job.Card.Number} on {job.Branch}, job {job.JobId}.");
        await Work(job, tools.Folders.For(job.JobId), stopping);
    }

    /// <summary>The job's work, step by step; a step that cannot go on ends it.</summary>
    private async Task Work(Job job, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            await Fetch(job, paths, stopping);
            await RunAgent(job, paths, stopping);
            await KeepUncommitted(paths, stopping);
        }
        catch (StepFailed failed)
        {
            log.Write($"The job ended: {failed.Message}.");
        }
    }

    /// <summary>
    /// Root clones the repository with the push key into its own bare repository, mode 700 inside
    /// the job's folder of secrets, and checks the job's branch out there: the remote's when it has
    /// one, from a run that was lost, and otherwise a new one at the base. The agent then clones it,
    /// through a bundle root writes in the agent's folder, since the agent cannot read root's, into
    /// its working copy, with the app's bot as its committer. Answers the commit the branch started
    /// at.
    /// </summary>
    private async Task<string> Fetch(Job job, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(tools.Folders.Secrets);
        if (Directory.Exists(paths.Secret))
        {
            Directory.Delete(paths.Secret, recursive: true);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the worker runs on Linux");
        }

        Directory.CreateDirectory(paths.Secret, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var credential = new PushCredential(job.PushKey, job.Repository.CloneAddress);
        await Root(paths, ["clone", "--bare", "--quiet", "--", job.Repository.CloneAddress.OriginalString, paths.Repository], FailureSide.OutsideCard, "The repository could not be fetched", stopping, credential, inSecret: true);

        var baseCommit = await Commit(paths, job.Base, stopping)
            ?? throw new StepFailed(FailureSide.OutsideCard, $"The base branch {job.Base} has no commit");
        var start = await Commit(paths, job.Branch, stopping);
        if (start is null)
        {
            // An empty old value: made only if it is not there.
            await Root(paths, ["update-ref", $"refs/heads/{job.Branch}", baseCommit, ""], FailureSide.OutsideCard, "The branch could not be made", stopping);
            start = baseCommit;
            log.Write($"Made {job.Branch} from {job.Base} at {start}.");
        }
        else
        {
            log.Write($"Continuing {job.Branch}, which the remote has at {start}.");
        }

        await Root(paths, ["symbolic-ref", "HEAD", $"refs/heads/{job.Branch}"], FailureSide.OutsideCard, "The branch could not be checked out", stopping);
        await Agent("mkdir", [paths.Work], tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}", $"refs/heads/{job.Base}"], paths.Copy,
            ["-c", $"user.name={job.Committer.Name}", "-c", $"user.email={job.Committer.Email}"], "The working copy could not be made", stopping);
        return start;
    }

    /// <summary>The agent, as <c>agent</c>, with the job's brief, schema, limits and Claude token,
    /// ended at the job's time limit whatever it is doing; and a failure in the card if it was.</summary>
    private async Task<AgentResult> RunAgent(Job job, JobPaths paths, CancellationToken stopping)
    {
        var limits = new AgentLimits(job.Limits.Turns, job.Limits.BudgetUsd, TimeSpan.FromMinutes(job.Limits.Minutes));
        var run = new AgentRun(paths.Copy, job.Brief, job.ReportSchema.GetRawText(), limits, new AgentCredential(job.ClaudeToken));
        using var limit = new CancellationTokenSource(limits.Time, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stopping, limit.Token);
        log.Write($"Starting the agent, for at most {limits.Time.TotalMinutes:0} minutes.");
        AgentResult result;
        try
        {
            result = await tools.Agent.Run(run, either.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            result = AgentResult.Failed(FailureSide.Card, $"it ran past the time limit of {limits.Time.TotalMinutes:0} minutes and was ended", [], "", null);
        }

        log.Write(result.Failure is { } failure ? $"The agent failed: {failure.Reason}." : "The agent finished with a report.");
        return result;
    }

    /// <summary>What the agent left uncommitted, tracked or new and not ignored, committed in its
    /// copy, as it, before anything is fetched from there.</summary>
    private async Task KeepUncommitted(JobPaths paths, CancellationToken stopping)
    {
        var status = await _git.AsAgent(paths.Copy, ["status", "--porcelain", "--untracked-files=all"], stopping);
        if (status.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.Card, $"The agent's copy is not a repository git can read: {Said(status.Stderr)}");
        }

        if (status.Stdout.Trim().Length == 0)
        {
            return;
        }

        log.Write("The agent left work uncommitted; committing it.");
        await AgentGit(paths.Copy, ["add", "--all"], "The work the agent left could not be added", stopping);
        await AgentGit(paths.Copy, ["commit", "--quiet", "--no-verify", "-m", LeftUncommitted], "The work the agent left could not be committed", stopping);
    }

    /// <summary>A clone the agent makes of <paramref name="refs"/> of root's repository, in
    /// <paramref name="target"/>, through a bundle root writes in the job's folder and removes
    /// after; the clone's remote is that bundle's path, which holds no credentials.</summary>
    private async Task CloneForAgent(Job job, JobPaths paths, IReadOnlyList<string> refs, string target, IReadOnlyList<string> config, string what, CancellationToken stopping)
    {
        var bundle = $"{target}.bundle";
        try
        {
            await Root(paths, ["bundle", "create", "--quiet", bundle, .. refs], FailureSide.OutsideCard, what, stopping);
            await AgentGit(paths.Work, ["clone", "--quiet", "--branch", job.Branch, .. config, "--", bundle, target], what, stopping);
        }
        finally
        {
            File.Delete(bundle);
        }
    }

    /// <summary>The commit a branch of root's repository is at, or null when it has none.</summary>
    private async Task<string?> Commit(JobPaths paths, string branch, CancellationToken stopping)
    {
        var found = await _git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"], stopping);
        return found.ExitCode == 0 ? found.Stdout.Trim() : null;
    }

    /// <summary>A root git command that must succeed, or the step fails with
    /// <paramref name="what"/> on <paramref name="side"/>, and what git said, the key scrubbed.</summary>
    private async Task Root(JobPaths paths, IReadOnlyList<string> arguments, FailureSide side, string what, CancellationToken stopping, PushCredential? credential = null, bool inSecret = false)
    {
        var outcome = await _git.AsRoot(paths, arguments, stopping, credential, inSecret);
        if (outcome.ExitCode != 0)
        {
            var said = Said(outcome.Stderr);
            throw new StepFailed(side, $"{what}: {credential?.Scrub(said) ?? said}");
        }
    }

    private async Task AgentGit(string directory, IReadOnlyList<string> arguments, string what, CancellationToken stopping)
    {
        var outcome = await _git.AsAgent(directory, arguments, stopping);
        if (outcome.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"{what}: {Said(outcome.Stderr)}");
        }
    }

    /// <summary>A program, not git, as the agent, which must succeed.</summary>
    private async Task Agent(string program, IReadOnlyList<string> arguments, string directory, FailureSide side, string what, CancellationToken stopping)
    {
        var outcome = await tools.AsAgent.Run(new ProcessSpec(program, arguments, directory, tools.User.Environment()), WorkerGit.Limit, stopping);
        if (outcome.ExitCode != 0)
        {
            throw new StepFailed(side, $"{what}: {Said(outcome.Stderr)}");
        }
    }

    /// <summary>What a program said, for a reason: its last five lines that are not git's hints,
    /// on one line.</summary>
    private static string Said(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("hint:", StringComparison.Ordinal))
            .TakeLast(5)
            .ToList();
        return lines.Count == 0 ? "it said nothing" : string.Join("; ", lines);
    }

    /// <summary>A step that cannot go on, with why and on which side.</summary>
    private sealed class StepFailed(FailureSide side, string message) : Exception(message)
    {
        public FailureSide Side { get; } = side;
    }
}
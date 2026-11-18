using System.Text.Json;
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Build;

/// <summary>What a build job runs with: the agent, a starter for root's processes and one for the
/// agent's, where the job's files go, and who the agent's processes run as.</summary>
internal sealed record BuildTools(IAgent Agent, IProcessStarter Root, IProcessStarter AsAgent, JobFolders Folders, AgentUser User);

/// <summary>
/// One build job, from the job Dev flow handed out to the result it gets back, as Settled in the
/// slice "The worker builds and pushes". Root clones the repository with the push key into a bare
/// repository of its own; the agent clones that, through a bundle, into its working copy, and works
/// there as <c>agent</c> within the time limit; what it left uncommitted is committed in its copy,
/// as it; root fetches its branch back; the check runs as <c>agent</c> on a clean clone of that
/// commit; and root pushes it, never forced. See "The worker" in devflow/README.md, and "Workers" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed partial class BuildJob(DevFlowClient devFlow, Guid workerId, BuildTools tools, TimeProvider time, WorkerLog log)
{
    /// <summary>The message of the commit the worker makes of what the agent left uncommitted.</summary>
    public const string LeftUncommitted = "Work the agent left uncommitted";

    /// <summary>How many lines of the check's output and the agent's standard error are kept.</summary>
    public const int TailLines = 200;

    /// <summary>The longest the check may take.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromMinutes(30);

    private readonly WorkerGit _git = new(tools.Root, tools.AsAgent, tools.User);

    /// <summary>Runs the job <paramref name="answer"/> holds to its end.</summary>
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
        if (Refusal(job) is { } refused)
        {
            log.Write($"Refused: {refused}.");
            return;
        }

        var outcome = await Work(job, tools.Folders.For(job.JobId), stopping);
        log.Write(outcome.Failure is { } failure ? $"The job failed: {failure.Reason}." : $"The job pushed {outcome.Head}.");
    }

    /// <summary>Why the job is refused before anything runs, or null. A job onto the default
    /// branch is never run, whatever Dev flow sent, and a branch git could read as anything but a
    /// branch name is not either.</summary>
    internal static string? Refusal(Job job)
    {
        if (job.Branch == job.Repository.DefaultBranch || job.Branch == job.Base)
        {
            return $"The job's branch, {job.Branch}, is the repository's default branch, which a worker never pushes to";
        }

        return SafeBranch(job.Branch) && SafeBranch(job.Base) ? null : "The job's branch or base is not a branch name the worker takes";
    }

    /// <summary>The job's work, step by step; a step that cannot go on ends it with its
    /// failure.</summary>
    private async Task<Outcome> Work(Job job, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            var start = await Fetch(job, paths, stopping);
            var result = await RunAgent(job, paths, stopping);
            await KeepUncommitted(paths, stopping);
            var head = await FetchBack(job, paths, start, stopping);
            CheckResult? check = null;
            if (result.Succeeded)
            {
                check = await Check(job, paths, stopping);
            }

            var pushed = await Push(job, paths, head, stopping);
            return pushed is { } refused
                ? Outcome.Failed(Combined(result.Failure, refused), result, check)
                : new Outcome(result.Failure, result, check, head);
        }
        catch (StepFailed failed)
        {
            log.Write($"The job ended: {failed.Message}.");
            return Outcome.Failed(new AgentFailure(failed.Side, failed.Message), null);
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

    /// <summary>
    /// Root fetches the agent's branch from its copy into its own repository, without force, so a
    /// history the agent rewrote is refused; git's side in the copy runs as the agent, see
    /// <see cref="WorkerGit.UploadPack"/>. Answers the head commit.
    /// </summary>
    private async Task<string> FetchBack(Job job, JobPaths paths, string start, CancellationToken stopping)
    {
        await Root(
            paths,
            ["-c", "protocol.file.allow=always", "fetch", "--quiet", "--no-tags", .. _git.UploadPack(), "--", paths.Copy, $"refs/heads/{job.Branch}:refs/heads/{job.Branch}"],
            FailureSide.Card,
            "The agent's branch could not be taken from its copy; a branch it rewrote or removed is not taken",
            stopping);
        var head = await Commit(paths, job.Branch, stopping) ?? throw new StepFailed(FailureSide.OutsideCard, "The branch has no commit after the fetch");
        log.Write(head == start ? $"The branch is still at {head}." : $"The agent's branch is at {head}.");
        return head;
    }

    /// <summary>The project's check, as <c>agent</c>, on a new clone of the head commit, with an
    /// environment built from nothing and <c>CI=true</c>, for at most 30 minutes: its exit code and
    /// the last 200 lines of its output, standard error with it in the order written.</summary>
    private async Task<CheckResult> Check(Job job, JobPaths paths, CancellationToken stopping)
    {
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Check, [], "The clean copy for the check could not be made", stopping);
        log.Write("Running the check.");
        var check = await tools.AsAgent.Run(
            new ProcessSpec("sh", ["-c", $"exec 2>&1\n{job.Check}"], paths.Check, tools.User.Environment(("CI", "true"))),
            CheckLimit,
            stopping);

        var output = Last(check.Stdout, TailLines);
        if (check.TimedOut)
        {
            output = Last($"{output}\nThe check ran past {CheckLimit.TotalMinutes:0} minutes and was ended.\n", TailLines);
        }

        log.Write($"The check exited with {check.ExitCode}.");
        return new CheckResult(check.ExitCode, output);
    }

    /// <summary>Root pushes the head commit to the job's branch from its own repository, with a
    /// fresh push key, never with force. Null when it was pushed; the failure, outside the card,
    /// when it was not.</summary>
    private async Task<AgentFailure?> Push(Job job, JobPaths paths, string head, CancellationToken stopping)
    {
        var answer = await devFlow.PushKey(job.JobId, new PushKeyRequest(workerId), stopping);
        PushKeyAnswer? key = null;
        if (answer.Kind == AnswerKind.Answered)
        {
            try
            {
                key = answer.Body.Deserialize<PushKeyAnswer>(WorkerApi.Json);
            }
            catch (JsonException)
            {
            }
        }

        if (key is null)
        {
            return new AgentFailure(FailureSide.OutsideCard, $"Dev flow gave no fresh push key ({answer.Detail}), so nothing was pushed");
        }

        var credential = new PushCredential(key.PushKey, job.Repository.CloneAddress);
        var push = await _git.AsRoot(paths, ["push", "--quiet", "--porcelain", "origin", $"{head}:refs/heads/{job.Branch}"], stopping, credential);
        if (push.ExitCode != 0)
        {
            var why = credential.Scrub(Said($"{push.Stdout}\n{push.Stderr}"));
            log.Write($"The push was refused: {why}");
            return new AgentFailure(FailureSide.OutsideCard, $"The code host refused the push of {head} to {job.Branch}: {why}");
        }

        log.Write($"Pushed {head} to {job.Branch}.");
        return null;
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

    /// <summary>The failure the result carries when the push failed too: the agent's, with the
    /// push's reason added, on the agent's side; or the push's alone.</summary>
    private static AgentFailure Combined(AgentFailure? agent, AgentFailure push) =>
        agent is null ? push : agent with { Reason = $"{agent.Reason}; and {char.ToLowerInvariant(push.Reason[0])}{push.Reason[1..]}" };

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
    private static bool SafeBranch(string branch) =>
        BranchName().IsMatch(branch)
        && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("//", StringComparison.Ordinal)
        && !branch.EndsWith('/') && !branch.EndsWith('.')
        && !branch.EndsWith(".lock", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]{0,254}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchName();

    /// <summary>How the job ended: its failure, or null; the agent's result, when it ran; the
    /// check's, when it ran; and the commit pushed, when one was.</summary>
    private sealed record Outcome(AgentFailure? Failure, AgentResult? Agent, CheckResult? Check, string? Head)
    {
        public static Outcome Failed(AgentFailure failure, AgentResult? agent, CheckResult? check = null) => new(failure, agent, check, null);
    }

    /// <summary>A step that cannot go on, with why and on which side.</summary>
    private sealed class StepFailed(FailureSide side, string message) : Exception(message)
    {
        public FailureSide Side { get; } = side;
    }
}
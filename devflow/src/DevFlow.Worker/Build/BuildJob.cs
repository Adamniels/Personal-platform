using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Build;

/// <summary>
/// One build job, from the job Dev flow handed out to the result it gets back, as Settled in the
/// slice "The worker builds and pushes"; a fix job is carried the same way, on the card's branch
/// from the commit it was handed, see "Fixing" in devflow/README.md. Root clones the repository with the push key into a bare
/// repository of its own; the agent clones that, through a bundle root writes in a folder only root
/// can write, into its working copy, and works
/// there as <c>agent</c> within the time limit; what it left uncommitted is committed in its copy,
/// as it; root fetches its branch back; the check runs as <c>agent</c> on a clean clone of that
/// commit; root pushes it, never forced; the result goes to Dev flow; and the job's folders are
/// removed. See "The worker" in devflow/README.md, and "Workers" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed partial class BuildJob(DevFlowClient devFlow, Guid workerId, JobTools tools, TimeProvider time, WorkerLog log)
    : WorkerJob(devFlow, workerId, tools, time, log)
{
    /// <summary>The message of the commit the worker makes of what the agent left uncommitted.</summary>
    public const string LeftUncommitted = "Work the agent left uncommitted";

    /// <summary>The longest the check may take.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromMinutes(30);

    /// <summary>How often the worker says again that the check runs, which marks it seen: under
    /// Dev flow's 10 minutes, after which a run handed out 100 minutes ago is lost.</summary>
    public static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(5);

    /// <summary>Runs the job <paramref name="answer"/> holds to its end and sends its result. Only
    /// stopping ends it early, with the folders removed and no result, so Dev flow finds the run
    /// lost and hands the card out again.</summary>
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

        if (job is null || !(job.Kind == Job.Build || (job.Kind == Job.Fix && job.ToFix is not null)))
        {
            Log.Write("The job is not a build or a fix job this worker can read; it is left.");
            return;
        }

        Log.Write($"{(job.Kind == Job.Fix ? "Fixing" : "Building")} card #{job.Card.Number} on {job.Branch}, job {job.JobId}.");
        var started = Time.GetTimestamp();
        Outcome outcome;
        if (Refusal(job) is { } refused)
        {
            Log.Write($"Refused: {refused}.");
            outcome = Outcome.Failed(new AgentFailure(FailureSide.Card, refused), null);
        }
        else
        {
            var paths = Tools.Folders.For(job.JobId);
            try
            {
                outcome = await Work(job, paths, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whatever went wrong, Dev flow hears of it, so the card is not left building.
                outcome = Outcome.Failed(new AgentFailure(FailureSide.OutsideCard, $"The worker failed: {e.GetType().Name}: {e.Message}"), null);
            }
            finally
            {
                await Remove(paths);
            }
        }

        await Send(job, outcome.Failure, outcome.Agent, outcome.Check, outcome.Head, Time.GetElapsedTime(started), stopping);
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
            var result = await RunAgent(job, paths.Copy, AgentAccess.Write, stopping);
            await KeepUncommitted(paths, stopping);
            var head = await FetchBack(job, paths, start, stopping);
            CheckResult? check = null;
            if (result.Succeeded)
            {
                await Progress(job, stopping);
                check = await Check(job, paths, stopping);
            }

            var pushed = await Push(job, paths, head, stopping);
            return pushed is { } refused
                ? Outcome.Failed(Combined(result.Failure, refused), result, check)
                : new Outcome(result.Failure, result, check, head);
        }
        catch (StepFailed failed)
        {
            Log.Write($"The job ended: {failed.Message}.");
            return Outcome.Failed(new AgentFailure(failed.Side, failed.Message), null);
        }
    }

    /// <summary>
    /// Root clones the repository with the push key into its own bare repository, mode 700 inside
    /// the job's folder of secrets, and checks the job's branch out there: the remote's when it has
    /// one, from a run that was lost, and otherwise a new one at the base; for a fix, the remote's,
    /// which must be at the commit the fix was handed, or the fix fails in the card before anything
    /// runs, since it would build on a commit Dev flow did not hand out. The agent then clones it,
    /// through a bundle root writes in the job's handover folder, which the agent can read and not
    /// write, into its working copy, with the app's bot as its committer. Answers the commit the
    /// branch started at.
    /// </summary>
    private async Task<string> Fetch(Job job, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(Tools.Folders.Secrets);
        Directory.CreateDirectory(Tools.Folders.Handover);
        RootsOwn(paths.Secret, RootOnly);
        RootsOwn(paths.Handover, RootWritesAgentReads);
        var credential = new PushCredential(job.PushKey, job.Repository.CloneAddress);
        await Root(paths, ["clone", "--bare", "--quiet", "--", job.Repository.CloneAddress.OriginalString, paths.Repository], FailureSide.OutsideCard, "The repository could not be fetched", stopping, credential, inSecret: true);

        var baseCommit = await Commit(paths, job.Base, stopping)
            ?? throw new StepFailed(FailureSide.OutsideCard, $"The base branch {job.Base} has no commit");
        var start = await Commit(paths, job.Branch, stopping);
        if (job.ToFix is { Commit: var handed } && !string.Equals(start, handed, StringComparison.Ordinal))
        {
            throw new StepFailed(FailureSide.Card, start is null
                ? $"The branch {job.Branch} is not on the remote, so the fix of {handed} was not started"
                : $"The branch {job.Branch} is at {start}, not at {handed}, the commit the fix was handed, so it was not started");
        }

        if (start is null)
        {
            // An empty old value: made only if it is not there.
            await Root(paths, ["update-ref", $"refs/heads/{job.Branch}", baseCommit, ""], FailureSide.OutsideCard, "The branch could not be made", stopping);
            start = baseCommit;
            Log.Write($"Made {job.Branch} from {job.Base} at {start}.");
        }
        else
        {
            Log.Write($"Continuing {job.Branch}, which the remote has at {start}.");
        }

        await Root(paths, ["symbolic-ref", "HEAD", $"refs/heads/{job.Branch}"], FailureSide.OutsideCard, "The branch could not be checked out", stopping);
        await Agent("mkdir", [paths.Work], Tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}", $"refs/heads/{job.Base}"], paths.Copy,
            ["-c", $"user.name={job.Committer.Name}", "-c", $"user.email={job.Committer.Email}"], "The working copy could not be made", stopping);
        return start;
    }

    /// <summary>What the agent left uncommitted, tracked or new and not ignored, committed in its
    /// copy, as it, before anything is fetched from there.</summary>
    private async Task KeepUncommitted(JobPaths paths, CancellationToken stopping)
    {
        var status = await Git.AsAgent(paths.Copy, ["status", "--porcelain", "--untracked-files=all"], stopping);
        if (status.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.Card, $"The agent's copy is not a repository git can read: {Said(status.Stderr)}");
        }

        if (status.Stdout.Trim().Length == 0)
        {
            return;
        }

        Log.Write("The agent left work uncommitted; committing it.");
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
            ["-c", "protocol.file.allow=always", "fetch", "--quiet", "--no-tags", .. Git.UploadPack(), "--", paths.Copy, $"refs/heads/{job.Branch}:refs/heads/{job.Branch}"],
            FailureSide.Card,
            "The agent's branch could not be taken from its copy; a branch it rewrote or removed is not taken",
            stopping);
        var head = await Commit(paths, job.Branch, stopping) ?? throw new StepFailed(FailureSide.OutsideCard, "The branch has no commit after the fetch");
        Log.Write(head == start ? $"The branch is still at {head}." : $"The agent's branch is at {head}.");
        return head;
    }

    /// <summary>The project's check, as <c>agent</c>, on a new clone of the head commit, with an
    /// environment built from nothing and <c>CI=true</c>, for at most 30 minutes: its exit code and
    /// the last 200 lines of its output, standard error with it in the order written.</summary>
    private async Task<CheckResult> Check(Job job, JobPaths paths, CancellationToken stopping)
    {
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Check, [], "The clean copy for the check could not be made", stopping);
        Log.Write("Running the check.");
        ProcessOutcome check;
        using (var running = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            var seen = StaySeen(job, running.Token);
            try
            {
                check = await Tools.AsAgent.Run(
                    new ProcessSpec("sh", ["-c", $"exec 2>&1\n{job.Check}"], paths.Check, Tools.User.Environment(("CI", "true"))),
                    CheckLimit,
                    stopping);
            }
            finally
            {
                await running.CancelAsync();
                await seen;
            }
        }

        var output = Last(check.Stdout, TailLines);
        if (check.TimedOut)
        {
            output = Last($"{output}\nThe check ran past {CheckLimit.TotalMinutes:0} minutes and was ended.\n", TailLines);
        }

        Log.Write($"The check exited with {check.ExitCode}.");
        return new CheckResult(check.ExitCode, output);
    }

    /// <summary>Root pushes the head commit to the job's branch from its own repository, with a
    /// fresh push key, never with force. Null when it was pushed; the failure, outside the card,
    /// when it was not.</summary>
    private async Task<AgentFailure?> Push(Job job, JobPaths paths, string head, CancellationToken stopping)
    {
        var answer = await Patiently(() => Client.PushKey(job.JobId, new PushKeyRequest(WorkerId), stopping), "a fresh push key", stopping);
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
        var push = await Git.AsRoot(paths, ["push", "--quiet", "--porcelain", "origin", $"{head}:refs/heads/{job.Branch}"], stopping, credential);
        if (push.ExitCode != 0)
        {
            var why = credential.Scrub(Said($"{push.Stdout}\n{push.Stderr}"));
            Log.Write($"The push was refused: {why}");
            return new AgentFailure(FailureSide.OutsideCard, $"The code host refused the push of {head} to {job.Branch}: {why}");
        }

        Log.Write($"Pushed {head} to {job.Branch}.");
        return null;
    }

    /// <summary>Says the check runs; Dev flow away or refusing it changes nothing here.</summary>
    private async Task Progress(Job job, CancellationToken stopping)
    {
        var answer = await Client.Progress(job.JobId, new ProgressRequest(WorkerId, ProgressRequest.Checking), stopping);
        if (answer.Kind != AnswerKind.Done)
        {
            Log.Write($"Dev flow did not take the progress: {answer.Detail}.");
        }
    }

    /// <summary>Says again that the check runs every <see cref="SeenEvery"/>, until
    /// <paramref name="running"/> is cancelled; the route is idempotent, and each call marks the
    /// worker seen.</summary>
    private async Task StaySeen(Job job, CancellationToken running)
    {
        try
        {
            while (true)
            {
                await Task.Delay(SeenEvery, Time, running);
                await Progress(job, running);
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
        }
    }

    /// <summary>The failure the result carries when the push failed too: the agent's, with the
    /// push's reason added, on the agent's side; or the push's alone.</summary>
    private static AgentFailure Combined(AgentFailure? agent, AgentFailure push) =>
        agent is null ? push : agent with { Reason = $"{agent.Reason}; and {char.ToLowerInvariant(push.Reason[0])}{push.Reason[1..]}" };

    /// <summary>How the job ended: its failure, or null; the agent's result, when it ran; the
    /// check's, when it ran; and the commit pushed, when one was.</summary>
    private sealed record Outcome(AgentFailure? Failure, AgentResult? Agent, CheckResult? Check, string? Head)
    {
        public static Outcome Failed(AgentFailure failure, AgentResult? agent, CheckResult? check = null) => new(failure, agent, check, null);
    }
}
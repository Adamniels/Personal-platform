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
/// from the commit it was handed, see "Fixing" in devflow/README.md; and a conflict job too, on the
/// feature's branch from the commit it was handed, with the default branch's commit beside it, held
/// to one merge commit of the two and pushed only once its check passed, see "A conflict" there; and
/// a whole feature's fix as a card's fix, on the feature's branch, pushed only once its agent reported
/// it done and its check passed, see "Fixing a feature" there; and a setup job as a build with no
/// card and no check, on the setup's branch, whose files Dev flow validates, see "Setting up a
/// project" there. Root clones the repository with the push key into a bare
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
    /// Dev flow's 10 minutes, after which a run handed out its minutes and 10 more ago is lost.</summary>
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

        // The project's values the job carried are scrubbed from every line logged while it runs,
        // and forgotten with it.
        using var hidden = Log.Hiding(job?.Values);
        if (job is { Kind: Job.RecipeCheck, ToCheck: { } toCheck })
        {
            await RunCheck(job, toCheck, stopping);
            return;
        }

        // A setup is no card's: it sets the project up on a branch of its own.
        var setup = job is { Kind: Job.Setup, Card: null };
        if (job is null || !(setup || (job.Card is not null && (job.Kind == Job.Build || (job.Kind is Job.Fix or Job.FeatureFix && job.ToFix is not null) || (job.Kind == Job.Conflict && job.ToResolve is not null)))))
        {
            Log.Write("The job is not a build, a fix, a conflict, a setup or a check job this worker can read; it is left.");
            return;
        }

        await using var sending = Sending(job);
        Log.Write(job.Kind switch
        {
            Job.Setup when job.ToFix is not null => $"Fixing the project's setup on {job.Branch}, job {job.JobId}.",
            Job.Setup => $"Setting the project up on {job.Branch}, job {job.JobId}.",
            Job.Fix => $"Fixing card #{job.Card!.Number} on {job.Branch}, job {job.JobId}.",
            Job.FeatureFix => $"Fixing feature #{job.Card!.Number} on its branch {job.Branch}, job {job.JobId}.",
            Job.Conflict => $"Bringing {job.Base} into {job.Branch}, the branch of feature #{job.Card!.Number}, job {job.JobId}.",
            _ => $"Building card #{job.Card!.Number} on {job.Branch}, job {job.JobId}.",
        });
        var started = Time.GetTimestamp();
        Outcome outcome;
        if (ProtocolError(job) is { } broken)
        {
            Log.Write($"Refused: {broken}.");
            outcome = Outcome.Failed(new AgentFailure(FailureSide.OutsideCard, broken), null);
        }
        else if (Refusal(job) is { } refused)
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

        await Send(job, sending, outcome.Failure, outcome.Agent, outcome.Check, outcome.Head, Time.GetElapsedTime(started), stopping, outcome.Screenshots, outcome.RemergeDiff);
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
        if (job.ToResolve is { } conflict)
        {
            return await Resolve(job, conflict, paths, stopping);
        }

        try
        {
            var start = await Fetch(job, paths, stopping);
            var result = await RunAgent(job, paths.Copy, AgentAccess.Write, stopping);
            await KeepUncommitted(paths, stopping);
            var head = await FetchBack(job, paths, start, stopping);
            // A feature's branch is shared, and the head of its pull request to the default branch:
            // only a fix whose agent succeeded with a done report and whose check passed is pushed
            // there. A stopped report or one out of form is no fix, whatever its check would say,
            // so the check is not run for it.
            var featureFixDone = job.Kind != Job.FeatureFix || ReportedDone(result);
            CheckResult? check = null;
            // A setup runs on a worker started from the base image, which cannot install the
            // project's tools, so it runs no check: what it pushed is validated by Dev flow.
            if (result.Succeeded && featureFixDone && job.Kind != Job.Setup)
            {
                await Progress(job, stopping);
                check = await Check(job, paths, stopping);
            }

            if (job.Kind == Job.FeatureFix && !(result.Succeeded && featureFixDone && check is { ExitCode: 0 }))
            {
                Log.Write("The fix did not pass, so nothing is pushed to the feature's branch.");
                return new Outcome(result.Failure, result, check, null);
            }

            var pushed = await Push(job, paths, head, stopping);
            if (pushed is { } refused)
            {
                return Outcome.Failed(Combined(result.Failure, refused), result, check);
            }

            // Pictures only of a card's work that passed its check and was pushed: of any other,
            // a feature's fix among them, nobody reads them, and taking them would spend minutes of
            // a worker.
            var screenshots = result.Succeeded && check is { ExitCode: 0 } && job.Kind is not (Job.FeatureFix or Job.Setup)
                ? await WhileSeen(job, () => Screenshots(job, paths, head, result, stopping), stopping)
                : null;
            return new Outcome(result.Failure, result, check, head, screenshots);
        }
        catch (StepFailed failed)
        {
            Log.Write($"The job ended: {failed.Message}.");
            return Outcome.Failed(new AgentFailure(failed.Side, failed.Message), null);
        }
    }

    /// <summary>Whether the agent succeeded with a report that matches its form and is done, read
    /// by <see cref="Report.TryRead"/>, the same reading Dev flow judges the run by, so the worker
    /// never pushes to a feature's branch work Dev flow would not count as a fix.</summary>
    internal static bool ReportedDone(AgentResult result) =>
        result is { Succeeded: true, Report: { } report }
        && Report.TryRead(JsonSerializer.SerializeToElement(report), out var read, out _)
        && read.Outcome == ReportOutcome.Done;

    /// <summary>
    /// A conflict job's work: the copy with both commits; the agent, which merges the default
    /// branch's commit and resolves the conflict; its branch fetched back, with nothing it left
    /// uncommitted taken, since the merge commit is the whole of its work; then, when the agent
    /// succeeded, <see cref="OneMerge"/>, the check on a clean copy, the remerge diff, and the push
    /// without force, each only after the one before it passed, so the feature's branch, which
    /// every later job of the feature builds on, only ever gets a merge whose check passed.
    /// </summary>
    private async Task<Outcome> Resolve(Job job, JobConflict conflict, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            var start = await Fetch(job, paths, stopping);
            var result = await RunAgent(job, paths.Copy, AgentAccess.Write, stopping);
            var head = await FetchBack(job, paths, start, stopping);
            if (!result.Succeeded)
            {
                return Outcome.Failed(result.Failure!, result);
            }

            if (await OneMerge(paths, head, conflict, job.Base, stopping) is { } notOne)
            {
                Log.Write($"Not taken: {notOne}.");
                return Outcome.Failed(new AgentFailure(FailureSide.Card, notOne), result);
            }

            await Progress(job, stopping);
            var check = await Check(job, paths, stopping);
            if (check.ExitCode != 0)
            {
                Log.Write("The check failed, so the merge is not pushed to the feature's branch.");
                return new Outcome(null, result, check, null);
            }

            var diff = await RemergeDiff(paths, head, stopping);
            if (await Push(job, paths, head, stopping) is { } refused)
            {
                return Outcome.Failed(refused, result, check);
            }

            return new Outcome(null, result, check, head, RemergeDiff: diff);
        }
        catch (StepFailed failed)
        {
            Log.Write($"The job ended: {failed.Message}.");
            return Outcome.Failed(new AgentFailure(failed.Side, failed.Message), null);
        }
    }

    /// <summary>
    /// Why the agent's branch, at <paramref name="head"/>, is not exactly one new commit on the
    /// feature's head, a merge whose first parent is that head and whose second is the default
    /// branch's commit handed, or null when it is: anything else is refused, since the only job that
    /// pushes a merge of the default branch onto a feature's branch must merge what Dev flow handed
    /// it and nothing more. Read as root in its own repository.
    /// </summary>
    private async Task<string?> OneMerge(JobPaths paths, string head, JobConflict conflict, string defaultBranch, CancellationToken stopping)
    {
        var listed = await Git.AsRoot(paths, ["rev-list", "--parents", "-n", "1", head], stopping);
        if (listed.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The agent's commit could not be read: {Said(listed.Stderr)}");
        }

        var parents = listed.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToList();
        if (head == conflict.FeatureCommit || parents.Count == 0 || parents[0] != conflict.FeatureCommit)
        {
            return $"The agent's branch is not one new commit on {conflict.FeatureCommit}: it is at {head}";
        }

        if (parents.Count == 1)
        {
            return $"The agent's commit {head} is not a merge: it has one parent";
        }

        if (parents.Count > 2)
        {
            return $"The agent's commit {head} is not a merge of two commits: it has {parents.Count} parents";
        }

        return parents[1] == conflict.DefaultCommit
            ? null
            : $"The agent's merge {head} brings in {parents[1]}, which is not {defaultBranch}'s head, {conflict.DefaultCommit}";
    }

    /// <summary><c>git show --remerge-diff</c> of the merge commit, as root in its own repository:
    /// what the agent chose against what git's own merge would have left, and nothing for the
    /// files that merged cleanly; cut as Dev flow keeps it, <see cref="RemergeDiffs.Cut"/>.</summary>
    private async Task<string> RemergeDiff(JobPaths paths, string head, CancellationToken stopping)
    {
        var shown = await Git.AsRoot(paths, ["show", "--remerge-diff", "--no-color", "--format=", head], stopping);
        if (shown.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The remerge diff of {head} could not be made: {Said(shown.Stderr)}");
        }

        return RemergeDiffs.Cut(shown.Stdout);
    }

    /// <summary>
    /// Root clones the repository with the push key into its own bare repository, mode 700 inside
    /// the job's folder of secrets, and checks the job's branch out there: the remote's when it has
    /// one, from a run that was lost, and otherwise a new one at the base; for a fix, the remote's,
    /// which must be at the commit the fix was handed, or the fix fails in the card before anything
    /// runs, since it would build on a commit Dev flow did not hand out; for a whole feature's fix,
    /// the commit is the one the feature's watch last named, which can lag behind the branch, so a
    /// branch elsewhere fails outside it, as a conflict's does. The agent then clones it,
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
        await CloneRepository(job, paths, stopping);

        var baseCommit = await Commit(paths, job.Base, stopping)
            ?? throw new StepFailed(FailureSide.OutsideCard, $"The base branch {job.Base} has no commit");
        var start = await Commit(paths, job.Branch, stopping);
        if (job.ToFix is { Commit: var handed } && !string.Equals(start, handed, StringComparison.Ordinal))
        {
            throw new StepFailed(job.Kind == Job.FeatureFix ? FailureSide.OutsideCard : FailureSide.Card, start is null
                ? $"The branch {job.Branch} is not on the remote, so the fix of {handed} was not started"
                : $"The branch {job.Branch} is at {start}, not at {handed}, the commit the fix was handed, so it was not started");
        }

        if (job.ToResolve is { } conflict)
        {
            // The commit handed is the one the feature's watch last named, which can lag behind
            // the branch; a push by anyone else stops the project, so a branch elsewhere is tried
            // again once the watch has read it, never held against the job.
            if (!string.Equals(start, conflict.FeatureCommit, StringComparison.Ordinal))
            {
                throw new StepFailed(FailureSide.OutsideCard, start is null
                    ? $"The feature's branch {job.Branch} is not on the remote, so its conflict was not started"
                    : $"The feature's branch {job.Branch} is at {start}, not at {conflict.FeatureCommit}, the commit the conflict was handed, so it was not started");
            }

            // The default branch's commit handed, which the agent finds as origin/<default>: root's
            // own ref of it moved there, in root's own repository, which never pushes it.
            if ((await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"{conflict.DefaultCommit}^{{commit}}"], stopping)).ExitCode != 0)
            {
                throw new StepFailed(FailureSide.Card, $"{job.Base}'s commit {conflict.DefaultCommit} is not in the repository, so its conflict was not started");
            }

            await Root(paths, ["update-ref", $"refs/heads/{job.Base}", conflict.DefaultCommit], FailureSide.OutsideCard, $"{job.Base}'s commit could not be named", stopping);
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
        // The agent's folder for its own pictures, beside its copy, which the brief names.
        await Agent("mkdir", [paths.Self], paths.Work, FailureSide.OutsideCard, "The folder for the agent's pictures could not be made", stopping);
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
        Log.Write($"Running the check: {job.Check}");
        var check = await WhileSeen(
            job,
            () => Tools.AsAgent.Run(
                new ProcessSpec("sh", ["-c", $"exec 2>&1\n{job.Check}"], paths.Check, Tools.User.WithValues(job.Values, ("CI", "true"))),
                CheckLimit,
                stopping),
            stopping);

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
        var (credential, answered) = await FreshKey(job, stopping);
        if (credential is null)
        {
            return new AgentFailure(FailureSide.OutsideCard, $"Dev flow gave no fresh push key ({answered}), so nothing was pushed");
        }

        Log.Write($"Pushing {job.Branch}");
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

    /// <summary>A fresh push key for the job's repository from the push key route, asked again
    /// while Dev flow is away; or null, with what Dev flow answered.</summary>
    private async Task<(PushCredential? Credential, string Why)> FreshKey(Job job, CancellationToken stopping)
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

        return key is null ? (null, answer.Detail) : (new PushCredential(key.PushKey, job.Repository.CloneAddress), "");
    }

    /// <summary><paramref name="work"/>, while the worker says again every
    /// <see cref="SeenEvery"/> that the job runs, so a long check or the pictures after it never
    /// look like a worker that is gone.</summary>
    private async Task<T> WhileSeen<T>(Job job, Func<Task<T>> work, CancellationToken stopping)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var seen = StaySeen(job, running.Token);
        try
        {
            return await work();
        }
        finally
        {
            await running.CancelAsync();
            await seen;
        }
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
    /// check's, when it ran; the commit pushed, when one was; its pictures, when they were tried;
    /// and for a conflict, the remerge diff of the merge it pushed.</summary>
    private sealed record Outcome(AgentFailure? Failure, AgentResult? Agent, CheckResult? Check, string? Head, ResultScreenshots? Screenshots = null, string? RemergeDiff = null)
    {
        public static Outcome Failed(AgentFailure failure, AgentResult? agent, CheckResult? check = null) => new(failure, agent, check, null);
    }
}
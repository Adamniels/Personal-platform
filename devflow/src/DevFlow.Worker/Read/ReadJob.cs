using System.Text.Json;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

namespace DevFlow.Worker.Read;

/// <summary>
/// One reading job, from the job Dev flow handed out to the reading it gets back, as Settled in the
/// slice "The reading job". Root clones the repository with the job's key, which only fetches, into
/// a bare repository of its own; the agent clones the job's branch from it, through a bundle in
/// root's handover folder, the default branch or the feature's branch where it exists, and runs
/// there read only, with the reader's brief and the reading's form. The cards it reads are quoted in
/// its brief. Nothing is committed, checked or pushed, no push key is asked for, and the result names
/// no commit. See "Jobs" in devflow/README.md, and "Reading a card" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed class ReadJob(DevFlowClient devFlow, Guid workerId, JobTools tools, TimeProvider time, WorkerLog log)
    : WorkerJob(devFlow, workerId, tools, time, log)
{
    /// <summary>Runs the job <paramref name="answer"/> holds to its end and sends its result. Only
    /// stopping ends it early, with the folders removed and no result, so Dev flow finds the run lost
    /// and hands the reading out again.</summary>
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

        if (job is not { Kind: Job.Read, ToRead: { } toRead })
        {
            Log.Write("The job is not a reading job this worker can read; it is left.");
            return;
        }

        Log.Write($"Reading {toRead.Cards.Count} {(toRead.Cards.Count == 1 ? "card" : "cards")} of #{job.Card.Number} on {job.Branch}, job {job.JobId}.");
        var started = Time.GetTimestamp();
        AgentFailure? failure;
        AgentResult? result = null;
        if (ProtocolError(job) is { } broken)
        {
            Log.Write($"Refused: {broken}.");
            failure = new AgentFailure(FailureSide.OutsideCard, broken);
        }
        else if (Refusal(job) is { } refused)
        {
            Log.Write($"Refused: {refused}.");
            failure = new AgentFailure(FailureSide.Card, refused);
        }
        else
        {
            var paths = Tools.Folders.For(job.JobId);
            try
            {
                (failure, result) = await Work(job, paths, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whatever went wrong, Dev flow hears of it, so the cards are not left reading.
                failure = new AgentFailure(FailureSide.OutsideCard, $"The worker failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                await Remove(paths);
            }
        }

        // Nothing was pushed, so the result names no commit.
        await Send(job, failure, result, null, null, Time.GetElapsedTime(started), stopping);
    }

    /// <summary>Why the job is refused before anything runs, or null: a branch or base git could
    /// read as anything but a branch name. A reading may read the default branch itself, so its
    /// branch may be its base.</summary>
    internal static string? Refusal(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return SafeBranch(job.Branch) && SafeBranch(job.Base)
            ? null
            : "The job's branch or base is not a branch name the worker takes";
    }

    /// <summary>The job's work, step by step; a step that cannot go on ends it with its failure.</summary>
    private async Task<(AgentFailure? Failure, AgentResult? Result)> Work(Job job, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            await Fetch(job, paths, stopping);
            var result = await RunAgent(job, paths.Copy, AgentAccess.ReadOnly, stopping);
            return (result.Failure, result);
        }
        catch (StepFailed failed)
        {
            Log.Write($"The job ended: {failed.Message}.");
            return (new AgentFailure(failed.Side, failed.Message), null);
        }
    }

    /// <summary>
    /// Root clones the repository with the job's key into its own bare repository, mode 700 inside
    /// the job's folder of secrets, and checks the branch to read is there; the agent clones that
    /// branch, through a bundle in the handover folder, into its working copy.
    /// </summary>
    private async Task Fetch(Job job, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(Tools.Folders.Secrets);
        Directory.CreateDirectory(Tools.Folders.Handover);
        RootsOwn(paths.Secret, RootOnly);
        RootsOwn(paths.Handover, RootWritesAgentReads);
        var credential = new PushCredential(job.PushKey, job.Repository.CloneAddress);
        await Root(paths, ["clone", "--bare", "--quiet", "--", job.Repository.CloneAddress.OriginalString, paths.Repository], FailureSide.OutsideCard, "The repository could not be fetched", stopping, credential, inSecret: true);
        if (await Commit(paths, job.Branch, stopping) is not { } head)
        {
            // Gone since it was handed out, or never made: another try reads it again.
            throw new StepFailed(FailureSide.OutsideCard, $"The branch {job.Branch} has no commit");
        }

        await Agent("mkdir", [paths.Work], Tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Copy, [], "The working copy could not be made", stopping);
        Log.Write($"The copy is at {head}.");
    }
}
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Build;

/// <summary>
/// A check job, as Settled in the slice "The check at registration": root clones the repository with
/// the job's key, which only fetches, into its own bare repository and names the commit it was
/// handed as the default branch there, in its own repository, which never pushes; the project's
/// check runs as <c>agent</c> on a clean clone of that commit, as a build's does, for at most 30
/// minutes; and when it passed and the job has a start and an address, the project is started from
/// another clean clone and its address taken at both sizes. The result is the check's exit code, its
/// last 200 lines and the pictures' bytes, which go to Dev flow, since nothing is pushed. No agent
/// runs and no push key is asked for. Whatever stops the check from running is a failure outside,
/// never a verdict on the project's setup. See "Setting up a project" in devflow/README.md.
/// </summary>
internal sealed partial class BuildJob
{
    /// <summary>Runs the check job to its end and sends its result. Only stopping ends it early,
    /// with the folders removed and no result, so Dev flow finds the run lost and hands the check
    /// out again.</summary>
    private async Task RunCheck(Job job, JobCheck toCheck, CancellationToken stopping)
    {
        await using var sending = Sending(job);
        Log.Write($"Checking version {toCheck.Version} of the project's setup at {toCheck.Commit} of {job.Branch}, job {job.JobId}.");
        var started = Time.GetTimestamp();
        AgentFailure? failure = null;
        CheckResult? check = null;
        ResultScreenshots? screenshots = null;
        IReadOnlyList<ResultPicture>? pictures = null;
        if (CheckRefusal(job, toCheck) is { } refused)
        {
            Log.Write($"Refused: {refused}.");
            failure = new AgentFailure(FailureSide.OutsideCard, refused);
        }
        else
        {
            var paths = Tools.Folders.For(job.JobId);
            try
            {
                (check, screenshots, pictures) = await Checked(job, toCheck, paths, stopping);
            }
            catch (StepFailed failed)
            {
                Log.Write($"The job ended: {failed.Message}.");
                failure = new AgentFailure(FailureSide.OutsideCard, failed.Message);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whatever went wrong, Dev flow hears of it, so the check is not left running.
                failure = new AgentFailure(FailureSide.OutsideCard, $"The worker failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                await Remove(paths);
            }
        }

        // A check that ran succeeds whatever its exit code, which is the verdict; nothing is pushed,
        // so the result names no commit.
        await Send(job, sending, failure, null, failure is null ? check : null, null, Time.GetElapsedTime(started), stopping, failure is null ? screenshots : null, pictures: failure is null ? pictures : null);
    }

    /// <summary>Why the check job is refused before anything runs, or null: its branch and base
    /// are not both the repository's default branch, or not a branch name the worker takes, or its
    /// commit is not a full commit id.</summary>
    internal static string? CheckRefusal(Job job, JobCheck toCheck)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(toCheck);
        if (job.Branch != job.Repository.DefaultBranch || job.Base != job.Branch || !SafeBranch(job.Branch))
        {
            return "The check's branch is not the repository's default branch, so Dev flow and the worker disagree on the protocol";
        }

        return CommitId().IsMatch(toCheck.Commit)
            ? null
            : "The check's commit is not a full commit id, so Dev flow and the worker disagree on the protocol";
    }

    /// <summary>The check's steps: the clone, the check, and the pictures when it passed.</summary>
    private async Task<(CheckResult Check, ResultScreenshots? Screenshots, IReadOnlyList<ResultPicture>? Pictures)> Checked(Job job, JobCheck toCheck, JobPaths paths, CancellationToken stopping)
    {
        await FetchCommit(job, toCheck.Commit, paths, stopping);
        await Progress(job, stopping);
        var check = await Check(job, paths, stopping);
        if (check.ExitCode != 0)
        {
            Log.Write("The check failed, so no pictures are taken.");
            return (check, null, null);
        }

        var (screenshots, pictures) = await WhileSeen(job, () => AddressPictures(job, paths, toCheck.Commit, stopping), stopping);
        return (check, screenshots, pictures);
    }

    /// <summary>
    /// Root clones the repository into its own bare repository, mode 700 inside the job's folder of
    /// secrets, checks the commit is there, and names it as the job's branch there, so every clean
    /// copy made of that branch is exactly the commit; the agent's folder for the job is made.
    /// </summary>
    private async Task FetchCommit(Job job, string commit, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(Tools.Folders.Secrets);
        Directory.CreateDirectory(Tools.Folders.Handover);
        RootsOwn(paths.Secret, RootOnly);
        RootsOwn(paths.Handover, RootWritesAgentReads);
        await CloneRepository(job, paths, stopping);
        if ((await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"{commit}^{{commit}}"], stopping)).ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The commit {commit} is not in the repository, so it was not checked");
        }

        await Root(paths, ["update-ref", $"refs/heads/{job.Branch}", commit], FailureSide.OutsideCard, "The commit could not be named", stopping);
        await Root(paths, ["symbolic-ref", "HEAD", $"refs/heads/{job.Branch}"], FailureSide.OutsideCard, "The branch could not be checked out", stopping);
        await Agent("mkdir", [paths.Work], Tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        Log.Write($"Checking {job.Branch} at {commit}.");
    }

    /// <summary>
    /// The project's address taken at both sizes from a clean copy of <paramref name="commit"/>,
    /// as a build's pages are, and the pictures' bytes; or no pictures and why, in the note. Like a
    /// build's, they never fail the job.
    /// </summary>
    private async Task<(ResultScreenshots Screenshots, IReadOnlyList<ResultPicture>? Pictures)> AddressPictures(Job job, JobPaths paths, string commit, CancellationToken stopping)
    {
        if (job.Start.Trim().Length == 0 || job.Url.Trim().Length == 0 || !Uri.TryCreate(job.Url, UriKind.Absolute, out var address))
        {
            Log.Write("The project has no start or url, so no pictures are taken.");
            return (new ResultScreenshots([], PageShots.NoPages), null);
        }

        try
        {
            var (pictures, failures) = await Take(job, paths, commit, [(PageShots.NameOf(address.AbsolutePath), "")], stopping);
            return (
                new ResultScreenshots([.. pictures.Select(picture => picture.Name)], failures.Count == 0 ? null : Note(string.Join("; ", failures))),
                pictures.Count == 0 ? null : [.. pictures.Select(picture => new ResultPicture(picture.Name, picture.Bytes))]);
        }
        catch (StepFailed failed)
        {
            Log.Write($"No pictures: {failed.Message}.");
            return (NoPictures(failed.Message), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"No pictures: {e.GetType().Name}: {e.Message}.");
            return (NoPictures($"the worker failed: {e.GetType().Name}: {e.Message}"), null);
        }
    }

    /// <summary>A full commit id, 40 or 64 lowercase hex digits.</summary>
    [GeneratedRegex(@"^([0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}
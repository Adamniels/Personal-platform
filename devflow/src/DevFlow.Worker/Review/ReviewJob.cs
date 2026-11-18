using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

namespace DevFlow.Worker.Review;

/// <summary>
/// One review job, from the job Dev flow handed out to the verdict it gets back, as Settled in the
/// slice "The review". Root clones the repository with the job's key, which only fetches, into a
/// bare repository of its own and points the card's branch there at the commit to review; the agent
/// clones that, through a bundle in root's handover folder, into its working copy, which is checked
/// to be at the commit. Root writes the evidence, <c>diff.patch</c> from the base to the commit,
/// <c>card.md</c>, <c>report.json</c>, <c>check.txt</c>, <c>earlier.json</c>, the card's earlier
/// rounds, and <c>screenshots.txt</c>, and an archive of the pictures the worker took of the commit
/// from <c>pr-screenshots</c>, in its handover folder, and the agent copies them into a folder
/// beside its copy and unpacks the pictures into <c>review/pictures</c>, so root never writes where
/// the agent can. The agent
/// then runs there read only, with the reviewer's brief and the verdict's form. Nothing is
/// committed or pushed, no push key is asked for, and the result names the commit the worker
/// checked out, never the agent's word. See "Review" in devflow/README.md, and "Review" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed partial class ReviewJob(DevFlowClient devFlow, Guid workerId, JobTools tools, TimeProvider time, WorkerLog log)
    : WorkerJob(devFlow, workerId, tools, time, log)
{
    /// <summary>The files a review is given beside the copy, in the order the brief names them.</summary>
    public static readonly IReadOnlyList<string> Evidence = ["card.md", "diff.patch", "report.json", "check.txt", "earlier.json", "screenshots.txt"];

    /// <summary>The folder in the review folder the pictures are put in.</summary>
    public const string PicturesFolder = "pictures";

    /// <summary>Runs the job <paramref name="answer"/> holds to its end and sends its result. Only
    /// stopping ends it early, with the folders removed and no result, so Dev flow finds the run
    /// lost and hands the review out again.</summary>
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

        if (job is not { Kind: Job.Review, ToReview: { } toReview })
        {
            Log.Write("The job is not a review job this worker can read; it is left.");
            return;
        }

        Log.Write($"Reviewing {toReview.Commit} of card #{job.Card.Number} on {job.Branch}, job {job.JobId}.");
        var started = Time.GetTimestamp();
        AgentFailure? failure;
        AgentResult? result = null;
        if (Refusal(job, toReview) is { } refused)
        {
            Log.Write($"Refused: {refused}.");
            failure = new AgentFailure(FailureSide.Card, refused);
        }
        else
        {
            var paths = Tools.Folders.For(job.JobId);
            try
            {
                (failure, result) = await Work(job, toReview, paths, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whatever went wrong, Dev flow hears of it, so the card is not left reviewing.
                failure = new AgentFailure(FailureSide.OutsideCard, $"The worker failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                await Remove(paths);
            }
        }

        // The commit reviewed is the worker's: the one it checked out, named only when the review
        // ran on it.
        await Send(job, failure, result, null, failure is null ? toReview.Commit : null, Time.GetElapsedTime(started), stopping);
    }

    /// <summary>Why the job is refused before anything runs, or null: a branch or base git could
    /// read as anything but a branch name, or a commit that is not a full commit id.</summary>
    internal static string? Refusal(Job job, JobReview toReview)
    {
        if (!SafeBranch(job.Branch) || !SafeBranch(job.Base) || job.Branch == job.Base)
        {
            return "The job's branch or base is not a branch name the worker takes";
        }

        return CommitId().IsMatch(toReview.Commit) ? null : "The commit to review is not a full commit id";
    }

    /// <summary>The job's work, step by step; a step that cannot go on ends it with its failure.</summary>
    private async Task<(AgentFailure? Failure, AgentResult? Result)> Work(Job job, JobReview toReview, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            await Fetch(job, toReview, paths, stopping);
            await WriteEvidence(job, toReview, paths, stopping);
            var result = await RunAgent(job, paths.Work, AgentAccess.ReadOnly, stopping);
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
    /// the job's folder of secrets, checks the commit and the base are there, and points the card's
    /// branch at the commit in its own repository, which is never pushed. The agent clones it,
    /// through a bundle in the handover folder, into its working copy, which must then be at the
    /// commit.
    /// </summary>
    private async Task Fetch(Job job, JobReview toReview, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(Tools.Folders.Secrets);
        Directory.CreateDirectory(Tools.Folders.Handover);
        RootsOwn(paths.Secret, RootOnly);
        RootsOwn(paths.Handover, RootWritesAgentReads);
        var credential = new PushCredential(job.PushKey, job.Repository.CloneAddress);
        await Root(paths, ["clone", "--bare", "--quiet", "--", job.Repository.CloneAddress.OriginalString, paths.Repository], FailureSide.OutsideCard, "The repository could not be fetched", stopping, credential, inSecret: true);

        var found = await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"{toReview.Commit}^{{commit}}"], stopping);
        if (found.ExitCode != 0 || found.Stdout.Trim() != toReview.Commit)
        {
            // Gone from the repository, by a branch forced over it: nothing to review, and another
            // try would find it gone too.
            throw new StepFailed(FailureSide.Card, $"The commit to review, {toReview.Commit}, is no longer in the repository");
        }

        if (await Commit(paths, job.Base, stopping) is null)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The base branch {job.Base} has no commit");
        }

        await Root(paths, ["update-ref", $"refs/heads/{job.Branch}", toReview.Commit], FailureSide.OutsideCard, "The branch could not be pointed at the commit", stopping);
        await Root(paths, ["symbolic-ref", "HEAD", $"refs/heads/{job.Branch}"], FailureSide.OutsideCard, "The branch could not be checked out", stopping);
        await Agent("mkdir", [paths.Work], Tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Copy, [], "The working copy could not be made", stopping);

        var head = await Git.AsAgent(paths.Copy, ["rev-parse", "HEAD"], stopping);
        if (head.ExitCode != 0 || head.Stdout.Trim() != toReview.Commit)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The working copy is not at {toReview.Commit}");
        }

        Log.Write($"The copy is at {toReview.Commit}.");
    }

    /// <summary>
    /// What the reviewer reads besides the code, written by root in the job's handover folder, which
    /// the agent can read and not write, and copied by the agent into the review folder beside its
    /// copy: the diff from where the branch left the base to the commit, the card as it was picked,
    /// the build's report as it came, and the build's check with its command, exit code and output.
    /// </summary>
    private async Task WriteEvidence(Job job, JobReview toReview, JobPaths paths, CancellationToken stopping)
    {
        // Three dots: from where the branch left the base, as the pull request shows it, so what
        // landed on the base since is not read as removed. No external diff or text conversion, so
        // nothing the repository's attributes name is run.
        var diff = await Git.AsRoot(paths, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", $"refs/heads/{job.Base}...{toReview.Commit}", "--"], stopping);
        if (diff.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The diff could not be made: {Said(diff.Stderr)}");
        }

        var (pictures, unread) = await ArchivePictures(job, toReview, paths, stopping);
        var check = toReview.Check;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card.md"] = Line(string.Create(CultureInfo.InvariantCulture, $"#{job.Card.Number} {job.Card.Title}\n\n{job.Card.Body}")),
            ["diff.patch"] = diff.Stdout,
            ["report.json"] = Line(toReview.Report.GetRawText()),
            ["check.txt"] = Line(string.Create(CultureInfo.InvariantCulture, $"$ {job.Check}\nexit {check.ExitCode}\n\n{check.Output}")),
            // The card's earlier rounds, as Dev flow sent them; an empty list on a first review.
            ["earlier.json"] = Line(JsonSerializer.Serialize(toReview.Earlier ?? [], WorkerApi.Json)),
            ["screenshots.txt"] = PicturesText(toReview, pictures, unread),
        };
        foreach (var name in Evidence)
        {
            var file = Path.Combine(paths.Handover, name);
            await File.WriteAllTextAsync(file, files[name], new UTF8Encoding(false), stopping);
            if (!OperatingSystem.IsWindows())
            {
                // Readable by the agent whatever root's umask is.
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        await Agent("mkdir", [paths.Review], paths.Work, FailureSide.OutsideCard, "The review folder could not be made", stopping);
        await Agent("cp", ["--", .. Evidence.Select(name => Path.Combine(paths.Handover, name)), paths.Review], paths.Work, FailureSide.OutsideCard, "The review's files could not be copied", stopping);
        if (pictures.Count > 0)
        {
            var folder = Path.Combine(paths.Review, PicturesFolder);
            await Agent("mkdir", [folder], paths.Work, FailureSide.OutsideCard, "The pictures' folder could not be made", stopping);
            await Agent("tar", ["-x", "-f", Path.Combine(paths.Handover, "pictures.tar"), "-C", folder], paths.Work, FailureSide.OutsideCard, "The pictures could not be unpacked", stopping);
        }

        Log.Write($"Wrote the review's files, the diff {diff.Stdout.Length} characters long, and {pictures.Count} pictures.");
    }

    /// <summary>
    /// The build's pictures of the commit, archived by root from its own clone of
    /// <see cref="PageShots.Branch"/>, which the clone fetched with everything else, into the handover
    /// folder, for the agent to unpack: the names it archived, each one the build named and the rules
    /// allow, and why none could be, when that is so. A picture missing is said, never a failure: the
    /// review goes on without it.
    /// </summary>
    private async Task<(IReadOnlyList<string> Pictures, string? Unread)> ArchivePictures(Job job, JobReview toReview, JobPaths paths, CancellationToken stopping)
    {
        var names = (toReview.Screenshots?.Names ?? []).Where(PageShots.IsName).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return ([], null);
        }

        var archive = Path.Combine(paths.Handover, "pictures.tar");
        var made = await Git.AsRoot(
            paths,
            ["archive", "--format=tar", $"--output={archive}", $"refs/heads/{PageShots.Branch}:{PageShots.Folder(job.Card.Number, toReview.Commit)}", "--", .. names.Select(name => $"{name}.png")],
            stopping);
        if (made.ExitCode != 0)
        {
            File.Delete(archive);
            Log.Write($"The pictures could not be read: {Said(made.Stderr)}");
            return ([], $"The pictures could not be read from {PageShots.Branch}: {Said(made.Stderr)}");
        }

        if (!OperatingSystem.IsWindows())
        {
            // Readable by the agent whatever root's umask is.
            File.SetUnixFileMode(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return (names, null);
    }

    /// <summary>What <c>screenshots.txt</c> says: each picture in the review's folder, then the
    /// build's note and why the pictures could not be read, each when there is one; or that the
    /// build took none.</summary>
    private static string PicturesText(JobReview toReview, IReadOnlyList<string> pictures, string? unread)
    {
        var said = new[] { toReview.Screenshots?.Note, unread }.OfType<string>().ToList();
        if (pictures.Count == 0 && said.Count == 0)
        {
            return "The build took no pictures of this commit.\n";
        }

        var text = new StringBuilder();
        if (pictures.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"The worker's pictures of {toReview.Commit[..7]}, taken after its check passed, each page full length:\n\n");
            foreach (var name in pictures)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {PicturesFolder}/{name}.png\n");
            }
        }

        foreach (var line in said)
        {
            text.Append(text.Length > 0 ? "\n" : "").Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary><paramref name="text"/> ending in one newline.</summary>
    private static string Line(string text) => text.EndsWith('\n') ? text : text + "\n";

    /// <summary>A full commit id, 40 or 64 lowercase hex digits.</summary>
    [GeneratedRegex(@"^([0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}
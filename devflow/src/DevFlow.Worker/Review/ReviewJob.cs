using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

namespace DevFlow.Worker.Review;

/// <summary>
/// One review job, of a card or of a whole feature, from the job Dev flow handed out to the verdict
/// it gets back, as Settled in the slices "The review" and "Reviewing the whole feature". Root
/// clones the repository with the job's key, which only fetches, into a
/// bare repository of its own and points the card's branch, or the feature's, there at the commit to review; the agent
/// clones that, through a bundle in root's handover folder, into its working copy, which is checked
/// to be at the commit. Root writes the evidence, <c>diff.patch</c> from the base to the commit,
/// <c>card.md</c>, <c>report.json</c>, <c>check.txt</c>, <c>earlier.json</c>, the card's earlier
/// rounds, and <c>screenshots.txt</c>, and an archive of the pictures the worker took of the commit
/// from <c>pr-screenshots</c>, in its handover folder, and the agent copies them into a folder
/// beside its copy and unpacks the pictures into <c>review/pictures</c>, so root never writes where
/// the agent can. A whole feature's review is given <c>feature.md</c>, <c>cards.md</c>,
/// <c>diff.patch</c> from the merge base with the default branch, <c>remerge.patch</c> when a
/// conflict was resolved since its last review, and <c>check.txt</c> instead. The agent
/// then runs there read only, with the reviewer's brief and the verdict's form. Nothing is
/// committed or pushed, no push key is asked for, and the result names the commit the worker
/// checked out, never the agent's word. See "Review" in devflow/README.md, and "Review" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed partial class ReviewJob(DevFlowClient devFlow, Guid workerId, JobTools tools, TimeProvider time, WorkerLog log)
    : WorkerJob(devFlow, workerId, tools, time, log)
{
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

        var commit = job switch
        {
            { Kind: Job.Review, ToReview: { } toReview } => toReview.Commit,
            { Kind: Job.FeatureReview, ToReviewFeature: { } toReviewFeature } => toReviewFeature.Commit,
            _ => null,
        };
        if (job is null || commit is null)
        {
            Log.Write("The job is not a review job this worker can read; it is left.");
            return;
        }

        Log.Write(job.Kind == Job.FeatureReview
            ? $"Reviewing {commit} of feature #{job.Card.Number} on {job.Branch}, job {job.JobId}."
            : $"Reviewing {commit} of card #{job.Card.Number} on {job.Branch}, job {job.JobId}.");
        var started = Time.GetTimestamp();
        AgentFailure? failure;
        AgentResult? result = null;
        if (ProtocolError(job) is { } broken)
        {
            Log.Write($"Refused: {broken}.");
            failure = new AgentFailure(FailureSide.OutsideCard, broken);
        }
        else if (Refusal(job, commit) is { } refused)
        {
            Log.Write($"Refused: {refused}.");
            failure = new AgentFailure(FailureSide.Card, refused);
        }
        else
        {
            var paths = Tools.Folders.For(job.JobId);
            try
            {
                (failure, result) = await Work(job, commit, paths, stopping);
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
        await Send(job, failure, result, null, failure is null ? commit : null, Time.GetElapsedTime(started), stopping);
    }

    /// <summary>Why the job is refused before anything runs, or null: a branch or base git could
    /// read as anything but a branch name, or a commit that is not a full commit id.</summary>
    internal static string? Refusal(Job job, string commit)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!SafeBranch(job.Branch) || !SafeBranch(job.Base) || job.Branch == job.Base)
        {
            return "The job's branch or base is not a branch name the worker takes";
        }

        return CommitId().IsMatch(commit) ? null : "The commit to review is not a full commit id";
    }

    /// <summary>The job's work, step by step; a step that cannot go on ends it with its failure.</summary>
    private async Task<(AgentFailure? Failure, AgentResult? Result)> Work(Job job, string commit, JobPaths paths, CancellationToken stopping)
    {
        try
        {
            await Fetch(job, commit, paths, stopping);
            var files = job.ToReviewFeature is { } toReviewFeature
                ? await FeatureEvidence(job, toReviewFeature, paths, stopping)
                : await CardEvidence(job, job.ToReview!, paths, stopping);
            await WriteEvidence(paths, files, stopping);
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
    /// branch, or the feature's, at the commit in its own repository, which is never pushed. The
    /// agent clones it, through a bundle in the handover folder, into its working copy, which must
    /// then be at the commit.
    /// </summary>
    private async Task Fetch(Job job, string commit, JobPaths paths, CancellationToken stopping)
    {
        Directory.CreateDirectory(Tools.Folders.Secrets);
        Directory.CreateDirectory(Tools.Folders.Handover);
        RootsOwn(paths.Secret, RootOnly);
        RootsOwn(paths.Handover, RootWritesAgentReads);
        var credential = new PushCredential(job.PushKey, job.Repository.CloneAddress);
        await Root(paths, ["clone", "--bare", "--quiet", "--", job.Repository.CloneAddress.OriginalString, paths.Repository], FailureSide.OutsideCard, "The repository could not be fetched", stopping, credential, inSecret: true);

        var found = await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", $"{commit}^{{commit}}"], stopping);
        if (found.ExitCode != 0 || found.Stdout.Trim() != commit)
        {
            // Gone from the repository, by a branch forced over it: nothing to review, and another
            // try would find it gone too.
            throw new StepFailed(FailureSide.Card, $"The commit to review, {commit}, is no longer in the repository");
        }

        if (await Commit(paths, job.Base, stopping) is null)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The base branch {job.Base} has no commit");
        }

        await Root(paths, ["update-ref", $"refs/heads/{job.Branch}", commit], FailureSide.OutsideCard, "The branch could not be pointed at the commit", stopping);
        await Root(paths, ["symbolic-ref", "HEAD", $"refs/heads/{job.Branch}"], FailureSide.OutsideCard, "The branch could not be checked out", stopping);
        await Agent("mkdir", [paths.Work], Tools.Folders.Work, FailureSide.OutsideCard, "The job's folder could not be made", stopping);
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Copy, [], "The working copy could not be made", stopping);

        var head = await Git.AsAgent(paths.Copy, ["rev-parse", "HEAD"], stopping);
        if (head.ExitCode != 0 || head.Stdout.Trim() != commit)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"The working copy is not at {commit}");
        }

        Log.Write($"The copy is at {commit}.");
    }

    /// <summary>
    /// What a card's reviewer reads besides the code, as file names and texts: the diff from where
    /// the branch left the base to the commit, the card as it was picked, the build's report as it
    /// came, the build's check with its command, exit code and output, the card's earlier rounds and
    /// which pictures there are, which root archives for the agent to unpack.
    /// </summary>
    private async Task<Evidence> CardEvidence(Job job, JobReview toReview, JobPaths paths, CancellationToken stopping)
    {
        var diff = await Diff(job, toReview.Commit, paths, stopping);
        var (pictures, unread) = await ArchivePictures(job, toReview, paths, stopping);
        var check = toReview.Check;
        return new Evidence(
            [
                ("card.md", Line(string.Create(CultureInfo.InvariantCulture, $"#{job.Card.Number} {job.Card.Title}\n\n{job.Card.Body}"))),
                ("diff.patch", diff),
                ("report.json", Line(toReview.Report.GetRawText())),
                ("check.txt", Line(string.Create(CultureInfo.InvariantCulture, $"$ {job.Check}\nexit {check.ExitCode}\n\n{check.Output}"))),
                // The card's earlier rounds, as Dev flow sent them; an empty list on a first review.
                ("earlier.json", Line(JsonSerializer.Serialize(toReview.Earlier ?? [], WorkerApi.Json))),
                ("screenshots.txt", PicturesText(toReview, pictures, unread)),
            ],
            pictures);
    }

    /// <summary>
    /// What a whole feature's reviewer reads besides the code, as file names and texts: the
    /// feature's own changes, the diff from where its branch and the default branch last met to the
    /// commit, so what was brought in from the default branch is not read as the feature's; the
    /// parent as it was picked; each card with its latest verdict and its handover note; the newest
    /// check Dev flow holds on the feature's branch, with the commit it ran on; its earlier fix
    /// rounds; and, when there is
    /// one, the remerge diff of each conflict resolved since the feature's last review, apart. See
    /// "Reviewing a feature" in devflow/README.md.
    /// </summary>
    private async Task<Evidence> FeatureEvidence(Job job, JobFeatureReview toReview, JobPaths paths, CancellationToken stopping)
    {
        List<(string Name, string Text)> files =
        [
            ("feature.md", Line(string.Create(CultureInfo.InvariantCulture, $"#{job.Card.Number} {job.Card.Title}\n\n{job.Card.Body}"))),
            ("cards.md", CardsText(toReview)),
            ("diff.patch", await Diff(job, toReview.Commit, paths, stopping)),
        ];
        if (toReview.RemergeDiff is { } remerge)
        {
            files.Add(("remerge.patch", Line(remerge)));
        }

        // The feature's earlier fix rounds, as Dev flow sent them; an empty list on a first review.
        files.Add(("earlier.json", Line(JsonSerializer.Serialize(toReview.Earlier ?? [], WorkerApi.Json))));
        files.Add(("check.txt", FeatureCheckText(job, toReview)));
        return new Evidence(files, []);
    }

    /// <summary>The diff from where the job's branch left its base to the commit, three dots, as a
    /// pull request shows it, so what landed on the base since is not read as removed; with no
    /// external diff or text conversion, so nothing the repository's attributes name is run.</summary>
    private async Task<string> Diff(Job job, string commit, JobPaths paths, CancellationToken stopping)
    {
        var diff = await Git.AsRoot(paths, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", $"refs/heads/{job.Base}...{commit}", "--"], stopping);
        return diff.ExitCode == 0 ? diff.Stdout : throw new StepFailed(FailureSide.OutsideCard, $"The diff could not be made: {Said(diff.Stderr)}");
    }

    /// <summary>
    /// Writes the evidence in the job's handover folder, as root, which the agent can read and not
    /// write, and has the agent copy it into the review folder beside its copy, and unpack the
    /// pictures there, so root never writes where the agent can.
    /// </summary>
    private async Task WriteEvidence(JobPaths paths, Evidence evidence, CancellationToken stopping)
    {
        foreach (var (name, text) in evidence.Files)
        {
            var file = Path.Combine(paths.Handover, name);
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), stopping);
            if (!OperatingSystem.IsWindows())
            {
                // Readable by the agent whatever root's umask is.
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        await Agent("mkdir", [paths.Review], paths.Work, FailureSide.OutsideCard, "The review folder could not be made", stopping);
        await Agent("cp", ["--", .. evidence.Files.Select(file => Path.Combine(paths.Handover, file.Name)), paths.Review], paths.Work, FailureSide.OutsideCard, "The review's files could not be copied", stopping);
        if (evidence.Pictures.Count > 0)
        {
            var folder = Path.Combine(paths.Review, PicturesFolder);
            await Agent("mkdir", [folder], paths.Work, FailureSide.OutsideCard, "The pictures' folder could not be made", stopping);
            await Agent("tar", ["-x", "-f", Path.Combine(paths.Handover, "pictures.tar"), "-C", folder], paths.Work, FailureSide.OutsideCard, "The pictures could not be unpacked", stopping);
        }

        Log.Write($"Wrote the review's files, {string.Join(", ", evidence.Files.Select(file => file.Name))}, and {evidence.Pictures.Count} pictures.");
    }

    /// <summary>What <c>cards.md</c> says: each card of the feature in its order, its number and
    /// title, its latest review's verdict and summary or that it has none, and its handover note or
    /// that it left none. Every text is an agent's or the owner's, and data.</summary>
    internal static string CardsText(JobFeatureReview toReview)
    {
        ArgumentNullException.ThrowIfNull(toReview);
        var text = new StringBuilder("# The feature's cards, in its order\n");
        foreach (var card in toReview.Cards)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## #{card.Number} {card.Title}\n\n");
            text.Append(card.Verdict is { } verdict
                ? string.Create(CultureInfo.InvariantCulture, $"Its latest review: {verdict}. {card.Summary}").TrimEnd() + "\n\n"
                : "It has no review.\n\n");
            text.Append("Its handover note:\n\n").Append(card.Handover.Trim().Length == 0 ? "It left no note." : card.Handover.TrimEnd()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>What <c>check.txt</c> says for a whole feature: the newest check a worker ran on the
    /// feature's branch, its command, the commit it ran on and whether that is the one reviewed, its
    /// exit code and output; or that Dev flow holds none.</summary>
    internal static string FeatureCheckText(Job job, JobFeatureReview toReview)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(toReview);
        if (toReview.Check is not { } check)
        {
            return "Dev flow holds no check a worker ran on the feature's branch.\n";
        }

        var command = check.Command.Trim().Length > 0 ? check.Command : job.Check;
        var on = string.Equals(check.Commit, toReview.Commit, StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"on {check.Commit}, the commit you review")
            : string.Create(CultureInfo.InvariantCulture, $"on {check.Commit}, which is not the commit you review: no worker ran the check on {toReview.Commit[..7]}, a merge GitHub made, so this is the newest result there is, and CI on the head is what the feature waits for");
        return Line(string.Create(CultureInfo.InvariantCulture, $"$ {command}\n{on}\nexit {check.ExitCode}\n\n{check.Output}"));
    }

    /// <summary>The files a review is given beside the copy, each with its text, and the pictures
    /// root archived for the agent.</summary>
    private sealed record Evidence(IReadOnlyList<(string Name, string Text)> Files, IReadOnlyList<string> Pictures);

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
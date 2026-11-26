using System.Globalization;
using System.Text;
using System.Text.Json;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Pages;

namespace DevFlow.Worker.Build;

/// <summary>
/// A build's pictures, as Settled in the slice "The worker's screenshots": after the check passed
/// and the branch was pushed, the agent clones the pushed commit afresh into <c>pages</c>; the
/// project's <c>start</c> runs there as <c>agent</c>, in an environment built from nothing and
/// <c>DEVFLOW=1</c>, in a process group of its own; once its <c>url</c> answers below 500, each of
/// the report's pages, at most 10, is taken full page at the desktop's size and a phone's with
/// Playwright, as <c>agent</c>, and the group is ended. Root reads each picture through a process
/// of the agent's, so a link the project left in its place reads nothing the agent could not, and
/// commits them to <c>pr-screenshots</c> in its own repository, pushed without force. Anything that
/// goes wrong is said in the result's note and never fails the job: the pictures are evidence, and
/// never decide anything. See "Screenshots" in devflow/README.md, and "The worker's pictures" in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed partial class BuildJob
{
    /// <summary>How long the project has to answer at its <c>url</c>.</summary>
    public static readonly TimeSpan UpWithin = TimeSpan.FromMinutes(3);

    /// <summary>How often its <c>url</c> is asked while it comes up.</summary>
    public static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(1);

    /// <summary>How long one ask may take.</summary>
    public static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long one picture may take.</summary>
    public static readonly TimeSpan PictureLimit = TimeSpan.FromSeconds(60);

    /// <summary>The most bytes a picture may have, as Dev flow serves one.</summary>
    public const int MostBytes = 5 * 1024 * 1024;

    /// <summary>What every PNG begins with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The pictures of the pushed commit <paramref name="head"/>, or why there are none.</summary>
    private async Task<ResultScreenshots> Screenshots(Job job, JobPaths paths, string head, AgentResult agent, CancellationToken stopping)
    {
        if (job.Start.Trim().Length == 0 || job.Url.Trim().Length == 0)
        {
            Log.Write("The project has no start or url, so no pictures are taken.");
            return new ResultScreenshots([], PageShots.NoPages);
        }

        if (job.Repository.DefaultBranch == PageShots.Branch)
        {
            return NoPictures($"the repository's default branch is {PageShots.Branch}, which a worker never pushes to");
        }

        var pages = Pages(agent.Report);
        if (pages.Count == 0)
        {
            Log.Write("The report names no pages, so no pictures are taken.");
            return new ResultScreenshots([], PageShots.NoPagesNamed);
        }

        try
        {
            var (pictures, failures) = await Take(job, paths, head, pages, stopping);
            if (pictures.Count > 0)
            {
                await PushPictures(job, paths, head, pictures, stopping);
            }

            return new ResultScreenshots(
                [.. pictures.Select(picture => picture.Name)],
                failures.Count == 0 ? null : Note(string.Join("; ", failures)));
        }
        catch (StepFailed failed)
        {
            Log.Write($"No pictures: {failed.Message}.");
            return NoPictures(failed.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever else went wrong is said too: the pictures never fail the job.
            Log.Write($"No pictures: {e.GetType().Name}: {e.Message}.");
            return NoPictures($"the worker failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The report's pages to take, each with its pictures' name: its first
    /// <see cref="PageShots.MostPages"/>, in its order, a page whose name an earlier one has left
    /// out. None when the report does not read, which Dev flow then stops the card for.</summary>
    internal static IReadOnlyList<(string Name, string Path)> Pages(System.Text.Json.Nodes.JsonObject? report)
    {
        if (report is null || !Report.TryRead(JsonSerializer.SerializeToElement(report, WorkerApi.Json), out var read, out _))
        {
            return [];
        }

        var pages = new List<(string Name, string Path)>();
        foreach (var page in read.Pages.Take(PageShots.MostPages))
        {
            var name = PageShots.NameOf(page.Path);
            if (!pages.Exists(taken => taken.Name == name))
            {
                pages.Add((name, page.Path));
            }
        }

        return pages;
    }

    /// <summary>
    /// The page's address: <paramref name="url"/> joined with <paramref name="path"/>, which is the
    /// agent's and so data, and must leave the address on the project's own origin. Null for a
    /// path with a space or a control character in it, and for any join that lands elsewhere.
    /// </summary>
    internal static string? Address(string url, string path)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttp || origin.Host != "localhost"
            || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        var joined = url.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
        return Uri.TryCreate(joined, UriKind.Absolute, out var page)
            && page.Scheme == origin.Scheme && page.Host == origin.Host && page.Port == origin.Port && page.UserInfo.Length == 0
            ? page.AbsoluteUri
            : null;
    }

    /// <summary>
    /// Starts the project from a clean copy of <paramref name="head"/>, waits for it, takes each
    /// page at each size, and ends its process group whatever happened. The pictures taken, each
    /// read and checked, and a line for each that was not.
    /// </summary>
    private async Task<(List<(string Name, byte[] Bytes)> Pictures, List<string> Failures)> Take(
        Job job, JobPaths paths, string head, IReadOnlyList<(string Name, string Path)> pages, CancellationToken stopping)
    {
        Log.Write("Taking pictures");
        await CloneForAgent(job, paths, [$"refs/heads/{job.Branch}"], paths.Pages, [], "the clean copy for the pictures could not be made", stopping);
        var at = await Git.AsAgent(paths.Pages, ["rev-parse", "HEAD"], stopping);
        if (at.ExitCode != 0 || at.Stdout.Trim() != head)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"the clean copy for the pictures is not at {head}");
        }

        await Agent("mkdir", [paths.Pictures], paths.Work, FailureSide.OutsideCard, "the folder for the pictures could not be made", stopping);
        Log.Write($"Starting the project at {head} for its pictures.");
        // What the start prints goes to the job's log as it comes, as the check's does.
        var project = await Tools.Pages.Starter.Start(new ProcessSpec("sh", ["-c", job.Start], paths.Pages, Tools.User.WithValues(job.Values, ("DEVFLOW", "1")), Lines: CheckLines()), stopping);
        try
        {
            await WaitUp(job.Url, project, stopping);
            var pictures = new List<(string Name, byte[] Bytes)>();
            var failures = new List<string>();
            foreach (var (name, path) in pages)
            {
                if (Address(job.Url, path) is not { } address)
                {
                    failures.Add($"{OneLine(path)}: not a path on the project's address");
                    continue;
                }

                foreach (var viewport in PageShots.Viewports)
                {
                    var picture = string.Create(CultureInfo.InvariantCulture, $"{name}-{viewport.Name}");
                    if (await Picture(paths, address, picture, viewport, stopping) is { } why)
                    {
                        failures.Add($"{OneLine(path)} at {viewport.Name} size: {why}");
                        continue;
                    }

                    if (await ReadPicture(paths, picture, stopping) is { } bytes)
                    {
                        pictures.Add((picture, bytes));
                    }
                    else
                    {
                        failures.Add($"{OneLine(path)} at {viewport.Name} size: the picture is not a PNG of at most 5 MB");
                    }
                }
            }

            Log.Write($"Took {pictures.Count} pictures; {failures.Count} could not be taken.");
            return (pictures, failures);
        }
        finally
        {
            await project.End();
            Log.Write("Ended the project's processes.");
        }
    }

    /// <summary>Asks <paramref name="url"/> every <see cref="AskEvery"/> until it answers with a
    /// status below 500, for at most <see cref="UpWithin"/>; the project's start exiting with a
    /// failure ends the wait at once.</summary>
    private async Task WaitUp(string url, IBackgroundProcess project, CancellationToken stopping)
    {
        using var http = new HttpClient(Tools.Pages.Probe, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var until = Time.GetUtcNow() + UpWithin;
        while (true)
        {
            if (project.ExitCode is { } code and not 0)
            {
                throw new StepFailed(FailureSide.Card, $"the project's start exited with {code}: {Said(project.Output)}");
            }

            using (var ask = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                ask.CancelAfter(AskLimit);
                try
                {
                    using var answer = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ask.Token);
                    if ((int)answer.StatusCode < 500)
                    {
                        Log.Write($"The project answers at {url}.");
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                }
            }

            if (Time.GetUtcNow() >= until)
            {
                var output = project.Output.Trim();
                throw new StepFailed(
                    FailureSide.Card,
                    $"the project did not answer at {url} within {UpWithin.TotalMinutes:0} minutes{(output.Length == 0 ? "" : $"; its start said: {Said(output)}")}");
            }

            await Task.Delay(AskEvery, Time, stopping);
        }
    }

    /// <summary>One picture, full page, as <c>agent</c>, with the pinned Playwright on its
    /// <c>PATH</c>; null when it was taken, or why not.</summary>
    private async Task<string?> Picture(JobPaths paths, string address, string name, Viewport viewport, CancellationToken stopping)
    {
        var file = Path.Combine(paths.Pictures, $"{name}.png");
        var shot = await Tools.AsAgent.Run(
            new ProcessSpec(
                Tools.Pages.Playwright,
                ["screenshot", "--full-page", "--viewport-size", string.Create(CultureInfo.InvariantCulture, $"{viewport.Width},{viewport.Height}"), address, file],
                paths.Pictures,
                Tools.User.Environment(("PLAYWRIGHT_BROWSERS_PATH", Tools.Pages.BrowsersPath))),
            PictureLimit,
            stopping);
        return shot.TimedOut ? $"it took longer than {PictureLimit.TotalSeconds:0} seconds"
            : shot.ExitCode != 0 ? Said($"{shot.Stdout}\n{shot.Stderr}")
            : null;
    }

    /// <summary>
    /// A picture's bytes, read by a process of the agent's, <c>head</c>, at most one byte past
    /// <see cref="MostBytes"/>, so whatever the project put at its name, a link or a file that never
    /// ends, root reads only what the agent could and never more than that; null unless they are a
    /// PNG of at most 5 MB.
    /// </summary>
    private async Task<byte[]?> ReadPicture(JobPaths paths, string name, CancellationToken stopping)
    {
        var read = await Tools.AsAgent.Run(
            new ProcessSpec("head", ["-c", (MostBytes + 1).ToString(CultureInfo.InvariantCulture), "--", Path.Combine(paths.Pictures, $"{name}.png")], paths.Pictures, Tools.User.Environment(), Bytes: true),
            WorkerGit.Limit,
            stopping);
        if (read.ExitCode != 0)
        {
            return null;
        }

        var bytes = Encoding.Latin1.GetBytes(read.Stdout);
        return bytes.Length <= MostBytes && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
    }

    /// <summary>
    /// Root commits the pictures to <see cref="PageShots.Branch"/> in its own repository, under
    /// <see cref="PageShots.Folder"/>, on top of the branch as the clone found it, or as a first
    /// commit when it has none; and pushes that commit with a fresh push key, never with force.
    /// When the push is refused, the branch is fetched again and the commit made once more on top.
    /// </summary>
    private async Task PushPictures(Job job, JobPaths paths, string head, List<(string Name, byte[] Bytes)> pictures, CancellationToken stopping)
    {
        var folder = PageShots.Folder(job.Card!.Number, head);
        var blobs = new List<(string Path, string Blob)>();
        var file = Path.Combine(paths.Secret, "picture.png");
        foreach (var (name, bytes) in pictures)
        {
            await File.WriteAllBytesAsync(file, bytes, stopping);
            var written = await Git.AsRoot(paths, ["hash-object", "-w", "--", file], stopping);
            if (written.ExitCode != 0)
            {
                throw new StepFailed(FailureSide.OutsideCard, $"a picture could not be stored: {Said(written.Stderr)}");
            }

            blobs.Add(($"{folder}/{name}.png", written.Stdout.Trim()));
        }

        var (credential, why) = await FreshKey(job, stopping);
        if (credential is null)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"Dev flow gave no fresh push key ({why}), so they were not pushed");
        }

        var parent = await Commit(paths, PageShots.Branch, stopping);
        for (var attempt = 1; ; attempt++)
        {
            var commit = await PicturesCommit(job, paths, head, parent, blobs, stopping);
            var push = await Git.AsRoot(paths, ["push", "--quiet", "--porcelain", "origin", $"{commit}:refs/heads/{PageShots.Branch}"], stopping, credential);
            if (push.ExitCode == 0)
            {
                Log.Write($"Pushed {pictures.Count} pictures to {PageShots.Branch} at {folder}.");
                return;
            }

            var refused = credential.Scrub(Said($"{push.Stdout}\n{push.Stderr}"));
            if (attempt == 2)
            {
                throw new StepFailed(FailureSide.OutsideCard, $"the push to {PageShots.Branch} was refused: {refused}");
            }

            // The branch moved since the clone, most likely another job's pictures: fetched again,
            // and the commit made once more on top of where it is now.
            Log.Write($"The push to {PageShots.Branch} was refused, fetching it again: {refused}");
            parent = await RemotePictures(paths, credential, stopping);
        }
    }

    /// <summary>A commit of the pictures on top of <paramref name="parent"/>, or with no parent,
    /// made with an index file of root's own in the job's folder of secrets, so nothing is checked
    /// out; the app's bot is its author and committer.</summary>
    private async Task<string> PicturesCommit(Job job, JobPaths paths, string head, string? parent, List<(string Path, string Blob)> blobs, CancellationToken stopping)
    {
        var index = Path.Combine(paths.Secret, "pictures.index");
        File.Delete(index);
        await Root(paths, ["read-tree", parent ?? "--empty"], FailureSide.OutsideCard, "the pictures' branch could not be read", stopping, index: index);
        foreach (var (path, blob) in blobs)
        {
            await Root(paths, ["update-index", "--add", "--cacheinfo", $"100644,{blob},{path}"], FailureSide.OutsideCard, "a picture could not be added", stopping, index: index);
        }

        var tree = await Git.AsRoot(paths, ["write-tree"], stopping, index: index);
        if (tree.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"the pictures could not be written: {Said(tree.Stderr)}");
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"Screenshots of #{job.Card!.Number} at {head[..7]}");
        string[] who = ["-c", $"user.name={job.Committer.Name}", "-c", $"user.email={job.Committer.Email}"];
        var made = await Git.AsRoot(paths, [.. who, "commit-tree", tree.Stdout.Trim(), .. parent is null ? Array.Empty<string>() : ["-p", parent], "-m", message], stopping);
        if (made.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"the pictures could not be committed: {Said(made.Stderr)}");
        }

        return made.Stdout.Trim();
    }

    /// <summary>Where <see cref="PageShots.Branch"/> is on the code host now, fetched into root's
    /// repository; null when the code host has no such branch.</summary>
    private async Task<string?> RemotePictures(JobPaths paths, PushCredential credential, CancellationToken stopping)
    {
        var listed = await Git.AsRoot(paths, ["ls-remote", "--heads", "origin", $"refs/heads/{PageShots.Branch}"], stopping, credential);
        if (listed.ExitCode != 0)
        {
            throw new StepFailed(FailureSide.OutsideCard, $"{PageShots.Branch} could not be read again: {credential.Scrub(Said(listed.Stderr))}");
        }

        if (listed.Stdout.Trim().Length == 0)
        {
            return null;
        }

        await Root(paths, ["fetch", "--quiet", "--no-tags", "origin", $"refs/heads/{PageShots.Branch}"], FailureSide.OutsideCard, $"{PageShots.Branch} could not be fetched again", stopping, credential);
        return await Git.AsRoot(paths, ["rev-parse", "--verify", "--quiet", "FETCH_HEAD^{commit}"], stopping) is { ExitCode: 0 } fetched
            ? fetched.Stdout.Trim()
            : throw new StepFailed(FailureSide.OutsideCard, $"{PageShots.Branch} has no commit after the fetch");
    }

    /// <summary>The note for pictures that could not be taken, cut to what a result keeps.</summary>
    private static string Note(string why)
    {
        var note = PageShots.CouldNot + why;
        return note.Length <= PageShots.MostNote ? note : note[..(PageShots.MostNote - 1)] + "…";
    }

    /// <summary>No pictures, and why.</summary>
    private static ResultScreenshots NoPictures(string why) => new([], Note(why));

    /// <summary>Text from the report made one line, for a note.</summary>
    private static string OneLine(string text) =>
        new([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
}
using System.Text;
using System.Text.Json;

using DevFlow.Briefs;
using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Recipes;
using DevFlow.Stops;
using DevFlow.Watching;
using DevFlow.Workers;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>What asking for work comes to.</summary>
public abstract record NextJob
{
    private NextJob()
    {
    }

    /// <summary>The worker has not said hello in its key's project.</summary>
    public sealed record Unknown : NextJob;

    /// <summary>No work for it now, whatever the reason.</summary>
    public sealed record None : NextJob;

    /// <summary>A job, handed to it.</summary>
    public sealed record Handed(Job Job) : NextJob;
}

/// <summary>What asking for a fresh push key comes to.</summary>
public abstract record FreshKey
{
    private FreshKey()
    {
    }

    /// <summary>The worker holds no such job under way.</summary>
    public sealed record NotHeld : FreshKey;

    /// <summary>The code host did not make one now.</summary>
    public sealed record Unavailable : FreshKey;

    /// <summary>A new key.</summary>
    public sealed record Made(PushKeyAnswer Key) : FreshKey;
}

/// <summary>
/// Hands out work. A worker asking for it is answered nothing when it needs a rebuild, when its
/// project is stopped or its default branch not watched lately, when its project's user has no
/// Claude login or one past its renewal, or when its project has no card to take; otherwise the
/// kind of job is chosen, a card's review before a whole feature's before a card's fix before a
/// whole feature's before a conflict before a build, everything that job needs from the code host is
/// read first, as the app, with a key for that kind, a feature's branch made for its first card,
/// and only then is a card, or for a conflict a feature, of that kind taken, in
/// one short transaction as the project's user, so no lock is held while GitHub is asked.
/// That transaction reads the watch's hold again under the project's lock, so a stop recorded
/// while GitHub was asked still holds the card back. Anything the code host does not answer is no
/// work now, and the worker asks again. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class Dispatcher(
    UserDb database,
    WorkerStore workers,
    WatchStore watch,
    ClaudeLogins logins,
    ICodeHost host,
    WorkerStart start,
    TimeProvider time,
    ILogger<Dispatcher> log)
{
    /// <summary>Where a project keeps its facts for Dev flow, on its default branch.</summary>
    public const string ProjectFilePath = ".devflow/project.json";

    /// <summary>The report's schema as every job carries it, written once from its type.</summary>
    private static readonly JsonElement ReportSchema = JsonSerializer.SerializeToElement(Report.Schema());

    /// <summary>The verdict's schema as every review job carries it, written once from its type.</summary>
    private static readonly JsonElement VerdictSchema = JsonSerializer.SerializeToElement(ReviewVerdict.Schema());

    /// <summary>
    /// Why a user's project hands out no work for their Claude login, as the project's page says
    /// it: <c>no_claude_login</c> without one, <c>claude_login_expired</c> past its renewal, or
    /// null. A login due soon still works. The one rule both handing out and the page read.
    /// </summary>
    public static string? HeldFor(ClaudeLoginStatus login)
    {
        ArgumentNullException.ThrowIfNull(login);
        return login.State switch
        {
            ClaudeLoginState.None => "no_claude_login",
            ClaudeLoginState.Expired => "claude_login_expired",
            _ => null,
        };
    }

    /// <summary>The worker's next job, or why there is none; marks the worker seen.</summary>
    public async Task<NextJob> Next(WorkerCaller caller, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        switch (await workers.Seen(caller, workerId, start.Image, cancellationToken))
        {
            case WorkerState.Unknown:
                return new NextJob.Unknown();
            case WorkerState.NeedsRebuild:
                // Built from an older recipe or on another base image: no work, until the owner
                // runs the start command again, as the project's page says.
                return new NextJob.None();
        }

        var (userId, projectId) = (caller.UserId, caller.ProjectId);
        // Stopped, or its default branch not watched for five minutes: no new work, whatever waits,
        // until its user resumes it or the branch is read again. See "Watching branches" in
        // devflow/README.md.
        // Waiting after failures outside its cards, until its wait is over. See "Stops" in
        // devflow/README.md.
        if (await watch.Hold(userId, projectId, cancellationToken) != ProjectHold.None
            || await ProjectWaits.Of(database, userId, projectId, cancellationToken) is { } wait && wait.WaitUntil > time.GetUtcNow()
            || HeldFor(await logins.Status(userId, cancellationToken)) is not null
            || await JobStore.NextKind(database, userId, projectId, time.GetUtcNow(), cancellationToken) is not { } kind
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new NextJob.None();
        }

        // The key is made for the kind found, before anything is taken: a review's, of a card or of a
        // whole feature, only fetches, and a build's, a fix's, a conflict's and a feature's fix's
        // fetch and push.
        var purpose = kind is Job.Review or Job.FeatureReview ? WritePurpose.Fetch : WritePurpose.Push;
        if (await Facts(projectId, userId, repository, cancellationToken) is not { } facts
            || await logins.ForRun(userId, cancellationToken) is not { } token
            || await Key(projectId, userId, repository, purpose, cancellationToken) is not { } key)
        {
            return new NextJob.None();
        }

        // A feature's first card is built on the feature's branch, so the app makes it first, with
        // the job's push key, outside the lock as every read for a job is.
        if (kind == Job.Build && !await FeatureBranch(projectId, userId, repository, facts.DefaultBranch, key, cancellationToken))
        {
            return new NextJob.None();
        }

        // A conflict's brief lists the files both sides changed, read as the app outside the lock,
        // for the feature it would take and the two commits it would merge.
        DueConflict? due = null;
        IReadOnlyList<string> files = [];
        if (kind == Job.Conflict)
        {
            due = await JobStore.ConflictDue(database, userId, projectId, time.GetUtcNow(), cancellationToken);
            if (due is null || await InConflict(projectId, userId, repository, due, cancellationToken) is not { } changed)
            {
                return new NextJob.None();
            }

            files = changed;
        }

        var now = Now();
        var job = await database.InUserTransaction<Job?>(userId, async transaction =>
        {
            if (await watch.HoldUnderLock(transaction, projectId, cancellationToken) != ProjectHold.None)
            {
                return null;
            }

            return kind switch
            {
                Job.Review => ReviewJob(await JobStore.ClaimReview(transaction, projectId, userId, workerId, now, cancellationToken), repository, facts, token, key),
                Job.FeatureReview => FeatureReviewJob(await JobStore.ClaimFeatureReview(transaction, projectId, userId, workerId, facts.DefaultBranch, facts.Check, now, cancellationToken), repository, facts, token, key),
                Job.Fix => FixJob(await JobStore.ClaimFix(transaction, projectId, userId, workerId, facts.Check, now, cancellationToken), repository, facts, token, key),
                Job.FeatureFix => FeatureFixJob(await JobStore.ClaimFeatureFix(transaction, projectId, userId, workerId, facts.DefaultBranch, facts.Check, now, cancellationToken), repository, facts, token, key),
                Job.Conflict => ConflictJob(await JobStore.ClaimConflict(transaction, projectId, userId, workerId, due!, facts.DefaultBranch, facts.Check, now, cancellationToken), repository, facts, token, key, files),
                _ => BuildJob(await JobStore.Claim(transaction, projectId, userId, workerId, facts.DefaultBranch, facts.Check, now, cancellationToken), repository, facts, token, key),
            };
        }, cancellationToken);
        if (job is null)
        {
            // Another worker took the last card of the kind since it was looked for, or the project
            // was stopped or went unwatched while GitHub was asked; the key made for it is dropped
            // unused, and runs out within the hour. The worker asks again.
            return new NextJob.None();
        }

        LogHandedOut(log, job.Kind, projectId, job.Card.Number, job.JobId);
        return new NextJob.Handed(job);
    }

    /// <summary>The build job for a card taken to build, or null when none was.</summary>
    private static Job? BuildJob(ClaimedCard? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Developer(
            new BriefCard(claimed.Number, claimed.Title, claimed.Body),
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            claimed.Comments,
            claimed.Feature);
        return new Job(
            claimed.RunId,
            Job.Build,
            new JobCard(claimed.Number, claimed.Title, claimed.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            ReportSchema,
            facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The conflict job for a feature taken to resolve, or null when none was: on the
    /// feature's branch, with the default branch as its base, the feature's parent as its card, the
    /// conflict brief, the report's form, the check, and the two commits it merges.</summary>
    private static Job? ConflictJob(ClaimedConflict? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key, IReadOnlyList<string> files)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Conflict(claimed.Feature, new ConflictFacts(facts.Check, claimed.Branch, claimed.Base, files));
        return new Job(
            claimed.RunId,
            Job.Conflict,
            new JobCard(claimed.Feature.Number, claimed.Feature.Title, claimed.Feature.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            ReportSchema,
            facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            ToResolve: new JobConflict(claimed.FeatureCommit, claimed.DefaultCommit));
    }

    /// <summary>The review job for a feature taken to review as a whole, or null when none was: on
    /// the feature's branch, with the default branch as its base, the feature's parent as its card,
    /// the feature reviewer's brief, the verdict's form, the project's check, and what the review
    /// reads besides the code.</summary>
    private static Job? FeatureReviewJob(ClaimedFeatureReview? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.FeatureReviewer(claimed.Feature, new FeatureReviewFacts(claimed.Commit, claimed.Branch, claimed.Base, claimed.RemergeDiff is not null, claimed.Earlier.Count));
        return new Job(
            claimed.RunId,
            Job.FeatureReview,
            new JobCard(claimed.Feature.Number, claimed.Feature.Title, claimed.Feature.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            VerdictSchema,
            facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            ToReviewFeature: new JobFeatureReview(claimed.Commit, claimed.Cards, claimed.Check, claimed.RemergeDiff, [.. claimed.Earlier.Select(Round)]));
    }

    /// <summary>The fix job for a feature taken to fix as a whole, or null when none was: a fix's job
    /// on the feature's branch, with the default branch as its base, the feature's parent as its
    /// card, the feature's fix brief, the report's form, and where the fix starts.</summary>
    private static Job? FeatureFixJob(ClaimedFeatureFix? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.FeatureFixer(
            claimed.Feature,
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            new FixInput(claimed.Round, claimed.Report, claimed.Review?.Verdict, claimed.Comments));
        return new Job(
            claimed.RunId,
            Job.FeatureFix,
            new JobCard(claimed.Feature.Number, claimed.Feature.Title, claimed.Feature.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            ReportSchema,
            facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            ToFix: new JobFix(claimed.FromCommit, claimed.Round),
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The fix job for a card taken to fix, or null when none was: a build's job on the
    /// card's branch and base, with the fix brief, the developer's with what to fix, the report's
    /// form, and where the fix starts.</summary>
    private static Job? FixJob(ClaimedFix? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Fixer(
            new BriefCard(claimed.Number, claimed.Title, claimed.Body),
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            new FixInput(claimed.Round, claimed.Report, claimed.Review?.Verdict, claimed.Comments),
            claimed.Feature);
        return new Job(
            claimed.RunId,
            Job.Fix,
            new JobCard(claimed.Number, claimed.Title, claimed.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            ReportSchema,
            facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            ToFix: new JobFix(claimed.FromCommit, claimed.Round),
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The review job for a card taken to review, or null when none was: the build's
    /// branch, base and check, the commit it left, its report, the check's result and its pictures,
    /// the reviewer's brief and the verdict's form.</summary>
    private static Job? ReviewJob(ClaimedReview? claimed, Repository repository, RepositoryFacts facts, ClaudeToken token, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var card = new BriefCard(claimed.Number, claimed.Title, claimed.Body);
        var brief = Brief.Reviewer(card, new ReviewFacts(claimed.Commit, claimed.Branch, claimed.Base, claimed.Earlier.Count), claimed.Feature);
        using var report = JsonDocument.Parse(claimed.Report);
        return new Job(
            claimed.RunId,
            Job.Review,
            new JobCard(claimed.Number, claimed.Title, claimed.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            VerdictSchema,
            claimed.Check ?? facts.Check,
            token.Value,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            JobLimits.Default,
            new JobReview(claimed.Commit, report.RootElement.Clone(), new CheckResult(claimed.CheckExitCode, claimed.CheckOutput), [.. claimed.Earlier.Select(Round)], claimed.Screenshots));
    }

    /// <summary>An earlier round as a review job carries it, its stored JSON read back as it was
    /// stored.</summary>
    private static JobRound Round(EarlierRound round)
    {
        using var findings = JsonDocument.Parse(round.Findings ?? "[]");
        using var comments = JsonDocument.Parse(round.Comments);
        using var replies = JsonDocument.Parse(round.Replies);
        return new JobRound(
            round.Round,
            round.ReviewedCommit,
            round.Verdict,
            round.Summary,
            findings.RootElement.Clone(),
            [.. comments.RootElement.EnumerateArray().Select(comment => comment.GetString() ?? "")],
            replies.RootElement.Clone(),
            round.FixedCommit);
    }

    /// <summary>A fresh push key for the job, only for the worker holding it while it runs, and
    /// only for a build, a fix, a conflict or a feature's fix: a review is given no key that pushes. Marks the worker
    /// seen.</summary>
    public async Task<FreshKey> PushKey(WorkerCaller caller, Guid runId, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await workers.Seen(caller, workerId, start.Image, cancellationToken) == WorkerState.Unknown
            || !await JobStore.HoldsBuild(database, caller.UserId, caller.ProjectId, runId, workerId, cancellationToken)
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new FreshKey.NotHeld();
        }

        return await Key(caller.ProjectId, caller.UserId, repository, WritePurpose.Push, cancellationToken) is { } key
            ? new FreshKey.Made(new PushKeyAnswer(key.Value, key.ExpiresAt))
            : new FreshKey.Unavailable();
    }

    /// <summary>What a job needs from the code host about the repository, read as the app, or null
    /// when any of it is not there now.</summary>
    private async Task<RepositoryFacts?> Facts(Guid projectId, Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        if (await host.DefaultBranch(userId, repository, cancellationToken) is not CodeHostResult<string>.Found { Value: var branch })
        {
            LogNotRead(log, projectId, "its default branch");
            return null;
        }

        if (await host.DefaultBranchFile(userId, repository, ProjectFilePath, cancellationToken) is not CodeHostResult<FileAtCommit>.Found { Value: var file })
        {
            LogNotRead(log, projectId, ProjectFilePath);
            return null;
        }

        if (!ProjectFile.TryRead(Encoding.UTF8.GetString(file.Content), out var project, out var error))
        {
            LogBadProjectFile(log, projectId, error);
            return null;
        }

        if (await host.CloneAddress(userId, repository, cancellationToken) is not CodeHostResult<Uri>.Found { Value: var clone })
        {
            LogNotRead(log, projectId, "its clone address");
            return null;
        }

        if (await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var committer })
        {
            LogNotRead(log, projectId, "the app's committer");
            return null;
        }

        return new RepositoryFacts(branch, project.Check, project.Start, project.Url, clone, committer);
    }

    /// <summary>
    /// The files in conflict between the feature's branch and the default branch, as their brief
    /// lists them: those each side changed since they parted, by the code host's comparison of each
    /// commit against the other, sorted. GitHub cannot say which conflict without merging, which is
    /// the worker's, so these are where the conflicts are, and the agent's <c>git merge</c> names
    /// them exactly. Null when the code host did not answer, which is no work now.
    /// </summary>
    private async Task<IReadOnlyList<string>?> InConflict(Guid projectId, Guid userId, Repository repository, DueConflict due, CancellationToken cancellationToken)
    {
        if (await host.ChangedFiles(userId, repository, due.DefaultCommit, due.FeatureCommit, cancellationToken) is not CodeHostResult<IReadOnlyList<string>>.Found { Value: var feature }
            || await host.ChangedFiles(userId, repository, due.FeatureCommit, due.DefaultCommit, cancellationToken) is not CodeHostResult<IReadOnlyList<string>>.Found { Value: var landed })
        {
            LogNotRead(log, projectId, "the files in conflict");
            return null;
        }

        return [.. feature.Intersect(landed, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>A key for the repository, narrowed to <paramref name="purpose"/>,
    /// <see cref="WritePurpose.Push"/> for a build or <see cref="WritePurpose.Fetch"/> for a
    /// review, or null when the code host made none.</summary>
    private async Task<AppWriteToken?> Key(Guid projectId, Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken)
    {
        if (await host.ForWrite(userId, repository, purpose, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var key })
        {
            return key;
        }

        LogNotRead(log, projectId, purpose == WritePurpose.Push ? "a push key" : "a fetch key");
        return null;
    }

    /// <summary>
    /// Makes the branch of the feature whose card is next, when it has none, at the head of
    /// <paramref name="defaultBranch"/>, as the app with <paramref name="key"/>, and records it on
    /// the feature with the commit. True when there is nothing to make, another worker is making
    /// it, it is made, or a branch of its name is there already, when the feature waits for the
    /// owner and the next card is another's; false when the code host did not make it now, and the
    /// worker asks again. See "A feature's branch" in devflow/docs/devflow-architecture.md.
    /// </summary>
    private async Task<bool> FeatureBranch(Guid projectId, Guid userId, Repository repository, string defaultBranch, AppWriteToken key, CancellationToken cancellationToken)
    {
        if (await FeatureBranchStore.Due(database, userId, projectId, time.GetUtcNow(), cancellationToken) is not { } feature)
        {
            return true;
        }

        var branch = FeatureBranchStore.Name(feature.Number, feature.Title);
        if (!await FeatureBranchStore.Claim(database, userId, feature.Id, branch, Now(), cancellationToken))
        {
            return true;
        }

        switch (await host.MakeBranch(userId, key, repository, branch, defaultBranch, cancellationToken))
        {
            case BranchResult.Made { Commit: var commit }:
                await FeatureBranchStore.Made(database, userId, feature.Id, branch, commit, Now(), cancellationToken);
                LogBranchMade(log, projectId, feature.Number, branch);
                return true;
            case BranchResult.Exists:
                // Not Dev flow's, since its making was claimed before GitHub was asked: never taken.
                await FeatureBranchStore.Taken(database, userId, feature.Id, branch, Now(), cancellationToken);
                LogBranchExists(log, projectId, feature.Number, branch);
                return true;
            case BranchResult.Refused { Status: var status }:
                await FeatureBranchStore.Released(database, userId, feature.Id, branch, Now(), cancellationToken);
                LogBranchRefused(log, projectId, feature.Number, status);
                return false;
            case BranchResult.Unavailable:
                await FeatureBranchStore.Released(database, userId, feature.Id, branch, Now(), cancellationToken);
                LogNotRead(log, projectId, "the feature's branch");
                return false;
            default:
                throw new InvalidOperationException("a branch result that is not one of the four");
        }
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed record RepositoryFacts(string DefaultBranch, string Check, string Start, string Url, Uri CloneAddress, Committer Committer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handed out the {Kind} of #{Card} of project {ProjectId} as run {RunId}")]
    private static partial void LogHandedOut(ILogger logger, string kind, Guid projectId, int card, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host did not answer {What}")]
    private static partial void LogNotRead(ILogger logger, Guid projectId, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made the branch {Branch} of feature {Feature} of project {ProjectId}")]
    private static partial void LogBranchMade(ILogger logger, Guid projectId, int feature, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feature {Feature} of project {ProjectId} waits for its owner: a branch named {Branch} already exists")]
    private static partial void LogBranchExists(ILogger logger, Guid projectId, int feature, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host refused to make the branch of feature {Feature}, with {Status}")]
    private static partial void LogBranchRefused(ILogger logger, Guid projectId, int feature, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: its .devflow/project.json is not valid: {Error}")]
    private static partial void LogBadProjectFile(ILogger logger, Guid projectId, string error);
}
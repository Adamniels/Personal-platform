using System.Text;
using System.Text.Json;

using DevFlow.Briefs;
using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Recipes;
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
/// kind of job is chosen, a review before a fix before a build, everything that job needs from the code host is
/// read first, as the app, with a key for that kind, and only then is a card of that kind taken, in
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
        // until its user resumes it or the branch is read again. See "Watching the default branch"
        // in devflow/README.md.
        if (await watch.Hold(userId, projectId, cancellationToken) != ProjectHold.None
            || HeldFor(await logins.Status(userId, cancellationToken)) is not null
            || await JobStore.NextKind(database, userId, projectId, cancellationToken) is not { } kind
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new NextJob.None();
        }

        // The key is made for the kind found, before anything is taken: a review's only fetches.
        var purpose = kind == Job.Review ? WritePurpose.Fetch : WritePurpose.Push;
        if (await Facts(projectId, userId, repository, cancellationToken) is not { } facts
            || await logins.ForRun(userId, cancellationToken) is not { } token
            || await Key(projectId, userId, repository, purpose, cancellationToken) is not { } key)
        {
            return new NextJob.None();
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
                Job.Fix => FixJob(await JobStore.ClaimFix(transaction, projectId, userId, workerId, facts.Check, now, cancellationToken), repository, facts, token, key),
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
            new BriefFacts(facts.Check, claimed.Branch, facts.DefaultBranch, claimed.RunId, facts.Start, facts.Url));
        return new Job(
            claimed.RunId,
            Job.Build,
            new JobCard(claimed.Number, claimed.Title, claimed.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            facts.DefaultBranch,
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
            new FixInput(claimed.Round, claimed.Report, claimed.Review?.Verdict, claimed.Comments));
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
        var brief = Brief.Reviewer(card, new ReviewFacts(claimed.Commit, claimed.Branch, claimed.Base, claimed.Earlier.Count));
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
    /// only for a build or a fix: a review is given no key that pushes. Marks the worker seen.</summary>
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

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed record RepositoryFacts(string DefaultBranch, string Check, string Start, string Url, Uri CloneAddress, Committer Committer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handed out the {Kind} of card {Card} of project {ProjectId} as run {RunId}")]
    private static partial void LogHandedOut(ILogger logger, string kind, Guid projectId, int card, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host did not answer {What}")]
    private static partial void LogNotRead(ILogger logger, Guid projectId, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: its .devflow/project.json is not valid: {Error}")]
    private static partial void LogBadProjectFile(ILogger logger, Guid projectId, string error);
}
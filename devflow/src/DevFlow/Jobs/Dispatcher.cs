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
/// Claude login or one past its renewal, or when its project has no card to take; otherwise
/// everything a job needs from the code host is read first, as the app, and only then is the card
/// taken, in one short transaction as the project's user, so no lock is held while GitHub is asked.
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
            || !await JobStore.HasWork(database, userId, projectId, cancellationToken)
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new NextJob.None();
        }

        if (await Facts(projectId, userId, repository, cancellationToken) is not { } facts
            || await logins.ForRun(userId, cancellationToken) is not { } token
            || await Key(projectId, userId, repository, cancellationToken) is not { } key)
        {
            return new NextJob.None();
        }

        var now = Now();
        var claimed = await database.InUserTransaction(userId, async transaction =>
            await watch.HoldUnderLock(transaction, projectId, cancellationToken) == ProjectHold.None
                ? await JobStore.Claim(transaction, projectId, userId, workerId, facts.DefaultBranch, now, cancellationToken)
                : null, cancellationToken);
        if (claimed is null)
        {
            // Another worker took the last card since it was looked for, or the project was
            // stopped or went unwatched while GitHub was asked; the key made for it is dropped
            // unused, and runs out within the hour.
            return new NextJob.None();
        }

        LogHandedOut(log, projectId, claimed.Number, claimed.RunId);
        var brief = Brief.Developer(
            new BriefCard(claimed.Number, claimed.Title, claimed.Body),
            new BriefFacts(facts.Check, claimed.Branch, facts.DefaultBranch));
        return new NextJob.Handed(new Job(
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
            JobLimits.Default));
    }

    /// <summary>A fresh push key for the job, only for the worker holding it while it runs; marks
    /// the worker seen.</summary>
    public async Task<FreshKey> PushKey(WorkerCaller caller, Guid runId, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await workers.Seen(caller, workerId, start.Image, cancellationToken) == WorkerState.Unknown
            || !await JobStore.Holds(database, caller.UserId, caller.ProjectId, runId, workerId, cancellationToken)
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new FreshKey.NotHeld();
        }

        return await Key(caller.ProjectId, caller.UserId, repository, cancellationToken) is { } key
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

        return new RepositoryFacts(branch, project.Check, clone, committer);
    }

    /// <summary>A push key for the repository, narrowed to <see cref="WritePurpose.Push"/>, or null
    /// when the code host made none.</summary>
    private async Task<AppWriteToken?> Key(Guid projectId, Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        if (await host.ForWrite(userId, repository, WritePurpose.Push, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var key })
        {
            return key;
        }

        LogNotRead(log, projectId, "a push key");
        return null;
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed record RepositoryFacts(string DefaultBranch, string Check, Uri CloneAddress, Committer Committer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handed out card {Card} of project {ProjectId} as run {RunId}")]
    private static partial void LogHandedOut(ILogger logger, Guid projectId, int card, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host did not answer {What}")]
    private static partial void LogNotRead(ILogger logger, Guid projectId, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: its .devflow/project.json is not valid: {Error}")]
    private static partial void LogBadProjectFile(ILogger logger, Guid projectId, string error);
}
using System.Text;
using System.Text.Json;

using Dapper;

using DevFlow.Briefs;
using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Projects;
using DevFlow.Protocol;
using DevFlow.Recipes;
using DevFlow.Setup;
using DevFlow.Watching;
using DevFlow.Workers;

using Npgsql;

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
/// Hands out work. Whether a worker asking gets any, and of what kind, is <see cref="RunDecision"/>'s,
/// the one rule the page reads too: a check of its setup version, its setup's next job for a worker
/// built from no recipe, or the cards' next job, a card's review before a whole feature's before a
/// card's fix before a whole feature's before a conflict before a reading before a build. Then
/// everything that job needs from the code host is read, as the app, with a key for that kind, a
/// feature's branch made for its first card, and only then is a card, or for a conflict a feature,
/// of that kind taken, in one short transaction as the project's user, so no lock is held while
/// GitHub is asked. That transaction reads the watch's hold, the pauses, the daily budget, the schedules, the
/// credential and the cap on the API key again under the project's lock, so a stop, a pause or a
/// schedule saved while GitHub was asked, a window that ended or a cap reached meanwhile, still holds
/// the card back, and a job never carries the key once the subscription's limit has reset. Anything the code host does not
/// answer is no work now, and the worker asks again. See "Jobs" in devflow/README.md and "Handing
/// out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class Dispatcher(
    UserDb database,
    WorkerStore workers,
    WatchStore watch,
    ClaudeLogins logins,
    ApiKeys apiKeys,
    ICodeHost host,
    WorkerStart start,
    TimeProvider time,
    ILogger<Dispatcher> log,
    Values.ProjectValues values)
{
    /// <summary>Where a project keeps its facts for Dev flow, on its default branch.</summary>
    public const string ProjectFilePath = ".devflow/project.json";

    /// <summary>The report's schema as every job carries it, written once from its type.</summary>
    private static readonly JsonElement ReportSchema = JsonSerializer.SerializeToElement(Report.Schema());

    /// <summary>The verdict's schema as every review job carries it, written once from its type.</summary>
    private static readonly JsonElement VerdictSchema = JsonSerializer.SerializeToElement(ReviewVerdict.Schema());

    /// <summary>The report's schema a check carries, null, since it runs no agent.</summary>
    private static readonly JsonElement NoSchema = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>The setup report's schema as every setup job carries it, written once from its type.</summary>
    private static readonly JsonElement SetupSchema = JsonSerializer.SerializeToElement(SetupReport.Schema());

    /// <summary>The reading's schema as every reading job carries it, written once from its type.</summary>
    private static readonly JsonElement ReadingSchema = JsonSerializer.SerializeToElement(Protocol.Reading.Schema());

    /// <summary>
    /// Why a user's project on <paramref name="runsOn"/> hands out no work for their Claude login,
    /// as the project's page says it: <c>no_claude_login</c> without one, <c>claude_login_expired</c>
    /// past its renewal, or null. A login due soon still works, and a project whose jobs carry the
    /// API key needs none: one on the key alone, and one continuing on it while the user's
    /// subscription is <paramref name="limited"/>. <see cref="RunDecision.LoginReason"/> words, as
    /// the project's page and the workers list name them.
    /// </summary>
    public static string? HeldFor(ClaudeLoginStatus login, string runsOn, bool limited)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (!RunsOn.NeedsClaudeLogin(runsOn, limited))
        {
            return null;
        }

        return RunDecision.LoginReason(login.State) switch
        {
            null => null,
            { Expired: true } => "claude_login_expired",
            _ => "no_claude_login",
        };
    }

    /// <summary>
    /// The worker's next job, or none; marks the worker seen. Whether anything runs, and what, is
    /// <see cref="RunDecision"/>'s, read for the worker in one query once it is marked seen: a wait
    /// or nothing to do is no job; a check due hands out that check, a worker built from no recipe
    /// its setup's next job, and any other the cards' next job of the kind the rule names.
    /// </summary>
    public async Task<NextJob> Next(WorkerCaller caller, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await workers.Seen(caller, workerId, start.Image, cancellationToken) == WorkerState.Unknown)
        {
            return new NextJob.Unknown();
        }

        if (await RunDecision.Read(database, caller.UserId, caller.ProjectId, workerId, start.Image, time.GetUtcNow(), cancellationToken) is not { } situation)
        {
            return new NextJob.None();
        }

        return RunDecision.Of(situation) switch
        {
            Decision.Run { Kind: Job.RecipeCheck } => await HandOutCheck(caller, workerId, situation.Version, cancellationToken),
            Decision.Run { Kind: Job.Setup } => await HandOutSetup(caller, workerId, situation.SetupKind!, cancellationToken),
            Decision.Run { Kind: var kind } => await HandOut(caller, workerId, kind, situation.Outside is not null, cancellationToken),
            _ => new NextJob.None(),
        };
    }

    /// <summary>
    /// Hands the worker the cards' next job of <paramref name="kind"/>, the kind the rule found:
    /// everything that job needs from the code host is read first, as the app, with a key for that
    /// kind and a feature's branch made for its first card, and only then is a card, or a feature,
    /// of that kind taken, in one short transaction as the project's user that reads the watch's
    /// hold, the pauses, the daily budget and the schedules again under the project's lock. Outside its schedules,
    /// as <paramref name="outside"/> found it before the lock and the schedules found it under
    /// it, only what an override, "Run now anyway", covers is read for and taken. No work when any
    /// of it is not there now.
    /// </summary>
    private async Task<NextJob> HandOut(WorkerCaller caller, Guid workerId, string kind, bool outside, CancellationToken cancellationToken)
    {
        var (userId, projectId) = (caller.UserId, caller.ProjectId);
        if (await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new NextJob.None();
        }

        // The key is made for the kind found, before anything is taken: a review's, of a card or of a
        // whole feature, and a reading's only fetch, and a build's, a fix's, a conflict's and a
        // feature's fix's fetch and push.
        var purpose = kind is Job.Review or Job.FeatureReview or Job.Read ? WritePurpose.Fetch : WritePurpose.Push;
        if (await Facts(projectId, userId, repository, cancellationToken) is not { } facts
            || await Credential(userId, projectId, cancellationToken) is not { } credential
            || await Key(projectId, userId, repository, purpose, cancellationToken) is not { } key)
        {
            return new NextJob.None();
        }

        // A feature's first card is built on the feature's branch, so the app makes it first, with
        // the job's push key, outside the lock as every read for a job is.
        if (kind == Job.Build && !await FeatureBranch(projectId, userId, repository, facts.DefaultBranch, key, outside, cancellationToken))
        {
            return new NextJob.None();
        }

        // A conflict's brief lists the files both sides changed, read as the app outside the lock,
        // for the feature it would take and the two commits it would merge.
        DueConflict? due = null;
        IReadOnlyList<string> files = [];
        if (kind == Job.Conflict)
        {
            due = await JobStore.ConflictDue(database, userId, projectId, time.GetUtcNow(), outside, cancellationToken);
            if (due is null || await InConflict(projectId, userId, repository, due, cancellationToken) is not { } changed)
            {
                return new NextJob.None();
            }

            files = changed;
        }

        var now = Now();
        var job = await database.InUserTransaction<Job?>(userId, async transaction =>
        {
            // The project's models and limits are read in the transaction that takes the work, so
            // the job carries them as they are now and the run keeps them whatever changes after.
            // The daily budget, the credential and the cap on the key are read again too, after the
            // pauses took the project's row, so a run that finished, a budget or a cap lowered, or
            // the subscription's limit reset meanwhile is seen.
            if (await watch.HoldUnderLock(transaction, projectId, cancellationToken) != ProjectHold.None
                || await Pauses.HeldUnderLock(transaction, projectId, cancellationToken) != PauseHold.None
                || await Usage.DailyBudget.ReachedIn(transaction, projectId, now, cancellationToken) is not null
                || await RunSettings.Read(transaction, projectId, cancellationToken) is not { } run
                || !await Carries(transaction, run, credential, now, cancellationToken)
                || await Usage.ApiCap.ReachedIn(transaction, projectId, now, cancellationToken) is not null)
            {
                return null;
            }

            // Outside its schedules, read after the pauses took the project's row, so schedules
            // saved or an override taken back meanwhile are seen, only covered work is taken.
            var runNowOnly = await Schedules.ScheduleStore.OutsideUnderLock(transaction, projectId, now, cancellationToken);

            // The project's values are opened in the same transaction, only for a kind that runs the
            // project's code, so they leave Dev flow only inside this job's answer.
            IReadOnlyList<JobValue> carried = Values.ProjectValues.Carries(kind) ? await values.Open(transaction, userId, projectId, cancellationToken) : [];
            var handed = kind switch
            {
                Job.Review => ReviewJob(await JobStore.ClaimReview(transaction, projectId, userId, workerId, run, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
                Job.FeatureReview => FeatureReviewJob(await JobStore.ClaimFeatureReview(transaction, projectId, userId, workerId, run, facts.DefaultBranch, facts.Check, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
                Job.Fix => FixJob(await JobStore.ClaimFix(transaction, projectId, userId, workerId, run, facts.Check, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
                Job.FeatureFix => FeatureFixJob(await JobStore.ClaimFeatureFix(transaction, projectId, userId, workerId, run, facts.DefaultBranch, facts.Check, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
                Job.Conflict => ConflictJob(await JobStore.ClaimConflict(transaction, projectId, userId, workerId, run, due!, facts.DefaultBranch, facts.Check, now, runNowOnly, cancellationToken), repository, facts, run, credential, key, files),
                Job.Read => ReadJob(await Readings.ReadingStore.Claim(transaction, projectId, userId, workerId, run, facts.DefaultBranch, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
                _ => BuildJob(await JobStore.Claim(transaction, projectId, userId, workerId, run, facts.DefaultBranch, facts.Check, now, runNowOnly, cancellationToken), repository, facts, run, credential, key),
            };
            if (handed is null)
            {
                return null;
            }

            await JobStore.RecordModel(transaction, handed.JobId, handed.Model, credential, cancellationToken);
            return handed with { Values = carried };
        }, cancellationToken);
        if (job is null)
        {
            // Another worker took the last card of the kind since it was looked for, or the project
            // was stopped, paused, left its schedules with nothing of the kind covered by an override,
            // was moved to another credential, or went unwatched while GitHub was asked; the key made for it is dropped
            // unused, and runs out within the hour. The worker asks again.
            return new NextJob.None();
        }

        LogHandedOut(log, job.Kind, projectId, job.Card?.Number ?? 0, job.JobId);
        return new NextJob.Handed(job);
    }

    /// <summary>
    /// Hands the worker the check of <paramref name="version"/>: what the job needs is read first,
    /// as the app, outside the lock, the project's <c>project.json</c>, the default branch's head
    /// and a key that only fetches; then the check is taken under the project's lock, while it is
    /// still due and the project not stopped, paused, outside its schedules or unwatched. No work when
    /// any of it is not there now.
    /// </summary>
    private async Task<NextJob> HandOutCheck(WorkerCaller caller, Guid workerId, string version, CancellationToken cancellationToken)
    {
        var (userId, projectId) = (caller.UserId, caller.ProjectId);
        if (await workers.RepositoryOf(caller, cancellationToken) is not { } repository
            || await Facts(projectId, userId, repository, cancellationToken) is not { } facts
            || await Head(projectId, userId, repository, facts.DefaultBranch, cancellationToken) is not { } head
            || await Key(projectId, userId, repository, WritePurpose.Fetch, cancellationToken) is not { } key)
        {
            return new NextJob.None();
        }

        var now = Now();
        var (claimed, carried) = await database.InUserTransaction<(ClaimedCheck?, IReadOnlyList<JobValue>)>(userId, async transaction =>
        {
            if (await watch.HoldUnderLock(transaction, projectId, cancellationToken) != ProjectHold.None
                || await Pauses.HeldUnderLock(transaction, projectId, cancellationToken) != PauseHold.None
                || await Usage.DailyBudget.ReachedIn(transaction, projectId, now, cancellationToken) is not null
                || await Schedules.ScheduleStore.OutsideUnderLock(transaction, projectId, now, cancellationToken)
                || await SetupStore.Claim(transaction, projectId, userId, workerId, version, facts.DefaultBranch, head, facts.Check, now, cancellationToken) is not { } taken)
            {
                return (null, []);
            }

            // A check runs the project's check and its start, so it carries the project's values.
            return (taken, await values.Open(transaction, userId, projectId, cancellationToken));
        }, cancellationToken);
        if (claimed is null)
        {
            // Another worker on the version took its check since, or the project was stopped, paused
            // or left its schedules meanwhile; the key made for it runs out unused.
            return new NextJob.None();
        }

        LogCheckHandedOut(log, version, projectId, claimed.RunId);
        return new NextJob.Handed(new Job(
            claimed.RunId,
            Job.RecipeCheck,
            null,
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            facts.DefaultBranch,
            facts.DefaultBranch,
            "",
            NoSchema,
            facts.Check,
            null,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            new JobLimits(0, 0m, SetupStore.CheckMinutes),
            "",
            Start: facts.Start,
            Url: facts.Url,
            ToCheck: new JobCheck(claimed.Version, claimed.Commit),
            Values: carried));
    }

    /// <summary>
    /// Hands a worker built from no recipe its project's next setup job of <paramref name="kind"/>,
    /// the setup's next kind the rule read: its review, a fix of its pull request, or a run, the
    /// first of a new setup among them; nothing when the code host does not answer what the job
    /// needs, the default branch, the clone address, the app's committer and a key, read first as
    /// the app outside the lock. The setup is taken under the project's lock, while the project is
    /// not stopped, paused, outside its schedules or unwatched.
    /// </summary>
    private async Task<NextJob> HandOutSetup(WorkerCaller caller, Guid workerId, string kind, CancellationToken cancellationToken)
    {
        var (userId, projectId) = (caller.UserId, caller.ProjectId);
        if (await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return new NextJob.None();
        }

        if (await host.DefaultBranch(userId, repository, cancellationToken) is not CodeHostResult<string>.Found { Value: var defaultBranch }
            || await host.CloneAddress(userId, repository, cancellationToken) is not CodeHostResult<Uri>.Found { Value: var clone }
            || await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var committer }
            || await Credential(userId, projectId, cancellationToken) is not { } credential
            || await Key(projectId, userId, repository, kind == SetupRunStore.ReviewKind ? WritePurpose.Fetch : WritePurpose.Push, cancellationToken) is not { } key)
        {
            LogNotRead(log, projectId, "what a setup job needs");
            return new NextJob.None();
        }

        var now = Now();
        var where = new JobRepository(repository.Owner, repository.Name, clone, defaultBranch);
        var by = new JobCommitter(committer.Name, committer.Email);
        var job = await database.InUserTransaction<Job?>(userId, async transaction =>
        {
            if (await watch.HoldUnderLock(transaction, projectId, cancellationToken) != ProjectHold.None
                || await Pauses.HeldUnderLock(transaction, projectId, cancellationToken) != PauseHold.None
                || await Usage.DailyBudget.ReachedIn(transaction, projectId, now, cancellationToken) is not null
                || await Schedules.ScheduleStore.OutsideUnderLock(transaction, projectId, now, cancellationToken)
                || await RunSettings.Read(transaction, projectId, cancellationToken) is not { } run
                || !await Carries(transaction, run, credential, now, cancellationToken)
                || await Usage.ApiCap.ReachedIn(transaction, projectId, now, cancellationToken) is not null)
            {
                return null;
            }

            if (kind == SetupRunStore.ReviewKind)
            {
                // A setup's review runs none of the project's code, so it carries none of its values.
                if (await SetupRunStore.ClaimReview(transaction, projectId, userId, workerId, run, defaultBranch, now, cancellationToken) is not { } review)
                {
                    return null;
                }

                await JobStore.RecordModel(transaction, review.RunId, run.ModelFor(Job.Review), credential, cancellationToken);
                return SetupReviewJob(review, where, run, credential, key, by) with { Values = [] };
            }

            var claimed = kind == Job.Fix
                ? await SetupRunStore.ClaimFix(transaction, projectId, userId, workerId, run, defaultBranch, now, cancellationToken)
                : await SetupRunStore.ClaimRun(transaction, projectId, userId, workerId, run, defaultBranch, now, cancellationToken);
            if (claimed is null)
            {
                return null;
            }

            var fix = claimed.FromCommit is not null || claimed.Comments.Count > 0
                ? new FixInput(claimed.FromCommit is null ? null : claimed.Round, claimed.Report, claimed.Review, claimed.Comments, run.FixRoundsCard)
                : null;
            await JobStore.RecordModel(transaction, claimed.RunId, run.ModelFor(Job.Setup), credential, cancellationToken);
            return new Job(
                claimed.RunId,
                Job.Setup,
                null,
                where,
                claimed.Branch,
                claimed.Base,
                Brief.Setup(new SetupFacts(claimed.Branch, claimed.Base), fix, claimed.SentBack),
                SetupSchema,
                "",
                credential,
                key.Value,
                key.ExpiresAt,
                by,
                run.Limits,
                run.ModelFor(Job.Setup),
                ToFix: claimed.FromCommit is { } from ? new JobFix(from, claimed.Round) : null,
                Values: await values.Open(transaction, userId, projectId, cancellationToken));
        }, cancellationToken);
        if (job is null)
        {
            return new NextJob.None();
        }

        LogSetupHandedOut(log, job.Kind, projectId, job.JobId);
        return new NextJob.Handed(job);
    }

    /// <summary>The review job of a setup taken to review: on its branch, the commit it pushed, the
    /// setup's reviewer brief, the verdict's form, and what the review reads, the setup report and
    /// Dev flow's validation as the check. Its card is the setup's pull request, under its title,
    /// since a worker's review reads one.</summary>
    private static Job SetupReviewJob(ClaimedSetupReview claimed, JobRepository where, RunSettings run, JobCredential credential, AppWriteToken key, JobCommitter by)
    {
        using var report = JsonDocument.Parse(claimed.Report);
        return new Job(
            claimed.RunId,
            Job.Review,
            new JobCard(claimed.PullRequest, SetupRunStore.Title, "Read the repository and write .devflow/Dockerfile and .devflow/project.json, so Dev flow can build this project's cards."),
            where,
            claimed.Branch,
            claimed.Base,
            Brief.SetupReviewer(new ReviewFacts(claimed.Commit, claimed.Branch, claimed.Base, claimed.Earlier.Count)),
            VerdictSchema,
            SetupFiles.Validation,
            credential,
            key.Value,
            key.ExpiresAt,
            by,
            run.Limits,
            run.ModelFor(Job.Review),
            new JobReview(claimed.Commit, report.RootElement.Clone(), new CheckResult(claimed.ExitCode, claimed.Output), [.. claimed.Earlier.Select(Round)]));
    }

    /// <summary>
    /// The credential a job of the project carries, for what it runs on now and whether the user's
    /// subscription is limited, by <see cref="RunsOn.Carried"/>: its user's Claude token, or their
    /// API key, each opened by its one method; null when the one it carries is not given, which is
    /// no work now. The project's setting and the limit are read again under its lock, by
    /// <see cref="Carries"/>, so a job never carries a credential the project was moved off, or the
    /// key once the limit has reset, meanwhile.
    /// </summary>
    private async Task<JobCredential?> Credential(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var now = Now();
        var (runsOn, limited) = await database.InUserTransaction(userId, async transaction => (
            await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT runs_on FROM project_settings WHERE project_id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken)),
            await Stops.SubscriptionLimits.UntilIn(transaction, now, cancellationToken)), cancellationToken);
        if (runsOn is null || !RunsOn.Values.Contains(runsOn, StringComparer.Ordinal))
        {
            return null;
        }

        if (RunsOn.Carried(runsOn, limited is not null) == JobCredential.Subscription)
        {
            return await logins.ForRun(userId, cancellationToken) is { } token
                ? new JobCredential(JobCredential.Subscription, token.Value)
                : null;
        }

        if (await apiKeys.ForRun(userId, cancellationToken) is { } apiKey)
        {
            return new JobCredential(JobCredential.ApiKey, apiKey.Value);
        }

        LogNoApiKey(log, projectId);
        return null;
    }

    /// <summary>Whether <paramref name="credential"/>, opened before the lock, is still the one a
    /// job of the project carries, as <paramref name="run"/> and the user's subscription's limit
    /// read it under the lock at <paramref name="now"/>.</summary>
    private static async Task<bool> Carries(NpgsqlTransaction transaction, RunSettings run, JobCredential credential, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunsOn.Carried(run.RunsOn, await Stops.SubscriptionLimits.UntilIn(transaction, now, cancellationToken) is not null) == credential.Kind;

    /// <summary>The commit <paramref name="branch"/> is at, read as the app, or null when the code
    /// host did not answer it now.</summary>
    private async Task<string?> Head(Guid projectId, Guid userId, Repository repository, string branch, CancellationToken cancellationToken)
    {
        if (await host.BranchHead(userId, repository, branch, cancellationToken) is CodeHostResult<string>.Found { Value: var head })
        {
            return head;
        }

        LogNotRead(log, projectId, "its default branch's head");
        return null;
    }

    /// <summary>The build job for a card taken to build, or null when none was.</summary>
    private static Job? BuildJob(ClaimedCard? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Developer(
            new BriefCard(claimed.Number, claimed.Title, claimed.Body),
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            claimed.Comments,
            claimed.Feature,
            claimed.SentBack,
            claimed.Reading);
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.Build),
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The reading job for the cards taken to read, or null when none were: the feature's
    /// parent or the card on its own as its card, or a refused card read again, alone, on the branch
    /// whose code is read, from the default branch, with the reader's brief, the reading's form, no
    /// check, the reading model, and every card to read as picked.</summary>
    private static Job? ReadJob(Readings.ClaimedRead? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Reader(claimed.Feature, claimed.Cards, new ReaderFacts(claimed.Branch), claimed.Again);
        return new Job(
            claimed.RunId,
            Job.Read,
            new JobCard(claimed.Card.Number, claimed.Card.Title, claimed.Card.Body),
            new JobRepository(repository.Owner, repository.Name, facts.CloneAddress, facts.DefaultBranch),
            claimed.Branch,
            claimed.Base,
            brief,
            ReadingSchema,
            "",
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.Read),
            ToRead: new JobRead([.. claimed.Cards.Select(card => new JobCard(card.Number, card.Title, card.Body))]));
    }

    /// <summary>The conflict job for a feature taken to resolve, or null when none was: on the
    /// feature's branch, with the default branch as its base, the feature's parent as its card, the
    /// conflict brief, the report's form, the check, and the two commits it merges.</summary>
    private static Job? ConflictJob(ClaimedConflict? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key, IReadOnlyList<string> files)
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.Conflict),
            ToResolve: new JobConflict(claimed.FeatureCommit, claimed.DefaultCommit));
    }

    /// <summary>The review job for a feature taken to review as a whole, or null when none was: on
    /// the feature's branch, with the default branch as its base, the feature's parent as its card,
    /// the feature reviewer's brief, the verdict's form, the project's check, and what the review
    /// reads besides the code.</summary>
    private static Job? FeatureReviewJob(ClaimedFeatureReview? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.FeatureReview),
            ToReviewFeature: new JobFeatureReview(claimed.Commit, claimed.Cards, claimed.Check, claimed.RemergeDiff, [.. claimed.Earlier.Select(Round)]));
    }

    /// <summary>The fix job for a feature taken to fix as a whole, or null when none was: a fix's job
    /// on the feature's branch, with the default branch as its base, the feature's parent as its
    /// card, the feature's fix brief, the report's form, and where the fix starts.</summary>
    private static Job? FeatureFixJob(ClaimedFeatureFix? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.FeatureFixer(
            claimed.Feature,
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            new FixInput(claimed.Round, claimed.Report, claimed.Review?.Verdict, claimed.Comments, run.FixRoundsFeature));
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.FeatureFix),
            ToFix: new JobFix(claimed.FromCommit, claimed.Round),
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The fix job for a card taken to fix, or null when none was: a build's job on the
    /// card's branch and base, with the fix brief, the developer's with what to fix, the report's
    /// form, and where the fix starts.</summary>
    private static Job? FixJob(ClaimedFix? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var brief = Brief.Fixer(
            new BriefCard(claimed.Number, claimed.Title, claimed.Body),
            new BriefFacts(facts.Check, claimed.Branch, claimed.Base, claimed.RunId, facts.Start, facts.Url),
            new FixInput(claimed.Round, claimed.Report, claimed.Review?.Verdict, claimed.Comments, run.FixRoundsCard),
            claimed.Feature,
            claimed.Reading);
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.Fix),
            ToFix: new JobFix(claimed.FromCommit, claimed.Round),
            Start: facts.Start,
            Url: facts.Url);
    }

    /// <summary>The review job for a card taken to review, or null when none was: the build's
    /// branch, base and check, the commit it left, its report, the check's result and its pictures,
    /// the reviewer's brief and the verdict's form.</summary>
    private static Job? ReviewJob(ClaimedReview? claimed, Repository repository, RepositoryFacts facts, RunSettings run, JobCredential credential, AppWriteToken key)
    {
        if (claimed is null)
        {
            return null;
        }

        var card = new BriefCard(claimed.Number, claimed.Title, claimed.Body);
        var brief = Brief.Reviewer(card, new ReviewFacts(claimed.Commit, claimed.Branch, claimed.Base, claimed.Earlier.Count), claimed.Feature, claimed.Reading);
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
            credential,
            key.Value,
            key.ExpiresAt,
            new JobCommitter(facts.Committer.Name, facts.Committer.Email),
            run.Limits,
            run.ModelFor(Job.Review),
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
    /// worker asks again. With <paramref name="runNowOnly"/>, the project outside its schedules,
    /// the next card is the next one an override covers. See "A feature's branch" in
    /// devflow/docs/devflow-architecture.md.
    /// </summary>
    private async Task<bool> FeatureBranch(Guid projectId, Guid userId, Repository repository, string defaultBranch, AppWriteToken key, bool runNowOnly, CancellationToken cancellationToken)
    {
        if (await FeatureBranchStore.Due(database, userId, projectId, time.GetUtcNow(), runNowOnly, cancellationToken) is not { } feature)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Handed out the setup's {Kind} of project {ProjectId} as run {RunId}")]
    private static partial void LogSetupHandedOut(ILogger logger, string kind, Guid projectId, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handed out the check of version {Version} of project {ProjectId} as run {RunId}")]
    private static partial void LogCheckHandedOut(ILogger logger, string version, Guid projectId, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host did not answer {What}")]
    private static partial void LogNotRead(ILogger logger, Guid projectId, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made the branch {Branch} of feature {Feature} of project {ProjectId}")]
    private static partial void LogBranchMade(ILogger logger, Guid projectId, int feature, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feature {Feature} of project {ProjectId} waits for its owner: a branch named {Branch} already exists")]
    private static partial void LogBranchExists(ILogger logger, Guid projectId, int feature, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: the code host refused to make the branch of feature {Feature}, with {Status}")]
    private static partial void LogBranchRefused(ILogger logger, Guid projectId, int feature, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: it runs on the API key, and none is given")]
    private static partial void LogNoApiKey(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No job for project {ProjectId}: its .devflow/project.json is not valid: {Error}")]
    private static partial void LogBadProjectFile(ILogger logger, Guid projectId, string error);
}
using System.Globalization;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.Stops;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>Where a reason a card waits is acted on, for the page to link to.</summary>
public enum ActedOn
{
    /// <summary>Nowhere: it passes by itself.</summary>
    Nowhere,

    /// <summary>The project's page.</summary>
    ProjectPage,

    /// <summary>Dev flow's settings, where the Claude login is given.</summary>
    Settings,

    /// <summary>"Waiting for you".</summary>
    WaitingForYou,

    /// <summary>The project's schedules, the card "When it runs" on its settings page.</summary>
    Schedules,

    /// <summary>The project's daily budget, the card "Usage" on its settings page.</summary>
    Budget,

    /// <summary>What the project runs on, "Runs on" in the card "Usage" on its settings page.</summary>
    RunsOn,
}

/// <summary>Why a project's next job, or a card, does not run yet: a closed set, each with its
/// sentence for the page and where it is acted on.</summary>
public abstract record WaitReason
{
    private WaitReason()
    {
    }

    /// <summary>What the page says of it.</summary>
    public abstract string Sentence { get; }

    /// <summary>Where it is acted on.</summary>
    public virtual ActedOn Where => ActedOn.Nowhere;

    /// <summary>Every project of the user is paused, by the switch on "Running now".</summary>
    public sealed record AllPaused : WaitReason
    {
        public override string Sentence => "Everything is paused: continue it";
    }

    /// <summary>The owner paused the project.</summary>
    public sealed record Paused : WaitReason
    {
        public override string Sentence => "Paused: continue it";

        public override ActedOn Where => ActedOn.ProjectPage;
    }

    /// <summary>The project's runs that finished today, in the user's zone, used
    /// <paramref name="Used"/> tokens, at least its daily budget, <paramref name="Budget"/>; it lifts
    /// at <paramref name="LiftsAt"/>, the next midnight there, in the zone's own offset, so its
    /// sentence says it as wall clock there.</summary>
    public sealed record BudgetReached(long Used, long Budget, DateTimeOffset LiftsAt) : WaitReason
    {
        public override string Sentence => string.Create(CultureInfo.InvariantCulture, $"Daily budget reached, runs again at {LiftsAt:ddd HH:mm}");

        public override ActedOn Where => ActedOn.Budget;
    }

    /// <summary>The project runs by schedule and is outside every window that is on; a window that
    /// is on starts next at <paramref name="NextStart"/>, in the user's zone's own offset, so its
    /// sentence says it as wall clock there, or null with none on.</summary>
    public sealed record OutsideSchedule(DateTimeOffset? NextStart) : WaitReason
    {
        public override string Sentence => NextStart is { } next
            ? string.Create(CultureInfo.InvariantCulture, $"Outside its schedule, runs again {next:ddd HH:mm}")
            : "No schedule is on";

        public override ActedOn Where => ActedOn.Schedules;
    }

    /// <summary>The worker asking is built from an older recipe or another base image.</summary>
    public sealed record NeedsRebuild : WaitReason
    {
        public override string Sentence => "Its worker needs building again";
    }

    /// <summary>Something landed on the default branch that Dev flow did not put there, and the
    /// owner has not resumed the project.</summary>
    public sealed record ProjectStopped : WaitReason
    {
        public override string Sentence => "The project is stopped: resume it";

        public override ActedOn Where => ActedOn.ProjectPage;
    }

    /// <summary>The default branch has not been read for <see cref="BranchWatch.UnwatchedAfter"/>.</summary>
    public sealed record Unwatched : WaitReason
    {
        public override string Sentence => string.Create(CultureInfo.InvariantCulture,
            $"Its default branch has not been read for {BranchWatch.UnwatchedAfter.TotalMinutes} minutes");
    }

    /// <summary>The project waits after failures outside its cards, until <paramref name="Until"/>,
    /// the last for <paramref name="Why"/>.</summary>
    public sealed record CannotRun(DateTimeOffset Until, string Why) : WaitReason
    {
        public override string Sentence => $"Dev flow cannot run its cards: {Why}";

        public override ActedOn Where => ActedOn.ProjectPage;
    }

    /// <summary>The worker's setup version has no check that passed, and is being checked when
    /// <paramref name="Checking"/>; or the project needs setting up.</summary>
    public sealed record NeedsSetup(bool Checking) : WaitReason
    {
        public override string Sentence => Checking ? "Its recipe is being checked" : "It needs setting up";
    }

    /// <summary>The project's user has no Claude login, or one past its renewal when
    /// <paramref name="Expired"/>.</summary>
    public sealed record NoClaudeLogin(bool Expired) : WaitReason
    {
        public override string Sentence => Expired ? "The Claude login has expired" : "No Claude login";

        public override ActedOn Where => ActedOn.Settings;
    }

    /// <summary>Claude's usage limit, which resets at <paramref name="Until"/>, in the user's zone's
    /// own offset, so its sentence says it as wall clock there: the user's subscription is limited,
    /// <see cref="Stops.SubscriptionLimits"/>, for a project on it, or the project's last failure
    /// outside its cards found the limit.</summary>
    public sealed record UsageLimit(DateTimeOffset Until) : WaitReason
    {
        public override string Sentence => string.Create(CultureInfo.InvariantCulture, $"At the usage limit, until {Until:ddd HH:mm}");

        public override ActedOn Where => ActedOn.RunsOn;
    }

    /// <summary>No worker of <paramref name="Project"/> was seen in the last
    /// <see cref="RunStore.Silence"/>.</summary>
    public sealed record NoWorker(string Project) : WaitReason
    {
        public override string Sentence => $"No worker is running for {Project}";

        public override ActedOn Where => ActedOn.ProjectPage;
    }

    /// <summary>The card waits for <paramref name="Issue"/>, <c>#n</c> in its repository or
    /// <c>owner/name#n</c> in another: an open issue it is blocked by, or the card before it in its
    /// feature.</summary>
    public sealed record WaitsForCard(string Issue) : WaitReason
    {
        public override string Sentence => $"Waits for {Issue}, which it depends on";
    }

    /// <summary>The card follows card <paramref name="Number"/> of its feature, which needs the
    /// owner's answers.</summary>
    public sealed record WaitsForAnswers(int Number) : WaitReason
    {
        public override string Sentence => string.Create(CultureInfo.InvariantCulture, $"Waits for your answers on #{Number}");

        public override ActedOn Where => ActedOn.WaitingForYou;
    }

    /// <summary>The card could run, but every worker of its project is busy; it is next for
    /// <paramref name="Worker"/>, whose run started first.</summary>
    public sealed record WorkersBusy(string Worker) : WaitReason
    {
        public override string Sentence => $"Next for {Worker}, after its current run";
    }
}

/// <summary>What the rule answers for a project or a card.</summary>
public abstract record Decision
{
    private Decision()
    {
    }

    /// <summary>It runs: a job of <paramref name="Kind"/>.</summary>
    public sealed record Run(string Kind) : Decision;

    /// <summary>It does not run yet, for <paramref name="Reason"/>.</summary>
    public sealed record Wait(WaitReason Reason) : Decision;

    /// <summary>Nothing is in its way, and there is nothing to run.</summary>
    public sealed record Idle : Decision;
}

/// <summary>Where the setup version of the worker asking stands.</summary>
public enum SetupGate
{
    /// <summary>A check of it passed.</summary>
    Checked,

    /// <summary>It has no check that passed, and one is to be handed out.</summary>
    CheckDue,

    /// <summary>A check of it is under way.</summary>
    CheckRunning,

    /// <summary>Its last check failed, and the owner has not asked for another.</summary>
    CheckFailed,

    /// <summary>The worker is built from no recipe, the base image a setup run starts.</summary>
    NotSetUp,
}

/// <summary>A project as the rule reads it, for the worker asking.</summary>
/// <param name="Project">The project's name, its repository's.</param>
/// <param name="NeedsRebuild">Whether the worker asking needs building again.</param>
/// <param name="Hold">Whether the project is stopped or its default branch not read lately.</param>
/// <param name="Wait">Its wait after failures outside its cards, null with none.</param>
/// <param name="Setup">Where the worker's setup version stands.</param>
/// <param name="SetupKind">The setup's next job for a worker built from no recipe, null with
/// none.</param>
/// <param name="Login">The project's user's Claude login, read only for a project on the
/// subscription, see <paramref name="RunsOn"/>.</param>
/// <param name="WorkerSeen">Whether any worker of the project was seen in the last
/// <see cref="RunStore.Silence"/>.</param>
/// <param name="NextKind">The kind of the cards' next job, <see cref="JobStore.NextKind"/>'s, null
/// with none.</param>
/// <param name="Version">The worker's setup version.</param>
/// <param name="Now">When it was read.</param>
/// <param name="Pause">Whether the project is paused, on its own or with everything.</param>
/// <param name="Outside">Why the project does not run by its schedules now, null when it may: it
/// runs always, or is inside a window that is on.</param>
/// <param name="RunNow">Whether an override of its schedules, "Run now anyway" on a feature or a
/// card, holds, <see cref="RunNowStore"/>.</param>
/// <param name="RunNowKind">The kind of the next job among those the overrides cover,
/// <see cref="JobStore.RunNowKindSql"/>'s, null with none.</param>
/// <param name="Budget">Why the project waits for its daily budget, the tokens of its runs that
/// finished today having reached it, by <see cref="Usage.DailyBudget"/>; null when it has none or
/// used less.</param>
/// <param name="RunsOn">What the project runs on, <see cref="Projects.RunsOn"/>: only one on the
/// subscription waits for the Claude login.</param>
/// <param name="Limited">Until when the user's subscription is limited, by
/// <see cref="Stops.SubscriptionLimits"/>, in their zone's own offset; null when it is not.</param>
public sealed record ProjectSituation(
    string Project,
    bool NeedsRebuild,
    ProjectHold Hold,
    ProjectWait? Wait,
    SetupGate Setup,
    string? SetupKind,
    ClaudeLoginState Login,
    bool WorkerSeen,
    string? NextKind,
    string Version,
    DateTimeOffset Now,
    PauseHold Pause = PauseHold.None,
    WaitReason.OutsideSchedule? Outside = null,
    bool RunNow = false,
    string? RunNowKind = null,
    WaitReason.BudgetReached? Budget = null,
    string RunsOn = Projects.RunsOn.Subscription,
    DateTimeOffset? Limited = null);

/// <summary>A card's decision, and the kind of job due for it, null for one that waits for another
/// card with none due.</summary>
public sealed record CardDecision(int Number, Decision Decision, string? Kind = null);

/// <summary>
/// The one rule that decides whether a project's next job runs and, for each card waiting, why it
/// does not run yet, so what the page says and what the dispatcher does never disagree. See "Jobs"
/// in devflow/README.md and "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunDecision
{
    /// <summary>
    /// The project's decision for its situation, its gates in the order the dispatcher meets them:
    /// everything is paused, then the project is, then its daily budget is reached, then it is
    /// outside its schedules, before any other, since they hold every kind of job, the setup's and
    /// the check among them; no override of the schedules gets past the budget. Outside its
    /// schedules with an override, "Run now anyway", that holds, the rest of the rule decides among
    /// the jobs the overrides cover alone, <see cref="ProjectSituation.RunNowKind"/>, and a setup's
    /// job or a check, which no override covers, still waits for the schedules; after that, the worker asking needs a rebuild; the project is stopped, or its default branch not read
    /// lately; it waits after failures outside its cards, other than a usage limit; the worker's
    /// setup version has no check that passed, when a check due is what runs, since it runs no agent
    /// and needs no Claude login, once a worker of the project was seen lately; no Claude login, or
    /// one past its renewal, for a project whose jobs carry the subscription; Claude's usage limit, for one on the subscription alone; no worker of the project seen lately. Past them a worker built from no recipe runs the setup's
    /// next job, and any other the cards' next, or nothing when there is none.
    /// </summary>
    public static Decision Of(ProjectSituation situation)
    {
        ArgumentNullException.ThrowIfNull(situation);
        var wait = situation.Wait is { } counted && counted.WaitUntil > situation.Now ? counted : null;
        switch (situation.Pause)
        {
            case PauseHold.AllPaused:
                return new Decision.Wait(new WaitReason.AllPaused());
            case PauseHold.Paused:
                return new Decision.Wait(new WaitReason.Paused());
        }

        if (situation.Budget is { } budget)
        {
            return new Decision.Wait(budget);
        }

        var nextKind = situation.NextKind;
        if (situation.Outside is { } outside)
        {
            if (!situation.RunNow)
            {
                return new Decision.Wait(outside);
            }

            nextKind = situation.RunNowKind;
        }

        var decision = Gates(situation, wait, nextKind);
        return situation.Outside is { } held && decision is Decision.Run { Kind: Protocol.Job.RecipeCheck or Protocol.Job.Setup }
            ? new Decision.Wait(held)
            : decision;
    }

    /// <summary>The rule's gates after the pauses and the schedules, with <paramref name="wait"/>,
    /// the project's wait still running, and <paramref name="nextKind"/>, the cards' next kind the
    /// rule runs.</summary>
    private static Decision Gates(ProjectSituation situation, ProjectWait? wait, string? nextKind)
    {
        if (situation.NeedsRebuild)
        {
            return new Decision.Wait(new WaitReason.NeedsRebuild());
        }

        switch (situation.Hold)
        {
            case ProjectHold.Stopped:
                return new Decision.Wait(new WaitReason.ProjectStopped());
            case ProjectHold.Unwatched:
                return new Decision.Wait(new WaitReason.Unwatched());
        }

        if (wait is { UntilReset: false })
        {
            return new Decision.Wait(new WaitReason.CannotRun(wait.WaitUntil, wait.Reason));
        }

        switch (situation.Setup)
        {
            case SetupGate.CheckRunning:
                return new Decision.Wait(new WaitReason.NeedsSetup(Checking: true));
            case SetupGate.CheckFailed:
            case SetupGate.NotSetUp when situation.SetupKind is null:
                return new Decision.Wait(new WaitReason.NeedsSetup(Checking: false));
            case SetupGate.CheckDue:
                // A check runs on a worker like any job, so with none seen lately the project waits
                // for one; the dispatcher's worker was just seen, so this holds back only the page.
                return situation.WorkerSeen
                    ? new Decision.Run(Protocol.Job.RecipeCheck)
                    : new Decision.Wait(new WaitReason.NoWorker(situation.Project));
        }

        if (Projects.RunsOn.NeedsClaudeLogin(situation.RunsOn, situation.Limited is not null) && LoginReason(situation.Login) is { } login)
        {
            return new Decision.Wait(login);
        }

        if (UsageLimitOf(situation, wait) is { } limit)
        {
            return new Decision.Wait(limit);
        }

        if (!situation.WorkerSeen)
        {
            return new Decision.Wait(new WaitReason.NoWorker(situation.Project));
        }

        if (situation.Setup == SetupGate.NotSetUp)
        {
            return new Decision.Run(Protocol.Job.Setup);
        }

        return nextKind is { } kind ? new Decision.Run(kind) : new Decision.Idle();
    }

    /// <summary>Claude's usage limit as it holds the project, until the later of its own wait for
    /// a reset and, for a project on the subscription, the user's subscription's limit; null when
    /// neither holds.</summary>
    private static WaitReason.UsageLimit? UsageLimitOf(ProjectSituation situation, ProjectWait? wait)
    {
        var own = wait is { UntilReset: true } ? wait.WaitUntil : (DateTimeOffset?)null;
        var users = situation.RunsOn == Projects.RunsOn.Subscription ? situation.Limited : null;
        return (own, users) switch
        {
            ({ } a, { } b) => new WaitReason.UsageLimit(a >= b ? a : b),
            ({ } a, null) => new WaitReason.UsageLimit(a),
            (null, { } b) => new WaitReason.UsageLimit(b),
            _ => null,
        };
    }

    /// <summary>Why a Claude login in <paramref name="state"/> runs nothing: none given, or one past
    /// its renewal; null for one that works, due soon among them.</summary>
    public static WaitReason.NoClaudeLogin? LoginReason(ClaudeLoginState state) => state switch
    {
        ClaudeLoginState.None => new WaitReason.NoClaudeLogin(Expired: false),
        ClaudeLoginState.Expired => new WaitReason.NoClaudeLogin(Expired: true),
        _ => null,
    };

    /// <summary>
    /// The project's situation for <paramref name="workerId"/> at <paramref name="now"/>, in one
    /// transaction as the project's user, from the fragments each gate is kept in:
    /// <see cref="Pauses.PauseSql"/>, the daily budget by <see cref="Usage.DailyBudget.ReachedIn"/>, the schedules by <see cref="Schedules.ScheduleStore.StateIn"/>, <see cref="Workers.WorkerStore.RebuildSql"/> with <paramref name="image"/>,
    /// <see cref="WatchStore.HoldSql"/>, the project's wait, the worker's setup version by
    /// <see cref="Setup.SetupStore"/>'s checks, the setup's and the cards' next kind, the user's
    /// Claude login and whether any worker was seen within <see cref="RunStore.Silence"/>. Null when
    /// the worker has not said hello in the project, or the user has no such project.
    /// </summary>
    public static Task<ProjectSituation?> Read(UserDb database, Guid userId, Guid projectId, Guid workerId, string image, DateTimeOffset now, CancellationToken cancellationToken) =>
        Read(database, userId, projectId, (Guid?)workerId, image, now, cancellationToken);

    /// <summary>
    /// The project's situation as the page reads it, with no worker asking: as the worker likeliest
    /// to take its next job would find it, one seen lately before one not, of those one that needs
    /// no rebuild by <see cref="Workers.WorkerStore.RebuildSql"/>, then the one seen last; or, with
    /// no worker at all, one built from the project's recipe as last read. Null when the user has no
    /// such project. See "Running now" under "Jobs" in devflow/README.md.
    /// </summary>
    public static Task<ProjectSituation?> ReadForPage(UserDb database, Guid userId, Guid projectId, string image, DateTimeOffset now, CancellationToken cancellationToken) =>
        Read(database, userId, projectId, null, image, now, cancellationToken);

    /// <summary>The setup version the rule judges, as SQL: the worker's, or with none the
    /// project's recipe as last read, or none.</summary>
    private const string VersionSql = $"coalesce(w.recipe_version, p.recipe_version, '{Protocol.WorkerApi.NoVersion}')";

    private static async Task<ProjectSituation?> Read(UserDb database, Guid userId, Guid projectId, Guid? workerId, string image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var (row, schedule, budget, zone, limited) = await database.InUserTransaction(userId, async transaction =>
        {
            // The dispatcher ends every override with nothing left to run before it reads anything,
            // so one that ended never covers a card added or sent back later; the page only reads.
            if (workerId is not null)
            {
                await RunNowStore.EndIn(transaction, projectId, cancellationToken);
            }

            var row = await transaction.Connection!.QuerySingleOrDefaultAsync<SituationRow>(new CommandDefinition(
                $"""
                SELECT p.name AS Project,
                       {Pauses.PauseSql} AS Pause,
                       {Workers.WorkerStore.RebuildSql},
                       {WatchStore.HoldSql} AS Hold,
                       pw.failures AS Failures, pw.reason AS Reason, pw.since AS Since, pw.wait_until AS WaitUntil, pw.until_reset AS UntilReset,
                       {VersionSql} AS Version,
                       {Setup.SetupStore.PassedSql(VersionSql)} AS Passed,
                       {Setup.SetupStore.DueSql(VersionSql)} AS Due,
                       {Setup.SetupStore.CheckingSql(VersionSql)} AS Checking,
                       {Setup.SetupRunStore.NextKindSql} AS SetupKind,
                       {JobStore.NextKindSql} AS NextKind,
                       {RunNowStore.AnySql} AS RunNow,
                       {JobStore.RunNowKindSql} AS RunNowKind,
                       (SELECT l.given_at FROM claude_logins l WHERE l.user_id = p.user_id) AS LoginGivenAt,
                       (SELECT ps.runs_on FROM project_settings ps WHERE ps.project_id = p.id) AS RunsOn,
                       EXISTS (SELECT 1 FROM workers s WHERE s.project_id = p.id AND s.last_seen_at >= @seenSince) AS WorkerSeen
                FROM projects p
                LEFT JOIN LATERAL (
                    SELECT w.* FROM workers w
                    CROSS JOIN LATERAL (SELECT {Workers.WorkerStore.RebuildSql}) rb
                    WHERE w.project_id = p.id AND (@workerId::uuid IS NULL OR w.id = @workerId)
                    ORDER BY w.last_seen_at >= @seenSince DESC, rb.rebuild IS NULL DESC, w.last_seen_at DESC, w.id
                    LIMIT 1) w ON true
                LEFT JOIN project_waits pw ON pw.project_id = p.id
                WHERE p.id = @projectId AND (@workerId::uuid IS NULL OR w.id IS NOT NULL)
                """,
                new { projectId, workerId, image, watchedSince = WatchStore.WatchedSince(now), seenSince = now - RunStore.Silence },
                transaction,
                cancellationToken: cancellationToken));
            if (row is null)
            {
                return (row, null, null, TimeZoneInfo.Utc, null);
            }

            var limited = await Stops.SubscriptionLimits.UntilIn(transaction, now, cancellationToken);
            return ((SituationRow?)row, await Schedules.ScheduleStore.StateIn(transaction, projectId, now, cancellationToken), await Usage.DailyBudget.ReachedIn(transaction, projectId, now, cancellationToken),
                await DevFlowTime.ZoneOf(transaction, cancellationToken), limited);
        }, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var setup = row.Version == Protocol.WorkerApi.NoVersion ? SetupGate.NotSetUp
            : row.Passed ? SetupGate.Checked
            : row.Due ? SetupGate.CheckDue
            : row.Checking ? SetupGate.CheckRunning
            : SetupGate.CheckFailed;
        var login = row.LoginGivenAt is { } given ? ClaudeLoginStatus.At(Utc(given), now).State : ClaudeLoginState.None;
        var wait = row.Failures is { } failures
            ? new ProjectWait(failures, row.Reason!, Utc(row.Since!.Value), TimeZoneInfo.ConvertTime(Utc(row.WaitUntil!.Value), zone), row.UntilReset ?? false)
            : null;
        return new ProjectSituation(row.Project, row.Rebuild is not null, WatchStore.Read(row.Hold), wait, setup, row.SetupKind, login, row.WorkerSeen, row.NextKind, row.Version, now, Pauses.Read(row.Pause),
            schedule is null ? null : Schedules.Schedule.Reason(schedule), row.RunNow, row.RunNowKind, budget, row.RunsOn ?? Projects.RunsOn.Subscription,
            limited is { } until ? TimeZoneInfo.ConvertTime(until, zone) : null);
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed class SituationRow
    {
        public string Project { get; init; } = "";

        public string? Pause { get; init; }

        public string? Rebuild { get; init; }

        public string? Hold { get; init; }

        public int? Failures { get; init; }

        public string? Reason { get; init; }

        public DateTime? Since { get; init; }

        public DateTime? WaitUntil { get; init; }

        public bool? UntilReset { get; init; }

        public string Version { get; init; } = "";

        public bool Passed { get; init; }

        public bool Due { get; init; }

        public bool Checking { get; init; }

        public string? SetupKind { get; init; }

        public string? NextKind { get; init; }

        public bool RunNow { get; init; }

        public string? RunNowKind { get; init; }

        public DateTime? LoginGivenAt { get; init; }

        public string? RunsOn { get; init; }

        public bool WorkerSeen { get; init; }
    }

    /// <summary>
    /// Each card of the project waiting to run, and its decision under the project's own, read from
    /// <paramref name="situation"/>, in the order they are handed out: by the kind of job, a review, a fix, a
    /// reading and a build, as <see cref="JobStore.NextKind"/> orders them, each by
    /// <see cref="DevFlow.Cards.CardStore.QueueOrder"/>, then the cards that wait for another. A card is
    /// waiting when it is queued, reviewing or fixing with no run under way, and either one of
    /// those jobs is due for it, by the predicates handing it out reads,
    /// <see cref="JobStore.Reviewable"/>, <see cref="JobStore.Fixable"/>,
    /// <see cref="Readings.ReadingStore.Due"/> and <see cref="JobStore.Buildable"/>, or, queued, it
    /// waits for an open issue it is blocked by or the card before it in its feature,
    /// <see cref="DevFlow.Cards.CardStore.HeldBackBy"/>. One the rule has no reason for, such as one whose
    /// review is kept, is not listed. Each says whether an override of the schedules covers it,
    /// <see cref="RunNowStore.CoversCard"/>. Read in one transaction as the project's user, at the
    /// situation's time.
    /// </summary>
    public static async Task<IReadOnlyList<CardDecision>> Cards(UserDb database, Guid userId, Guid projectId, ProjectSituation situation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(situation);
        var project = Of(situation);
        var now = situation.Now;
        var parameters = new { projectId, watchedSince = WatchStore.WatchedSince(now), seenSince = now - RunStore.Silence };
        var (cards, busy) = await database.InUserTransaction(userId, async transaction =>
        {
            var rows = await transaction.Connection!.QueryAsync<WaitingCard>(new CommandDefinition(
                $"""
                SELECT c.number AS Number, c.kind AS Kind, c.waits_for AS WaitsFor, c.held_back_by AS HeldBackBy,
                       coalesce(c.held_for_answers, false) AS HeldForAnswers, {JobStore.SettingsAccepted} AS SettingsAccepted,
                       c.run_now AS RunNow
                FROM (
                    SELECT cards.number, cards.priority, cards.issue_created_at, {RunNowStore.CoversCard} AS run_now,
                           CASE WHEN {JobStore.Reviewable} THEN '{Protocol.Job.Review}'
                                WHEN {JobStore.Fixable} THEN '{Protocol.Job.Fix}'
                                WHEN {Readings.ReadingStore.Due} THEN '{Protocol.Job.Read}'
                                WHEN {JobStore.Buildable} THEN '{Protocol.Job.Build}' END AS kind,
                           CASE WHEN cards.state = 'queued' THEN cards.waits_for[1] END AS waits_for,
                           CASE WHEN cards.state = 'queued' AND cards.feature_id IS NOT NULL THEN {DevFlow.Cards.CardStore.HeldBackBy} END AS held_back_by,
                           (SELECT hb.state = 'needs_answers' FROM cards hb
                            WHERE hb.project_id = cards.project_id AND hb.number = {DevFlow.Cards.CardStore.HeldBackBy}) AS held_for_answers
                    FROM cards
                    WHERE cards.project_id = @projectId AND cards.state IN ('queued', 'reviewing', 'fixing')
                      AND NOT EXISTS (SELECT 1 FROM runs u WHERE u.project_id = cards.project_id AND u.card_number = cards.number AND u.finished_at IS NULL)
                ) c
                WHERE c.kind IS NOT NULL OR c.waits_for IS NOT NULL OR c.held_back_by IS NOT NULL
                ORDER BY CASE c.kind WHEN '{Protocol.Job.Review}' THEN 0 WHEN '{Protocol.Job.Fix}' THEN 1 WHEN '{Protocol.Job.Read}' THEN 2
                                     WHEN '{Protocol.Job.Build}' THEN 3 ELSE 4 END,
                         {DevFlow.Cards.CardStore.QueueOrder}
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            // Every worker seen lately is busy when none of them is free; the one whose run started
            // first is named, as the likeliest to be free next.
            var named = await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                """
                SELECT w.name FROM workers w
                JOIN runs r ON r.project_id = w.project_id AND r.worker_id = w.id AND r.finished_at IS NULL
                WHERE w.project_id = @projectId AND w.last_seen_at >= @seenSince
                  AND NOT EXISTS (SELECT 1 FROM workers f
                                  WHERE f.project_id = w.project_id AND f.last_seen_at >= @seenSince
                                    AND NOT EXISTS (SELECT 1 FROM runs fr WHERE fr.project_id = f.project_id AND fr.worker_id = f.id AND fr.finished_at IS NULL))
                ORDER BY r.started_at, r.id
                LIMIT 1
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            return (rows.ToList(), named);
        }, cancellationToken);
        return [.. cards.Select(card => new CardDecision(card.Number, ForCard(project, card, busy, situation.Outside), card.Kind))];
    }

    /// <summary>
    /// A waiting card's decision: the project's when it waits; <paramref name="outside"/>, the
    /// project's schedules, for a card no override covers while the project is outside them, since
    /// the project then runs only what the overrides cover; otherwise the card's own wait, for
    /// an open issue it is blocked by, then for the card before it in its feature, for the owner's
    /// answers when that one needs them; then, for a job due, the setup the project runs first, the
    /// settings step for a reading or a build, and <paramref name="busy"/>, the worker it is next for
    /// when every worker is busy; and otherwise it runs.
    /// </summary>
    public static Decision ForCard(Decision project, WaitingCard card, string? busy, WaitReason.OutsideSchedule? outside = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(card);
        if (project is Decision.Wait)
        {
            return project;
        }

        if (outside is not null && !card.RunNow)
        {
            return new Decision.Wait(outside);
        }

        if (card.WaitsFor is { } issue)
        {
            return new Decision.Wait(new WaitReason.WaitsForCard(issue));
        }

        if (card.HeldBackBy is { } before)
        {
            return new Decision.Wait(card.HeldForAnswers
                ? new WaitReason.WaitsForAnswers(before)
                : new WaitReason.WaitsForCard(string.Create(CultureInfo.InvariantCulture, $"#{before}")));
        }

        return (project, card.Kind) switch
        {
            (Decision.Run { Kind: Protocol.Job.RecipeCheck }, _) => new Decision.Wait(new WaitReason.NeedsSetup(Checking: true)),
            (Decision.Run { Kind: Protocol.Job.Setup }, _) => new Decision.Wait(new WaitReason.NeedsSetup(Checking: false)),
            (_, Protocol.Job.Read or Protocol.Job.Build) when !card.SettingsAccepted => new Decision.Wait(new WaitReason.NeedsSetup(Checking: false)),
            _ when busy is not null => new Decision.Wait(new WaitReason.WorkersBusy(busy)),
            (_, { } kind) => new Decision.Run(kind),
            _ => throw new ArgumentException("a waiting card with no job due and nothing it waits for", nameof(card)),
        };
    }
}

/// <summary>A card waiting to run, as <see cref="RunDecision.Cards"/> reads it.</summary>
public sealed class WaitingCard
{
    /// <summary>Its number.</summary>
    public int Number { get; init; }

    /// <summary>The kind of job due for it, null with none.</summary>
    public string? Kind { get; init; }

    /// <summary>The first open issue it is blocked by, for a queued card, null with none.</summary>
    public string? WaitsFor { get; init; }

    /// <summary>The card before it in its feature that holds it back, for a queued card, null with
    /// none.</summary>
    public int? HeldBackBy { get; init; }

    /// <summary>Whether the card holding it back needs the owner's answers.</summary>
    public bool HeldForAnswers { get; init; }

    /// <summary>Whether the project's settings step is done, before which no reading or build
    /// runs.</summary>
    public bool SettingsAccepted { get; init; }

    /// <summary>Whether an override of the schedules, its own or its feature's, covers it.</summary>
    public bool RunNow { get; init; }
}
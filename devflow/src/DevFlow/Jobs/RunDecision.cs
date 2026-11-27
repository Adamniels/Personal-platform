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

    /// <summary>The project's last failure outside its cards was Claude's usage limit, which resets
    /// at <paramref name="Until"/>.</summary>
    public sealed record UsageLimit(DateTimeOffset Until) : WaitReason
    {
        public override string Sentence => string.Create(CultureInfo.InvariantCulture,
            $"At Claude's usage limit until {Until.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
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
/// <param name="Login">The project's user's Claude login.</param>
/// <param name="WorkerSeen">Whether any worker of the project was seen in the last
/// <see cref="RunStore.Silence"/>.</param>
/// <param name="NextKind">The kind of the cards' next job, <see cref="JobStore.NextKind"/>'s, null
/// with none.</param>
/// <param name="Version">The worker's setup version.</param>
/// <param name="Now">When it was read.</param>
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
    DateTimeOffset Now);

/// <summary>A card's decision.</summary>
public sealed record CardDecision(int Number, Decision Decision);

/// <summary>
/// The one rule that decides whether a project's next job runs and, for each card waiting, why it
/// does not run yet, so what the page says and what the dispatcher does never disagree. See "Jobs"
/// in devflow/README.md and "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunDecision
{
    /// <summary>
    /// The project's decision for its situation, its gates in the order the dispatcher meets them:
    /// the worker asking needs a rebuild; the project is stopped, or its default branch not read
    /// lately; it waits after failures outside its cards, other than a usage limit; the worker's
    /// setup version has no check that passed, when a check due is what runs, since it runs no agent
    /// and needs no Claude login; no Claude login, or one past its renewal; Claude's usage limit; no
    /// worker of the project seen lately. Past them a worker built from no recipe runs the setup's
    /// next job, and any other the cards' next, or nothing when there is none.
    /// </summary>
    public static Decision Of(ProjectSituation situation)
    {
        ArgumentNullException.ThrowIfNull(situation);
        var wait = situation.Wait is { } counted && counted.WaitUntil > situation.Now ? counted : null;
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
                return new Decision.Run(Protocol.Job.RecipeCheck);
        }

        if (LoginReason(situation.Login) is { } login)
        {
            return new Decision.Wait(login);
        }

        if (wait is { UntilReset: true })
        {
            return new Decision.Wait(new WaitReason.UsageLimit(wait.WaitUntil));
        }

        if (!situation.WorkerSeen)
        {
            return new Decision.Wait(new WaitReason.NoWorker(situation.Project));
        }

        if (situation.Setup == SetupGate.NotSetUp)
        {
            return new Decision.Run(Protocol.Job.Setup);
        }

        return situation.NextKind is { } kind ? new Decision.Run(kind) : new Decision.Idle();
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
    /// query as the project's user, from the fragments each gate is kept in:
    /// <see cref="Workers.WorkerStore.RebuildSql"/> with <paramref name="image"/>,
    /// <see cref="WatchStore.HoldSql"/>, the project's wait, the worker's setup version by
    /// <see cref="Setup.SetupStore"/>'s checks, the setup's and the cards' next kind, the user's
    /// Claude login and whether any worker was seen within <see cref="RunStore.Silence"/>. Null when
    /// the worker has not said hello in the project, or the user has no such project.
    /// </summary>
    public static async Task<ProjectSituation?> Read(UserDb database, Guid userId, Guid projectId, Guid workerId, string image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<SituationRow>(new CommandDefinition(
            $"""
            SELECT p.name AS Project,
                   {Workers.WorkerStore.RebuildSql},
                   {WatchStore.HoldSql} AS Hold,
                   pw.failures AS Failures, pw.reason AS Reason, pw.since AS Since, pw.wait_until AS WaitUntil, pw.until_reset AS UntilReset,
                   w.recipe_version AS Version,
                   {Setup.SetupStore.PassedSql("w.recipe_version")} AS Passed,
                   {Setup.SetupStore.DueSql("w.recipe_version")} AS Due,
                   {Setup.SetupStore.CheckingSql("w.recipe_version")} AS Checking,
                   {Setup.SetupRunStore.NextKindSql} AS SetupKind,
                   {JobStore.NextKindSql} AS NextKind,
                   (SELECT l.given_at FROM claude_logins l WHERE l.user_id = p.user_id) AS LoginGivenAt,
                   EXISTS (SELECT 1 FROM workers s WHERE s.project_id = p.id AND s.last_seen_at >= @seenSince) AS WorkerSeen
            FROM projects p
            JOIN workers w ON w.project_id = p.id AND w.id = @workerId
            LEFT JOIN project_waits pw ON pw.project_id = p.id
            WHERE p.id = @projectId
            """,
            new { projectId, workerId, image, watchedSince = WatchStore.WatchedSince(now), seenSince = now - RunStore.Silence },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
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
            ? new ProjectWait(failures, row.Reason!, Utc(row.Since!.Value), Utc(row.WaitUntil!.Value), row.UntilReset ?? false)
            : null;
        return new ProjectSituation(row.Project, row.Rebuild is not null, WatchStore.Read(row.Hold), wait, setup, row.SetupKind, login, row.WorkerSeen, row.NextKind, row.Version, now);
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed class SituationRow
    {
        public string Project { get; init; } = "";

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

        public DateTime? LoginGivenAt { get; init; }

        public bool WorkerSeen { get; init; }
    }

    /// <summary>Each card waiting to run and its decision, in the order they are handed out.</summary>
    public static Task<IReadOnlyList<CardDecision>> Cards(UserDb database, Guid userId, Guid projectId, Decision project, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
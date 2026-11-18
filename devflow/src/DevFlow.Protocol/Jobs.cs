using System.Globalization;
using System.Text.Json;

namespace DevFlow.Protocol;

/// <summary>
/// A job, what <c>jobs/next</c> answers a worker with <c>200</c>: one card of its project to build,
/// to review or to fix, with everything the worker needs for it and nothing more. The Claude token and the
/// key leave Dev flow only here, to the worker holding the job. See "Jobs", "Review" and
/// "Fixing" in devflow/README.md.
/// </summary>
/// <param name="JobId">The run's id, which the job's other routes take.</param>
/// <param name="Kind"><see cref="Build"/>, <see cref="Review"/> or <see cref="Fix"/>.</param>
/// <param name="Card">The card, as it was picked.</param>
/// <param name="Repository">Where the code is.</param>
/// <param name="Branch">The branch the work goes on, <c>devflow/&lt;number&gt;-&lt;slug&gt;</c>.</param>
/// <param name="Base">The branch it is made from, the repository's default branch.</param>
/// <param name="Brief">What the agent is asked to do, as text.</param>
/// <param name="ReportSchema">The JSON Schema the agent's answer follows: for a build the report's,
/// contracts/devflow/report.schema.json, and for a review the verdict's,
/// contracts/devflow/verdict.schema.json.</param>
/// <param name="Check">The command that checks the working copy, from the project's
/// <c>.devflow/project.json</c> on its default branch; for a review, the one the build ran.</param>
/// <param name="ClaudeToken">The run's user's Claude token, for the agent's
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c> and nowhere else.</param>
/// <param name="PushKey">The key git is given for the repository: for a build one that fetches and
/// pushes its code and does nothing else; for a review one that only fetches it.</param>
/// <param name="PushKeyExpiresAt">When the key stops working; for a build a fresh one is asked
/// for at <see cref="WorkerApi.PushKeyPath"/>, and a review asks for none.</param>
/// <param name="Committer">Who the worker's commits are made as; a review makes none.</param>
/// <param name="Limits">When the run is stopped.</param>
/// <param name="ToReview">What a review reads besides the code, null on a build or a fix.</param>
/// <param name="ToFix">Where a fix starts and which round it is, null on a build or a review.</param>
public sealed record Job(
    Guid JobId,
    string Kind,
    JobCard Card,
    JobRepository Repository,
    string Branch,
    string Base,
    string Brief,
    JsonElement ReportSchema,
    string Check,
    string ClaudeToken,
    string PushKey,
    DateTimeOffset PushKeyExpiresAt,
    JobCommitter Committer,
    JobLimits Limits,
    JobReview? ToReview = null,
    JobFix? ToFix = null)
{
    /// <summary>Building a card.</summary>
    public const string Build = "build";

    /// <summary>Reviewing the commit a build left on a card's pull request.</summary>
    public const string Review = "review";

    /// <summary>Fixing a card on its branch, from its head, after a review asked for changes or the
    /// owner commented on its pull request.</summary>
    public const string Fix = "fix";

    /// <summary>Its kind and card, never the token or the key.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Job {{ JobId = {JobId}, Kind = {Kind}, Card = {Card.Number}, Branch = {Branch} }}");
}

/// <summary>
/// What a review job reads besides the code: the commit it reviews, which a build left on the
/// card's pull request and which is the one the verdict is for, the report of the run that made
/// it, and the check's result on it.
/// </summary>
/// <param name="Commit">The full id of the commit to review.</param>
/// <param name="Report">The build's report as it came, in its form.</param>
/// <param name="Check">The check's result on the commit, as the build's worker sent it.</param>
public sealed record JobReview(string Commit, JsonElement Report, CheckResult Check);

/// <summary>
/// Where a fix job starts: the commit the card's branch must be at, the newest one a build or a fix
/// of the card left there, which the worker checks before anything runs, and the round.
/// </summary>
/// <param name="Commit">The full id of the commit the branch is at, which the fix builds on.</param>
/// <param name="Round">The fix round, 1 to 3, or null when the owner's comment started it, which
/// is not a round.</param>
public sealed record JobFix(string Commit, int? Round);

/// <summary>The card a job builds or reviews, as it was picked; its title and body are the
/// owner's words.</summary>
public sealed record JobCard(int Number, string Title, string Body);

/// <summary>The repository a job's code is in.</summary>
/// <param name="CloneAddress">Where git fetches and pushes it, with the push key.</param>
/// <param name="DefaultBranch">Its default branch, which the job's branch is made from.</param>
public sealed record JobRepository(string Owner, string Name, Uri CloneAddress, string DefaultBranch);

/// <summary>Who a worker's commits are made as: the app's bot and its no-reply address.</summary>
public sealed record JobCommitter(string Name, string Email);

/// <summary>When a run is stopped, fixed in code until a project's settings hold them.</summary>
/// <param name="Turns">The most turns.</param>
/// <param name="BudgetUsd">The most it may cost, in US dollars.</param>
/// <param name="Minutes">The longest it may take.</param>
public sealed record JobLimits(int Turns, decimal BudgetUsd, int Minutes)
{
    public static JobLimits Default { get; } = new(200, 25m, 90);
}

/// <summary>What a worker sends to ask for a fresh push key for the job it holds.</summary>
/// <param name="WorkerId">The id it said hello with.</param>
public sealed record PushKeyRequest(Guid? WorkerId);

/// <summary>A fresh push key for a running job.</summary>
public sealed record PushKeyAnswer(string PushKey, DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"PushKeyAnswer {{ ExpiresAt = {ExpiresAt:O} }}");
}

/// <summary>What a worker sends when its job moves on.</summary>
/// <param name="WorkerId">The id it said hello with.</param>
/// <param name="State"><see cref="Checking"/>, the one state a worker reports: the agent has
/// finished and the check runs.</param>
public sealed record ProgressRequest(Guid? WorkerId, string? State)
{
    public const string Checking = "checking";
}

/// <summary>
/// What a worker sends when its job has ended, whatever happened. Each field is nullable so Dev
/// flow answers one that is missing with <c>422</c> naming it; which are required is
/// "Jobs" in devflow/README.md.
/// </summary>
/// <param name="WorkerId">The id it said hello with.</param>
/// <param name="Outcome"><see cref="Succeeded"/> when the agent answered a report, or
/// <see cref="Failed"/> when it did not.</param>
/// <param name="Report">The agent's report as it came, on a success; Dev flow checks it against
/// its schema.</param>
/// <param name="Failure">Why the run failed, and whether that lies in the card, on a failure.</param>
/// <param name="Check">The check's result, when it ran.</param>
/// <param name="Branch">The branch the work was pushed to.</param>
/// <param name="HeadCommit">The commit the branch was at after the push, null when nothing was
/// pushed.</param>
/// <param name="AuthMethod">How Claude Code said it was logged in, null when it said nothing.</param>
/// <param name="Usage">Claude's usage as it came, an object, empty when it gave none.</param>
/// <param name="DurationMs">How long the job took, in milliseconds.</param>
/// <param name="Stderr">The last 200 lines of the agent's standard error.</param>
/// <param name="PermissionDenials">The actions the agent was refused, as Claude Code listed them,
/// an array; empty when there were none, and null from a worker that sends none. A build's fail
/// its run; a review's are recorded, since a reviewer cannot write by construction.</param>
public sealed record ResultRequest(
    Guid? WorkerId,
    string? Outcome,
    JsonElement? Report,
    RunFailure? Failure,
    CheckResult? Check,
    string? Branch,
    string? HeadCommit,
    string? AuthMethod,
    JsonElement? Usage,
    long? DurationMs,
    string? Stderr,
    JsonElement? PermissionDenials = null)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>Why a run failed.</summary>
/// <param name="Reason">A sentence.</param>
/// <param name="InCard">Whether the card's own work caused it, rather than the login, the API or
/// the machine.</param>
public sealed record RunFailure(string? Reason, bool? InCard);

/// <summary>What the project's check did.</summary>
/// <param name="ExitCode">Its exit code, <c>0</c> when it passed.</param>
/// <param name="Output">The last 200 lines of its output.</param>
public sealed record CheckResult(int? ExitCode, string? Output);
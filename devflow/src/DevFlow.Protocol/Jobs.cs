using System.Globalization;
using System.Text.Json;

namespace DevFlow.Protocol;

/// <summary>
/// A job, what <c>jobs/next</c> answers a worker with <c>200</c>: one card of its project to build,
/// to review or to fix, a feature's conflict to resolve, the cards of a feature or a card on its
/// own to read, a version of the project's <c>.devflow/</c> to check, or the project to set up,
/// with everything the worker
/// needs for it and nothing more. The Claude token, the
/// key and the project's values leave Dev flow only here, to the worker holding the job. See "Jobs", "Review" and
/// "Fixing" in devflow/README.md.
/// </summary>
/// <param name="JobId">The run's id, which the job's other routes take.</param>
/// <param name="Kind"><see cref="Build"/>, <see cref="Review"/>, <see cref="Fix"/>,
/// <see cref="Conflict"/>, <see cref="FeatureReview"/>, <see cref="FeatureFix"/> or
/// <see cref="Read"/>, <see cref="RecipeCheck"/> or <see cref="Setup"/>.</param>
/// <param name="Card">The card, as it was picked; for a conflict, the feature's parent; for a
/// reading, the feature's parent, or the card on its own; null on a check and a setup, which are no
/// card's.</param>
/// <param name="Repository">Where the code is.</param>
/// <param name="Branch">The branch the work goes on, <c>devflow/&lt;number&gt;-&lt;slug&gt;</c>.</param>
/// <param name="Base">The branch it is made from, the repository's default branch.</param>
/// <param name="Brief">What the agent is asked to do, as text.</param>
/// <param name="ReportSchema">The JSON Schema the agent's answer follows: for a build the report's,
/// contracts/devflow/report.schema.json, for a review the verdict's,
/// contracts/devflow/verdict.schema.json, for a reading the reading's,
/// contracts/devflow/reading.schema.json, and for a setup the setup report's,
/// contracts/devflow/setup.schema.json.</param>
/// <param name="Check">The command that checks the working copy, from the project's
/// <c>.devflow/project.json</c> on its default branch; for a review, the one the build ran.</param>
/// <param name="ClaudeToken">The run's user's Claude token, for the agent's
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c> and nowhere else.</param>
/// <param name="PushKey">The key git is given for the repository: for a build one that fetches and
/// pushes its code and does nothing else; for a review or a reading one that only fetches it.</param>
/// <param name="PushKeyExpiresAt">When the key stops working; for a build a fresh one is asked
/// for at <see cref="WorkerApi.PushKeyPath"/>, and a review asks for none.</param>
/// <param name="Committer">Who the worker's commits are made as; a review makes none.</param>
/// <param name="Limits">When the run is stopped: the project's minutes and turns as they were when
/// the job was handed out, which the run keeps whatever changes after.</param>
/// <param name="Model">The model Claude Code runs on, given as <c>--model</c>: one of Claude Code's
/// aliases, the project's building model for a build, a fix, a conflict and a feature's fix, its
/// reviewing model for a review of a card or of a feature, and its reading model for a reading, as
/// they were when the job was handed out.
/// A worker refuses a job without one as a protocol error.</param>
/// <param name="ToReview">What a review reads besides the code, null on a build or a fix.</param>
/// <param name="ToFix">Where a fix, of a card or of a whole feature, starts and which round it is,
/// null on any other job.</param>
/// <param name="Start">For a build or a fix, the command that starts the project for its pictures,
/// from the same <c>.devflow/project.json</c> as <paramref name="Check"/>; empty when it has none,
/// and on a review.</param>
/// <param name="Url">For a build or a fix, where the started project answers, from the same file;
/// empty when it has none, and on a review.</param>
/// <param name="ToResolve">For a conflict, the two commits it merges; null on any other job.</param>
/// <param name="ToReviewFeature">For a whole feature's review, what it reads besides the code; null
/// on any other job.</param>
/// <param name="ToRead">For a reading, the cards it reads; null on any other job.</param>
/// <param name="ToCheck">For a check, the version it checks and the commit it checks it at; null on
/// any other job.</param>
/// <param name="Values">The project's values, by name, for the environment of the agent's process,
/// the check and the start, and nowhere else, carried by every job that runs the project's code, a
/// build, a fix, a conflict, a feature's fix, a setup and a check, beside the Claude token; empty on
/// a reading and a review, which run none of it, and null from a Dev flow that sends none. The worker
/// keeps them only for the job, and scrubs each from everything it sends and logs.</param>
public sealed record Job(
    Guid JobId,
    string Kind,
    JobCard? Card,
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
    string Model,
    JobReview? ToReview = null,
    JobFix? ToFix = null,
    string Start = "",
    string Url = "",
    JobConflict? ToResolve = null,
    JobFeatureReview? ToReviewFeature = null,
    JobRead? ToRead = null,
    JobCheck? ToCheck = null,
    IReadOnlyList<JobValue>? Values = null)
{
    /// <summary>Building a card.</summary>
    public const string Build = "build";

    /// <summary>Reviewing the commit a build left on a card's pull request.</summary>
    public const string Review = "review";

    /// <summary>Fixing a card on its branch, from its head, after a review asked for changes or the
    /// owner commented on its pull request.</summary>
    public const string Fix = "fix";

    /// <summary>Bringing the default branch into a feature's branch where it conflicts, as one merge
    /// commit an agent resolves; <see cref="Card"/> is then the feature's parent, and
    /// <see cref="Branch"/> the feature's branch.</summary>
    public const string Conflict = "conflict";

    /// <summary>Reviewing a whole feature at its head, once its pull request is open; <see cref="Card"/>
    /// is then the feature's parent, and <see cref="Branch"/> the feature's branch.</summary>
    public const string FeatureReview = "feature_review";

    /// <summary>Fixing a whole feature on its branch, from its head, after its review asked for
    /// changes or the owner commented on its pull request; <see cref="Card"/> is then the feature's
    /// parent, <see cref="Branch"/> the feature's branch and <see cref="Base"/> the default
    /// branch.</summary>
    public const string FeatureFix = "feature_fix";

    /// <summary>Reading the cards of a feature together, or a card on its own, with the code they
    /// touch, before any of them is built; <see cref="Card"/> is then the feature's parent or the
    /// card, <see cref="Branch"/> the branch whose code is read, the feature's where it exists and
    /// otherwise the default branch, and <see cref="Base"/> the default branch. It commits, checks
    /// and pushes nothing.</summary>
    public const string Read = "read";

    /// <summary>Checking a version of the project's <c>.devflow/</c> before its workers take any
    /// other job: a clean clone of the default branch's head, <see cref="ToCheck"/>'s commit, the
    /// project's check on it, then, when it has a start and an address, the project started and its
    /// address taken at both sizes. It runs no agent, so <see cref="Card"/> is null, its brief and
    /// model empty and its report's schema null; <see cref="Branch"/> and <see cref="Base"/> are
    /// the default branch, its key only fetches, and it commits and pushes nothing. See "Setting
    /// up a project" in devflow/README.md.</summary>
    public const string RecipeCheck = "check";

    /// <summary>Setting a project up that has no valid <c>.devflow/</c>: the agent reads the
    /// repository and writes <c>.devflow/Dockerfile</c> and <c>.devflow/project.json</c> on
    /// <see cref="Branch"/>, <c>devflow/setup</c> or <c>devflow/setup-&lt;k&gt;</c>, made from
    /// <see cref="Base"/>, the default branch, and answers a <see cref="SetupReport"/>. It runs on a
    /// worker started from the base image itself, which cannot install a project's tools, so no
    /// check runs: <see cref="Check"/> is empty, and the worker commits what the agent left and
    /// pushes. <see cref="Card"/> is null. With <see cref="ToFix"/> it is a fix of the setup's pull
    /// request, from that commit. See "Setting up a project" in devflow/README.md.</summary>
    public const string Setup = "setup";

    /// <summary>Its kind and card, never the token, the key or a value.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Job {{ JobId = {JobId}, Kind = {Kind}, Card = {Card?.Number}, Branch = {Branch} }}");
}

/// <summary>
/// What a review job reads besides the code: the commit it reviews, which a build left on the
/// card's pull request and which is the one the verdict is for, the report of the run that made
/// it, and the check's result on it.
/// </summary>
/// <param name="Commit">The full id of the commit to review.</param>
/// <param name="Report">The build's report as it came, in its form.</param>
/// <param name="Check">The check's result on the commit, as the build's worker sent it.</param>
/// <param name="Earlier">Each earlier round of the card, oldest first, empty on its first review.</param>
/// <param name="Screenshots">The pictures the worker took of the commit after its check passed, on
/// <see cref="PageShots.Branch"/> under <see cref="PageShots.Folder"/>, and its note; null when it
/// tried none.</param>
public sealed record JobReview(string Commit, JsonElement Report, CheckResult Check, IReadOnlyList<JobRound> Earlier, ResultScreenshots? Screenshots = null);

/// <summary>
/// An earlier round of a card, as a re-review reads it: what sent the card back, the review that
/// asked for changes or the owner's comment, the developer's reply to each finding, and the commit
/// its fix left. Every text is the reviewer's, the owner's or the developer's, and data.
/// </summary>
/// <param name="Round">The fix round, 1 to the project's fix rounds, or null when the owner's comment started it.</param>
/// <param name="ReviewedCommit">The commit the review it answered was for, null when it answered
/// none.</param>
/// <param name="Verdict">That review's verdict, null when it answered none.</param>
/// <param name="Summary">That review's summary, null when it answered none.</param>
/// <param name="Findings">That review's findings, as the verdict's form holds them; empty when it
/// answered none.</param>
/// <param name="Comments">The owner's comments it answered, each exactly as he wrote it.</param>
/// <param name="Replies">The fix's replies, as its report holds them.</param>
/// <param name="FixedCommit">The commit the fix left on the branch.</param>
public sealed record JobRound(
    int? Round,
    string? ReviewedCommit,
    string? Verdict,
    string? Summary,
    JsonElement Findings,
    IReadOnlyList<string> Comments,
    JsonElement Replies,
    string FixedCommit);

/// <summary>
/// Where a fix job starts: the commit the card's branch must be at, the newest one a build or a fix
/// of the card left there, or for a whole feature's fix the commit the feature's watch last named,
/// which the worker checks before anything runs, and the round.
/// </summary>
/// <param name="Commit">The full id of the commit the branch is at, which the fix builds on.</param>
/// <param name="Round">The fix round, 1 to the project's fix rounds, or null when the owner's comment started it, which
/// is not a round.</param>
public sealed record JobFix(string Commit, int? Round);

/// <summary>
/// Where a conflict job starts and what it brings in: the commit the feature's branch must be at,
/// which the worker checks before anything runs, and the default branch's commit, which the agent
/// finds as <c>origin/&lt;default&gt;</c>. The merge commit it hands back has exactly these two as
/// its parents, in this order.
/// </summary>
/// <param name="FeatureCommit">The full id of the commit the feature's branch is at.</param>
/// <param name="DefaultCommit">The full id of the default branch's commit to bring in.</param>
public sealed record JobConflict(string FeatureCommit, string DefaultCommit);

/// <summary>
/// What a whole feature's review reads besides the code: the commit it reviews, the feature
/// branch's head as Dev flow's watch last named it, which the verdict is for; each card of the
/// feature in its order; the newest check Dev flow holds on the feature's branch; and the remerge
/// diff of each conflict an agent resolved since the feature's last review. The diff from the merge
/// base the worker makes itself. Every text but the commits is an agent's, the owner's or the
/// check's, and data.
/// </summary>
/// <param name="Commit">The full id of the commit to review.</param>
/// <param name="Cards">The feature's cards, in its order.</param>
/// <param name="Check">The newest check a worker ran on the feature's branch, and on which commit;
/// null when Dev flow holds none.</param>
/// <param name="RemergeDiff">The remerge diffs of the conflicts resolved since the feature's last
/// review, each under a line naming its run, cut as <see cref="RemergeDiffs.Cut"/> cuts one; null
/// when there is none.</param>
/// <param name="Earlier">Each earlier fix round of the whole feature, oldest first, as a card's
/// re-review reads its rounds; empty on its first review, and null from a Dev flow that sends
/// none.</param>
public sealed record JobFeatureReview(string Commit, IReadOnlyList<JobFeatureCard> Cards, JobFeatureCheck? Check, string? RemergeDiff, IReadOnlyList<JobRound>? Earlier = null);

/// <summary>A card of a feature, as its whole review reads it: its number and title as picked, its
/// latest review's verdict and summary, each null when it has none, and the handover note of its
/// newest build or fix that succeeded, empty when it left none.</summary>
public sealed record JobFeatureCard(int Number, string Title, string? Verdict, string? Summary, string Handover);

/// <summary>A check a worker ran on a commit of a feature's branch, as Dev flow recorded it: the
/// command, the commit, its exit code and the last lines it printed.</summary>
public sealed record JobFeatureCheck(string Command, string Commit, int ExitCode, string Output);

/// <summary>What a reading reads besides the code: each card it is to answer for, in its feature's
/// order, as it was picked. Its brief quotes them too; the cards here are what the reading must
/// name, each exactly once.</summary>
/// <param name="Cards">The cards to read, each with its title and body as picked.</param>
public sealed record JobRead(IReadOnlyList<JobCard> Cards);

/// <summary>What a check checks: the project's setup version, the newest commit of its default
/// branch that changed anything under <c>.devflow/</c>, which the worker asking was built from; and
/// the default branch's head as Dev flow read it when the check was handed out, which the worker
/// checks out exactly, so the check, its start and its address, which the job carries from that
/// branch's <c>project.json</c>, run on one known commit.</summary>
/// <param name="Version">The setup version, as the worker reported it.</param>
/// <param name="Commit">The full id of the commit to check.</param>
public sealed record JobCheck(string Version, string Commit);

/// <summary>The card a job builds or reviews, as it was picked, or the feature a conflict job or a
/// whole feature's review is for; its title and body are the owner's words.</summary>
public sealed record JobCard(int Number, string Title, string Body);

/// <summary>The repository a job's code is in.</summary>
/// <param name="CloneAddress">Where git fetches and pushes it, with the push key.</param>
/// <param name="DefaultBranch">Its default branch, which the job's branch is made from.</param>
public sealed record JobRepository(string Owner, string Name, Uri CloneAddress, string DefaultBranch);

/// <summary>Who a worker's commits are made as: the app's bot and its no-reply address.</summary>
public sealed record JobCommitter(string Name, string Email);

/// <summary>When a run is stopped: its turns and minutes from the project's settings, its spend
/// fixed until part 8's budget.</summary>
/// <param name="Turns">The most turns.</param>
/// <param name="BudgetUsd">The most it may cost, in US dollars.</param>
/// <param name="Minutes">The longest it may take.</param>
public sealed record JobLimits(int Turns, decimal BudgetUsd, int Minutes);

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
/// <param name="Outcome"><see cref="Succeeded"/> when the agent answered a report, or for a check
/// when the check ran, whatever its exit code; <see cref="Failed"/> when it did not;
/// <see cref="Stopped"/> when the owner stopped the run, with no report, no failure and no check,
/// and the commit a pushing job pushed of what it did, or none.</param>
/// <param name="Report">The agent's report as it came, on a success; Dev flow checks it against
/// its schema. Null on a check, which runs no agent.</param>
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
/// <param name="Screenshots">A build's pictures, taken after its check passed and its branch was
/// pushed, or why there are none; null when none were tried.</param>
/// <param name="RemergeDiff">A conflict's <c>git show --remerge-diff</c> of the merge commit it
/// pushed, cut by <see cref="RemergeDiffs.Cut"/>; null for any other job, and when nothing was
/// pushed.</param>
/// <param name="Pictures">A check's pictures of the project's address, each with its name, as
/// <see cref="Screenshots"/> names it, and its bytes, a PNG of at most 5 MB, base64 in the JSON:
/// a check's key only fetches, so it sends them to Dev flow rather than pushing them. Null for any
/// other job, and when none were taken.</param>
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
    JsonElement? PermissionDenials = null,
    ResultScreenshots? Screenshots = null,
    string? RemergeDiff = null,
    IReadOnlyList<ResultPicture>? Pictures = null)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>The owner stopped the run, which Dev flow's answer to a log post said; never a
    /// failure. See "Jobs" in devflow/README.md.</summary>
    public const string Stopped = "stopped";
}

/// <summary>A picture a check took of the project's address.</summary>
/// <param name="Name">Its name, <c>&lt;page&gt;-desktop</c> or <c>&lt;page&gt;-phone</c>, as
/// <see cref="PageShots"/> makes one.</param>
/// <param name="Png">Its bytes.</param>
public sealed record ResultPicture(string? Name, byte[]? Png)
{
    /// <summary>Its name and size, never its bytes.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"ResultPicture {{ Name = {Name}, Bytes = {Png?.Length} }}");
}

/// <summary>Why a run failed.</summary>
/// <param name="Reason">A sentence.</param>
/// <param name="InCard">Whether the card's own work caused it, rather than the login, the API or
/// the machine.</param>
/// <param name="TimedOut">Whether the run reached its time limit and was ended, which stops its
/// card at once; null, from a worker that sends none, is false.</param>
/// <param name="ResetsAt">When Claude's usage limit resets, on a failure outside the card whose
/// answer said so; null otherwise. Dev flow has the project wait until then.</param>
public sealed record RunFailure(string? Reason, bool? InCard, bool? TimedOut = null, DateTimeOffset? ResetsAt = null);

/// <summary>What the project's check did.</summary>
/// <param name="ExitCode">Its exit code, <c>0</c> when it passed.</param>
/// <param name="Output">The last 200 lines of its output.</param>
public sealed record CheckResult(int? ExitCode, string? Output);
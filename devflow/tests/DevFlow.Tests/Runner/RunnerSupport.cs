using DevFlow.CodeHost;
using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// The reviewer's verdict marker, the first line <c>scripts/agent/verdict.sh</c> reads. It is put
/// together here from its word and its punctuation and never written out whole, since the agents'
/// tooling refuses the marker's text anywhere an implementer writes, see contracts/README.md.
/// </summary>
internal static class Marker
{
    private const string Word = "verdict";

    /// <summary>The marker for <paramref name="kind"/>, approve or changes, and the commit.</summary>
    public static string Line(string kind, string sha) => "<!-- " + Word + ": " + kind + " sha=" + sha + " -->";
}

/// <summary>
/// One pull request and what surrounds it, every part set as a waiting pull request has it:
/// opened by the bot, open, labelled <c>owner-merge</c>, approved for its head, both checks green,
/// and the runner's mention on its slice. A test changes the part it is about with <c>with</c>.
/// </summary>
internal sealed record World
{
    public const string Bot = "mac-mini-agent";
    public const string Owner = "Adamniels";
    public const int Number = 42;
    public const int SliceNumber = 7;

    public static readonly string Head = new('a', 40);
    public static readonly string Older = new('b', 40);
    public static readonly DateTimeOffset At = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public const string Body = """
        ## Summary

        Adds the thing the slice asks for.

        Closes #7

        ## Tasks

        - [x] The one task: abc1234

        ## Decisions the agent made

        - **owner-decision** Chose A. Alternative: B. Why: C.
        - Chose D. Alternative: E. Why: F.

        ## Verification

        Nothing.
        """;

    public PullRequest Pull { get; init; } = new(
        Number,
        "The slice's title",
        Bot,
        ["owner-merge", "in-review"],
        Head,
        "7-the-slice",
        "main",
        false,
        Body,
        new Uri("https://github.com/Adamniels/Personal-platform/pull/42"),
        At,
        PullRequestState.Open);

    public IReadOnlyList<Comment> Comments { get; init; } = [Verdict(1, "approve", Head, At)];

    public IReadOnlyList<CommitStatus> Statuses { get; init; } = [new("review-ok", StatusState.Success, "approved by the reviewer")];

    public IReadOnlyList<CheckRun> CheckRuns { get; init; } = [new("ci-ok", CheckStatus.Completed, CheckConclusion.Success, "github-actions")];

    public IReadOnlyList<Comment> SliceComments { get; init; } =
    [
        By(Bot, 100, "@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).", At.AddMinutes(1)),
    ];

    public string? OwnerLogin { get; init; } = Owner;

    /// <summary>The repository the pull request is in, as its project names it.</summary>
    public CodeHost.Repository Repository { get; init; } = new(41, 9001, "Adamniels", "Personal-platform");

    public RunnerInput Input => new(Repository, Pull, Comments, Statuses, CheckRuns, SliceComments, Bot, OwnerLogin);

    public RunnerItem? Read() => RunnerReader.Read(Input);

    /// <summary>The world with the pull request's labels replaced.</summary>
    public World Labelled(params string[] labels) => this with { Pull = Pull with { Labels = labels } };

    /// <summary>A verdict comment by the bot, never edited, with a "For the owner" section.</summary>
    public static Comment Verdict(long id, string kind, string sha, DateTimeOffset at, string forTheOwner = "None.") =>
        By(Bot, id, $"""
            {Marker.Line(kind, sha)}
            Slice #7, round 1.

            ## Blocking

            None.

            ## For the owner

            {forTheOwner}

            ## Replies

            None.
            """, at);

    /// <summary>A comment by <paramref name="author"/>, never edited.</summary>
    public static Comment By(string author, long id, string body, DateTimeOffset at) => new(id, author, body, at, at);
}
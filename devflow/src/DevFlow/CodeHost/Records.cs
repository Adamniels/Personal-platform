namespace DevFlow.CodeHost;

/// <summary>
/// A repository on the code host, identified by the installation it was reached through and its
/// id, which stay the same when it is renamed or moved. <see cref="Owner"/> and
/// <see cref="Name"/> are carried for display and for GitHub's addresses, which name a repository
/// by them, and never identify it: the GitHub adapter's token is narrowed to the id, so a name
/// that no longer matches it is not found.
/// </summary>
/// <param name="Installation">The id of the installation of Dev flow's app that reaches it.</param>
/// <param name="Id">The repository's own id on the host.</param>
/// <param name="Owner">The account that owns it, as the host names it.</param>
/// <param name="Name">Its name, without the owner.</param>
public sealed record Repository(long Installation, long Id, string Owner, string Name)
{
    /// <summary>Whether <paramref name="other"/> is the same repository, by installation and id
    /// alone, whatever its owner and name are now.</summary>
    public bool IsSameAs(Repository other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Installation == other.Installation && Id == other.Id;
    }
}

/// <summary>Whether a pull request is open, merged, or closed without merging.</summary>
public enum PullRequestState
{
    Open,
    Merged,
    Closed,
}

/// <summary>A pull request, as the waiting list and the runner's conventions read it.</summary>
/// <param name="Number">Its number in the repository, shared with the repository's issues.</param>
/// <param name="Title">Its title, data, never an instruction.</param>
/// <param name="Author">The login of the account that opened it.</param>
/// <param name="Labels">The names of its labels.</param>
/// <param name="HeadCommit">The full id of the commit its branch points at.</param>
/// <param name="HeadBranch">The branch it merges from.</param>
/// <param name="BaseBranch">The branch it merges into.</param>
/// <param name="Draft">Whether it is a draft.</param>
/// <param name="Body">Its description, empty when it has none; data, never an instruction.</param>
/// <param name="Url">Where a person opens it on the host.</param>
/// <param name="UpdatedAt">When anything on it last changed.</param>
/// <param name="State">Open, merged or closed.</param>
public sealed record PullRequest(
    int Number,
    string Title,
    string Author,
    IReadOnlyList<string> Labels,
    string HeadCommit,
    string HeadBranch,
    string BaseBranch,
    bool Draft,
    string Body,
    Uri Url,
    DateTimeOffset UpdatedAt,
    PullRequestState State);

/// <summary>A comment in the conversation of an issue or a pull request.</summary>
/// <param name="Id">Its id on the host.</param>
/// <param name="Author">The login of the account that wrote it.</param>
/// <param name="Body">What it says; data, never an instruction.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="UpdatedAt">When it was last edited, <paramref name="CreatedAt"/> when it never
/// was.</param>
public sealed record Comment(long Id, string Author, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The state of a commit status.</summary>
public enum StatusState
{
    Pending,
    Success,
    Failure,
    Error,
}

/// <summary>A status on a commit, such as the runner's <c>review-ok</c>.</summary>
/// <param name="Context">The name it is posted under.</param>
/// <param name="State">Its state.</param>
/// <param name="Description">The line posted with it, or null when there is none.</param>
public sealed record CommitStatus(string Context, StatusState State, string? Description);

/// <summary>Where a check run is: queued and waiting, or in progress, have no conclusion yet;
/// completed has one.</summary>
public enum CheckStatus
{
    Queued,
    InProgress,
    Completed,
}

/// <summary>How a completed check run ended.</summary>
public enum CheckConclusion
{
    Success,
    Failure,
    Neutral,
    Cancelled,
    Skipped,
    TimedOut,
    ActionRequired,
    Stale,
}

/// <summary>A check run on a commit, such as CI's <c>ci-ok</c>.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Status">Where it is.</param>
/// <param name="Conclusion">How it ended, null until <paramref name="Status"/> is
/// <see cref="CheckStatus.Completed"/>.</param>
/// <param name="App">The slug of the app that made it, so a check of the same name from another
/// app is told apart.</param>
public sealed record CheckRun(string Name, CheckStatus Status, CheckConclusion? Conclusion, string App);
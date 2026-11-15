namespace DevFlow.CodeHost;

/// <summary>
/// Everything Dev flow asks of a code host, GitHub the only one built. It names things as the
/// development flow's design does, a repository, a pull request, a comment, never as GitHub's API
/// does, so a second host can sit behind it. Two implementations: <see cref="FakeCodeHost"/>, in
/// memory, which every test and the runner's stack use, and <see cref="GitHub.GitHubCodeHost"/>,
/// which is tested on its own against recorded answers. Every operation answers
/// its value or one of the two failures in <see cref="CodeHostResult{T}"/>; nothing is thrown for
/// an expected condition. See "The code host seam" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// Every operation takes the platform account it is asked for. The fake keeps a world per account,
/// so tests registering their own accounts never see each other's; the GitHub adapter reads as
/// the app and writes, in later slices, as the account's linked GitHub user.
/// </remarks>
public interface ICodeHost
{
    /// <summary>The repository's open pull requests, in no particular order.</summary>
    Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken);

    /// <summary>One pull request, whatever its state.</summary>
    Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken);

    /// <summary>The conversation comments of the issue or pull request with the number, oldest
    /// first. Review comments on a pull request's code are not among them.</summary>
    Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken);

    /// <summary>The statuses posted on a commit, one per context, the latest of each.</summary>
    Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken);

    /// <summary>The check runs on a commit, the latest of each name and app.</summary>
    Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken);

    /// <summary>A file's bytes on a branch.</summary>
    Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken);
}

/// <summary>
/// What a code host operation answers: <see cref="Found"/> with the value, or one of the two
/// failures every caller handles, <see cref="NotFound"/> and <see cref="Unavailable"/>. A closed
/// set: the constructor is private, so a <c>switch</c> over the three is complete.
/// </summary>
public abstract record CodeHostResult<T>
{
    private CodeHostResult()
    {
    }

    /// <summary>The value asked for.</summary>
    public sealed record Found(T Value) : CodeHostResult<T>;

    /// <summary>What was asked for does not exist, or the host does not let Dev flow see it, which
    /// a host does not tell apart: the repository, the pull request, the commit or the file.</summary>
    public sealed record NotFound : CodeHostResult<T>;

    /// <summary>The host did not answer, or answered that it cannot now, such as for a rate
    /// limit. <paramref name="RetryAt"/> is when to try again, when the host said; null when it
    /// did not.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : CodeHostResult<T>;
}
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
/// so tests registering their own accounts never see each other's. The GitHub adapter reads as
/// the app with keys that can only read, and writes along two typed paths: what the owner does
/// from the page as his linked GitHub user, with a <see cref="UserAccessToken"/>, and the
/// program's own writes as the app, with an <see cref="AppWriteToken"/> narrowed to one purpose.
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

    /// <summary>The installations of Dev flow's app that the person whose token
    /// <paramref name="userToken"/> is can reach, each with the account it is on, in no particular
    /// order. Read as the person, never as the app, so what is listed is what they may
    /// connect.</summary>
    Task<CodeHostResult<IReadOnlyList<Installation>>> Installations(Guid userId, string userToken, CancellationToken cancellationToken);

    /// <summary>The repositories of the <paramref name="installation"/> that the person whose token
    /// <paramref name="userToken"/> is can reach, in no particular order. Not found when the
    /// installation is not one they can reach.</summary>
    Task<CodeHostResult<IReadOnlyList<Repository>>> Repositories(Guid userId, string userToken, long installation, CancellationToken cancellationToken);

    /// <summary>
    /// Merges the pull request into its base with a merge commit, as the person whose token
    /// <paramref name="token"/> is, and only while its head is <paramref name="commit"/>: the host
    /// checks that, so a pull request that moved on since it was shown answers head moved and is
    /// not merged. Never as the app: the token's type is not one the app's credentials are.
    /// </summary>
    Task<MergeResult> Merge(Guid userId, UserAccessToken token, Repository repository, int number, string commit, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="body"/>, exactly, as a comment in the conversation of the
    /// pull request with the number, as the person whose token <paramref name="token"/> is. Never
    /// as the app.</summary>
    Task<CommentResult> Comment(Guid userId, UserAccessToken token, Repository repository, int number, string body, CancellationToken cancellationToken);

    /// <summary>
    /// A new key for the app to write to <paramref name="repository"/> for
    /// <paramref name="purpose"/> alone, never kept by the code host: each call makes its own, so
    /// a push key handed to one worker is never another's. Not found when the repository is not
    /// one the app reaches, or the installation has not granted what the purpose needs;
    /// unavailable for any other failure. The only way to an <see cref="AppWriteToken"/>.
    /// </summary>
    Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken);

    /// <summary>Where git fetches and pushes the repository: GitHub's <c>https</c> address, which
    /// a push key authorises, or for the fake a <c>file://</c> address of a bare repository of its
    /// own.</summary>
    Task<CodeHostResult<Uri>> CloneAddress(Guid userId, Repository repository, CancellationToken cancellationToken);

    /// <summary>Opens a pull request from <paramref name="headBranch"/> into <paramref name="baseBranch"/>, as
    /// the app, with a <see cref="WritePurpose.Post"/> key; answers it as the host made it.</summary>
    Task<WriteResult<PullRequest>> OpenPullRequest(Guid userId, AppWriteToken token, Repository repository, string headBranch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="body"/>, exactly, in the conversation of the issue or pull
    /// request with the number, as the app, with a <see cref="WritePurpose.Post"/> key.</summary>
    Task<WriteResult<Comment>> CommentOnIssue(Guid userId, AppWriteToken token, Repository repository, int number, string body, CancellationToken cancellationToken);

    /// <summary>Adds the label to the issue or pull request, as the app, with a
    /// <see cref="WritePurpose.Post"/> key; answers the names of its labels after.</summary>
    Task<WriteResult<IReadOnlyList<string>>> AddLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken);

    /// <summary>Takes the label off the issue or pull request, as the app, with a
    /// <see cref="WritePurpose.Post"/> key; answers the names of its labels after, and refused
    /// with <c>404</c> when it did not carry it.</summary>
    Task<WriteResult<IReadOnlyList<string>>> RemoveLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken);

    /// <summary>Opens an issue with the labels, as the app, with a
    /// <see cref="WritePurpose.Post"/> key.</summary>
    Task<WriteResult<Issue>> OpenIssue(Guid userId, AppWriteToken token, Repository repository, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    /// <summary>Merges the pull request with a merge commit, as the app, with a
    /// <see cref="WritePurpose.Merge"/> key, and only while its head is
    /// <paramref name="commit"/>, which the host checks; answers as <see cref="Merge"/> does.</summary>
    Task<MergeResult> MergeAsApp(Guid userId, AppWriteToken token, Repository repository, int number, string commit, CancellationToken cancellationToken);

    /// <summary>Where a person installs Dev flow's app on the code host: GitHub's page for the
    /// app, or for the fake a page of its own under <paramref name="origin"/>, the platform's
    /// public origin.</summary>
    Uri InstallAddress(Uri origin);
}

/// <summary>An installation of Dev flow's app, on one account.</summary>
/// <param name="Id">Its id on the host, which repositories are reached through.</param>
/// <param name="Account">The login of the account, a person or an organisation, it is
/// installed on.</param>
public sealed record Installation(long Id, string Account);

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
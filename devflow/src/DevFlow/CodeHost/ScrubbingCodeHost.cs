using DevFlow.Values;

namespace DevFlow.CodeHost;

/// <summary>
/// The code host every part of Dev flow is given, in front of the real one: every text it posts, a
/// pull request's title and body, a comment as the app or as the owner, an issue's title and body
/// and a bringing in's commit message, has each known value of the user's projects on that
/// repository scrubbed first, by <see cref="Protocol.ValueScrub"/>, the function that scrubs what a
/// worker sends too. In one place, so no poster, one added later included, can post a value an
/// agent wrote. Every read and every other write passes through unchanged. See "Setting up a
/// project" in devflow/README.md.
/// </summary>
internal sealed class ScrubbingCodeHost(ICodeHost inner, ProjectValues values) : ICodeHost
{
    /// <summary>The key the code host behind it is registered under.</summary>
    public const string Unscrubbed = "unscrubbed";

    public Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        inner.OpenPullRequests(userId, repository, cancellationToken);

    public Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.PullRequest(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.Comments(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        inner.Statuses(userId, repository, commit, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        inner.CheckRuns(userId, repository, commit, cancellationToken);

    public Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken) =>
        inner.File(userId, repository, path, branch, cancellationToken);

    public Task<CodeHostResult<FileAtCommit>> DefaultBranchFile(Guid userId, Repository repository, string path, CancellationToken cancellationToken) =>
        inner.DefaultBranchFile(userId, repository, path, cancellationToken);

    public Task<CodeHostResult<FileAtCommit>> FolderFile(Guid userId, Repository repository, string folder, string path, CancellationToken cancellationToken) =>
        inner.FolderFile(userId, repository, folder, path, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<Issue>>> LabelledIssues(Guid userId, Repository repository, string label, CancellationToken cancellationToken) =>
        inner.LabelledIssues(userId, repository, label, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<SubIssue>>> SubIssues(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.SubIssues(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<Dependency>>> BlockedBy(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.BlockedBy(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<IssueEdit>>> Edits(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.Edits(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<LabelEvent>>> LabelEvents(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.LabelEvents(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<string>> DefaultBranch(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        inner.DefaultBranch(userId, repository, cancellationToken);

    public Task<CodeHostResult<string>> BranchHead(Guid userId, Repository repository, string branch, CancellationToken cancellationToken) =>
        inner.BranchHead(userId, repository, branch, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<string>>> ChangedFiles(Guid userId, Repository repository, string from, string head, CancellationToken cancellationToken) =>
        inner.ChangedFiles(userId, repository, from, head, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<ChangedFile>>> PullRequestFiles(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        inner.PullRequestFiles(userId, repository, number, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<BranchActivity>>> Activity(Guid userId, Repository repository, string branch, long? since, CancellationToken cancellationToken) =>
        inner.Activity(userId, repository, branch, since, cancellationToken);

    public Task<CodeHostResult<Committer>> AppCommitter(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        inner.AppCommitter(userId, repository, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<Installation>>> Installations(Guid userId, string userToken, CancellationToken cancellationToken) =>
        inner.Installations(userId, userToken, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<Repository>>> Repositories(Guid userId, string userToken, long installation, CancellationToken cancellationToken) =>
        inner.Repositories(userId, userToken, installation, cancellationToken);

    public Task<MergeResult> Merge(Guid userId, UserAccessToken token, Repository repository, int number, string commit, CancellationToken cancellationToken) =>
        inner.Merge(userId, token, repository, number, commit, cancellationToken);

    public async Task<CommentResult> Comment(Guid userId, UserAccessToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.Comment(userId, token, repository, number, scrub.Scrub(body), cancellationToken);
    }

    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken) =>
        inner.ForWrite(userId, repository, purpose, cancellationToken);

    public Task<CodeHostResult<Uri>> CloneAddress(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        inner.CloneAddress(userId, repository, cancellationToken);

    public async Task<WriteResult<PullRequest>> OpenPullRequest(Guid userId, AppWriteToken token, Repository repository, string headBranch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.OpenPullRequest(userId, token, repository, headBranch, baseBranch, scrub.Scrub(title), scrub.Scrub(body), cancellationToken);
    }

    public async Task<WriteResult<PullRequest>> EditPullRequestBody(Guid userId, AppWriteToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.EditPullRequestBody(userId, token, repository, number, scrub.Scrub(body), cancellationToken);
    }

    public async Task<WriteResult<Comment>> CommentOnIssue(Guid userId, AppWriteToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.CommentOnIssue(userId, token, repository, number, scrub.Scrub(body), cancellationToken);
    }

    public Task<WriteResult<IReadOnlyList<string>>> AddLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken) =>
        inner.AddLabel(userId, token, repository, number, label, cancellationToken);

    public Task<UnlabelResult> Unlabel(Guid userId, UserAccessToken token, Repository repository, int number, string label, CancellationToken cancellationToken) =>
        inner.Unlabel(userId, token, repository, number, label, cancellationToken);

    public Task<WriteResult<IReadOnlyList<string>>> RemoveLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken) =>
        inner.RemoveLabel(userId, token, repository, number, label, cancellationToken);

    public Task<WriteResult<Issue>> SetIssueState(Guid userId, AppWriteToken token, Repository repository, int number, bool open, CancellationToken cancellationToken) =>
        inner.SetIssueState(userId, token, repository, number, open, cancellationToken);

    public async Task<WriteResult<Issue>> OpenIssue(Guid userId, AppWriteToken token, Repository repository, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.OpenIssue(userId, token, repository, scrub.Scrub(title), scrub.Scrub(body), labels, cancellationToken);
    }

    public Task<BranchResult> MakeBranch(Guid userId, AppWriteToken token, Repository repository, string name, string from, CancellationToken cancellationToken) =>
        inner.MakeBranch(userId, token, repository, name, from, cancellationToken);

    public async Task<BringInResult> BringIn(Guid userId, AppWriteToken token, Repository repository, string baseBranch, string head, string message, CancellationToken cancellationToken)
    {
        var scrub = await values.ScrubFor(userId, repository, cancellationToken);
        return await inner.BringIn(userId, token, repository, baseBranch, head, scrub.Scrub(message), cancellationToken);
    }

    public Task<MergeResult> MergeAsApp(Guid userId, AppWriteToken token, Repository repository, int number, string commit, CancellationToken cancellationToken) =>
        inner.MergeAsApp(userId, token, repository, number, commit, cancellationToken);

    public Uri InstallAddress(Uri origin) => inner.InstallAddress(origin);
}
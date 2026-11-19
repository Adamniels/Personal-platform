using System.Net;
using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The program's own writes, as the app, as GitHub documents them. Each takes an
/// <see cref="AppWriteToken"/>, a key <see cref="InstallationTokens.ForWrite"/> made for one
/// purpose on one repository, and refuses one for another purpose or repository before GitHub is
/// asked, so what each can do is what its key's permissions allow and no more. Nothing is
/// retried: the caller is a loop that decides. See "The program's own writes" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>A new key from <see cref="InstallationTokens.ForWrite"/>, never kept.</summary>
    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken) =>
        _tokens.ForWrite(repository, purpose, cancellationToken);

    /// <summary><c>https://github.com/{owner}/{repo}.git</c>, which git reaches with a push key;
    /// GitHub is not asked.</summary>
    public Task<CodeHostResult<Uri>> CloneAddress(Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return Task.FromResult<CodeHostResult<Uri>>(new CodeHostResult<Uri>.Found(
            new Uri(GitHubHttp.WebAddress, $"{Escape(repository.Owner)}/{Escape(repository.Name)}.git")));
    }

    /// <summary><c>POST /repos/{owner}/{repo}/pulls</c> with <c>title</c>, <c>head</c>,
    /// <c>base</c> and <c>body</c>; <c>201</c> is written, with the pull request GitHub
    /// answers.</summary>
    public async Task<WriteResult<PullRequest>> OpenPullRequest(Guid userId, AppWriteToken token, Repository repository, string headBranch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Post, repository);
        var answer = await Send(HttpMethod.Post, $"{Repo(repository)}/pulls", new PullBody(title, headBranch, baseBranch, body), token.Value, cancellationToken);
        return Written<PullAnswer, PullRequest>(answer, HttpStatusCode.Created, ToPullRequest);
    }

    /// <summary><c>POST /repos/{owner}/{repo}/issues/{number}/comments</c> with <c>body</c>, the
    /// text exactly; <c>201</c> is written, with the comment.</summary>
    public async Task<WriteResult<Comment>> CommentOnIssue(Guid userId, AppWriteToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Post, repository);
        ArgumentNullException.ThrowIfNull(body);
        var answer = await Send(HttpMethod.Post, Invariant($"{Repo(repository)}/issues/{number}/comments"), new CommentBody(body), token.Value, cancellationToken);
        return Written<CommentAnswer, Comment>(answer, HttpStatusCode.Created, comment =>
            new Comment(comment.Id, Login(comment.User), comment.Body ?? "", comment.CreatedAt, comment.UpdatedAt));
    }

    /// <summary><c>POST /repos/{owner}/{repo}/issues/{number}/labels</c> with
    /// <c>{"labels": [label]}</c>, which adds to the labels it has; <c>200</c> is written, with
    /// every label it has after.</summary>
    public async Task<WriteResult<IReadOnlyList<string>>> AddLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Post, repository);
        ArgumentException.ThrowIfNullOrEmpty(label);
        var answer = await Send(HttpMethod.Post, Invariant($"{Repo(repository)}/issues/{number}/labels"), new LabelsBody([label]), token.Value, cancellationToken);
        return Written<IReadOnlyList<LabelAnswer>, IReadOnlyList<string>>(answer, HttpStatusCode.OK, Names);
    }

    /// <summary><c>DELETE /repos/{owner}/{repo}/issues/{number}/labels/{name}</c>, the name
    /// escaped; <c>200</c> is written, with the labels left, and GitHub answers <c>404</c> for a
    /// label the issue does not carry.</summary>
    public async Task<WriteResult<IReadOnlyList<string>>> RemoveLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Post, repository);
        ArgumentException.ThrowIfNullOrEmpty(label);
        var answer = await Send<object>(HttpMethod.Delete, Invariant($"{Repo(repository)}/issues/{number}/labels/") + Escape(label), null, token.Value, cancellationToken);
        return Written<IReadOnlyList<LabelAnswer>, IReadOnlyList<string>>(answer, HttpStatusCode.OK, Names);
    }

    /// <summary><c>POST /repos/{owner}/{repo}/issues</c> with <c>title</c>, <c>body</c> and
    /// <c>labels</c>; <c>201</c> is written, with the issue.</summary>
    public async Task<WriteResult<Issue>> OpenIssue(Guid userId, AppWriteToken token, Repository repository, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Post, repository);
        ArgumentNullException.ThrowIfNull(labels);
        var answer = await Send(HttpMethod.Post, $"{Repo(repository)}/issues", new IssueBody(title, body, labels), token.Value, cancellationToken);
        return Written<IssueAnswer, Issue>(answer, HttpStatusCode.Created, ToIssue);
    }

    /// <summary><c>PUT /repos/{owner}/{repo}/pulls/{number}/merge</c> with <c>sha</c> and
    /// <c>merge_method</c> <c>merge</c>, as the owner's merge is, answered alike.</summary>
    public async Task<MergeResult> MergeAsApp(Guid userId, AppWriteToken token, Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Merge, repository);
        ArgumentNullException.ThrowIfNull(commit);
        return ToMergeResult(await Send(HttpMethod.Put, Invariant($"{Repo(repository)}/pulls/{number}/merge"), new MergeBody(commit, "merge"), token.Value, cancellationToken));
    }

    /// <summary>What GitHub says with <c>422</c> for a ref whose name is taken.</summary>
    internal const string ReferenceExists = "Reference already exists";

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/git/ref/heads/{from}</c> for the commit <paramref name="from"/>
    /// is at, then <c>POST /repos/{owner}/{repo}/git/refs</c> with <c>ref</c>
    /// <c>refs/heads/{name}</c> and that <c>sha</c>, both with the push key, which GitHub files
    /// under contents. <c>201</c> is made, at the commit GitHub answers; <c>422</c> with
    /// <see cref="ReferenceExists"/> is a branch that exists, and never moved, since the request
    /// only creates; any other answer is refused with GitHub's words, or unavailable without one.
    /// </summary>
    public async Task<BranchResult> MakeBranch(Guid userId, AppWriteToken token, Repository repository, string name, string from, CancellationToken cancellationToken)
    {
        Require(token, WritePurpose.Push, repository);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(from);
        var head = Ref(await Send<object>(HttpMethod.Get, $"{Repo(repository)}/git/ref/heads/{Segments(from)}", null, token.Value, cancellationToken), HttpStatusCode.OK);
        if (head is not BranchResult.Made { Commit: var commit })
        {
            return head;
        }

        var answer = await Send(HttpMethod.Post, $"{Repo(repository)}/git/refs", new RefBody($"refs/heads/{name}", commit), token.Value, cancellationToken);
        return answer is { Status: HttpStatusCode.UnprocessableEntity } && Message(answer.Body) == ReferenceExists
            ? new BranchResult.Exists()
            : Ref(answer, HttpStatusCode.Created);
    }

    /// <summary>A ref's answer: <paramref name="success"/> with a full commit id in
    /// <c>object.sha</c> is made at it; any other status refused, with GitHub's words; and no
    /// answer, or a success that does not parse, unavailable.</summary>
    private static BranchResult Ref(GitHubHttp.Answer? answer, HttpStatusCode success)
    {
        if (answer is null)
        {
            return new BranchResult.Unavailable(RetryAt: null);
        }

        if (answer.Status != success)
        {
            return new BranchResult.Refused((int)answer.Status, Message(answer.Body));
        }

        return Parse<RefAnswer>(answer.Body)?.Object is { Sha: var sha } && CommitId().IsMatch(sha)
            ? new BranchResult.Made(sha)
            : new BranchResult.Unavailable(RetryAt: null);
    }

    /// <summary>A branch's name in an address, each part between its slashes escaped, since a
    /// branch may be named with them.</summary>
    private static string Segments(string branch) => string.Join('/', branch.Split('/').Select(Escape));

    private static void Require(AppWriteToken token, WritePurpose purpose, Repository repository)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(purpose, repository);
    }

    /// <summary>
    /// A write's answer: <paramref name="success"/> is written, with the body mapped by
    /// <paramref name="map"/>; any other status refused with it and GitHub's words; and no answer,
    /// or a success whose body does not parse, unavailable, since what was made is then not known.
    /// </summary>
    private static WriteResult<TValue> Written<TAnswer, TValue>(GitHubHttp.Answer? answer, HttpStatusCode success, Func<TAnswer, TValue> map)
        where TAnswer : class
    {
        if (answer is null)
        {
            return new WriteResult<TValue>.Unavailable(RetryAt: null);
        }

        if (answer.Status != success)
        {
            return new WriteResult<TValue>.Refused((int)answer.Status, Message(answer.Body));
        }

        try
        {
            return Parse<TAnswer>(answer.Body) is { } parsed
                ? new WriteResult<TValue>.Written(map(parsed))
                : new WriteResult<TValue>.Unavailable(RetryAt: null);
        }
        catch (JsonException)
        {
            return new WriteResult<TValue>.Unavailable(RetryAt: null);
        }
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<LabelAnswer> labels) => [.. labels.Select(label => label.Name)];

    /// <summary>What opening a pull request sends.</summary>
    private sealed record PullBody(string Title, string Head, string Base, string Body);

    /// <summary>What adding labels sends.</summary>
    private sealed record LabelsBody(IReadOnlyList<string> Labels);

    /// <summary>What opening an issue sends.</summary>
    private sealed record IssueBody(string Title, string Body, IReadOnlyList<string> Labels);

    /// <summary>What making a branch sends: its full ref and the commit it is made at.</summary>
    private sealed record RefBody(string Ref, string Sha);

    /// <summary>A ref as GitHub answers one, of which only the commit is read.</summary>
    private sealed record RefAnswer(RefObject Object);

    /// <summary>What a ref points at.</summary>
    private sealed record RefObject(string Sha);
}
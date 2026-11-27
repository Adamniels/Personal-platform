using System.Net;
using System.Text;
using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The adapter's three writes as a person, as GitHub documents them, made only with a person's own
/// token, a <see cref="UserAccessToken"/>, so what they do is recorded on GitHub as done by that
/// person: a merge, a comment, and a label taken off an issue. None can be made as the app: they
/// never ask <see cref="InstallationTokens"/> for a token, and their token's type is not the string
/// an installation's token is. Nothing is retried, since a
/// person pressed a button and is told what happened. See "Acting from the page" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// A repository is named by the owner and name the project holds, as GitHub's addresses name it.
/// A merge also names the commit its head must be, so a name that has come to mean another
/// repository merges nothing there; a comment has no such check, which "Security" in the
/// architecture document lists as an accepted risk.
/// </remarks>
public sealed partial class GitHubCodeHost
{
    /// <summary>The most of GitHub's own words kept from a refusal, so a long answer is not shown
    /// whole.</summary>
    private const int MostMessage = 500;

    /// <summary>
    /// <c>PUT /repos/{owner}/{repo}/pulls/{number}/merge</c> with <c>sha</c>, which GitHub
    /// requires the head to match, and <c>merge_method</c> <c>merge</c>, a merge commit as the
    /// runner makes and as the repository's rules may require. <c>200</c> is merged, <c>409</c>
    /// head moved, <c>405</c> not mergeable, any other status refused with it, and no answer
    /// unavailable.
    /// </summary>
    public async Task<MergeResult> Merge(Guid userId, UserAccessToken token, Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return ToMergeResult(await Write(HttpMethod.Put, Invariant($"{Repo(repository)}/pulls/{number}/merge"), new MergeBody(commit, "merge"), token, cancellationToken));
    }

    /// <summary>A merge's answer: <c>200</c> merged, <c>409</c> head moved, <c>405</c> not
    /// mergeable, any other status refused with it, and none unavailable. The owner's merge and
    /// the app's read it alike.</summary>
    private static MergeResult ToMergeResult(GitHubHttp.Answer? answer)
    {
        if (answer is null)
        {
            return new MergeResult.Unavailable(RetryAt: null);
        }

        return answer.Status switch
        {
            HttpStatusCode.OK => new MergeResult.Merged(Parse<MergedAnswer>(answer.Body)?.Sha is { } sha && CommitId().IsMatch(sha) ? sha : null),
            HttpStatusCode.Conflict => new MergeResult.HeadMoved(),
            HttpStatusCode.MethodNotAllowed => new MergeResult.NotMergeable(Message(answer.Body)),
            _ => new MergeResult.Refused((int)answer.Status, Message(answer.Body)),
        };
    }

    /// <summary><c>POST /repos/{owner}/{repo}/issues/{number}/comments</c> with <c>body</c>, the
    /// text exactly. <c>201</c> is posted, with the comment's id when its answer holds a positive
    /// one, any other status refused with it, and no answer unavailable.</summary>
    public async Task<CommentResult> Comment(Guid userId, UserAccessToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var answer = await Write(HttpMethod.Post, Invariant($"{Repo(repository)}/issues/{number}/comments"), new CommentBody(body), token, cancellationToken);
        if (answer is null)
        {
            return new CommentResult.Unavailable(RetryAt: null);
        }

        return answer.Status == HttpStatusCode.Created
            ? new CommentResult.Posted(Parse<PostedAnswer>(answer.Body) is { Id: > 0 } posted ? posted.Id : null)
            : new CommentResult.Refused((int)answer.Status, Message(answer.Body));
    }

    /// <summary><c>DELETE /repos/{owner}/{repo}/issues/{number}/labels/{name}</c>, the name escaped.
    /// <c>200</c> is removed, any other status refused with it, <c>404</c> among them for a label
    /// the issue does not carry, and no answer unavailable.</summary>
    public async Task<UnlabelResult> Unlabel(Guid userId, UserAccessToken token, Repository repository, int number, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrEmpty(label);
        var answer = await Send<object>(HttpMethod.Delete, Invariant($"{Repo(repository)}/issues/{number}/labels/") + Escape(label), null, token.Value, cancellationToken);
        if (answer is null)
        {
            return new UnlabelResult.Unavailable(RetryAt: null);
        }

        return answer.Status == HttpStatusCode.OK
            ? new UnlabelResult.Removed()
            : new UnlabelResult.Refused((int)answer.Status, Message(answer.Body));
    }

    /// <summary>A request with <paramref name="body"/> as JSON, as the person whose token
    /// <paramref name="token"/> is, and GitHub's answer, or null when there was none.</summary>
    private async Task<GitHubHttp.Answer?> Write<T>(HttpMethod method, string address, T body, UserAccessToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        return await Send(method, address, body, token.Value, cancellationToken);
    }

    /// <summary>A write to <paramref name="address"/> with <paramref name="bearer"/>, a person's
    /// token or the app's key, and <paramref name="body"/> as JSON, or no body when it is null;
    /// GitHub's answer, or null when there was none. Never retried.</summary>
    private async Task<GitHubHttp.Answer?> Send<T>(HttpMethod method, string address, T? body, string bearer, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.Request(method, Address(address), GitHubHttp.JsonMediaType);
        request.Headers.Authorization = new("Bearer", bearer);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, GitHubHttp.PlainJsonMediaType);
        }

        return await GitHubHttp.Send(_factory, request, cancellationToken);
    }

    /// <summary>GitHub's <c>message</c> in an answer that is not a success, cut to
    /// <see cref="MostMessage"/> characters, or null when it has none.</summary>
    private static string? Message(byte[] body) =>
        Parse<MessageAnswer>(body)?.Message is { Length: > 0 } message
            ? message.Length > MostMessage ? message[..MostMessage] : message
            : null;

    /// <summary>The merge commit a merge answers with; optional, so an answer without it is still
    /// merged.</summary>
    private sealed record MergedAnswer(string? Sha = null);

    /// <summary>What a merge sends.</summary>
    private sealed record MergeBody(string Sha, string MergeMethod);

    /// <summary>What a comment sends.</summary>
    private sealed record CommentBody(string Body);

    /// <summary>The one field read from a refusal; optional, since not every answer has it.</summary>
    private sealed record MessageAnswer(string? Message = null);

    /// <summary>The part of GitHub's answer to a posted comment that is read: its id.</summary>
    private sealed record PostedAnswer(long Id);
}
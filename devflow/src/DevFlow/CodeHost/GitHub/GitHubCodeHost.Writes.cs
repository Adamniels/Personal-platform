using System.Net;
using System.Text;
using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The adapter's two writes, as GitHub documents them, made only with a person's own token, a
/// <see cref="UserAccessToken"/>, so what they do is recorded on GitHub as done by that person.
/// Neither can be made as the app: they never ask <see cref="InstallationTokens"/> for a token, and
/// their token's type is not the string an installation's token is. Nothing is retried, since a
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
        var answer = await Write(HttpMethod.Put, Invariant($"{Repo(repository)}/pulls/{number}/merge"), new MergeBody(commit, "merge"), token, cancellationToken);
        if (answer is null)
        {
            return new MergeResult.Unavailable(RetryAt: null);
        }

        return answer.Status switch
        {
            HttpStatusCode.OK => new MergeResult.Merged(),
            HttpStatusCode.Conflict => new MergeResult.HeadMoved(),
            HttpStatusCode.MethodNotAllowed => new MergeResult.NotMergeable(Message(answer.Body)),
            _ => new MergeResult.Refused((int)answer.Status, Message(answer.Body)),
        };
    }

    /// <summary><c>POST /repos/{owner}/{repo}/issues/{number}/comments</c> with <c>body</c>, the
    /// text exactly. <c>201</c> is posted, any other status refused with it, and no answer
    /// unavailable.</summary>
    public async Task<CommentResult> Comment(Guid userId, UserAccessToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var answer = await Write(HttpMethod.Post, Invariant($"{Repo(repository)}/issues/{number}/comments"), new CommentBody(body), token, cancellationToken);
        if (answer is null)
        {
            return new CommentResult.Unavailable(RetryAt: null);
        }

        return answer.Status == HttpStatusCode.Created
            ? new CommentResult.Posted()
            : new CommentResult.Refused((int)answer.Status, Message(answer.Body));
    }

    /// <summary>A request with <paramref name="body"/> as JSON, as the person whose token
    /// <paramref name="token"/> is, and GitHub's answer, or null when there was none.</summary>
    private async Task<GitHubHttp.Answer?> Write<T>(HttpMethod method, string address, T body, UserAccessToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        using var request = GitHubHttp.Request(method, Address(address), GitHubHttp.JsonMediaType);
        request.Headers.Authorization = new("Bearer", token.Value);
        request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, GitHubHttp.PlainJsonMediaType);
        return await GitHubHttp.Send(_factory, request, cancellationToken);
    }

    /// <summary>GitHub's <c>message</c> in an answer that is not a success, cut to
    /// <see cref="MostMessage"/> characters, or null when it has none.</summary>
    private static string? Message(byte[] body) =>
        Parse<MessageAnswer>(body)?.Message is { Length: > 0 } message
            ? message.Length > MostMessage ? message[..MostMessage] : message
            : null;

    /// <summary>What a merge sends.</summary>
    private sealed record MergeBody(string Sha, string MergeMethod);

    /// <summary>What a comment sends.</summary>
    private sealed record CommentBody(string Body);

    /// <summary>The one field read from a refusal; optional, since not every answer has it.</summary>
    private sealed record MessageAnswer(string? Message = null);
}
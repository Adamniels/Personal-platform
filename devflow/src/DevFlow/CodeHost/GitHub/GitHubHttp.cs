using System.Net;
using System.Net.Http.Headers;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The HTTP every request to GitHub shares: where it goes, the headers it carries, how it is sent
/// and what a failed answer means. Nothing is retried here: the caller is a loop that comes back.
/// See "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
internal static class GitHubHttp
{
    /// <summary>GitHub's API, a constant: a test replaces the handler, never the address.</summary>
    public static readonly Uri BaseAddress = new("https://api.github.com/");

    /// <summary>GitHub's JSON, which every request but a file's asks for.</summary>
    public const string JsonMediaType = "application/vnd.github+json";

    /// <summary>A file's bytes as they are, up to 100 MB.</summary>
    public const string RawMediaType = "application/vnd.github.raw+json";

    /// <summary>The version of GitHub's API the adapter is written against, sent with every
    /// request so GitHub answers in its shape whatever its newest version is.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The user agent GitHub requires, naming the program and nothing about the owner.</summary>
    public const string UserAgent = "platform-devflow";

    /// <summary>How long a request may take, its body included, before it is unavailable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>What GitHub answered: the status, the headers and the whole body.</summary>
    public sealed record Answer(HttpStatusCode Status, HttpResponseHeaders Headers, byte[] Body);

    /// <summary>A request for <paramref name="address"/>, relative to <see cref="BaseAddress"/> or
    /// absolute on it, asking for <paramref name="mediaType"/>. The version and the user agent are
    /// the client's own, set where it is registered.</summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri address, string mediaType)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        return request;
    }

    /// <summary>
    /// GitHub's answer, read whole, or null when there was none: the network failed, or it took
    /// longer than <see cref="Timeout"/>. A caller that gives up is told so with its own
    /// <see cref="OperationCanceledException"/>. No exception's message is kept, so nothing sent,
    /// a token included, can reach a log through one.
    /// </summary>
    public static async Task<Answer?> Send(IHttpClientFactory factory, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await factory.CreateClient(GitHubCodeHost.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new Answer(response.StatusCode, response.Headers, body);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout, not the caller.
            return null;
        }
    }

    /// <summary><c>404</c> is not found; anything else that is not a success is unavailable.</summary>
    public static CodeHostResult<T> Failure<T>(Answer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer.Status == HttpStatusCode.NotFound
            ? new CodeHostResult<T>.NotFound()
            : new CodeHostResult<T>.Unavailable(RetryAt: null);
    }
}
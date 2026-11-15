using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using DevFlow.Settings;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The installation tokens the adapter reads with, one per installation and repository. Each is
/// asked for with read permissions alone, on the one repository being read, so no key the program
/// makes for itself can write, although the app holds write access to code for merging: this is
/// what keeps "only the owner writes" true by construction. Each is kept in memory until five
/// minutes before it expires, written nowhere and never printed. See "GitHub" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
internal sealed class InstallationTokens(IHttpClientFactory factory, GitHubSettings settings, TimeProvider time)
{
    /// <summary>How long before it expires a token is no longer used, so a read that starts with
    /// it ends before GitHub stops taking it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>Exactly what every token asks for, each read. Its type has no other field, so no
    /// write can be added by a value.</summary>
    private static readonly Permissions ReadOnly = new(
        Contents: "read", PullRequests: "read", Issues: "read", Statuses: "read", Checks: "read", Metadata: "read");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly ConcurrentDictionary<(long Installation, long Repository), Kept> _kept = new();

    /// <summary>
    /// A token for reading <paramref name="repository"/>, kept or new. Not found when GitHub has no
    /// such installation, or the repository is not in it; unavailable for any other failure, with
    /// the time GitHub gives for a rate limit.
    /// </summary>
    public async Task<CodeHostResult<string>> For(Repository repository, CancellationToken cancellationToken)
    {
        var key = (repository.Installation, repository.Id);
        if (_kept.TryGetValue(key, out var kept) && time.GetUtcNow() < kept.ExpiresAt - Margin)
        {
            return new CodeHostResult<string>.Found(kept.Value);
        }

        var address = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"app/installations/{repository.Installation}/access_tokens"),
            UriKind.Relative);
        using var request = GitHubHttp.Request(HttpMethod.Post, address, GitHubHttp.JsonMediaType);
        request.Headers.Authorization = new("Bearer", AppToken.Make(settings.ClientId, settings.PrivateKey, time.GetUtcNow()));
        request.Content = JsonContent.Create(new TokenRequest([repository.Id], ReadOnly), options: Json);

        var answer = await GitHubHttp.Send(factory, request, cancellationToken);
        if (answer is null)
        {
            return new CodeHostResult<string>.Unavailable(RetryAt: null);
        }

        if (answer.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            // No such installation, or the repository is not one it reaches.
            return new CodeHostResult<string>.NotFound();
        }

        // 201 Created, which GitHub documents; any other success is taken too.
        if ((int)answer.Status is < 200 or > 299)
        {
            return GitHubHttp.Failure<string>(answer);
        }

        TokenAnswer? token;
        try
        {
            token = JsonSerializer.Deserialize<TokenAnswer>(answer.Body, Json);
        }
        catch (JsonException)
        {
            token = null;
        }

        if (token is null || token.Token.Length == 0)
        {
            return new CodeHostResult<string>.Unavailable(RetryAt: null);
        }

        _kept[key] = new Kept(token.Token, token.ExpiresAt);
        return new CodeHostResult<string>.Found(token.Token);
    }

    private sealed record TokenRequest(IReadOnlyList<long> RepositoryIds, Permissions Permissions);

    private sealed record Permissions(string Contents, string PullRequests, string Issues, string Statuses, string Checks, string Metadata);

    /// <summary>What GitHub answers, the fields read and no more.</summary>
    private sealed record TokenAnswer(string Token, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"TokenAnswer {{ ExpiresAt = {ExpiresAt} }}";
    }

    /// <summary>A token kept, which prints when it expires and never itself.</summary>
    private sealed record Kept(string Value, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"Kept {{ ExpiresAt = {ExpiresAt} }}";
    }
}
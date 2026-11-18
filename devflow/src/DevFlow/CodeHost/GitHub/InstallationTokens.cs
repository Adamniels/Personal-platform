using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using DevFlow.Settings;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The installation tokens the adapter asks GitHub for, on the one repository each is for. A read
/// token is asked for with read permissions alone, answered as a string, and kept in memory per
/// installation and repository until five minutes before it expires. A write key, an
/// <see cref="AppWriteToken"/>, is asked for by <see cref="ForWrite"/> with exactly its purpose's
/// permissions, is never kept, and is the only code here that makes one. Neither path asks for
/// <c>workflows</c>. Nothing is written anywhere or printed. See "GitHub" and "The program's own
/// writes" in devflow/docs/devflow-architecture.md.
/// </summary>
internal sealed class InstallationTokens(IHttpClientFactory factory, GitHubSettings settings, TimeProvider time)
{
    /// <summary>How long before it expires a token is no longer used, so a read that starts with
    /// it ends before GitHub stops taking it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>Exactly what every read token asks for, each read. Its type has no other field.</summary>
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

        switch (await Ask(repository, ReadOnly, cancellationToken))
        {
            case CodeHostResult<TokenAnswer>.Found found:
                _kept[key] = new Kept(found.Value.Token, found.Value.ExpiresAt);
                return new CodeHostResult<string>.Found(found.Value.Token);
            case CodeHostResult<TokenAnswer>.NotFound:
                return new CodeHostResult<string>.NotFound();
            case CodeHostResult<TokenAnswer>.Unavailable unavailable:
                return new CodeHostResult<string>.Unavailable(unavailable.RetryAt);
            default:
                throw new InvalidOperationException("not a result");
        }
    }

    /// <summary>
    /// A new key for the app to write to <paramref name="repository"/> for
    /// <paramref name="purpose"/>, asked for with that purpose's permissions alone, see
    /// <see cref="WritePermissions.For"/>, and never kept: each call asks GitHub for its own, so a
    /// push key handed to one worker is never another's, and no write key is ever used to read.
    /// Not found when the installation is gone, the repository is not in it, or it has not granted
    /// what the purpose needs; unavailable for any other failure.
    /// </summary>
    public async Task<CodeHostResult<AppWriteToken>> ForWrite(Repository repository, WritePurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return await Ask(repository, WritePermissions.For(purpose), cancellationToken) switch
        {
            CodeHostResult<TokenAnswer>.Found found => new CodeHostResult<AppWriteToken>.Found(
                new AppWriteToken(found.Value.Token, repository, purpose, found.Value.ExpiresAt)),
            CodeHostResult<TokenAnswer>.NotFound => new CodeHostResult<AppWriteToken>.NotFound(),
            CodeHostResult<TokenAnswer>.Unavailable unavailable => new CodeHostResult<AppWriteToken>.Unavailable(unavailable.RetryAt),
            _ => throw new InvalidOperationException("not a result"),
        };
    }

    /// <summary>
    /// <c>POST /app/installations/{id}/access_tokens</c> with <paramref name="permissions"/> and
    /// <c>repository_ids</c> holding the one repository, as the app. Not found on <c>404</c>, or
    /// on <c>422</c>, which GitHub answers when the repository is not in the installation or a
    /// permission asked for was not granted to it.
    /// </summary>
    private async Task<CodeHostResult<TokenAnswer>> Ask<TPermissions>(Repository repository, TPermissions permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var address = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"app/installations/{repository.Installation}/access_tokens"),
            UriKind.Relative);
        using var request = GitHubHttp.Request(HttpMethod.Post, address, GitHubHttp.JsonMediaType);
        request.Headers.Authorization = new("Bearer", AppToken.Make(settings.ClientId, settings.PrivateKey, time.GetUtcNow()));
        request.Content = JsonContent.Create(new TokenRequest<TPermissions>([repository.Id], permissions), options: Json);

        var answer = await GitHubHttp.Send(factory, request, cancellationToken);
        if (answer is null)
        {
            return new CodeHostResult<TokenAnswer>.Unavailable(RetryAt: null);
        }

        if (answer.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            return new CodeHostResult<TokenAnswer>.NotFound();
        }

        // 201 Created, which GitHub documents; any other success is taken too.
        if ((int)answer.Status is < 200 or > 299)
        {
            return GitHubHttp.Failure<TokenAnswer>(answer, time);
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

        return token is null || token.Token.Length == 0
            ? new CodeHostResult<TokenAnswer>.Unavailable(RetryAt: null)
            : new CodeHostResult<TokenAnswer>.Found(token);
    }

    private sealed record TokenRequest<TPermissions>(IReadOnlyList<long> RepositoryIds, TPermissions Permissions);

    private sealed record Permissions(string Contents, string PullRequests, string Issues, string Statuses, string Checks, string Metadata);

    /// <summary>
    /// What a write key asks for: a permission the purpose does not need is null and left out of
    /// the request, so GitHub grants nothing for it. No field names <c>workflows</c>, so no value
    /// can ask for it.
    /// </summary>
    private sealed record WritePermissions(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contents,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PullRequests,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issues,
        string Metadata)
    {
        /// <summary>Exactly what each purpose needs, see <see cref="WritePurpose"/>.</summary>
        public static WritePermissions For(WritePurpose purpose) => purpose switch
        {
            WritePurpose.Push => new(Contents: "write", PullRequests: null, Issues: null, Metadata: "read"),
            WritePurpose.Post => new(Contents: null, PullRequests: "write", Issues: "write", Metadata: "read"),
            WritePurpose.Merge => new(Contents: "write", PullRequests: "read", Issues: null, Metadata: "read"),
            WritePurpose.Fetch => new(Contents: "read", PullRequests: null, Issues: null, Metadata: "read"),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a write purpose"),
        };
    }

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
using System.Globalization;
using System.Net;
using System.Text.Json;

using DevFlow.Settings;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// <see cref="ICodeHost"/>'s read side on GitHub's REST API, read as Dev flow's GitHub App with
/// installation tokens that can only read, see <see cref="InstallationTokens"/>. Written by hand
/// on one <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>, with GitHub's answers in
/// records private to it, since the calls are few and a GitHub library would be a dependency for
/// what a dozen typed requests do. Chosen by <c>DEVFLOW_CODE_HOST=github</c>, and tested against
/// GitHub's real answers recorded in contracts/github/, since no automated test touches GitHub.
/// See "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// Reads are as the app, so the platform account each operation takes does not change what is
/// read: which account may read which repository is decided by the caller, from the repositories
/// that account connected. A repository is reached by its owner and name, as GitHub's addresses
/// are, and the token is narrowed to its id, so a name that no longer matches the id is not found
/// rather than another repository read.
/// </remarks>
public sealed partial class GitHubCodeHost : ICodeHost
{
    /// <summary>The name of the <see cref="HttpClient"/> every request is sent with.</summary>
    public const string HttpClientName = "github";

    private readonly IHttpClientFactory _factory;
    private readonly InstallationTokens _tokens;

    public GitHubCodeHost(IHttpClientFactory factory, GitHubSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _tokens = new InstallationTokens(factory, settings, time);
    }

    public async Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<PullAnswer>, PullAnswer>(repository, $"{Repo(repository)}/pulls?state=open&per_page=100", page => page, cancellationToken),
            pulls => (IReadOnlyList<PullRequest>)[.. pulls.Select(ToPullRequest)]);

    public async Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await One<PullAnswer>(repository, Invariant($"{Repo(repository)}/pulls/{number}"), cancellationToken),
            ToPullRequest);

    public async Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<CommentAnswer>, CommentAnswer>(repository, Invariant($"{Repo(repository)}/issues/{number}/comments?per_page=100"), page => page, cancellationToken),
            comments => (IReadOnlyList<Comment>)[.. comments.Select(comment =>
                new Comment(comment.Id, Login(comment.User), comment.Body ?? "", comment.CreatedAt, comment.UpdatedAt))]);

    /// <summary>GitHub lists every status posted on the commit, newest first, so the first of each
    /// context is its latest.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<StatusAnswer>, StatusAnswer>(repository, $"{Repo(repository)}/commits/{Escape(commit)}/statuses?per_page=100", page => page, cancellationToken),
            statuses => (IReadOnlyList<CommitStatus>)[.. statuses
                .DistinctBy(status => status.Context, StringComparer.Ordinal)
                .Select(status => new CommitStatus(status.Context, ToStatusState(status.State), status.Description))]);

    /// <summary>GitHub lists the latest run of each name and app by default.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Map(
            await List<CheckRunsAnswer, CheckRunAnswer>(repository, $"{Repo(repository)}/commits/{Escape(commit)}/check-runs?per_page=100", page => page.CheckRuns, cancellationToken),
            runs => (IReadOnlyList<CheckRun>)[.. runs.Select(run =>
                new CheckRun(run.Name, ToCheckStatus(run.Status), run.Conclusion is null ? null : ToConclusion(run.Conclusion), run.App.Slug))]);

    /// <summary>The file's bytes as they are, by the raw media type; each segment of the path
    /// escaped and its slashes kept, the branch a query value.</summary>
    public async Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return await Get(repository, $"{Repo(repository)}/contents/{segments}?ref={Escape(branch)}", GitHubHttp.RawMediaType, cancellationToken);
    }

    /// <summary>One JSON document, read into <typeparamref name="TAnswer"/>.</summary>
    private async Task<CodeHostResult<TAnswer>> One<TAnswer>(Repository repository, string address, CancellationToken cancellationToken)
        where TAnswer : class =>
        Read<TAnswer>(await Get(repository, address, GitHubHttp.JsonMediaType, cancellationToken));

    /// <summary>A list, its items taken from the page by <paramref name="items"/>.</summary>
    private async Task<CodeHostResult<IReadOnlyList<TItem>>> List<TPage, TItem>(Repository repository, string address, Func<TPage, IEnumerable<TItem>> items, CancellationToken cancellationToken)
        where TPage : class =>
        Map(await One<TPage>(repository, address, cancellationToken), page => (IReadOnlyList<TItem>)[.. items(page)]);

    /// <summary>
    /// The body at <paramref name="address"/>, read with a token for the repository: not found
    /// when the token cannot be had for it or GitHub answers 404, and otherwise unavailable when
    /// it is not a success.
    /// </summary>
    private async Task<CodeHostResult<byte[]>> Get(Repository repository, string address, string mediaType, CancellationToken cancellationToken)
    {
        var token = await _tokens.For(repository, cancellationToken);
        if (token is not CodeHostResult<string>.Found found)
        {
            return Fail<string, byte[]>(token);
        }

        using var request = GitHubHttp.Request(HttpMethod.Get, new Uri(address, UriKind.Relative), mediaType);
        request.Headers.Authorization = new("Bearer", found.Value);
        var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
        if (answer is null)
        {
            return new CodeHostResult<byte[]>.Unavailable(RetryAt: null);
        }

        return answer.Status == HttpStatusCode.OK
            ? new CodeHostResult<byte[]>.Found(answer.Body)
            : GitHubHttp.Failure<byte[]>(answer);
    }

    /// <summary>The body read as <typeparamref name="TAnswer"/>, or unavailable when it does not
    /// parse.</summary>
    private static CodeHostResult<TAnswer> Read<TAnswer>(CodeHostResult<byte[]> body)
        where TAnswer : class =>
        body switch
        {
            CodeHostResult<byte[]>.Found found => Parse<TAnswer>(found.Value) is { } answer
                ? new CodeHostResult<TAnswer>.Found(answer)
                : new CodeHostResult<TAnswer>.Unavailable(RetryAt: null),
            _ => Fail<byte[], TAnswer>(body),
        };

    /// <summary>The value mapped by <paramref name="map"/>, which throws a
    /// <see cref="JsonException"/> for a value it does not know, an answer that does not parse.</summary>
    private static CodeHostResult<TValue> Map<TAnswer, TValue>(CodeHostResult<TAnswer> result, Func<TAnswer, TValue> map)
    {
        if (result is not CodeHostResult<TAnswer>.Found found)
        {
            return Fail<TAnswer, TValue>(result);
        }

        try
        {
            return new CodeHostResult<TValue>.Found(map(found.Value));
        }
        catch (JsonException)
        {
            return new CodeHostResult<TValue>.Unavailable(RetryAt: null);
        }
    }

    /// <summary>A failure of one type as the same failure of another.</summary>
    private static CodeHostResult<TTo> Fail<TFrom, TTo>(CodeHostResult<TFrom> failure) => failure switch
    {
        CodeHostResult<TFrom>.NotFound => new CodeHostResult<TTo>.NotFound(),
        CodeHostResult<TFrom>.Unavailable unavailable => new CodeHostResult<TTo>.Unavailable(unavailable.RetryAt),
        _ => throw new ArgumentException("a failure, not a value", nameof(failure)),
    };

    /// <summary>The repository's address on GitHub, owner and name escaped.</summary>
    private static string Repo(Repository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return $"repos/{Escape(repository.Owner)}/{Escape(repository.Name)}";
    }

    private static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
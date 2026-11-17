using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using DevFlow.Settings;

using Microsoft.Extensions.Caching.Memory;

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
/// Listing what a person can connect is read as that person, with their token, so what is listed
/// is what they can reach. Every other read is as the app, so the platform account each operation takes does not change what is
/// read: which account may read which repository is decided by the caller, from the repositories
/// that account connected. A repository is reached by its owner and name, as GitHub's addresses
/// are, and the token is narrowed to its id, so a name that no longer matches the id is not found
/// rather than another repository read.
/// </remarks>
public sealed partial class GitHubCodeHost : ICodeHost, ICodeHostSignIn, IDisposable
{
    /// <summary>The name of the <see cref="HttpClient"/> every request is sent with.</summary>
    public const string HttpClientName = "github";

    /// <summary>The most pages a list is read across, 10,000 items at 100 a page, so a list that
    /// never ends, or a <c>Link</c> that loops, ends as unavailable.</summary>
    private const int MostPages = 100;

    /// <summary>How many bytes of answers are remembered for conditional requests, all
    /// installations together. Past it, the least recently used are forgotten first.</summary>
    private const long RememberedBytes = 64 * 1024 * 1024;

    /// <summary>How long an answer not asked for again is remembered. The poll asks for what it
    /// follows every minute, so what is forgotten is what nothing asks for any more, such as the
    /// statuses of a commit no pull request points at now.</summary>
    private static readonly TimeSpan RememberedFor = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _factory;
    private readonly TimeProvider _time;
    private readonly InstallationTokens _tokens;
    private readonly GitHubSettings _settings;

    /// <summary>The last <c>ETag</c> and answer per installation, media type and address, in memory
    /// only, for conditional requests.</summary>
    private readonly MemoryCache _remembered = new(new MemoryCacheOptions { SizeLimit = RememberedBytes });

    public GitHubCodeHost(IHttpClientFactory factory, GitHubSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _time = time;
        _settings = settings;
        _tokens = new InstallationTokens(factory, settings, time);
    }

    public void Dispose() => _remembered.Dispose();

    public async Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<PullAnswer>, PullAnswer>($"{Repo(repository)}/pulls?state=open&per_page=100", page => page, AsApp(repository, cancellationToken)),
            pulls => (IReadOnlyList<PullRequest>)[.. pulls.Select(ToPullRequest)]);

    public async Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await One<PullAnswer>(repository, Invariant($"{Repo(repository)}/pulls/{number}"), cancellationToken),
            ToPullRequest);

    public async Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<CommentAnswer>, CommentAnswer>(Invariant($"{Repo(repository)}/issues/{number}/comments?per_page=100"), page => page, AsApp(repository, cancellationToken)),
            comments => (IReadOnlyList<Comment>)[.. comments.Select(comment =>
                new Comment(comment.Id, Login(comment.User), comment.Body ?? "", comment.CreatedAt, comment.UpdatedAt))]);

    /// <summary>GitHub lists every status posted on the commit, newest first, so the first of each
    /// context is its latest.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<StatusAnswer>, StatusAnswer>($"{Repo(repository)}/commits/{Escape(commit)}/statuses?per_page=100", page => page, AsApp(repository, cancellationToken)),
            statuses => (IReadOnlyList<CommitStatus>)[.. statuses
                .DistinctBy(status => status.Context, StringComparer.Ordinal)
                .Select(status => new CommitStatus(status.Context, ToStatusState(status.State), status.Description))]);

    /// <summary>GitHub lists the latest run of each name and app by default.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Map(
            await List<CheckRunsAnswer, CheckRunAnswer>($"{Repo(repository)}/commits/{Escape(commit)}/check-runs?per_page=100", page => page.CheckRuns, AsApp(repository, cancellationToken)),
            runs => (IReadOnlyList<CheckRun>)[.. runs.Select(run =>
                new CheckRun(run.Name, ToCheckStatus(run.Status), run.Conclusion is null ? null : ToConclusion(run.Conclusion), run.App.Slug))]);

    /// <summary>The file's bytes as they are, by the raw media type; each segment of the path
    /// escaped and its slashes kept, the branch a query value.</summary>
    public async Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return Map(
            await Get(repository, Address($"{Repo(repository)}/contents/{segments}?ref={Escape(branch)}"), GitHubHttp.RawMediaType, cancellationToken),
            page => page.Body);
    }

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/commits?path=…&amp;per_page=1</c>, which with no <c>sha</c>
    /// lists the default branch's history, newest first, of commits that changed the path; then the
    /// file raw at exactly that commit, so the bytes answered are the commit's. Not found when no
    /// commit is listed. A commit id that is not 40 or 64 lowercase hex digits is an answer that
    /// does not parse, since it is handed on as a header and in a shell command.
    /// </summary>
    public async Task<CodeHostResult<FileAtCommit>> DefaultBranchFile(Guid userId, Repository repository, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var commits = Map(
            await Get(repository, Address($"{Repo(repository)}/commits?path={Escape(path)}&per_page=1"), GitHubHttp.JsonMediaType, cancellationToken),
            page => Parse<IReadOnlyList<CommitAnswer>>(page.Body) ?? throw new JsonException("the answer does not parse"));
        if (commits is not CodeHostResult<IReadOnlyList<CommitAnswer>>.Found found)
        {
            return Fail<IReadOnlyList<CommitAnswer>, FileAtCommit>(commits);
        }

        if (found.Value.Count == 0)
        {
            return new CodeHostResult<FileAtCommit>.NotFound();
        }

        var commit = found.Value[0].Sha;
        if (!CommitId().IsMatch(commit))
        {
            return new CodeHostResult<FileAtCommit>.Unavailable(RetryAt: null);
        }

        return Map(await File(userId, repository, path, commit, cancellationToken), bytes => new FileAtCommit(bytes, commit));
    }

    /// <summary><c>GET /user/installations</c>, as the person: the installations of this app they
    /// can reach.</summary>
    public async Task<CodeHostResult<IReadOnlyList<Installation>>> Installations(Guid userId, string userToken, CancellationToken cancellationToken) =>
        Map(
            await List<InstallationsAnswer, InstallationAnswer>("user/installations?per_page=100", page => page.Installations, AsPerson(userToken, cancellationToken)),
            installations => (IReadOnlyList<Installation>)[.. installations.Select(installation => new Installation(installation.Id, AccountName(installation.Account)))]);

    /// <summary><c>GET /user/installations/{id}/repositories</c>, as the person: the repositories
    /// of the installation they can reach. GitHub answers <c>404</c> for an installation they
    /// cannot.</summary>
    public async Task<CodeHostResult<IReadOnlyList<Repository>>> Repositories(Guid userId, string userToken, long installation, CancellationToken cancellationToken) =>
        Map(
            await List<RepositoriesAnswer, RepositoryAnswer>(Invariant($"user/installations/{installation}/repositories?per_page=100"), page => page.Repositories, AsPerson(userToken, cancellationToken)),
            repositories => (IReadOnlyList<Repository>)[.. repositories.Select(repository => new Repository(installation, repository.Id, repository.Owner.Login, repository.Name))]);

    /// <summary>The app's page on GitHub where a person installs it on an account,
    /// <c>https://github.com/apps/&lt;slug&gt;/installations/new</c>; the origin is the fake's.</summary>
    public Uri InstallAddress(Uri origin) => new(GitHubHttp.WebAddress, $"apps/{Escape(_settings.AppSlug)}/installations/new");

    /// <summary>Reads a page as the app, with a token for the repository, see <see cref="Get"/>.</summary>
    private Func<Uri, Task<CodeHostResult<Page>>> AsApp(Repository repository, CancellationToken cancellationToken) =>
        address => Get(repository, address, GitHubHttp.JsonMediaType, cancellationToken);

    /// <summary>
    /// Reads a page as the person whose token <paramref name="userToken"/> is, never as the app.
    /// Nothing is remembered: these are read when a person asks, not by the poll, and an answer
    /// read with one person's token is never one to give another. Not found on <c>404</c>, and
    /// otherwise as <see cref="GitHubHttp.Failure"/> says.
    /// </summary>
    private Func<Uri, Task<CodeHostResult<Page>>> AsPerson(string userToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userToken);
        return async address =>
        {
            using var request = GitHubHttp.Request(HttpMethod.Get, address, GitHubHttp.JsonMediaType);
            request.Headers.Authorization = new("Bearer", userToken);
            var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
            if (answer is null)
            {
                return new CodeHostResult<Page>.Unavailable(RetryAt: null);
            }

            return answer.Status == HttpStatusCode.OK
                ? new CodeHostResult<Page>.Found(new Page(answer.Body, Next(answer.Headers)))
                : GitHubHttp.Failure<Page>(answer, _time);
        };
    }

    /// <summary>One JSON document, read into <typeparamref name="TAnswer"/>, or unavailable when it
    /// does not parse.</summary>
    private async Task<CodeHostResult<TAnswer>> One<TAnswer>(Repository repository, string address, CancellationToken cancellationToken)
        where TAnswer : class =>
        Map(await Get(repository, Address(address), GitHubHttp.JsonMediaType, cancellationToken), page =>
            Parse<TAnswer>(page.Body) ?? throw new JsonException("the answer does not parse"));

    /// <summary>
    /// A list, its pages read by <paramref name="get"/> and its items taken from each by
    /// <paramref name="items"/>, following the
    /// <c>Link</c> header's <c>next</c> until there is none. A next page anywhere but GitHub's API
    /// is never asked for, since the request would carry the token there, and the list is
    /// unavailable instead; so is one past <see cref="MostPages"/>, or a page asked for twice. A
    /// page that fails fails the list: half a list is never answered as the whole. With
    /// <paramref name="enough"/>, no page is asked for once the items read so far are enough.
    /// </summary>
    private static async Task<CodeHostResult<IReadOnlyList<TItem>>> List<TPage, TItem>(string address, Func<TPage, IEnumerable<TItem>> items, Func<Uri, Task<CodeHostResult<Page>>> get, Func<IReadOnlyList<TItem>, bool>? enough = null)
        where TPage : class
    {
        var all = new List<TItem>();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        for (Uri? next = Address(address); next is not null;)
        {
            if (!IsGitHub(next) || !asked.Add(next.AbsoluteUri) || asked.Count > MostPages)
            {
                return new CodeHostResult<IReadOnlyList<TItem>>.Unavailable(RetryAt: null);
            }

            var page = await get(next);
            if (page is not CodeHostResult<Page>.Found found)
            {
                return Fail<Page, IReadOnlyList<TItem>>(page);
            }

            if (Parse<TPage>(found.Value.Body) is not { } parsed)
            {
                return new CodeHostResult<IReadOnlyList<TItem>>.Unavailable(RetryAt: null);
            }

            all.AddRange(items(parsed));
            next = enough is not null && enough(all) ? null : found.Value.Next;
        }

        return new CodeHostResult<IReadOnlyList<TItem>>.Found(all);
    }

    /// <summary>
    /// The page at <paramref name="address"/>, read with a token for the repository, as a
    /// conditional request when an answer for it is remembered: <c>If-None-Match</c> carries its
    /// <c>ETag</c>, and on <c>304</c>, which GitHub does not count against the rate limit, it is
    /// what is answered, its next page included. A new answer with an <c>ETag</c> replaces it.
    /// Not found when the token cannot be had for the repository or GitHub answers <c>404</c>;
    /// otherwise unavailable when it is not a success, see <see cref="GitHubHttp.Failure"/>.
    /// </summary>
    private async Task<CodeHostResult<Page>> Get(Repository repository, Uri address, string mediaType, CancellationToken cancellationToken)
    {
        var token = await _tokens.For(repository, cancellationToken);
        if (token is not CodeHostResult<string>.Found found)
        {
            return Fail<string, Page>(token);
        }

        var key = new RememberedKey(repository.Installation, mediaType, address.AbsoluteUri);
        var remembered = _remembered.Get<Remembered>(key);
        using var request = GitHubHttp.Request(HttpMethod.Get, address, mediaType);
        request.Headers.Authorization = new("Bearer", found.Value);
        if (remembered is not null)
        {
            request.Headers.IfNoneMatch.Add(remembered.ETag);
        }

        var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
        if (answer is null)
        {
            return new CodeHostResult<Page>.Unavailable(RetryAt: null);
        }

        if (answer.Status == HttpStatusCode.NotModified)
        {
            // Unchanged; with nothing remembered there is nothing to answer it with.
            return remembered is null
                ? new CodeHostResult<Page>.Unavailable(RetryAt: null)
                : new CodeHostResult<Page>.Found(remembered.Page);
        }

        if (answer.Status != HttpStatusCode.OK)
        {
            return GitHubHttp.Failure<Page>(answer, _time);
        }

        var page = new Page(answer.Body, Next(answer.Headers));
        if (answer.Headers.ETag is { } etag)
        {
            _remembered.Set(key, new Remembered(etag, page), new MemoryCacheEntryOptions
            {
                Size = page.Body.Length + 1,
                SlidingExpiration = RememberedFor,
            });
        }
        else
        {
            _remembered.Remove(key);
        }

        return new CodeHostResult<Page>.Found(page);
    }

    /// <summary>The <c>Link</c> header's <c>next</c>, as given, or null when there is none.</summary>
    private static Uri? Next(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            foreach (Match link in LinkPattern().Matches(value))
            {
                if (link.Groups["rel"].Value.Split(' ').Contains("next", StringComparer.Ordinal))
                {
                    // Relative or malformed, it is not GitHub's API, which List refuses.
                    return Uri.TryCreate(link.Groups["url"].Value, UriKind.RelativeOrAbsolute, out var next)
                        ? next
                        : new Uri("about:blank");
                }
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="address"/> is on GitHub's API, the one place a token goes.</summary>
    private static bool IsGitHub(Uri address) =>
        address.IsAbsoluteUri
        && Uri.Compare(address, GitHubHttp.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>An address relative to GitHub's API, made absolute.</summary>
    private static Uri Address(string relative) => new(GitHubHttp.BaseAddress, relative);

    /// <summary>A full commit id, SHA-1 or SHA-256, as GitHub writes it.</summary>
    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();

    [GeneratedRegex("<(?<url>[^>]*)>\\s*;\\s*rel=\"(?<rel>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    /// <summary>A page's body, and the address of the next one, if any.</summary>
    private sealed record Page(byte[] Body, Uri? Next);

    /// <summary>An answer remembered with its <c>ETag</c>.</summary>
    private sealed record Remembered(EntityTagHeaderValue ETag, Page Page);

    /// <summary>Answers are remembered per installation, as each is read with its own tokens.</summary>
    private readonly record struct RememberedKey(long Installation, string MediaType, string Address);

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
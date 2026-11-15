using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;
using DevFlow.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The GitHub mode's four settings, over a private key made for the test run and written to a
/// file of its own, which the settings read as the hosted copy's key file is read.
/// </summary>
internal static class TestApp
{
    public const string ClientId = "Iv23liTestClient0001";
    public const string ClientSecret = "0123456789abcdef0123456789abcdef01234567";
    public const string AppSlug = "platform-devflow-test";

    /// <summary>The app's key, the private half written to <see cref="KeyFile"/>; a test verifies
    /// with its public half.</summary>
    public static RSA Key { get; } = RSA.Create(2048);

    public static string KeyFile { get; } = WriteKey(Key.ExportRSAPrivateKeyPem());

    /// <summary>A vault key, which the GitHub mode needs before it reaches these settings.</summary>
    public static readonly (string Name, string Value) VaultKey =
        ("DEVFLOW_VAULT_KEY", $"k1:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");

    /// <summary>Every setting the GitHub mode needs, the vault's key among them.</summary>
    public static (string Name, string Value)[] Environment =>
    [
        ("DEVFLOW_CODE_HOST", "github"),
        VaultKey,
        ("DEVFLOW_GITHUB_CLIENT_ID", ClientId),
        ("DEVFLOW_GITHUB_CLIENT_SECRET", ClientSecret),
        ("DEVFLOW_GITHUB_PRIVATE_KEY_FILE", KeyFile),
        ("DEVFLOW_GITHUB_APP_SLUG", AppSlug),
    ];

    /// <summary>The settings from the minimal environment with <paramref name="set"/> on top; a
    /// name set to null is left out.</summary>
    public static DevFlowSettings Load(params (string Name, string? Value)[] set)
    {
        var values = new Dictionary<string, string?>(Harness.MinimalEnvironment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));
        foreach (var (name, value) in set)
        {
            values[name] = value;
        }

        return DevFlowSettings.Load(name => values.GetValueOrDefault(name));
    }

    /// <summary>The GitHub mode's settings, every one set.</summary>
    public static GitHubSettings Settings { get; } =
        Load([.. Environment.Select(pair => (pair.Name, (string?)pair.Value))]).GitHub!;

    /// <summary>A file in a folder made for it, which the run leaves behind in the temporary
    /// folder; it holds a key made for the tests alone.</summary>
    public static string WriteKey(string pem)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("devflow-github-").FullName, "app.pem");
        File.WriteAllText(path, pem);
        return path;
    }
}

/// <summary>A request the stub received, copied, since the request is disposed after it is sent.</summary>
internal sealed record Received(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary>The header's values joined with commas, or null when it was not sent.</summary>
    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());

    /// <summary>The path, without the query.</summary>
    public string Path => Uri.AbsolutePath;

    /// <summary>The query's parameters, unescaped.</summary>
    public IReadOnlyDictionary<string, string> Query => Uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
}

/// <summary>
/// GitHub's API, answered from memory: each request is recorded, then answered by
/// <see cref="Answer"/>. Unless a test says otherwise, a request for an installation token is
/// answered with contracts/github/installation-token.json, expiring an hour after the clock's
/// now, and anything else is 404.
/// </summary>
internal sealed class StubGitHub(FakeTimeProvider time) : HttpMessageHandler
{
    private readonly ConcurrentQueue<Received> _received = new();

    /// <summary>The token contracts/github/installation-token.json holds, made up.</summary>
    public static string FixtureToken => (string)Fixture.Node("installation-token.json")["token"]!;

    public IReadOnlyList<Received> Requests => [.. _received];

    /// <summary>The requests other than those for installation tokens.</summary>
    public IReadOnlyList<Received> Reads => [.. _received.Where(request => !IsTokenRequest(request))];

    /// <summary>The requests for installation tokens.</summary>
    public IReadOnlyList<Received> TokenRequests => [.. _received.Where(IsTokenRequest)];

    /// <summary>How a read is answered; a token request is answered by <see cref="TokenAnswer"/>.</summary>
    public Func<Received, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public Func<Received, HttpResponseMessage>? TokenAnswer { get; set; }

    public static bool IsTokenRequest(Received request) =>
        request.Method == HttpMethod.Post && request.Path.EndsWith("/access_tokens", StringComparison.Ordinal);

    /// <summary>The fixture's token, expiring <paramref name="after"/> from the clock's now.</summary>
    public HttpResponseMessage Token(TimeSpan after)
    {
        var body = Fixture.Node("installation-token.json").AsObject();
        body["expires_at"] = time.GetUtcNow().Add(after).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        return Json(body.ToJsonString(), HttpStatusCode.Created);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>();
        foreach (var (name, values) in request.Headers)
        {
            headers[name.ToLowerInvariant()] = string.Join(",", values);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name.ToLowerInvariant()] = string.Join(",", values);
            }
        }

        var received = new Received(request.Method, request.RequestUri!, headers, body);
        _received.Enqueue(received);
        var response = IsTokenRequest(received)
            ? (TokenAnswer ?? (_ => Token(TimeSpan.FromHours(1))))(received)
            : Answer(received);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>Every log line written, with its exception, at every level.</summary>
internal sealed class CapturingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {state} {exception}");
        }
    }
}

/// <summary>
/// The GitHub code host as Dev flow builds it, through <c>AddGitHubCodeHost</c>, with the
/// stub's handler in place of the network, a fake clock, and every log line captured.
/// </summary>
internal sealed class GitHubHost : IDisposable
{
    public static readonly CodeRepository Platform = new(Installation: 41, Id: 1066577263, Owner: "Adamniels", Name: "Personal-platform");

    private readonly ServiceProvider _services;

    public GitHubHost(long? clockAt = null)
    {
        Time = new FakeTimeProvider(clockAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        Stub = new StubGitHub(Time);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddGitHubCodeHost(TestApp.Settings);
        // The handler, never the address: the adapter's own base address stays.
        services.AddHttpClient(GitHubCodeHost.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Stub)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        _services = services.BuildServiceProvider();
        CodeHost = _services.GetRequiredService<ICodeHost>();
    }

    public FakeTimeProvider Time { get; }

    public StubGitHub Stub { get; }

    public CapturingLogs Logs { get; } = new();

    public ICodeHost CodeHost { get; }

    public IServiceProvider Services => _services;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Guid User { get; } = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    public void Dispose()
    {
        _services.Dispose();
        Stub.Dispose();
    }
}

/// <summary>The recorded answers in contracts/github/, see contracts/README.md.</summary>
internal static class Fixture
{
    public static string Text(string name) => File.ReadAllText(Tests.Repository.Path("contracts", "github", name));

    public static JsonNode Node(string name) => JsonNode.Parse(Text(name))!;
}

/// <summary>Reads a result's value, failing the test with the result when it is not found.</summary>
internal static class Results
{
    public static T Value<T>(CodeHostResult<T> result) => result switch
    {
        CodeHostResult<T>.Found found => found.Value,
        _ => throw new Xunit.Sdk.XunitException($"expected a value, got {result}"),
    };
}
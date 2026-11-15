using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using NSec.Cryptography;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>The repository's files the tests read.</summary>
internal static class Repository
{
    /// <summary>The repository root, found by walking up from the test binary to the folder
    /// holding contracts/, so the tests run from any working directory.</summary>
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "contracts", "token")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("contracts/token/ not found above the test binary");
    }
}

/// <summary>The access token as the core produces it, from contracts/token/, see contracts/README.md.</summary>
internal static class Contract
{
    public static string Token => File.ReadAllText(Repository.Path("contracts", "token", "token.jwt"));

    public static string Jwks => File.ReadAllText(Repository.Path("contracts", "token", "jwks.json"));

    private static JsonObject Claims =>
        JsonNode.Parse(File.ReadAllText(Repository.Path("contracts", "token", "claims.json")))!.AsObject();

    public static string Sub => (string)Claims["sub"]!;

    public static string Kid => (string)Claims["kid"]!;

    public static long IssuedAt => (long)Claims["iat"]!;

    public static long ExpiresAt => (long)Claims["exp"]!;

    /// <summary>The fixture's claims in a token signed with <paramref name="key"/> under
    /// <paramref name="kid"/>, so a test controls which key signed it.</summary>
    public static string SignedWith(Key key, string kid)
    {
        static string Segment(JsonNode value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value.ToJsonString()));

        var payload = Claims;
        payload.Remove("kid");
        var header = new JsonObject { ["typ"] = "JWT", ["alg"] = "EdDSA", ["kid"] = kid };
        var signingInput = $"{Segment(header)}.{Segment(payload)}";
        var signature = SignatureAlgorithm.Ed25519.Sign(key, Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }
}

/// <summary>The core's key set endpoint, answered from memory with contracts/token/jwks.json.</summary>
internal sealed class StubCore : HttpMessageHandler
{
    public const string Url = "http://127.0.0.1:8080";

    public KeyCache Cache(TimeProvider time) => new(new HttpClient(this), new Uri(Url), time);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Contract.Jwks) });
}

/// <summary>
/// Dev flow on a <see cref="TestServer"/>, over a build folder of its own holding an index.html and
/// one asset, with a key cache over <see cref="StubCore"/> and a clock fixed at the fixture token's
/// <c>iat</c> unless a test moves it, so the committed token is valid. A test that calls a route
/// using the database hands it a door over a database of its own.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    public const string IndexHtml = "<!doctype html><title>Dev flow</title><div id=\"root\"></div>\n";
    public const string Asset = "console.log(\"devflow\");\n";
    public const string AssetName = "index-abc123.js";

    private readonly StubCore _core;
    private readonly WebApplication _app;
    private readonly UserDb _database;
    private readonly string _webRoot;

    private Harness(WebApplication app, StubCore core, UserDb database, string webRoot)
    {
        _app = app;
        _core = core;
        _database = database;
        _webRoot = webRoot;
    }

    public TestServer Server => _app.GetTestServer();

    /// <summary>The smallest environment the settings load from: the one required setting, the
    /// app's database URL, which nothing here connects to.</summary>
    public static readonly IReadOnlyDictionary<string, string> MinimalEnvironment = new Dictionary<string, string>
    {
        ["DEVFLOW_APP_DATABASE_URL"] = "postgres://devflow_app:secret-a1b2@127.0.0.1:5432/devflow",
    };

    public static DevFlowSettings Defaults => DevFlowSettings.Load(name => MinimalEnvironment.GetValueOrDefault(name));

    /// <summary>A folder shaped like web/dist, removed with the harness.</summary>
    private static string MakeBuild()
    {
        var root = Directory.CreateTempSubdirectory("devflow-web-").FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(root, "assets"));
        File.WriteAllText(System.IO.Path.Combine(root, "index.html"), IndexHtml);
        File.WriteAllText(System.IO.Path.Combine(root, "assets", AssetName), Asset);
        return root;
    }

    /// <param name="database">The door the app uses, disposed with the harness. Without one it is
    /// never opened: the test takes no database, and no route it calls uses one.</param>
    /// <param name="codeHost">A code host registered before the app's own, which it keeps, so a
    /// test can put a code host of its own behind the app.</param>
    public static async Task<Harness> Start(DevFlowSettings? settings = null, long? clockAt = null, UserDb? database = null, ICodeHost? codeHost = null)
    {
        var webRoot = MakeBuild();
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(clockAt ?? Contract.IssuedAt));
        var core = new StubCore();
        settings ??= Defaults;
        database ??= UserDb.Unopened(DevFlowSettings.Service, settings.AppDatabase);
        WebApplication app;
        try
        {
            app = DevFlowApp.Build(settings, database, webRoot, builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<TimeProvider>(time);
                // Registered first, so AddPlatformAuth keeps it rather than reaching a real core.
                builder.Services.AddSingleton(core.Cache(time));
                if (codeHost is not null)
                {
                    builder.Services.AddSingleton(codeHost);
                }
            });
        }
        catch
        {
            // A test that expects the app to refuse leaves nothing behind.
            core.Dispose();
            Directory.Delete(webRoot, recursive: true);
            throw;
        }

        await app.StartAsync();
        return new Harness(app, core, database, webRoot);
    }

    /// <summary>A request as a browser on the platform sends it, with the token in the cookie
    /// when one is given.</summary>
    public Task<HttpContext> Send(string method, string pathAndQuery, string? token = null, params (string Name, string Value)[] headers) =>
        Server.SendAsync(context =>
        {
            context.Request.Method = method;
            var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
            context.Request.QueryString = new QueryString(query < 0 ? "" : pathAndQuery[query..]);
            if (token is not null)
            {
                context.Request.Headers.Cookie = $"__Host-access={token}";
            }

            foreach (var (name, value) in headers)
            {
                context.Request.Headers.Append(name, value);
            }
        }, TestContext.Current.CancellationToken);

    public Task<HttpContext> Get(string pathAndQuery, string? token = null) => Send("GET", pathAndQuery, token);

    /// <summary>A request with <paramref name="json"/> as its body, from the platform's own
    /// origin, as the page sends a change.</summary>
    public Task<HttpContext> SendJson(string method, string path, string? token, string json) =>
        Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Headers.Origin = "https://platform.test";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            if (token is not null)
            {
                context.Request.Headers.Cookie = $"__Host-access={token}";
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>The app's services, for a test that checks what a route did to one of them.</summary>
    public IServiceProvider Services => _app.Services;

    public static async Task<string> Body(HttpContext context) =>
        await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
        _core.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }
}
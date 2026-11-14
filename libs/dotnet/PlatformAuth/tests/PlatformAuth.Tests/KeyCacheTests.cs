using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using NSec.Cryptography;

using static PlatformAuth.Tests.Tokens;

namespace PlatformAuth.Tests;

/// <summary>
/// The core's key set endpoint, answered from memory, counting requests. <see cref="Answer"/> can
/// be changed mid test, as the core rotating its keys or going down would.
/// </summary>
internal sealed class StubCore : HttpMessageHandler
{
    public const string Url = "http://127.0.0.1:8080";

    private int _requests;

    public StubCore(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? answer = null)
    {
        Answer = answer ?? Serves(Contract.Jwks);
    }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; }

    public List<HttpRequestMessage> Requests { get; } = [];

    public int Count => Volatile.Read(ref _requests);

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Serves(
        string body, HttpStatusCode status = HttpStatusCode.OK) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    public static Task<HttpResponseMessage> Unreachable(HttpRequestMessage request, CancellationToken token) =>
        throw new HttpRequestException("connection refused");

    public KeyCache Cache(TimeProvider time) => new(new HttpClient(this), new Uri(Url), time);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Answer(request, cancellationToken);
    }
}

/// <summary>
/// <see cref="KeyCache"/>, by the rule in "How a feature authenticates" in
/// High-level/docs/feature-contract.md. The C# twin of
/// libs/python/platform-auth/tests/test_keys.py.
/// </summary>
public sealed class KeyCacheTests : IDisposable
{
    private static readonly string FixtureKid = Contract.Keys().Keys.Single();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(Now));
    private readonly StubCore _core = new();
    private readonly KeyCache _cache;

    public KeyCacheTests()
    {
        _cache = _core.Cache(_time);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _core.Dispose();
    }

    private void Advance(int seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task TheKeySetIsReadAsTheCoreServesIt()
    {
        var first = await _cache.FindAsync(FixtureKid, TestContext.Current.CancellationToken);
        Advance(1);
        var second = await _cache.FindAsync(FixtureKid, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Same(first, second);
        var x = (string)JsonNode.Parse(Contract.Jwks)!["keys"]![0]!["x"]!;
        Assert.Equal(UnB64(x), first.Export(KeyBlobFormat.RawPublicKey));

        var request = Assert.Single(_core.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri($"{StubCore.Url}/.well-known/jwks.json"), request.RequestUri);
        Assert.Equal("/.well-known/jwks.json", KeyCache.JwksPath);
        Assert.Equal(TimeSpan.FromSeconds(2), KeyCache.FetchTimeout);
    }

    [Fact]
    public async Task AnUnknownKeyIdRefetchesOnceAtMostEveryThirtySeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(TimeSpan.FromSeconds(30), KeyCache.RefetchCooldown);

        // The first lookup fetches, and counts for the cooldown.
        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        Advance(29);
        Assert.Null(await _cache.FindAsync("unknown", ct));
        Assert.Equal(1, _core.Count);

        Advance(71);
        Assert.Null(await _cache.FindAsync("first-unknown", ct));
        Assert.Equal(2, _core.Count);

        // A known key is still found without asking again.
        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        Assert.Equal(2, _core.Count);

        Advance(29);
        Assert.Null(await _cache.FindAsync("second-unknown", ct));
        Assert.Equal(2, _core.Count);

        Advance(1);
        Assert.Null(await _cache.FindAsync("second-unknown", ct));
        Assert.Equal(3, _core.Count);
    }

    [Fact]
    public async Task AKeyTheCoreStartsServingIsFoundByTheRefetch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var next = NewKey();
        var rotated = JsonNode.Parse(Contract.Jwks)!.AsObject();
        rotated["keys"]!.AsArray().Add(Jwk(next, "next"));

        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        _core.Answer = StubCore.Serves(rotated.ToJsonString());
        Advance(100);

        Assert.NotNull(await _cache.FindAsync("next", ct));
        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        Assert.Equal(2, _core.Count);
    }

    [Fact]
    public async Task AFailedFetchKeepsTheKeysItHad()
    {
        var ct = TestContext.Current.CancellationToken;
        var known = await _cache.FindAsync(FixtureKid, ct);
        Assert.NotNull(known);
        _core.Answer = StubCore.Unreachable;
        Advance(1000);

        Assert.Same(known, await _cache.FindAsync(FixtureKid, ct));
        // A refetch that fails leaves the set as it was.
        Assert.Null(await _cache.FindAsync("unknown", ct));
        Assert.Equal(2, _core.Count);
        Assert.Same(known, await _cache.FindAsync(FixtureKid, ct));
    }

    public static TheoryData<string> Failures() =>
        ["unreachable", "timeout", "500", "404", "not json", "a list", "keys not a list", "no keys"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedFetchFindsNoKeyAndIsRetriedAfterTheCooldown(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        _core.Answer = failure switch
        {
            "unreachable" => StubCore.Unreachable,
            "timeout" => async (_, token) =>
            {
                // Waits until the cache gives up, which only its own timeout can make it do.
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            }
            ,
            "500" => StubCore.Serves(Contract.Jwks, HttpStatusCode.InternalServerError),
            "404" => StubCore.Serves("", HttpStatusCode.NotFound),
            "not json" => StubCore.Serves("not json"),
            "a list" => StubCore.Serves("[\"not\", \"a\", \"key\", \"set\"]"),
            "keys not a list" => StubCore.Serves("{\"keys\": \"none\"}"),
            "no keys" => StubCore.Serves("{\"no keys\": []}"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        var fetchedAt = _time.GetUtcNow();
        var lookup = _cache.FindAsync(FixtureKid, ct);
        if (failure == "timeout")
        {
            // Let the request start, then move the fake clock to the fetch timeout.
            while (_core.Count == 0)
            {
                await Task.Yield();
            }

            _time.Advance(KeyCache.FetchTimeout);
        }

        Assert.Null(await lookup);
        // 29 seconds after the fetch started, which is when the cooldown counts from.
        _time.SetUtcNow(fetchedAt.AddSeconds(29));
        Assert.Null(await _cache.FindAsync(FixtureKid, ct));
        Assert.Equal(1, _core.Count);

        _core.Answer = StubCore.Serves(Contract.Jwks);
        Advance(1);
        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        Assert.Equal(2, _core.Count);
    }

    [Fact]
    public void OnlyEd25519KeysAreTaken()
    {
        using var good = NewKey();
        using var other = NewKey();
        JsonObject Entry(string kid, string field, JsonNode? value)
        {
            var entry = Jwk(other, kid);
            entry[field] = value;
            return entry;
        }

        var padded = Jwk(other, "padded");
        padded["x"] = (string)padded["x"]! + "=";
        var served = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject { ["kty"] = "RSA", ["kid"] = "rsa", ["n"] = "AQAB", ["e"] = "AQAB" },
                Entry("x25519", "crv", "X25519"),
                Entry("ec", "kty", "EC"),
                Entry("short", "x", "AAAA"),
                padded,
                Entry("no-x", "x", null),
                Entry("kid-not-a-string", "kid", 7),
                "not an object",
                Jwk(good, "good")),
        };

        var keys = KeyCache.ParseKeySet(Element(served.ToJsonString()));

        Assert.NotNull(keys);
        Assert.Equal(["good"], keys.Keys);
        Assert.Equal(good.PublicKey.Export(KeyBlobFormat.RawPublicKey), keys["good"].Export(KeyBlobFormat.RawPublicKey));
    }

    [Fact]
    public async Task ALookupCancelledMidFetchDoesNotAbandonTheFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        _core.Answer = async (_, token) =>
        {
            // Honours the token it is given, as a real connection would.
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Contract.Jwks) };
        };

        // A request whose client hangs up while its lookup is fetching the set.
        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelled = _cache.FindAsync(FixtureKid, hangUp.Token);
        while (_core.Count == 0)
        {
            await Task.Yield();
        }

        await hangUp.CancelAsync();
        release.SetResult();
        _ = await Record.ExceptionAsync(() => cancelled);

        // Inside the cooldown, the next request finds the key the fetch brought.
        Advance(1);
        Assert.NotNull(await _cache.FindAsync(FixtureKid, ct));
        Assert.Equal(1, _core.Count);
    }

    [Fact]
    public async Task OneFetchRunsAtATime()
    {
        var release = new TaskCompletionSource();
        _core.Answer = async (_, _) =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Contract.Jwks) };
        };

        var lookups = Enumerable.Range(0, 5)
            .Select(_ => _cache.FindAsync(FixtureKid, TestContext.Current.CancellationToken))
            .ToArray();
        release.SetResult();
        var found = await Task.WhenAll(lookups);

        Assert.All(found, Assert.NotNull);
        Assert.Equal(1, _core.Count);
    }
}
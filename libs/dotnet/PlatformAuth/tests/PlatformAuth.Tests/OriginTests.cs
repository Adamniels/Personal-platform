using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace PlatformAuth.Tests;

/// <summary>
/// The Origin check, over a stub route that answers anything, as Core/tests/origin.rs does with
/// the same table: every method that can change state needs the platform's own origin, and a GET
/// is never checked. The C# twin of libs/python/platform-auth/tests/test_origin.py.
/// </summary>
public sealed class OriginTests
{
    private const string Platform = "https://platform.test";

    // A service with one route at `path`, answering anything, behind the check for `origin`.
    private static async Task<WebApplication> AppAt(string path, string origin)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UsePlatformOriginCheck(origin);
        app.UseRouting();
        app.MapMethods(path, ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"], () => "reached");
        await app.StartAsync();
        return app;
    }

    private static Task<HttpContext> Send(WebApplication app, string method, string path, params string[] origins) =>
        app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            foreach (var origin in origins)
            {
                context.Request.Headers.Append("Origin", origin);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheCoreOriginTableHolds()
    {
        await using var app = await AppAt("/", Platform);
        var cases = new (string Method, string[] Origins, int Expected)[]
        {
            ("POST", [Platform], 200),
            ("POST", ["https://evil.test"], 403),
            ("POST", [], 403),
            ("POST", ["null"], 403),
            ("POST", ["http://platform.test"], 403),
            ("POST", ["https://platform.test:8443"], 403),
            ("POST", ["https://platform.test.evil.test"], 403),
            ("POST", ["https://platform.test/"], 403),
            ("POST", ["HTTPS://PLATFORM.TEST"], 403),
            ("POST", [Platform, "https://evil.test"], 403),
            ("POST", [Platform, Platform], 403),
            ("PUT", [], 403),
            ("PATCH", ["https://evil.test"], 403),
            ("DELETE", [Platform], 200),
            ("GET", ["https://evil.test"], 200),
            ("GET", [], 200),
            ("HEAD", ["https://evil.test"], 200),
            ("OPTIONS", ["https://evil.test"], 200),
        };

        foreach (var (method, origins, expected) in cases)
        {
            var response = await Send(app, method, "/", origins);

            Assert.True(
                response.Response.StatusCode == expected,
                $"{method} with Origin [{string.Join(", ", origins)}]: {response.Response.StatusCode}, expected {expected}");
        }

        // The check sits in front of routing: a path the stub does not serve is refused first, so
        // a route added later is covered without anyone remembering to.
        await using var other = await AppAt("/feature", "https://other.test");
        Assert.Equal(403, (await Send(other, "POST", "/feature", Platform)).Response.StatusCode);
        Assert.Equal(403, (await Send(other, "POST", "/nowhere", Platform)).Response.StatusCode);
        Assert.Equal(404, (await Send(other, "POST", "/nowhere", "https://other.test")).Response.StatusCode);
    }

    [Fact]
    public async Task ARefusalIsPlainAndNeverReachesTheRoute()
    {
        await using var app = await AppAt("/", Platform);

        var response = await Send(app, "POST", "/", "https://evil.test");
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(403, response.Response.StatusCode);
        Assert.StartsWith("text/plain", response.Response.ContentType, StringComparison.Ordinal);
        Assert.DoesNotContain("reached", body, StringComparison.Ordinal);
    }
}
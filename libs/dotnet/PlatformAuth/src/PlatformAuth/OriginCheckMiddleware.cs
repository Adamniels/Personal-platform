using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PlatformAuth;

/// <summary>
/// The <c>Origin</c> check: every request that can change state must come from the platform's own
/// origin. With <c>SameSite=Lax</c> and never changing state on a GET, it is the whole CSRF
/// defence, see "How a feature authenticates" in <c>High-level/docs/feature-contract.md</c>. The
/// C# twin of <c>Core/src/origin.rs</c> and platform-auth's <c>origin.py</c>.
///
/// It sits in front of routing, so a route added later is covered without anyone remembering to,
/// and runs before a body is read. A websocket handshake is a GET and passes: no service serves
/// one yet, and one added later needs its own check on the handshake's Origin.
/// </summary>
public sealed class OriginCheckMiddleware
{
    /// <summary>The body of a refusal.</summary>
    public const string Refusal = "Forbidden: this request did not come from the platform's own origin.\n";

    private readonly RequestDelegate _next;
    private readonly string _origin;

    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="publicOrigin">The platform's origin, already in the form a browser sends.</param>
    public OriginCheckMiddleware(RequestDelegate next, string publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentException.ThrowIfNullOrEmpty(publicOrigin);
        _next = next;
        _origin = publicOrigin;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Exactly these three, as sent: methods are case sensitive, so "get" is checked.
        var method = context.Request.Method;
        if (method is "GET" or "HEAD" or "OPTIONS")
        {
            return _next(context);
        }

        // Exactly one Origin, equal byte for byte. No normalising and no prefix match, which is
        // how https://platform.test.evil.test would get through.
        var sent = context.Request.Headers.Origin;
        if (sent.Count == 1 && string.Equals(sent[0], _origin, StringComparison.Ordinal))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(Refusal, context.RequestAborted);
    }
}

public static class OriginCheckApplicationBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="OriginCheckMiddleware"/>. Call it first, before routing, authentication
    /// and every endpoint.
    /// </summary>
    public static IApplicationBuilder UsePlatformOriginCheck(this IApplicationBuilder app, string publicOrigin) =>
        app.UseMiddleware<OriginCheckMiddleware>(publicOrigin);
}
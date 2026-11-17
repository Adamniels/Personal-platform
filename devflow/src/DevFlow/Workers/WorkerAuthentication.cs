using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace DevFlow.Workers;

/// <summary>
/// The second way a request to Dev flow authenticates, beside <c>PlatformAuth</c>: a worker's key,
/// in <c>Authorization: Bearer dfw_…</c>, which leads to its project and so to that project's
/// user. A worker route takes <see cref="Policy"/> and nothing else, and a user's route only the
/// user's token, so neither stands in for the other: a user's token is not the shape of a key, and
/// a key is not a token the core signed. The cookie is never read, so a browser's session cannot
/// call a worker route, and worker routes sit outside the <c>Origin</c> check, since a worker is not
/// a browser. See "How a feature authenticates" in <c>High-level/docs/feature-contract.md</c> and
/// "Workers" in devflow/README.md.
/// </summary>
public static class WorkerAuthentication
{
    /// <summary>The scheme, and the policy a worker route requires.</summary>
    public const string Policy = "DevFlow.Worker";

    /// <summary>Where every worker route is, and the one path the <c>Origin</c> check leaves
    /// out.</summary>
    public const string RoutePrefix = "/devflow/api/worker";

    /// <summary>Registers the scheme and its policy. <see cref="WorkerKeys"/> must be registered
    /// too.</summary>
    public static IServiceCollection AddWorkerAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, WorkerAuthenticationHandler>(Policy, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Policy)
            .RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a worker route's, which the <c>Origin</c> check leaves
    /// out. Matched as written, case and all, so a path that only routes the same way is still
    /// checked; nothing but worker routes is under the prefix.
    /// </summary>
    public static bool IsWorkerPath(PathString path) =>
        path.StartsWithSegments(RoutePrefix, StringComparison.Ordinal);

    /// <summary>The project and user of the worker making the request, which the scheme set.</summary>
    public static WorkerCaller Worker(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.GetRequiredFeature<WorkerCaller>();
    }
}

/// <summary>
/// Reads the key from the one <c>Authorization</c> header and nowhere else, finds it through
/// <see cref="WorkerKeys.Find"/>, and answers anything else, no header included, with <c>401</c>
/// and an empty body, the same whatever the reason. Its failure messages are fixed phrases, so the
/// key never reaches a log through them.
/// </summary>
internal sealed class WorkerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    WorkerKeys keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Bearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        // Exactly one header, the Bearer scheme and one space; the key's own shape, checked by
        // Find, refuses anything after it.
        if (headers.Count != 1 || headers[0] is not { } header || !header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("unreadable Authorization header");
        }

        if (await keys.Find(header[Bearer.Length..], Context.RequestAborted) is not { } caller)
        {
            return AuthenticateResult.Fail("worker key refused");
        }

        Context.Features.Set(caller);
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", caller.UserId.ToString("D")),
                new Claim("project", caller.ProjectId.ToString("D")),
            ],
            Scheme.Name,
            nameType: "sub",
            roleType: null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
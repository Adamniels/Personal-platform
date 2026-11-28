using System.Security.Claims;
using System.Text.Encodings.Web;

using DevFlow.Protocol;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace DevFlow.Machines;

/// <summary>
/// The third way a request to Dev flow authenticates, beside <c>PlatformAuth</c> and the worker
/// scheme: a machine's key, in <c>Authorization: Bearer dfm_…</c>, which leads to its machine and
/// so to the machine's user. A machine route, <c>enroll</c> aside, takes <see cref="Policy"/> and
/// nothing else, so neither a user's token nor a worker's key stands in for it, nor it for them.
/// The cookie is never read, and machine routes sit outside the <c>Origin</c> check, since a machine
/// is not a browser. It is <see cref="Workers.WorkerAuthentication"/> again for another key, written
/// apart rather than shared, so each scheme reads one prefix of key and leads to one kind of
/// caller. See "Machines" in devflow/README.md.
/// </summary>
public static class MachineAuthentication
{
    /// <summary>The scheme, and the policy a machine route requires.</summary>
    public const string Policy = "DevFlow.Machine";

    /// <summary>Where every machine route is, and the second path the <c>Origin</c> check leaves
    /// out.</summary>
    public const string RoutePrefix = MachineApi.Prefix;

    /// <summary>Registers the scheme and its policy. <see cref="MachineKeys"/> must be registered
    /// too.</summary>
    public static IServiceCollection AddMachineAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MachineAuthenticationHandler>(Policy, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Policy)
            .RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a machine route's, which the <c>Origin</c> check leaves
    /// out. Matched as written, case and all, and by whole segments, so the user's
    /// <c>/devflow/api/machines</c> is still checked.
    /// </summary>
    public static bool IsMachinePath(PathString path) =>
        path.StartsWithSegments(RoutePrefix, StringComparison.Ordinal);

    /// <summary>The machine and user making the request, which the scheme set.</summary>
    public static MachineCaller Machine(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.GetRequiredFeature<MachineCaller>();
    }
}

/// <summary>
/// Reads the key from the one <c>Authorization</c> header and nowhere else, finds it through
/// <see cref="MachineKeys.Find"/>, and answers anything else, no header included, with <c>401</c>
/// and an empty body, the same whatever the reason. Its failure messages are fixed phrases, so the
/// key never reaches a log through them.
/// </summary>
internal sealed class MachineAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MachineKeys keys)
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
            return AuthenticateResult.Fail("machine key refused");
        }

        Context.Features.Set(caller);
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", caller.UserId.ToString("D")),
                new Claim("machine", caller.MachineId.ToString("D")),
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
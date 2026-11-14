using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PlatformAuth;

public static class PlatformAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the key cache, the authentication handler under one scheme per policy, and the
    /// two policies, <see cref="PlatformAuthPolicies.Page"/> and <see cref="PlatformAuthPolicies.Api"/>,
    /// which a route takes with <c>RequireAuthorization</c>. A <see cref="KeyCache"/> or a
    /// <see cref="TimeProvider"/> registered before this call is kept, which is how tests answer
    /// the core from memory and fix the clock. The service still calls <c>UseAuthentication</c>
    /// and <c>UseAuthorization</c>, and <c>UsePlatformOriginCheck</c> in front of both.
    /// </summary>
    public static IServiceCollection AddPlatformAuth(this IServiceCollection services, Action<PlatformAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new PlatformAuthOptions();
        configure(options);
        var coreUrl = options.CoreUrl
            ?? throw new ArgumentException("PlatformAuthOptions.CoreUrl must be set to the core's own address.", nameof(configure));

        services.TryAddSingleton(TimeProvider.System);
        // One client for the life of the service, its connections renewed so a moved core is
        // found again.
        services.TryAddSingleton(provider => new KeyCache(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
            coreUrl,
            provider.GetRequiredService<TimeProvider>()));

        services.AddAuthentication()
            .AddScheme<PlatformAuthSchemeOptions, PlatformAuthHandler>(
                PlatformAuthPolicies.Page, scheme => scheme.RedirectToRefresh = true)
            .AddScheme<PlatformAuthSchemeOptions, PlatformAuthHandler>(
                PlatformAuthPolicies.Api, scheme => scheme.RedirectToRefresh = false);
        services.AddAuthorizationBuilder()
            .AddPolicy(PlatformAuthPolicies.Page, policy => policy
                .AddAuthenticationSchemes(PlatformAuthPolicies.Page)
                .RequireAuthenticatedUser())
            .AddPolicy(PlatformAuthPolicies.Api, policy => policy
                .AddAuthenticationSchemes(PlatformAuthPolicies.Api)
                .RequireAuthenticatedUser());
        return services;
    }
}
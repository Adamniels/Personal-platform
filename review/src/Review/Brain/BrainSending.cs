using Microsoft.Extensions.DependencyInjection.Extensions;

using PlatformAuth;

using Review.Settings;

namespace Review.Brain;

/// <summary>
/// How grades reach the brain, wired into the app: the port, its adapter and the sender, and the
/// hook that starts sending after a user's request. See "What Review tells the brain" in
/// review/README.md.
/// </summary>
internal static class BrainSending
{
    /// <summary>The adapter as <see cref="IBrain"/>, unless one is registered first, as tests
    /// register a fake, and the sender over it.</summary>
    public static IServiceCollection AddBrain(this IServiceCollection services, ReviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBrain>(provider => new BrainAdapter(
            BrainAdapter.Client(), settings.BrainUrl, settings.PublicOrigin, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GradeSender>();
        return services;
    }

    /// <summary>
    /// After the response to any authenticated request under <c>/review/api/</c> has gone, the
    /// user's unsent grades are sent with the token that request was verified with. Placed after
    /// authorization, so the token is known, and before the endpoint, so the response is not yet
    /// started. The request never waits for the brain.
    /// </summary>
    public static IApplicationBuilder UseBrainSending(this IApplicationBuilder app)
    {
        var sender = app.ApplicationServices.GetRequiredService<GradeSender>();
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/review/api", StringComparison.Ordinal)
                && context.Features.Get<IPlatformToken>() is { } token)
            {
                var user = token.Claims.AccountId;
                var raw = token.Token;
                context.Response.OnCompleted(() =>
                {
                    sender.Schedule(user, raw);
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }
}
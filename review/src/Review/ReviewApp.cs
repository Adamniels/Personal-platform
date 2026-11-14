using System.Text.Json;

using PlatformAuth;

using Review.Health;
using Review.Settings;

namespace Review;

/// <summary>
/// Review as an ASP.NET Core app, built from its settings, so tests build one from settings of
/// their own. See review/README.md.
/// </summary>
public static class ReviewApp
{
    /// <param name="settings">Read once at startup, see <see cref="ReviewSettings.Load"/>.</param>
    /// <param name="configure">Runs before the app is built: <c>Program</c> sets where Kestrel
    /// listens, and tests put the app on a test server, with a key cache and a clock of their
    /// own, which <c>AddPlatformAuth</c> keeps.</param>
    public static WebApplication Build(ReviewSettings settings, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var builder = WebApplication.CreateBuilder();
        configure?.Invoke(builder);

        // snake_case on the wire, as the brain's and canary's JSON is.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        // An unhandled error is an RFC 9457 problem document, application/problem+json.
        builder.Services.AddProblemDetails();
        builder.Services.AddPlatformAuth(options => options.CoreUrl = settings.CoreUrl);

        var app = builder.Build();
        app.UseExceptionHandler();
        // In front of routing, so every route, one added later included, is covered.
        app.UsePlatformOriginCheck(settings.PublicOrigin);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealth();
        return app;
    }
}
using System.Text.Json;
using System.Text.Json.Serialization;

using DevFlow.Health;
using DevFlow.Pages;
using DevFlow.Settings;

using PlatformAuth;

namespace DevFlow;

/// <summary>
/// Dev flow as an ASP.NET Core app, built from its settings, so tests build one from settings of
/// their own. See devflow/README.md.
/// </summary>
public static class DevFlowApp
{
    /// <param name="settings">Read once at startup, see <see cref="DevFlowSettings.Load"/>.</param>
    /// <param name="webRoot">The page's build, web/dist from devflow's folder when it runs.</param>
    /// <param name="configure">Runs before the app is built: <c>Program</c> sets where Kestrel
    /// listens, and tests put the app on a test server, with a key cache and a clock of their
    /// own, which <c>AddPlatformAuth</c> keeps.</param>
    /// <exception cref="StartupException"><paramref name="webRoot"/> holds no build.</exception>
    public static WebApplication Build(DevFlowSettings settings, string webRoot, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PageEndpoints.CheckBuild(webRoot);

        // Named for this assembly rather than whichever one started the process, a test host
        // included, so the app is the same app either way: the schema's tags come from it.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DevFlowApp).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);

        // snake_case on the wire, as review's, the brain's and canary's JSON is. A number is a JSON
        // number, never a string holding one, which the web defaults would accept.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        // An unhandled error is an RFC 9457 problem document, application/problem+json.
        builder.Services.AddProblemDetails();
        builder.Services.AddPlatformAuth(options => options.CoreUrl = settings.CoreUrl);
        if (settings.ExposeOpenApi)
        {
            builder.Services.AddDevFlowOpenApi();
        }

        var app = builder.Build();
        app.UseExceptionHandler();
        // In front of routing, so every route, one added later included, is covered.
        app.UsePlatformOriginCheck(settings.PublicOrigin);
        app.UseBuiltFiles(webRoot);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealth();
        app.MapPages(webRoot);
        if (settings.ExposeOpenApi)
        {
            app.MapOpenApi(DevFlowOpenApi.Path);
        }

        return app;
    }
}
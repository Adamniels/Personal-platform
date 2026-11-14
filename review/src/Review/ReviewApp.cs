using System.Text.Json;
using System.Text.Json.Serialization;

using PlatformAuth;

using Review.Cards;
using Review.Database;
using Review.Decks;
using Review.Display;
using Review.Health;
using Review.Pages;
using Review.Scheduling;
using Review.Settings;

namespace Review;

/// <summary>
/// Review as an ASP.NET Core app, built from its settings, so tests build one from settings of
/// their own. See review/README.md.
/// </summary>
public static class ReviewApp
{
    /// <param name="settings">Read once at startup, see <see cref="ReviewSettings.Load"/>.</param>
    /// <param name="database">The door, opened at startup, see <see cref="ReviewDb.Open"/>. The
    /// app holds it and its caller disposes it.</param>
    /// <param name="webRoot">The page's build, web/dist from review's folder when it runs.</param>
    /// <param name="configure">Runs before the app is built: <c>Program</c> sets where Kestrel
    /// listens, and tests put the app on a test server, with a key cache and a clock of their
    /// own, which <c>AddPlatformAuth</c> keeps.</param>
    /// <exception cref="StartupException"><paramref name="webRoot"/> holds no build.</exception>
    public static WebApplication Build(ReviewSettings settings, ReviewDb database, string webRoot, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);
        PageEndpoints.CheckBuild(webRoot);

        // Named for this assembly rather than whichever one started the process, a test host
        // included, so the app is the same app either way: the schema's tags come from it.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ReviewApp).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);

        // snake_case on the wire, as the brain's and canary's JSON is. A number is a JSON number,
        // never a string holding one, which the web defaults would accept, and the schema says so.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        // An unhandled error is an RFC 9457 problem document, application/problem+json.
        builder.Services.AddProblemDetails();
        builder.Services.AddPlatformAuth(options => options.CoreUrl = settings.CoreUrl);
        // Registered as an instance, so the container hands it out and never disposes it.
        builder.Services.AddSingleton(database);
        if (settings.ExposeOpenApi)
        {
            builder.Services.AddReviewOpenApi();
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
        app.MapDecks();
        app.MapCards();
        app.MapGrades();
        app.MapQueue();
        app.MapDisplay(settings.DayZone);
        app.MapPages(webRoot);
        if (settings.ExposeOpenApi)
        {
            app.MapOpenApi(ReviewOpenApi.Path);
        }

        return app;
    }
}
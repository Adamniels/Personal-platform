using System.Text.Json;
using System.Text.Json.Serialization;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;
using DevFlow.Health;
using DevFlow.Link;
using DevFlow.Pages;
using DevFlow.Projects;
using DevFlow.Reading;
using DevFlow.Settings;
using DevFlow.Status;

using Microsoft.Extensions.DependencyInjection.Extensions;

using PlatformAuth;

using PlatformData;

namespace DevFlow;

/// <summary>
/// Dev flow as an ASP.NET Core app, built from its settings, so tests build one from settings of
/// their own. See devflow/README.md.
/// </summary>
public static class DevFlowApp
{
    /// <param name="settings">Read once at startup, see <see cref="DevFlowSettings.Load"/>.</param>
    /// <param name="database">The door, opened at startup with Dev flow's name, see
    /// <see cref="UserDb.Open"/>. The app holds it and its caller disposes it.</param>
    /// <param name="webRoot">The page's build, web/dist from devflow's folder when it runs.</param>
    /// <param name="configure">Runs before the app is built: <c>Program</c> sets where Kestrel
    /// listens, and tests put the app on a test server, with a key cache and a clock of their
    /// own, which <c>AddPlatformAuth</c> keeps.</param>
    /// <exception cref="StartupException"><paramref name="webRoot"/> holds no build.</exception>
    public static WebApplication Build(DevFlowSettings settings, UserDb database, string webRoot, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);
        PageEndpoints.CheckBuild(webRoot);

        // Named for this assembly rather than whichever one started the process, a test host
        // included, so the app is the same app either way: the schema's tags come from it.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DevFlowApp).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);
        // The request lines carry the query, and the return from the code host's carries a code
        // and a state, which nothing may print. See "Linking GitHub" in devflow/README.md.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        // The clock decides when a link's state and a user's token expire; a test's comes first.
        builder.Services.TryAddSingleton(TimeProvider.System);
        AddCodeHost(builder.Services, settings);

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
        // The one door every query goes through, as the caller. See "The door" in devflow/README.md.
        builder.Services.AddSingleton(database);
        // Seals and opens a user's secrets, through the door's transaction. See "The vault" in
        // devflow/README.md.
        builder.Services.AddSingleton(new Vault(settings.Vault));
        // Linking a user's GitHub account, and the one way to a token that acts as them. See
        // "Linking GitHub" in devflow/README.md.
        var redirect = GitHubLink.RedirectFor(settings.PublicOrigin);
        builder.Services.AddSingleton(provider => new GitHubLink(
            database, provider.GetRequiredService<Vault>(), provider.GetRequiredService<ICodeHostSignIn>(), provider.GetRequiredService<TimeProvider>(), redirect));
        builder.Services.AddSingleton(provider => new UserTokens(
            database, provider.GetRequiredService<Vault>(), provider.GetRequiredService<ICodeHostSignIn>(), provider.GetRequiredService<TimeProvider>()));
        // A user's projects, connected from what the code host lists for them. See "Connecting a
        // repository" in devflow/README.md.
        builder.Services.AddSingleton(provider => new ProjectStore(
            database, provider.GetRequiredService<UserTokens>(), provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>()));
        // Reading each project into the snapshot the pages read, the one seam that asks for a read,
        // and the loop that asks for every project's. See "Reading and the snapshot" in
        // devflow/docs/devflow-architecture.md.
        builder.Services.AddSingleton(provider => new ProjectReader(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ProjectReader>>()));
        builder.Services.AddSingleton(provider => new ProjectRefresher(
            provider.GetRequiredService<ProjectReader>(), provider.GetRequiredService<ILogger<ProjectRefresher>>()));
        if (settings.PollInterval is { } interval)
        {
            builder.Services.AddHostedService(provider => new Poller(
                database, provider.GetRequiredService<ProjectRefresher>(), provider.GetRequiredService<TimeProvider>(), interval, provider.GetRequiredService<ILogger<Poller>>()));
        }

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
        app.MapStatus(settings.CodeHost);
        app.MapGitHubLink();
        app.MapProjects(new Uri(settings.PublicOrigin));
        app.MapPages(webRoot);
        if (settings.CodeHost == CodeHostMode.Fake)
        {
            // Only in the fake mode: in the GitHub mode none of them exists. See "The code host"
            // in devflow/README.md.
            app.MapFakeCodeHost();
            app.MapFakeSignIn(redirect);
        }

        if (settings.ExposeOpenApi)
        {
            app.MapOpenApi(DevFlowOpenApi.Path);
        }

        return app;
    }

    /// <summary>
    /// The <see cref="ICodeHost"/> and <see cref="ICodeHostSignIn"/> the mode names, unless
    /// <c>configure</c> registered one first, so a test can put a code host of its own behind the
    /// app; a test's code host that also signs in is the sign in too, and a sign in a test
    /// registered is kept in either mode. See "The code host" in devflow/README.md.
    /// </summary>
    private static void AddCodeHost(IServiceCollection services, DevFlowSettings settings)
    {
        if (services.Any(service => service.ServiceType == typeof(ICodeHost)))
        {
            services.TryAddSingleton(provider => provider.GetRequiredService<ICodeHost>() as ICodeHostSignIn
                ?? throw new InvalidOperationException("the code host a test registered does not sign in, and registered no ICodeHostSignIn"));
            return;
        }

        switch (settings.CodeHost)
        {
            case CodeHostMode.Fake:
                // Registered as itself too, for the addresses that stage its worlds and sign in.
                services.AddSingleton(provider => new FakeCodeHost(provider.GetRequiredService<TimeProvider>()));
                services.AddSingleton<ICodeHost>(provider => provider.GetRequiredService<FakeCodeHost>());
                services.TryAddSingleton<ICodeHostSignIn>(provider => provider.GetRequiredService<FakeCodeHost>());
                break;
            case CodeHostMode.GitHub:
                // Read as the app, with the settings DevFlowSettings.Load required in this mode.
                services.AddGitHubCodeHost(settings.GitHub
                    ?? throw new ArgumentException("the GitHub mode's settings are missing", nameof(settings)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.CodeHost, "not a code host mode");
        }
    }
}
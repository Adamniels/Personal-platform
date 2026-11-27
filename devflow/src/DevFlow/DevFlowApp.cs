using System.Text.Json;
using System.Text.Json.Serialization;

using DevFlow.Acting;
using DevFlow.Cards;
using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;
using DevFlow.Decisions;
using DevFlow.Features;
using DevFlow.Health;
using DevFlow.Jobs;
using DevFlow.Link;
using DevFlow.Merging;
using DevFlow.Pages;
using DevFlow.Projects;
using DevFlow.PullRequests;
using DevFlow.Reading;
using DevFlow.Recipes;
using DevFlow.Reviews;
using DevFlow.Settings;
using DevFlow.Setup;
using DevFlow.Status;
using DevFlow.Stops;
using DevFlow.Values;
using DevFlow.Watching;
using DevFlow.Workers;

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
        builder.Services.AddSingleton(new WorkerStart(settings.WorkerImage, settings.WorkerUrl, settings.PublicOrigin));
        // A project's values, sealed in the vault, carried by every job that runs its code and
        // scrubbed from what Dev flow stores and posts. See "Setting up a project" in
        // devflow/README.md.
        builder.Services.AddSingleton(provider => new ProjectValues(database, provider.GetRequiredService<Vault>()));
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
            database, provider.GetRequiredService<UserTokens>(), provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ClaudeLogins>(), provider.GetRequiredService<WorkerStart>()));
        // A project's settings, made with it. See "Cards" in devflow/README.md.
        builder.Services.AddSingleton(new ProjectSettingsStore(database));
        // Reading each project into the snapshot the pages read, the one seam that asks for a read,
        // and the loop that asks for every project's. See "Reading and the snapshot" in
        // devflow/docs/devflow-architecture.md.
        builder.Services.AddSingleton(provider => new ProjectReader(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ProjectReader>>(),
            provider.GetRequiredService<RunLogFeed>()));
        builder.Services.AddSingleton(provider => new ProjectRefresher(
            provider.GetRequiredService<ProjectReader>(), provider.GetRequiredService<ILogger<ProjectRefresher>>()));
        builder.Services.AddSingleton(provider => new WaitingStore(database, provider.GetRequiredService<TimeProvider>()));
        // Merging and sending back from the page, as the user and never as the app. See "Acting
        // from the page" in devflow/README.md.
        builder.Services.AddSingleton(new ActionStore(database));
        // Each project's worker key, and the second way a request authenticates, a worker's key,
        // which leads to its project's user. See "Workers" in devflow/README.md.
        builder.Services.AddSingleton(provider => new WorkerKeys(database, provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddWorkerAuthentication();
        builder.Services.AddSingleton(provider => new WorkerStore(database, provider.GetRequiredService<TimeProvider>()));
        // The lines of each run's log as they are stored, which the live pages read. See "Jobs" in
        // devflow/README.md.
        builder.Services.AddSingleton(new RunLogFeed());
        // Watching each project's default branch: whether it hands out work, its stopped projects and
        // resuming one, and Dev flow's own merges, recorded so the watch finds them fine. See
        // "Watching branches" in devflow/README.md.
        builder.Services.AddSingleton(provider => new WatchStore(database, provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider => new AppMerges(database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>()));
        // The merge check, which merges an approved card as the app when every condition holds, the
        // seam that asks for one right after a result, the issues for the decisions a merged card
        // marked for the owner, and the comment and closing that tell a feature's card it landed.
        // See "The merge check" in devflow/README.md.
        builder.Services.AddSingleton(provider => new MergeChecker(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<AppMerges>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<MergeChecker>>()));
        // Kept when a test registered its own, which asks with the loops off.
        builder.Services.TryAddSingleton(new MergeRequests(enabled: settings.PollInterval is not null));
        builder.Services.AddSingleton(provider => new DecisionIssues(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<DecisionIssues>>()));
        builder.Services.AddSingleton(provider => new LandedCards(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<ILogger<LandedCards>>()));
        // The end of each feature whose cards are all done: the default branch brought into its
        // branch as the app. See "The end of a feature" in devflow/README.md.
        builder.Services.AddSingleton(provider => new FeatureEnds(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FeatureEnds>>()));
        // Each feature whose pull request is open: whether its head needs a review of the whole
        // feature, and whether it is ready for the owner. See "Reviewing a feature" in
        // devflow/README.md.
        builder.Services.AddSingleton(provider => new FeatureReadiness(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FeatureReadiness>>()));
        // Each feature whose pull request merged or closed: its cards told how it ended, as the app.
        // See "Merging a feature" in devflow/README.md.
        builder.Services.AddSingleton(provider => new FeatureOutcomes(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FeatureOutcomes>>()));
        // Each user's Claude login, sealed in the vault and opened only for one of their runs. See
        // "Claude login" in devflow/README.md.
        builder.Services.AddSingleton(provider => new ClaudeLogins(
            database, provider.GetRequiredService<Vault>(), provider.GetRequiredService<TimeProvider>()));
        // Handing a worker its project's next card, with the brief and what the job needs. See
        // "Jobs" in devflow/README.md.
        builder.Services.AddSingleton(provider => new Dispatcher(
            database,
            provider.GetRequiredService<WorkerStore>(),
            provider.GetRequiredService<WatchStore>(),
            provider.GetRequiredService<ClaudeLogins>(),
            provider.GetRequiredService<ICodeHost>(),
            provider.GetRequiredService<WorkerStart>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Dispatcher>>(),
            provider.GetRequiredService<ProjectValues>()));
        // Opening each built card's pull request as the app, on the same minute as the reads. See
        // "The pull request Dev flow opens" in devflow/README.md.
        builder.Services.AddSingleton(provider => new PullRequestOpener(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<PullRequestOpener>>()));
        // Posting each recorded verdict's copy on its pull request as the app, on the same minute.
        // See "Review" in devflow/README.md.
        builder.Services.AddSingleton(provider => new ReviewPoster(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ReviewPoster>>()));
        // Marking each stopped card on its issue as the app, on the same minute. See "Stops" in
        // devflow/README.md.
        builder.Services.AddSingleton(provider => new StopMarker(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), settings.PublicOrigin, provider.GetRequiredService<ILogger<StopMarker>>()));
        // Posting the questions of each card read with them as the app, on the same minute. See
        // "Jobs" in devflow/README.md.
        builder.Services.AddSingleton(provider => new Readings.RefusalMarker(
            database, provider.GetRequiredService<ICodeHost>(), settings.PublicOrigin, provider.GetRequiredService<ILogger<Readings.RefusalMarker>>()));
        builder.Services.AddSingleton(provider => new Readings.QuestionComments(
            database, provider.GetRequiredService<ICodeHost>(), settings.PublicOrigin, provider.GetRequiredService<ILogger<Readings.QuestionComments>>()));
        // Opening, following and taking the owner's comments on each setup's pull request, on the
        // same minute. See "Setting up a project" in devflow/README.md.
        builder.Services.AddSingleton(provider => new Setup.SetupLoop(
            database, provider.GetRequiredService<ICodeHost>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<Setup.SetupLoop>>()));
        if (settings.PollInterval is { } interval)
        {
            builder.Services.AddHostedService(provider => new Poller(
                database, provider.GetRequiredService<ProjectRefresher>(), provider.GetRequiredService<TimeProvider>(), interval, provider.GetRequiredService<ILogger<Poller>>()));
            builder.Services.AddHostedService(provider => new OpeningLoop(
                database,
                provider.GetRequiredService<PullRequestOpener>(),
                provider.GetRequiredService<ReviewPoster>(),
                provider.GetRequiredService<MergeChecker>(),
                provider.GetRequiredService<DecisionIssues>(),
                provider.GetRequiredService<LandedCards>(),
                provider.GetRequiredService<FeatureEnds>(),
                provider.GetRequiredService<FeatureReadiness>(),
                provider.GetRequiredService<FeatureOutcomes>(),
                provider.GetRequiredService<StopMarker>(),
                provider.GetRequiredService<Readings.QuestionComments>(),
                provider.GetRequiredService<Readings.RefusalMarker>(),
                provider.GetRequiredService<Setup.SetupLoop>(),
                provider.GetRequiredService<TimeProvider>(),
                interval,
                provider.GetRequiredService<ILogger<OpeningLoop>>()));
            // Deleting the lines of each run finished 90 days ago or more, once a day. See "Jobs"
            // in devflow/README.md.
            builder.Services.AddHostedService(provider => new RunLogPruner(
                database, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<RunLogPruner>>()));
            builder.Services.AddHostedService(provider => new MergeRequestLoop(
                provider.GetRequiredService<MergeRequests>(),
                provider.GetRequiredService<MergeChecker>(),
                provider.GetRequiredService<DecisionIssues>(),
                provider.GetRequiredService<LandedCards>(),
                provider.GetRequiredService<ILogger<MergeRequestLoop>>()));
        }

        if (settings.ExposeOpenApi)
        {
            builder.Services.AddDevFlowOpenApi();
        }

        var app = builder.Build();
        app.UseExceptionHandler();
        // In front of routing, so every route, one added later included, is covered, but the
        // worker's: a worker is not a browser, sends no Origin, and its routes never read a cookie,
        // so there is no session for another site to ride.
        app.UseWhen(
            context => !WorkerAuthentication.IsWorkerPath(context.Request.Path),
            browser => browser.UsePlatformOriginCheck(settings.PublicOrigin));
        app.UseBuiltFiles(webRoot);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealth();
        app.MapStatus(settings.CodeHost);
        app.MapGitHubLink();
        app.MapProjects(new Uri(settings.PublicOrigin));
        app.MapWaiting();
        app.MapScreenshots();
        app.MapActions();
        app.MapAnswers();
        app.MapDisplay();
        app.MapAttention();
        app.MapWorkers();
        app.MapWatch();
        app.MapJobs();
        app.MapStops();
        app.MapRunning();
        app.MapRunLogStream();
        app.MapRunLog();
        app.MapOwnerStops();
        app.MapCards();
        app.MapCardScreenshots();
        app.MapFeatures();
        app.MapRecipe();
        app.MapSetup();
        app.MapValues();
        app.MapClaudeLogin();
        app.MapPages(webRoot);
        if (settings.CodeHost == CodeHostMode.Fake)
        {
            // Only in the fake mode: in the GitHub mode none of them exists. See "The code host"
            // in devflow/README.md.
            app.MapFakeCodeHost();
            app.MapFakeSignIn(redirect);
            app.MapFakeWorker();
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
        AddUnscrubbedCodeHost(services, settings);
        ScrubPosts(services);
    }

    /// <summary>
    /// Puts <see cref="ScrubbingCodeHost"/> in front of the code host registered, which moves to
    /// the key <see cref="ScrubbingCodeHost.Unscrubbed"/>: so every part of Dev flow that asks for
    /// an <see cref="ICodeHost"/>, a test's own behind it included, posts through the one scrub of
    /// the project's values. See "Setting up a project" in devflow/README.md.
    /// </summary>
    private static void ScrubPosts(IServiceCollection services)
    {
        var registered = services.Last(service => service.ServiceType == typeof(ICodeHost) && !service.IsKeyedService);
        services.Remove(registered);
        services.Add(registered switch
        {
            { ImplementationInstance: ICodeHost instance } => ServiceDescriptor.KeyedSingleton(ScrubbingCodeHost.Unscrubbed, instance),
            { ImplementationFactory: { } factory } => ServiceDescriptor.KeyedSingleton<ICodeHost>(ScrubbingCodeHost.Unscrubbed, (provider, _) => (ICodeHost)factory(provider)),
            { ImplementationType: { } type } => ServiceDescriptor.KeyedSingleton(typeof(ICodeHost), ScrubbingCodeHost.Unscrubbed, type),
            _ => throw new InvalidOperationException("the code host is registered in a way Dev flow does not know"),
        });
        services.AddSingleton<ICodeHost>(provider => new ScrubbingCodeHost(
            provider.GetRequiredKeyedService<ICodeHost>(ScrubbingCodeHost.Unscrubbed), provider.GetRequiredService<ProjectValues>()));
    }

    private static void AddUnscrubbedCodeHost(IServiceCollection services, DevFlowSettings settings)
    {
        if (services.Any(service => service.ServiceType == typeof(ICodeHost)))
        {
            // The test's own code host, not the scrub in front of it, is the sign in when it signs in.
            services.TryAddSingleton(provider => provider.GetRequiredKeyedService<ICodeHost>(ScrubbingCodeHost.Unscrubbed) as ICodeHostSignIn
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
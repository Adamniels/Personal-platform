// Review's entry point: the settings from the environment, then the app, listening where
// REVIEW_LISTEN_ADDR says. A setting that does not parse stops it here, naming the setting. See
// review/README.md.
using Review;
using Review.Settings;

try
{
    var settings = ReviewSettings.Load(Environment.GetEnvironmentVariable);
    var app = ReviewApp.Build(settings, builder => builder.WebHost.UseUrls(settings.ListenUrl));
    await app.RunAsync();
    return 0;
}
catch (StartupException refused)
{
    await Console.Error.WriteLineAsync($"review: {refused.Message}");
    return 1;
}
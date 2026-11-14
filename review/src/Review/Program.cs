// Review's entry point: the settings from the environment, then the app over the page's build in
// web/dist, listening where REVIEW_LISTEN_ADDR says. A setting that does not parse, or a missing
// build, stops it here, naming what is wrong. It runs from review/, as infra/services.json starts
// it. See review/README.md.
using Review;
using Review.Settings;

try
{
    var settings = ReviewSettings.Load(Environment.GetEnvironmentVariable);
    var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "web", "dist");
    var app = ReviewApp.Build(settings, webRoot, builder => builder.WebHost.UseUrls(settings.ListenUrl));
    await app.RunAsync();
    return 0;
}
catch (StartupException refused)
{
    await Console.Error.WriteLineAsync($"review: {refused.Message}");
    return 1;
}
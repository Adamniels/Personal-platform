// Dev flow's entry point: the settings from the environment, then the app over the page's build in
// web/dist, listening where DEVFLOW_LISTEN_ADDR says. A setting that does not parse, or a missing
// build, stops it here, naming what is wrong. It runs from devflow/, as infra/services.json starts
// it. See devflow/README.md.
using DevFlow;
using DevFlow.Settings;

try
{
    var settings = DevFlowSettings.Load(Environment.GetEnvironmentVariable);
    var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "web", "dist");
    await using var app = DevFlowApp.Build(settings, webRoot, builder => builder.WebHost.UseUrls(settings.ListenUrl));
    await app.RunAsync();
    return 0;
}
catch (StartupException refused)
{
    await Console.Error.WriteLineAsync($"devflow: {refused.Message}");
    return 1;
}
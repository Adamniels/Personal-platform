// Dev flow's entry point: the settings from the environment, then the app, listening where
// DEVFLOW_LISTEN_ADDR says. A setting that does not parse stops it here, naming it. It runs from
// devflow/, as infra/services.json starts it. See devflow/README.md.
using DevFlow;
using DevFlow.Settings;

try
{
    var settings = DevFlowSettings.Load(Environment.GetEnvironmentVariable);
    await using var app = DevFlowApp.Build(settings, builder => builder.WebHost.UseUrls(settings.ListenUrl));
    await app.RunAsync();
    return 0;
}
catch (StartupException refused)
{
    await Console.Error.WriteLineAsync($"devflow: {refused.Message}");
    return 1;
}
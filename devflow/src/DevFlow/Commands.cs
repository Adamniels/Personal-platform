using DevFlow.Settings;

using PlatformData;

namespace DevFlow;

/// <summary>
/// What the process does, by its arguments: none serves, and <c>migrate</c> applies the
/// migrations and exits. Anything else is refused. Migrations never run when it serves. See
/// devflow/README.md.
/// </summary>
internal static class Commands
{
    /// <param name="read">Gives an environment variable's value, or null when it is unset.</param>
    /// <returns>The exit code: 0, 1 when it could not start or finish, 2 for arguments it does not
    /// take.</returns>
    public static async Task<int> Run(string[] args, Func<string, string?> read, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        switch (args)
        {
            case []:
                return await Serve(read, stderr, cancellationToken);
            case ["migrate"]:
                return await MigrateCommand.Run(
                    DevFlowSettings.Service, DevFlowSettings.OwnerDatabaseUrl, typeof(Commands).Assembly, read, stdout, stderr, migrations: null, cancellationToken);
            default:
                await stderr.WriteLineAsync("devflow: usage: devflow [migrate]");
                return 2;
        }
    }

    /// <summary>The settings from the environment, the door to the database as devflow_app, then
    /// the app over the page's build in web/dist, listening where DEVFLOW_LISTEN_ADDR says. A
    /// setting that does not parse, a database it cannot reach, or a missing build stops it here,
    /// naming what is wrong. It runs from devflow/, as infra/services.json starts it.</summary>
    private static async Task<int> Serve(Func<string, string?> read, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            var settings = DevFlowSettings.Load(read);
            await using var database = await UserDb.Open(DevFlowSettings.Service, settings.AppDatabase, DevFlowSettings.AppDatabaseUrl, cancellationToken);
            var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "web", "dist");
            await using var app = DevFlowApp.Build(settings, database, webRoot, builder => builder.WebHost.UseUrls(settings.ListenUrl));
            await app.RunAsync(cancellationToken);
            return 0;
        }
        catch (StartupException refused)
        {
            await stderr.WriteLineAsync($"devflow: {refused.Message}");
            return 1;
        }
    }
}
using Review.Database;
using Review.Database.Migrations;
using Review.Settings;

namespace Review;

/// <summary>
/// What the process does, by its arguments: none serves, and <c>migrate</c> applies the
/// migrations and exits. Anything else is refused. See review/README.md.
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
                return await MigrateCommand.Run(read, stdout, stderr, migrations: null, cancellationToken);
            default:
                await stderr.WriteLineAsync("review: usage: review [migrate]");
                return 2;
        }
    }

    /// <summary>The settings from the environment, the door to the database as review_app, then
    /// the app over the page's build in web/dist, listening where REVIEW_LISTEN_ADDR says. A
    /// setting that does not parse, a database it cannot reach, or a missing build stops it here,
    /// naming what is wrong. It runs from review/, as infra/services.json starts it.</summary>
    private static async Task<int> Serve(Func<string, string?> read, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            var settings = ReviewSettings.Load(read);
            await using var database = await ReviewDb.Open(settings.AppDatabase, cancellationToken);
            var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "web", "dist");
            await using var app = ReviewApp.Build(settings, database, webRoot, builder => builder.WebHost.UseUrls(settings.ListenUrl));
            await app.RunAsync(cancellationToken);
            return 0;
        }
        catch (StartupException refused)
        {
            await stderr.WriteLineAsync($"review: {refused.Message}");
            return 1;
        }
    }
}
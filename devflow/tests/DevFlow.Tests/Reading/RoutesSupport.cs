using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Settings;
using DevFlow.Tests.Link;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Reading;

/// <summary>
/// Dev flow on a test server over a migrated database of its own, with the fake code host behind a
/// <see cref="CountingCodeHost"/> on the harness's clock, for the routes that read the snapshot and
/// ask for a read. The caller is the fixture token's user, <see cref="User"/>; the loop is off, as
/// in every test's app.
/// </summary>
internal sealed class Routes : IAsyncDisposable
{
    public const string WaitingPath = "/devflow/api/waiting";

    private Routes(TestDatabase database, Harness app, FakeCodeHost fake, CountingCodeHost host)
    {
        Database = database;
        App = app;
        Fake = fake;
        Host = host;
    }

    public static Guid User => Linking.User;

    public static Guid Bob => Linking.Bob;

    public TestDatabase Database { get; }

    public Harness App { get; }

    public FakeCodeHost Fake { get; }

    public CountingCodeHost Host { get; }

    public FakeTimeProvider Time => App.Time;

    public UserDb Door => App.Services.GetRequiredService<UserDb>();

    public ProjectReader Reader => App.Services.GetRequiredService<ProjectReader>();

    /// <param name="configure">More, run after the harness's own, such as a log provider.</param>
    public static async Task<Routes> Start(Action<WebApplicationBuilder>? configure = null)
    {
        var database = await Databases.DevFlow.Migrated(Reading.Ct);
        var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Reading.Ct);
        var time = Harness.Clock();
        var fake = new FakeCodeHost(time);
        var host = new CountingCodeHost(fake);
        // The fake as itself too, for the fake mode's own routes, as the app registers it there.
        var app = await Harness.Start(database: door, codeHost: host, signIn: fake, time: time, configure: builder =>
        {
            builder.Services.AddSingleton(fake);
            configure?.Invoke(builder);
        });
        return new Routes(database, app, fake, host);
    }

    /// <summary>A project of <paramref name="user"/>'s, as connecting stores one.</summary>
    public Task<Guid> Project(Guid user, long repository = Reading.RepositoryId, string name = "Personal-platform") =>
        Reading.InsertProject(Door, user, repository, name);

    /// <summary>
    /// Whether every card a read picks is taken as already read and ready, as a reading job that
    /// found it so would leave it, so a test of anything after reading hands out its build at once.
    /// True unless a test of reading itself turns it off.
    /// </summary>
    public bool CardsRead { get; set; } = true;

    /// <summary>Reads the project as its user, as the loop would, then, with
    /// <see cref="CardsRead"/>, takes each card it picked that has no reading as read and
    /// ready.</summary>
    public async Task<ReadResult> Read(Guid project, Guid user)
    {
        var read = await Reader.Read(project, user, Reading.Ct);
        if (CardsRead)
        {
            await Readings.ReadingFixture.AllReady(Database, project, Reading.Ct);
        }

        return read;
    }

    /// <summary>Asks for the project to be read, as the page does, a POST from the platform's
    /// origin.</summary>
    public Task<HttpContext> Refresh(Guid project) =>
        App.Send("POST", $"/devflow/api/projects/{project}/refresh", Contract.Token, ("Origin", Linking.Origin));

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Database.DisposeAsync();
    }
}
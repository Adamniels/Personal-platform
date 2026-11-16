using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Settings;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Acting;

/// <summary>
/// Dev flow on a test server over a migrated database of its own, with the fake code host behind a
/// <see cref="CountingCodeHost"/> on the harness's clock, for merging and sending back from the
/// page. The caller is the fixture token's user, <see cref="User"/>, who is <see cref="World.Owner"/>
/// at the fake, so the runner's conventions read their comments as the owner's.
/// </summary>
internal sealed class Acting : IAsyncDisposable
{
    /// <summary>Who the user is at the fake, the owner the runner mentions.</summary>
    public static readonly FakeUser Owner = new(World.Owner, 70000001);

    /// <summary>A waiting pull request, 42, approved for its head with both checks green.</summary>
    public static readonly World Waiting = new();

    /// <summary>A stopped one, 43, which the runner gave up on.</summary>
    public static readonly World Stopped = new World
    {
        Pull = Waiting.Pull with { Number = 43, HeadCommit = new string('c', 40), Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/43") },
        Comments = [],
    }.Labelled("owner-blocked");

    private Acting(TestDatabase database, Harness app, FakeCodeHost fake, CountingCodeHost host)
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

    public UserDb Door => App.Services.GetRequiredService<UserDb>();

    /// <summary>The user's project, once <see cref="Start"/> made it.</summary>
    public Guid Project { get; private set; }

    /// <summary>
    /// Dev flow with <paramref name="pulls"/> staged in the user's world, as <see cref="Owner"/>,
    /// the user's GitHub linked unless <paramref name="link"/> is false, their project connected
    /// and read once, as the loop would.
    /// </summary>
    public static async Task<Acting> Start(bool link = true, params World[] pulls)
    {
        var database = await Databases.DevFlow.Migrated(Reading.Reading.Ct);
        var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Reading.Reading.Ct);
        var time = Harness.Clock();
        var fake = new FakeCodeHost(time);
        var host = new CountingCodeHost(fake);
        var app = await Harness.Start(database: door, codeHost: host, signIn: fake, time: time);
        var acting = new Acting(database, app, fake, host);
        acting.Stage(pulls.Length == 0 ? [Waiting] : pulls);
        if (link)
        {
            await acting.Link();
        }

        acting.Project = await Reading.Reading.InsertProject(acting.Door, User);
        await acting.Read();
        return acting;
    }

    /// <summary>Stages <paramref name="pulls"/> in the user's world, with the slice the runner
    /// mentions them on, and the user as <see cref="Owner"/>.</summary>
    public void Stage(params World[] pulls) =>
        Fake.Replace(User, Reading.Reading.Staging([.. pulls.Select(Reading.Reading.From)], [Reading.Reading.SliceOf(Waiting)]) with { User = Owner });

    /// <summary>Links the user's GitHub account all the way, as the Projects tab and the fake's
    /// page do.</summary>
    public async Task Link()
    {
        var started = await App.Send("POST", Linking.StartPath, Contract.Token, ("Origin", Linking.Origin));
        Assert.Equal(StatusCodes.Status200OK, started.Response.StatusCode);
        var query = Linking.Query(new Uri((string)JsonNode.Parse(await Harness.Body(started))!["authorize_url"]!));
        var code = Fake.Grant(User, query["code_challenge"], Linking.Redirect);
        var returned = await App.Send("GET", $"{GitHubLink.CallbackPath}?code={code}&state={query["state"]}", Contract.Token);
        Assert.DoesNotContain("failed", returned.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        await AccessToken();
    }

    /// <summary>Reads the user's project as the loop would.</summary>
    public async Task Read() =>
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await App.Services.GetRequiredService<DevFlow.Reading.ProjectReader>().Read(Project, User, Reading.Reading.Ct));

    /// <summary>The user's stored items, by number, read around row level security.</summary>
    public Task<List<Reading.Reading.ItemRow>> Items() => Reading.Reading.StoredItems(Database, Project);

    /// <summary>The stored item with the number.</summary>
    public async Task<Reading.Reading.ItemRow> Item(int number) => (await Items()).Single(item => item.Number == number);

    /// <summary>An item of Bob's, another user, in a project of his read as him.</summary>
    public async Task<Guid> BobsItem()
    {
        var bobs = await Reading.Reading.InsertProject(Door, Bob, name: "bobs-repository");
        Fake.Replace(Bob, Reading.Reading.Staging([Reading.Reading.From(Waiting)], [Reading.Reading.SliceOf(Waiting)]));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await App.Services.GetRequiredService<DevFlow.Reading.ProjectReader>().Read(bobs, Bob, Reading.Reading.Ct));
        return Assert.Single(await Reading.Reading.StoredItems(Database, bobs)).Id;
    }

    /// <summary>Asks to merge the item, as the page does, naming the commit it showed.</summary>
    public Task<HttpContext> Merge(Guid item, string? headCommit) =>
        App.SendJson("POST", $"/devflow/api/waiting/{item}/merge", Contract.Token, JsonSerializer.Serialize(new { head_commit = headCommit }));

    /// <summary>Asks to send the item back with the comment, as the page does.</summary>
    public Task<HttpContext> SendBack(Guid item, string? comment) =>
        App.SendJson("POST", $"/devflow/api/waiting/{item}/send-back", Contract.Token, JsonSerializer.Serialize(new { comment }));

    /// <summary>The user's access token, as <see cref="UserTokens.For"/> gives it now.</summary>
    public async Task<string> AccessToken() =>
        Assert.IsType<UserToken.Usable>(await App.Services.GetRequiredService<UserTokens>().For(User, Reading.Reading.Ct)).Token.Value;

    /// <summary>Runs <paramref name="sql"/> as the superuser, around row level security, as a test
    /// changes what a person did elsewhere.</summary>
    public async Task Execute(string sql)
    {
        await using var admin = await Database.OpenAdmin(Reading.Reading.Ct);
        await admin.ExecuteAsync(new CommandDefinition(sql, cancellationToken: Reading.Reading.Ct));
    }

    /// <summary>The answer's problem document, asserting the status and its media type.</summary>
    public static async Task<JsonObject> Problem(HttpContext response, int status)
    {
        Assert.Equal(status, response.Response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Response.ContentType, StringComparison.Ordinal);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Database.DisposeAsync();
    }
}
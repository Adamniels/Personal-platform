using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.Link;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// Connecting a repository, which makes it a project, listing them and disconnecting one, through
/// Dev flow's routes against the fake code host and a real Postgres. What can be connected is what
/// the code host lists for the person, read with their own token; a project is stored with the
/// owner and name the code host gave, never the request's. See "Connecting a repository" in
/// devflow/README.md.
/// </summary>
public sealed class ConnectTests
{
    private const string RepositoriesPath = "/devflow/api/github/repositories";
    private const string ProjectsPath = "/devflow/api/projects";

    private static CancellationToken Ct => Linking.Ct;

    /// <summary>Two installations, one on the person's own account with two repositories and one
    /// on an organisation's with one.</summary>
    private static FakeWorld World => new(
    [
        new FakeInstallation(41, "Adamniels",
        [
            Empty(9001, "Adamniels", "Personal-platform"),
            Empty(9002, "Adamniels", "habit-tracker"),
        ]),
        new FakeInstallation(52, "uppsala-lab", [Empty(9101, "uppsala-lab", "thesis")]),
    ]);

    private static FakeRepository Empty(long id, string owner, string name) => new(id, owner, name, [], [], [], [], []);

    /// <summary>Dev flow with the world staged for the user, who has linked GitHub.</summary>
    private static async Task<Linking> Linked()
    {
        var linking = await Linking.Start();
        linking.Fake.Replace(Linking.User, World);
        Assert.Equal("/devflow/projects", await linking.LinkAll());
        return linking;
    }

    private static Task<HttpContext> Connect(Linking linking, long installation, long repository, string? extra = null) =>
        linking.App.SendJson("POST", ProjectsPath, Contract.Token, $$"""{"installation_id": {{installation}}, "repository_id": {{repository}}{{extra}}}""");

    private static async Task<JsonNode> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response))!;

    [Fact]
    public async Task TheRepositoriesICanConnectAreListed()
    {
        await using var linking = await Linked();
        Assert.Equal(StatusCodes.Status201Created, (await Connect(linking, 41, 9002)).Response.StatusCode);

        var response = await linking.App.Get(RepositoriesPath, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        // Each installation with the account it is on, its repositories with their owner and name,
        // and whether each is connected already; each sorted by name, so the page shows them so.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""
                {
                  "installations": [
                    {
                      "id": 41,
                      "account": "Adamniels",
                      "repositories": [
                        { "id": 9002, "owner": "Adamniels", "name": "habit-tracker", "connected": true },
                        { "id": 9001, "owner": "Adamniels", "name": "Personal-platform", "connected": false }
                      ]
                    },
                    {
                      "id": 52,
                      "account": "uppsala-lab",
                      "repositories": [
                        { "id": 9101, "owner": "uppsala-lab", "name": "thesis", "connected": false }
                      ]
                    }
                  ],
                  "install_url": "https://platform.test/devflow/fake-github/install"
                }
                """),
            await Json(response)));

        // With the app installed nowhere the person can reach, the list is empty and the address
        // to install it is still given.
        linking.Fake.Replace(Linking.User, FakeWorld.Empty);
        var nothing = await Json(await linking.App.Get(RepositoriesPath, Contract.Token));
        Assert.Empty(nothing["installations"]!.AsArray());
        Assert.Equal("https://platform.test/devflow/fake-github/install", (string)nothing["install_url"]!);
    }

    [Fact]
    public async Task ConnectingMakesAProject()
    {
        await using var linking = await Linked();
        var now = linking.Time.GetUtcNow();

        // An owner and a name in the request are not the request's to give.
        var response = await Connect(linking, 41, 9001, """, "owner": "someone", "name": "elsewhere" """);

        Assert.Equal(StatusCodes.Status201Created, response.Response.StatusCode);
        var created = (await Json(response)).AsObject();
        var id = Guid.Parse((string)created["id"]!);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse($$"""
                {
                  "id": "{{id}}",
                  "installation_id": 41,
                  "repository_id": 9001,
                  "owner": "Adamniels",
                  "name": "Personal-platform",
                  "created_at": "{{now:yyyy-MM-ddTHH:mm:ss}}+00:00"
                }
                """),
            created));

        // Listed, and stored with the code host's owner and name, GitHub as its code host and the
        // runner's bot as whose pull requests are read.
        var listed = await Json(await linking.App.Get(ProjectsPath, Contract.Token));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"projects": [{{created.ToJsonString()}}]}"""), listed));
        var row = Assert.Single(await Rows(linking));
        Assert.Equal(new ProjectRow(id, Linking.User, "github", 41, 9001, "Adamniels", "Personal-platform", "mac-mini-agent"), row);
    }

    [Fact]
    public async Task ConnectingTwiceIsRefused()
    {
        await using var linking = await Linked();
        Assert.Equal(StatusCodes.Status201Created, (await Connect(linking, 41, 9001)).Response.StatusCode);

        var again = await Connect(linking, 41, 9001);

        Assert.Equal(StatusCodes.Status409Conflict, again.Response.StatusCode);
        Assert.StartsWith("application/problem+json", again.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("This repository is already a project", (string)(await Json(again))["title"]!);
        Assert.Single(await Rows(linking));
    }

    [Fact]
    public async Task ARepositoryICannotReachIsRefused()
    {
        await using var linking = await Linked();

        // An id the installation does not list, an installation the person cannot reach, and a
        // repository they can reach named under another installation than its own.
        foreach (var (installation, repository) in new[] { (41L, 9999L), (77L, 9001L), (41L, 9101L) })
        {
            var response = await Connect(linking, installation, repository);

            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
            Assert.StartsWith("application/problem+json", response.Response.ContentType, StringComparison.Ordinal);
        }

        Assert.Empty(await Rows(linking));
        Assert.Empty(await PollTargets(linking));
    }

    [Fact]
    public async Task WithoutALinkedAccountConnectingIsRefusedSayingSo()
    {
        await using var linking = await Linking.Start();
        linking.Fake.Replace(Linking.User, World);

        var connect = await Connect(linking, 41, 9001);
        var list = await linking.App.Get(RepositoriesPath, Contract.Token);

        foreach (var response in new[] { connect, list })
        {
            Assert.Equal(StatusCodes.Status409Conflict, response.Response.StatusCode);
            Assert.StartsWith("application/problem+json", response.Response.ContentType, StringComparison.Ordinal);
            var problem = await Json(response);
            Assert.Equal(409, (int)problem["status"]!);
            Assert.Equal("GitHub is not linked", (string)problem["title"]!);
            Assert.Equal("Link your GitHub account on the Projects tab, then connect a repository.", (string)problem["detail"]!);
        }

        Assert.Empty(await Rows(linking));

        // A link GitHub stopped taking says so too.
        await linking.LinkAll();
        await linking.Door.InUserTransaction(Linking.User, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE github_accounts SET state = 'needs_relink'", transaction: transaction, cancellationToken: Ct)), Ct);
        var expired = await Connect(linking, 41, 9001);
        Assert.Equal(StatusCodes.Status409Conflict, expired.Response.StatusCode);
        Assert.Equal("Your GitHub link has expired", (string)(await Json(expired))["title"]!);

        // Listing projects needs no link.
        Assert.Equal(StatusCodes.Status200OK, (await linking.App.Get(ProjectsPath, Contract.Token)).Response.StatusCode);
    }

    [Fact]
    public async Task WhenGitHubDoesNotAnswerItSaysSoAndStoresNothing()
    {
        await using var linking = await Linked();
        linking.Fake.SetUnavailable(Linking.User, unavailable: true);

        foreach (var response in new[] { await linking.App.Get(RepositoriesPath, Contract.Token), await Connect(linking, 41, 9001) })
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.Response.StatusCode);
            Assert.Equal("GitHub did not answer", (string)(await Json(response))["title"]!);
        }

        Assert.Empty(await Rows(linking));
    }

    [Fact]
    public async Task AMissingFieldIsRefusedNamingIt()
    {
        await using var linking = await Linked();

        var response = await linking.App.SendJson("POST", ProjectsPath, Contract.Token, """{"installation_id": 41}""");

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
        var problem = await Json(response);
        Assert.Equal(["repository_id"], problem["errors"]!.AsObject().Select(pair => pair.Key));
        Assert.Empty(await Rows(linking));
    }

    [Fact]
    public async Task DisconnectingRemovesTheProjectAndWhatWasReadForIt()
    {
        await using var linking = await Linked();
        var id = Guid.Parse((string)(await Json(await Connect(linking, 41, 9001)))["id"]!);
        await Connect(linking, 41, 9002);
        // What a later slice reads for a project hangs off it the way this table does.
        await using (var owner = await linking.Database.OpenOwner(Ct))
        {
            await TestDatabase.Execute(owner, $"""
                CREATE TABLE probe_reads (project_id uuid NOT NULL REFERENCES projects (id) ON DELETE CASCADE, user_id uuid NOT NULL);
                ALTER TABLE probe_reads ENABLE ROW LEVEL SECURITY;
                ALTER TABLE probe_reads FORCE ROW LEVEL SECURITY;
                CREATE POLICY probe_reads_user ON probe_reads USING ({Guard}) WITH CHECK ({Guard});
                """, Ct);
        }

        await linking.Door.InUserTransaction(Linking.User, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO probe_reads (project_id, user_id) VALUES (@id, @user)", new { id, user = Linking.User }, transaction, cancellationToken: Ct)), Ct);
        var world = linking.Fake.World(Linking.User);
        // Disconnecting needs no link.
        await linking.App.Send("DELETE", "/devflow/api/github/link", Contract.Token, ("Origin", Linking.Origin));

        var response = await linking.App.Send("DELETE", $"{ProjectsPath}/{id}", Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal([9002L], (await Rows(linking)).Select(row => row.RepositoryId));
        Assert.DoesNotContain(id, await PollTargets(linking));
        await using (var admin = await linking.Database.OpenAdmin(Ct))
        {
            Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM probe_reads", cancellationToken: Ct)));
        }

        // Nothing changed on the code host, and a second disconnect finds nothing.
        Assert.Same(world, linking.Fake.World(Linking.User));
        var again = await linking.App.Send("DELETE", $"{ProjectsPath}/{id}", Contract.Token, ("Origin", Linking.Origin));
        Assert.Equal(StatusCodes.Status404NotFound, again.Response.StatusCode);
        Assert.StartsWith("application/problem+json", again.Response.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneUsersProjectsAreNotAnothers()
    {
        await using var linking = await Linked();
        // Bob connected two repositories, one of them the same as the user will.
        var bobs = Guid.NewGuid();
        await linking.Door.InUserTransaction(Linking.Bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
            VALUES (@bobs, @bob, 'github', 41, 9001, 'Adamniels', 'Personal-platform', now()),
                   (gen_random_uuid(), @bob, 'github', 41, 9002, 'Adamniels', 'habit-tracker', now())
            """,
            new { bobs, bob = Linking.Bob },
            transaction,
            cancellationToken: Ct)), Ct);

        // The user connects the same repository as their own project.
        Assert.Equal(StatusCodes.Status201Created, (await Connect(linking, 41, 9001)).Response.StatusCode);

        var listed = (await Json(await linking.App.Get(ProjectsPath, Contract.Token)))["projects"]!.AsArray();
        Assert.NotEqual(bobs, Guid.Parse((string)Assert.Single(listed)!["id"]!));
        // What Bob connected is not connected for the user.
        var repositories = (await Json(await linking.App.Get(RepositoriesPath, Contract.Token)))["installations"]![0]!["repositories"]!.AsArray();
        Assert.Equal([("habit-tracker", false), ("Personal-platform", true)], repositories.Select(r => ((string)r!["name"]!, (bool)r["connected"]!)));

        // Bob's project is not there for the user to disconnect.
        var response = await linking.App.Send("DELETE", $"{ProjectsPath}/{bobs}", Contract.Token, ("Origin", Linking.Origin));
        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal(2, (await Rows(linking, Linking.Bob)).Count);
    }

    private static readonly string Guard = PlatformData.UserDb.GuardFor(Settings.DevFlowSettings.Service);

    /// <summary>The user's projects, read around row level security.</summary>
    private static async Task<List<ProjectRow>> Rows(Linking linking, Guid? user = null)
    {
        await using var admin = await linking.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT id AS Id, user_id AS UserId, code_host AS CodeHost, installation_id AS InstallationId, repository_id AS RepositoryId,
                   owner AS Owner, name AS Name, bot_login AS BotLogin
            FROM projects WHERE user_id = @user ORDER BY name
            """,
            new { user = user ?? Linking.User },
            cancellationToken: Ct))];
    }

    /// <summary>Every poll target's project.</summary>
    private static async Task<List<Guid>> PollTargets(Linking linking)
    {
        await using var admin = await linking.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<Guid>(new CommandDefinition("SELECT project_id FROM poll_targets", cancellationToken: Ct))];
    }

    internal sealed record ProjectRow(Guid Id, Guid UserId, string CodeHost, long InstallationId, long RepositoryId, string Owner, string Name, string BotLogin);
}
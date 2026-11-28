using System.Reflection;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.GitHub;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http;

using PlatformData;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The worker API, <c>/devflow/api/worker/</c>: a worker introduces itself with <c>hello</c> and
/// asks for work with <c>jobs/next</c>, holding its project's key, and acts as that project's user
/// and no one else; and the project's workers route, the user's, lists them. The worker scheme
/// and the user's token never stand in for each other. See "Workers" in devflow/README.md.
/// </summary>
public sealed class WorkerApiTests
{
    private static CancellationToken Ct => Workers.Ct;

    [Fact]
    public async Task AWorkerIntroducesItselfAndIsListed()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var made = routes.Time.GetUtcNow();
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        var first = routes.Time.GetUtcNow();
        var worker = Guid.Parse("0190a8f0-0000-7000-8000-0000000000f1");

        var hello = await Workers.SayHello(routes, key, worker);

        Assert.Equal(StatusCodes.Status204NoContent, hello.Response.StatusCode);
        Assert.Equal("", await Harness.Body(hello));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse($$"""
                {
                  "key_made_at": "{{made:yyyy-MM-ddTHH:mm:ss}}+00:00",
                  "recipe_version": null,
                  "held": "unwatched",
                  "cannot_run": null,
                  "budget": null,
                  "workers": [
                    {
                      "id": "{{worker}}",
                      "name": "fodr-worker-1",
                      "recipe_version": "{{Workers.RecipeVersion}}",
                      "base_image": "{{Workers.BaseImage}}",
                      "first_seen_at": "{{first:yyyy-MM-ddTHH:mm:ss}}+00:00",
                      "last_seen_at": "{{first:yyyy-MM-ddTHH:mm:ss}}+00:00",
                      "rebuild": "base_image"
                    }
                  ]
                }
                """),
            WithoutCommand(await Workers.List(routes, project))));
        var row = Assert.Single(await Workers.Rows(routes.Database));
        Assert.Equal(new Workers.WorkerRow(project, worker, Workers.User, "fodr-worker-1", Workers.RecipeVersion, Workers.BaseImage), row);

        // Saying hello again, as a restarted worker with the same id would, keeps when it was first
        // seen and takes what it reports now.
        routes.Time.Advance(TimeSpan.FromMinutes(5));
        var again = routes.Time.GetUtcNow();
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.SayHello(routes, key, worker, name: "fodr-worker-2", recipe: "4")).Response.StatusCode);
        var listed = Assert.Single((await Workers.List(routes, project))["workers"]!.AsArray())!;
        Assert.Equal("fodr-worker-2", (string)listed["name"]!);
        Assert.Equal("4", (string)listed["recipe_version"]!);
        Assert.Equal($"{first:yyyy-MM-ddTHH:mm:ss}+00:00", (string)listed["first_seen_at"]!);
        Assert.Equal($"{again:yyyy-MM-ddTHH:mm:ss}+00:00", (string)listed["last_seen_at"]!);

        // A second worker of the project is listed after the first.
        var second = Guid.Parse("0190a8f0-0000-7000-8000-0000000000f2");
        await Workers.SayHello(routes, key, second, name: "fodr-worker-3");
        Assert.Equal([worker.ToString(), second.ToString()], (await Workers.List(routes, project))["workers"]!.AsArray().Select(w => (string)w!["id"]!));
    }

    [Fact]
    public async Task AProjectWithoutAKeyListsNoWorkers()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"key_made_at": null, "recipe_version": null, "held": "unwatched", "cannot_run": null, "budget": null, "workers": []}"""),
            WithoutCommand(await Workers.List(routes, project))));
    }

    [Fact]
    public async Task AHelloBreakingARuleIs422()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var worker = Guid.NewGuid();
        var tooLong = new string('a', 256);

        foreach (var (json, field) in new[]
        {
            ($$"""{"name": "w", "recipe_version": "3", "base_image": "b"}""", "worker_id"),
            ($$"""{"worker_id": "00000000-0000-0000-0000-000000000000", "name": "w", "recipe_version": "3", "base_image": "b"}""", "worker_id"),
            ($$"""{"worker_id": "{{worker}}", "recipe_version": "3", "base_image": "b"}""", "name"),
            ($$"""{"worker_id": "{{worker}}", "name": " ", "recipe_version": "3", "base_image": "b"}""", "name"),
            ($$"""{"worker_id": "{{worker}}", "name": "{{tooLong}}", "recipe_version": "3", "base_image": "b"}""", "name"),
            ($$"""{"worker_id": "{{worker}}", "name": "w", "base_image": "b"}""", "recipe_version"),
            ($$"""{"worker_id": "{{worker}}", "name": "w", "recipe_version": "3", "base_image": ""}""", "base_image"),
        })
        {
            var response = await Workers.AsWorker(routes.App, Workers.HelloPath, json, null, Workers.Bearer(key));

            Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
            Assert.Contains(field, (await Workers.Json(response))["errors"]!.AsObject().Select(pair => pair.Key));
        }

        Assert.Empty(await Workers.Rows(routes.Database));
    }

    [Fact]
    public async Task AskingForWorkAnswersNothingYet()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var worker = Guid.NewGuid();
        await Workers.SayHello(routes, key, worker);
        routes.Time.Advance(TimeSpan.FromMinutes(3));
        var asked = routes.Time.GetUtcNow();

        var response = await Workers.AskForWork(routes, key, worker);

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        Assert.Equal("", await Harness.Body(response));
        var listed = Assert.Single((await Workers.List(routes, project))["workers"]!.AsArray())!;
        Assert.Equal($"{asked:yyyy-MM-ddTHH:mm:ss}+00:00", (string)listed["last_seen_at"]!);

        // A worker that has not said hello is told to, and nothing is stored for it.
        var stranger = await Workers.AskForWork(routes, key, Guid.NewGuid());
        Assert.Equal(StatusCodes.Status409Conflict, stranger.Response.StatusCode);
        Assert.Equal("Say hello first", (string)(await Workers.Json(stranger))["title"]!);
        Assert.Single(await Workers.Rows(routes.Database));
        // And a request without its id breaks a rule.
        var nameless = await Workers.AsWorker(routes.App, Workers.NextPath, "{}", null, Workers.Bearer(key));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, nameless.Response.StatusCode);
    }

    [Fact]
    public async Task AMissingWrongOrOldKeyIs401()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var old = await Workers.NewKey(routes, project);
        var key = await Workers.NewKey(routes, project);
        var worker = Guid.NewGuid();
        var unknown = "dfw_" + new string('A', 43);

        string[][] refused =
        [
            // No header.
            [],
            // Malformed: another scheme, no key, a key of the wrong shape, a space too many, two headers.
            ["Basic " + key],
            ["Bearer"],
            ["Bearer "],
            [key],
            ["Bearer dfw_short"],
            ["Bearer " + key[4..]],
            ["Bearer " + key + "="],
            ["Bearer  " + key],
            ["Bearer " + key + " extra"],
            [Workers.Bearer(key), Workers.Bearer(key)],
            // Unknown, and replaced.
            [Workers.Bearer(unknown)],
            [Workers.Bearer(old)],
        ];

        foreach (var path in new[] { Workers.HelloPath, Workers.NextPath })
        {
            foreach (var headers in refused)
            {
                var response = await Workers.AsWorker(routes.App, path, Workers.Hello(worker), null, headers);

                Assert.True(
                    response.Response.StatusCode == StatusCodes.Status401Unauthorized,
                    $"{path} with [{string.Join(", ", headers.Select(h => h.Length > 12 ? h[..12] + "…" : h))}] answered {response.Response.StatusCode}");
                Assert.Equal("", await Harness.Body(response));
            }
        }

        Assert.Empty(await Workers.Rows(routes.Database));
        // The right key, to show the requests above were refused for their header alone.
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.SayHello(routes, key, worker)).Response.StatusCode);
    }

    [Fact]
    public async Task AWorkerKeyCannotCallTheUsersRoutes()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var worker = Guid.NewGuid();

        // A worker's key on the user's routes, as the header a worker sends, reading and changing.
        foreach (var (method, path) in new[]
        {
            ("GET", "/devflow/api/projects"),
            ("GET", Workers.ListPath(project)),
            ("GET", "/devflow/api/whoami"),
            ("POST", Workers.KeyPath(project)),
            ("DELETE", $"/devflow/api/projects/{project}"),
        })
        {
            var response = await routes.App.Send(method, path, null, ("Authorization", Workers.Bearer(key)), ("Origin", Linking.Origin));
            Assert.True(response.Response.StatusCode == StatusCodes.Status401Unauthorized, $"{method} {path} answered {response.Response.StatusCode}");
        }

        // The user's token on the worker's routes, in the header and in the cookie.
        foreach (var path in new[] { Workers.HelloPath, Workers.NextPath })
        {
            var header = await Workers.AsWorker(routes.App, path, Workers.Hello(worker), null, Workers.Bearer(Contract.Token));
            var cookie = await Workers.AsWorker(routes.App, path, Workers.Hello(worker), Contract.Token);
            Assert.Equal(StatusCodes.Status401Unauthorized, header.Response.StatusCode);
            Assert.Equal(StatusCodes.Status401Unauthorized, cookie.Response.StatusCode);
        }

        // The project and its key are untouched, and no worker was stored.
        Assert.Empty(await Workers.Rows(routes.Database));
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.SayHello(routes, key, worker)).Response.StatusCode);
        // A worker's request needs no Origin, and sets no cookie.
        var hello = await Workers.SayHello(routes, key, worker);
        Assert.False(hello.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task AWorkerActsAsItsProjectsUserOnly()
    {
        await using var routes = await Routes.Start();
        var alices = await routes.Project(Workers.User);
        var bobs = await routes.Project(Workers.Bob, repository: 9002, name: "habit-tracker");
        var aliceKey = await Workers.NewKey(routes, alices);
        var bobKey = await Workers.KeyOf(routes, Workers.Bob, bobs);
        // The same worker id in both, which a worker of one cannot use to reach the other's.
        var worker = Guid.NewGuid();

        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.SayHello(routes, aliceKey, worker, name: "alices")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.SayHello(routes, bobKey, worker, name: "bobs")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.AskForWork(routes, aliceKey, worker)).Response.StatusCode);

        // Each row is its own user's and project's, and through the door each user sees only theirs.
        Assert.Equal(
            new HashSet<Workers.WorkerRow>
            {
                new(alices, worker, Workers.User, "alices", Workers.RecipeVersion, Workers.BaseImage),
                new(bobs, worker, Workers.Bob, "bobs", Workers.RecipeVersion, Workers.BaseImage),
            },
            (await Workers.Rows(routes.Database)).ToHashSet());
        Assert.Equal(["alices"], await Names(routes.Door, Workers.User));
        Assert.Equal(["bobs"], await Names(routes.Door, Workers.Bob));
        Assert.Equal(["alices"], (await Workers.List(routes, alices))["workers"]!.AsArray().Select(w => (string)w!["name"]!));

        // The lookup outside a user only names a project and its user; what the worker may do is
        // decided as that user, through row level security. A lookup row pointed at Bob, which no
        // code path writes, opens nothing of his: Alice's key is not among his.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE worker_key_lookup SET user_id = @bob WHERE project_id = @alices",
                new { bob = Workers.Bob, alices },
                cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, (await Workers.SayHello(routes, aliceKey, Guid.NewGuid(), name: "planted")).Response.StatusCode);

        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            // Alice's hash leading to Bob's project and Bob himself, in place of his own key's row.
            await admin.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM worker_key_lookup WHERE project_id = @bobs;
                UPDATE worker_key_lookup SET project_id = @bobs, user_id = @bob WHERE project_id = @alices;
                """,
                new { bobs, alices, bob = Workers.Bob },
                cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, (await Workers.SayHello(routes, aliceKey, Guid.NewGuid(), name: "planted")).Response.StatusCode);
        Assert.Equal(2, (await Workers.Rows(routes.Database)).Count);
    }

    [Fact]
    public async Task TheKeyIsComparedInConstantTime()
    {
        // The presented key is hashed and the hash looked up: the table outside a user holds the
        // hash as its key and nothing that is the key.
        await using var routes = await Routes.Start();
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var columns = await admin.QueryAsync<(string, string)>(new CommandDefinition(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'worker_key_lookup' ORDER BY column_name",
            cancellationToken: Ct));
        Assert.Equal([("key_hash", "bytea"), ("project_id", "uuid"), ("user_id", "uuid")], columns);
        var key = await admin.QueryAsync<string>(new CommandDefinition(
            """
            SELECT a.attname FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'worker_key_lookup'::regclass AND i.indisprimary
            """,
            cancellationToken: Ct));
        Assert.Equal(["key_hash"], key);

        // And no code in the worker's namespace compares strings or spans for equality, which is
        // where a key compared with a stored value in ordinary time would show.
        var equality = new List<MethodBase>();
        equality.AddRange(typeof(string).GetMethods().Where(m => m.Name is "Equals" or "op_Equality" or "op_Inequality" or "Compare" or "CompareOrdinal" or "CompareTo"));
        equality.AddRange(typeof(MemoryExtensions).GetMethods().Where(m => m.Name is "SequenceEqual" or "Equals" or "SequenceCompareTo" or "CompareTo")
            .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(char)) : m));
        equality.AddRange(typeof(MemoryExtensions).GetMethods().Where(m => m.Name is "SequenceEqual" && m.IsGenericMethodDefinition)
            .Select(m => m.MakeGenericMethod(typeof(byte))));
        equality.AddRange(typeof(Enumerable).GetMethods().Where(m => m.Name == "SequenceEqual")
            .SelectMany(m => new[] { m.MakeGenericMethod(typeof(char)), m.MakeGenericMethod(typeof(byte)) }));
        equality.AddRange(typeof(StringComparer).GetMethods().Where(m => m.Name is "Equals" or "Compare"));
        equality.AddRange(typeof(object).GetMethods().Where(m => m.Name == "Equals"));

        var comparing = IlScan.MethodsCalling(typeof(WorkerKeys).Assembly, equality)
            .Where(method => IlScan.Outermost(method.DeclaringType!).Namespace == typeof(WorkerKeys).Namespace)
            .Select(method => $"{IlScan.Outermost(method.DeclaringType!).Name}.{method.Name}")
            .ToList();
        Assert.Empty(comparing);
    }

    /// <summary>The list without its start command, which <c>StartCommandTests</c> checks.</summary>
    private static JsonNode WithoutCommand(JsonNode list)
    {
        Assert.Contains("docker run", (string)list["start_command"]!, StringComparison.Ordinal);
        list.AsObject().Remove("start_command");
        return list;
    }

    private static async Task<List<string>> Names(UserDb door, Guid user) =>
        [.. await door.InUserTransaction(user, transaction => transaction.Connection!.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM workers ORDER BY name", transaction: transaction, cancellationToken: Ct)), Ct)];
}
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Settings;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests;

/// <summary>
/// The fake code host's addresses under <c>/devflow/api/fake/</c>, through which a test stages its
/// account's world: mapped only in the fake mode, behind a token and the origin check like any
/// other route. See "The code host" in devflow/README.md.
/// </summary>
public sealed class FakeEndpointsTests
{
    private const string WorldPath = "/devflow/api/fake/world";
    private const string UnavailablePath = "/devflow/api/fake/unavailable";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A world as a test stages it: one installation, one repository, one pull request
    /// with its conversation, its head's status and check, and a file. Every field is written, and
    /// times with their offset, as Dev flow writes them back.</summary>
    internal const string StagedWorld = """
        {
          "installations": [
            {
              "id": 41,
              "account": "Adamniels",
              "repositories": [
                {
                  "id": 9001,
                  "owner": "Adamniels",
                  "name": "Personal-platform",
                  "pull_requests": [
                    {
                      "number": 304,
                      "title": "The vault",
                      "author": "mac-mini-agent",
                      "labels": ["owner-merge"],
                      "head_commit": "3f2c9a8b7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b",
                      "head_branch": "272-the-vault",
                      "base_branch": "main",
                      "draft": false,
                      "body": "Closes #272",
                      "url": "https://github.com/Adamniels/Personal-platform/pull/304",
                      "updated_at": "2026-11-15T09:30:00+00:00",
                      "state": "open"
                    }
                  ],
                  "conversations": [
                    {
                      "number": 304,
                      "comments": [
                        {
                          "id": 1001,
                          "author": "Adamniels",
                          "body": "Why AES-GCM?",
                          "created_at": "2026-11-15T09:35:00+00:00",
                          "updated_at": "2026-11-15T09:39:00+00:00"
                        }
                      ]
                    }
                  ],
                  "commits": [
                    {
                      "id": "3f2c9a8b7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b",
                      "statuses": [{ "context": "review-ok", "state": "success", "description": null }],
                      "check_runs": [{ "name": "ci-ok", "status": "completed", "conclusion": "success", "app": "github-actions" }]
                    }
                  ],
                  "files": [{ "path": "AGENTS.md", "branch": "main", "content": "IyBXb3JraW5nIGluIHRoaXMgcmVwb3NpdG9yeQo=" }],
                  "issues": [
                    {
                      "number": 338, "title": "Settings and picking cards", "author": "Adamniels", "labels": ["devflow"],
                      "body": "## Goal", "url": "https://github.com/Adamniels/Personal-platform/issues/338",
                      "created_at": "2026-11-17T07:46:17+00:00", "open": true, "blocked_by": [339],
                      "edits": [{ "id": "UCE_1", "editor": "Adamniels", "at": "2026-11-17T08:00:00+00:00", "kind": "title" }],
                      "label_events": [{ "label": "devflow", "change": "added", "actor": "Adamniels", "at": "2026-11-17T07:46:17+00:00" }]
                    },
                    {
                      "number": 339, "title": "Handing out work", "author": "Adamniels", "labels": [],
                      "body": "", "url": "https://github.com/Adamniels/Personal-platform/issues/339",
                      "created_at": "2026-11-17T07:47:00+00:00", "open": false, "blocked_by": [], "edits": []
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static readonly CodeRepository Platform = new(41, 9001, "Adamniels", "Personal-platform");

    /// <summary>The staged world with <paramref name="change"/> made to a copy.</summary>
    private static string Changed(Action<JsonObject> change)
    {
        var world = JsonNode.Parse(StagedWorld)!.AsObject();
        change(world);
        return world.ToJsonString();
    }

    private static JsonObject Repo(JsonObject world) => world["installations"]![0]!["repositories"]![0]!.AsObject();

    private static async Task<JsonNode?> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response));

    [Fact]
    public async Task TheWorldIsReplacedAndReadBack()
    {
        await using var devflow = await Harness.Start();

        // Before anything is staged, the account's world is empty.
        var before = await devflow.Get(WorldPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, before.Response.StatusCode);
        Assert.StartsWith("application/json", before.Response.ContentType, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"installations": []}"""), await Json(before)));

        var put = await devflow.SendJson("PUT", WorldPath, Contract.Token, StagedWorld);
        Assert.Equal(StatusCodes.Status204NoContent, put.Response.StatusCode);

        var after = await devflow.Get(WorldPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, after.Response.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(StagedWorld), await Json(after)), await Harness.Body(await devflow.Get(WorldPath, Contract.Token)));

        // It is the code host the app answers with, for the token's account.
        var host = devflow.Services.GetRequiredService<ICodeHost>();
        var account = Guid.Parse(Contract.Sub);
        var open = Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.Found>(await host.OpenPullRequests(account, Platform, Ct));
        Assert.Equal(304, Assert.Single(open.Value).Number);
        var file = Assert.IsType<CodeHostResult<byte[]>.Found>(await host.File(account, Platform, "AGENTS.md", "main", Ct));
        Assert.Equal("# Working in this repository\n"u8.ToArray(), file.Value);

        // A second PUT replaces it whole.
        Assert.Equal(StatusCodes.Status204NoContent, (await devflow.SendJson("PUT", WorldPath, Contract.Token, """{"installations": []}""")).Response.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"installations": []}"""), await Json(await devflow.Get(WorldPath, Contract.Token))));

        // Being unavailable is switched on and off for the account, and the world is kept.
        Assert.Equal(StatusCodes.Status204NoContent, (await devflow.SendJson("POST", UnavailablePath, Contract.Token, """{"unavailable": true}""")).Response.StatusCode);
        Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.Unavailable>(await host.OpenPullRequests(account, Platform, Ct));
        Assert.Equal(StatusCodes.Status204NoContent, (await devflow.SendJson("POST", UnavailablePath, Contract.Token, """{"unavailable": false}""")).Response.StatusCode);
        Assert.IsType<CodeHostResult<IReadOnlyList<PullRequest>>.NotFound>(await host.OpenPullRequests(account, Platform, Ct));
    }

    public static TheoryData<string, string> Malformed() => new()
    {
        { "installations[0].repositories[0].name", Changed(world => Repo(world).Remove("name")) },
        { "installations[0].repositories[0].pull_requests[0].state", Changed(world => Repo(world)["pull_requests"]![0]!["state"] = "approved") },
        { "installations[0].repositories[0].pull_requests[0].number", Changed(world => Repo(world)["pull_requests"]![0]!["number"] = "304") },
        { "installations[0].repositories[0].pull_requests[0].title", Changed(world => Repo(world)["pull_requests"]![0]!["title"] = null) },
        { "installations[0].repositories[0].commits[0].sha", Changed(world => Repo(world)["commits"]![0]!["sha"] = "3f2c") },
        { "installations[0].repositories[0].commits[0].statuses[0].description", Changed(world => Repo(world)["commits"]![0]!["statuses"]![0]!.AsObject().Remove("description")) },
        { "installations[0].repositories[0].files[0].content", Changed(world => Repo(world)["files"]![0]!["content"] = "not base64!") },
        { "installations[0].account", Changed(world => world["installations"]![0]!.AsObject().Remove("account")) },
        { "installations", """{"installations": null}""" },
        { "installations", "{}" },
        { "installations[1].id", Changed(world => world["installations"]!.AsArray().Add(world["installations"]![0]!.DeepClone())) },
        { "installations[0].repositories[0].pull_requests[1].number", Changed(world => Repo(world)["pull_requests"]!.AsArray().Add(Repo(world)["pull_requests"]![0]!.DeepClone())) },
        { "installations[0].repositories[0].files[1]", Changed(world => Repo(world)["files"]!.AsArray().Add(Repo(world)["files"]![0]!.DeepClone())) },
        { "installations[0].repositories[0].issues", Changed(world => Repo(world).Remove("issues")) },
        { "installations[0].repositories[0].issues[0].edits[0].kind", Changed(world => Repo(world)["issues"]![0]!["edits"]![0]!["kind"] = "label") },
        { "installations[0].repositories[0].issues[0].label_events[0].change", Changed(world => Repo(world)["issues"]![0]!["label_events"]![0]!["change"] = "renamed") },
        { "installations[0].repositories[0].issues[0].label_events[0].actor", Changed(world => Repo(world)["issues"]![0]!["label_events"]![0]!["actor"] = null) },
        { "installations[0].repositories[0].issues[1].number", Changed(world => Repo(world)["issues"]![1]!["number"] = 338) },
        { "installations[0].repositories[0].issues[1].number", Changed(world =>
        {
            // 304 is the pull request's; nothing else blocks on 339 by then.
            Repo(world)["issues"]![0]!["blocked_by"] = new JsonArray();
            Repo(world)["issues"]![1]!["number"] = 304;
        }) },
        { "installations[0].repositories[0].issues[0].blocked_by[0]", Changed(world => Repo(world)["issues"]![0]!["blocked_by"]![0] = 400) },
        { "installations[0].repositories[0].issues[0].sub_issues[0].number", Changed(world => Repo(world)["issues"]![0]!["sub_issues"] = new JsonArray(new JsonObject { ["number"] = 400, ["repository"] = null })) },
        { "installations[0].repositories[0].issues[0].sub_issues[0].number", Changed(world => Repo(world)["issues"]![0]!["sub_issues"] = new JsonArray(new JsonObject { ["number"] = 338, ["repository"] = null })) },
        { "installations[0].repositories[0].issues[0].sub_issues[1].repository", Changed(world => Repo(world)["issues"]![0]!["sub_issues"] = new JsonArray(
            new JsonObject { ["number"] = 339, ["repository"] = null }, new JsonObject { ["number"] = 339, ["repository"] = 9999 })) },
        { "installations[0].repositories[0].issues[0].sub_issues[0].repository", Changed(world => Repo(world)["issues"]![0]!["sub_issues"] = new JsonArray(new JsonObject { ["number"] = 339 })) },
        { "the body", "{\"installations\": [" },
        { "the body", "" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task AMalformedWorldIsRefusedNamingTheField(string field, string body)
    {
        await using var devflow = await Harness.Start();
        await devflow.SendJson("PUT", WorldPath, Contract.Token, StagedWorld);

        var refused = await devflow.SendJson("PUT", WorldPath, Contract.Token, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.StartsWith("application/problem+json", refused.Response.ContentType, StringComparison.Ordinal);
        var problem = (await Json(refused))!;
        Assert.Equal("The world is malformed", (string?)problem["title"]);
        Assert.StartsWith($"{field}: ", (string?)problem["detail"], StringComparison.Ordinal);
        // Nothing of .NET's own names reaches the caller.
        Assert.DoesNotContain("DevFlow.", (string?)problem["detail"], StringComparison.Ordinal);

        // The world before it is kept.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(StagedWorld), await Json(await devflow.Get(WorldPath, Contract.Token))));
    }

    [Theory]
    [InlineData("{}", "unavailable")]
    [InlineData("""{"unavailable": "yes"}""", "unavailable")]
    [InlineData("""{"unavailable": true, "for": 5}""", "for")]
    public async Task AMalformedSwitchIsRefusedNamingTheField(string body, string field)
    {
        await using var devflow = await Harness.Start();

        var refused = await devflow.SendJson("POST", UnavailablePath, Contract.Token, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.StartsWith($"{field}: ", (string?)(await Json(refused))!["detail"], StringComparison.Ordinal);
    }

    /// <summary>Every address under /devflow/api/fake/, with the method it answers.</summary>
    public static TheoryData<string, string, string?> Addresses() => new()
    {
        { "GET", WorldPath, null },
        { "PUT", WorldPath, StagedWorld },
        { "POST", UnavailablePath, """{"unavailable": true}""" },
        { "POST", "/devflow/api/fake/expire-token", "{}" },
        { "POST", "/devflow/api/fake/claude-login-age", """{"days": 340}""" },
        { "POST", "/devflow/api/fake/worker/run", "{}" },
    };

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task TheyAre404InGitHubMode(string method, string path, string? body)
    {
        // The GitHub adapter is not built yet, so a stand in takes its place.
        await using var devflow = await Harness.Start(Harness.Defaults with { CodeHost = CodeHostMode.GitHub }, codeHost: new FakeCodeHost());

        var response = body is null ? await devflow.Get(path, Contract.Token) : await devflow.SendJson(method, path, Contract.Token, body);

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task TheyNeedATokenAndTheOrigin(string method, string path, string? body)
    {
        await using var devflow = await Harness.Start();

        // Without a token, 401 with an empty body, as every API route answers.
        var anonymous = body is null ? await devflow.Get(path) : await devflow.SendJson(method, path, token: null, body);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Equal("", await Harness.Body(anonymous));

        // A change from another origin, or with none, is 403 before routing.
        if (body is not null)
        {
            foreach (var origin in new (string, string)[][] { [], [("Origin", "https://evil.test")] })
            {
                var response = await devflow.Send(method, path, Contract.Token, origin);
                Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task TheFakeIsLeftOutOfTheSchema()
    {
        await using var devflow = await Harness.Start(Harness.Defaults with { ExposeOpenApi = true });

        var schema = await Harness.Body(await devflow.Get("/devflow/api/openapi.json"));

        Assert.Contains("/devflow/api/status", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("/devflow/api/fake", schema, StringComparison.Ordinal);
    }
}
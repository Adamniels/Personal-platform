using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

using PlatformData.Testing;

namespace DevFlow.Tests.Workers;

/// <summary>
/// What the tests of workers' keys and the worker API share: the routes, making a key as the page
/// does, and a request as a worker sends one, with its key in the <c>Authorization</c> header, no
/// cookie and no <c>Origin</c>. See "Workers" in devflow/README.md.
/// </summary>
internal static class Workers
{
    public const string HelloPath = "/devflow/api/worker/hello";
    public const string NextPath = "/devflow/api/worker/jobs/next";
    public const string RecipeVersion = "3";
    public const string BaseImage = "platform-hosted/devflow-worker:2026.11.17";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Guid User => Routes.User;

    public static Guid Bob => Routes.Bob;

    public static string KeyPath(Guid project) => $"/devflow/api/projects/{project}/worker-key";

    public static string ListPath(Guid project) => $"/devflow/api/projects/{project}/workers";

    /// <summary>Makes the project's key as the page does: a POST from the platform's origin with
    /// the user's token in its cookie.</summary>
    public static Task<HttpContext> MakeKey(Routes routes, Guid project) =>
        routes.App.Send("POST", KeyPath(project), Contract.Token, ("Origin", Linking.Origin));

    /// <summary>The key making one answered, after checking it answered one.</summary>
    public static async Task<string> NewKey(Routes routes, Guid project)
    {
        var response = await MakeKey(routes, project);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (string)(await Json(response))["key"]!;
    }

    /// <summary>A key for a project of another user than the token's, made by the store as that
    /// user, since the tests hold no token of theirs.</summary>
    public static async Task<string> KeyOf(Routes routes, Guid user, Guid project) =>
        await routes.App.Services.GetRequiredService<WorkerKeys>().Make(user, project, Ct)
        ?? throw new InvalidOperationException("the store made no key for a project the user has");

    /// <summary>The project's workers as the user's page reads them.</summary>
    public static async Task<JsonNode> List(Routes routes, Guid project)
    {
        var response = await routes.App.Get(ListPath(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return await Json(response);
    }

    public static string Hello(Guid worker, string name = "fodr-worker-1", string recipe = RecipeVersion) =>
        $$"""{"worker_id": "{{worker}}", "name": "{{name}}", "recipe_version": "{{recipe}}", "base_image": "{{BaseImage}}"}""";

    public static string Next(Guid worker) => $$"""{"worker_id": "{{worker}}"}""";

    /// <summary>The header a worker sends its key in.</summary>
    public static string Bearer(string key) => $"Bearer {key}";

    /// <summary>
    /// A POST as a worker sends one: <paramref name="json"/> as its body, each of
    /// <paramref name="authorization"/> as an <c>Authorization</c> header, none when there are
    /// none, and no <c>Origin</c>, since a worker is not a browser. A cookie only when a test
    /// gives one.
    /// </summary>
    public static Task<HttpContext> AsWorker(Harness app, string path, string json, string? cookie = null, params string[] authorization) =>
        app.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Request.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes(json);
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(HasBody.Instance);
            foreach (var header in authorization)
            {
                context.Request.Headers.Append("Authorization", header);
            }

            if (cookie is not null)
            {
                context.Request.Headers.Cookie = $"__Host-access={cookie}";
            }
        }, Ct);

    /// <summary>A worker's <c>hello</c> with its key.</summary>
    public static Task<HttpContext> SayHello(Routes routes, string key, Guid worker, string name = "fodr-worker-1", string recipe = RecipeVersion) =>
        AsWorker(routes.App, HelloPath, Hello(worker, name, recipe), null, Bearer(key));

    /// <summary>A worker asking for work with its key.</summary>
    public static Task<HttpContext> AskForWork(Routes routes, string key, Guid worker) =>
        AsWorker(routes.App, NextPath, Next(worker), null, Bearer(key));

    /// <summary>The SHA-256 of the key as written, which is all Dev flow keeps of it.</summary>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static async Task<JsonNode> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response))!;

    /// <summary>The workers stored, read as the superuser, past row level security.</summary>
    public static async Task<List<WorkerRow>> Rows(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<WorkerRow>(new CommandDefinition(
            """
            SELECT project_id AS ProjectId, id AS Id, user_id AS UserId, name AS Name, recipe_version AS RecipeVersion, base_image AS BaseImage
            FROM workers ORDER BY user_id, project_id, id
            """,
            cancellationToken: Ct))];
    }

    internal sealed record WorkerRow(Guid ProjectId, Guid Id, Guid UserId, string Name, string RecipeVersion, string BaseImage);

    /// <summary>Says a request has a body, as one sent with JSON does.</summary>
    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public static readonly HasBody Instance = new();

        public bool CanHaveBody => true;
    }
}
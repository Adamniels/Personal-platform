using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Machines;

using DevFlow.Settings;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Machines;

/// <summary>
/// What the tests of machines share: the user's routes as the page calls them, a POST from the
/// platform's origin with the user's token in its cookie, and the machine's as the machine program
/// calls them, with its key in the <c>Authorization</c> header, no cookie and no <c>Origin</c>. See
/// "Machines" in devflow/README.md.
/// </summary>
internal static class Machines
{
    public const string ListPath = "/devflow/api/machines";
    public const string EnrollmentPath = "/devflow/api/machines/enrollment";
    public const string EnrollPath = "/devflow/api/machine/enroll";
    public const string HelloPath = "/devflow/api/machine/hello";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Guid User => Routes.User;

    public static Guid Bob => Routes.Bob;

    public static string RemovePath(Guid machine) => $"/devflow/api/machines/{machine}";

    /// <summary>The app's settings with <paramref name="set"/> on top of the harness's
    /// environment.</summary>
    public static DevFlowSettings Settings(params (string Name, string Value)[] set)
    {
        var values = new Dictionary<string, string>(Harness.MinimalEnvironment);
        foreach (var (name, value) in set)
        {
            values[name] = value;
        }

        return DevFlowSettings.Load(name => values.GetValueOrDefault(name));
    }

    /// <summary>"Add a machine" as the page presses it.</summary>
    public static Task<HttpContext> NewEnrollment(Routes routes) =>
        routes.App.Send("POST", EnrollmentPath, Contract.Token, ("Origin", Linking.Origin));

    /// <summary>A new code of the fixture's user's, made by the store as "Add a machine" makes one;
    /// <c>MachineInstallTests</c> checks the route itself.</summary>
    public static Task<string> NewCode(Routes routes) => CodeOf(routes, User);

    /// <summary>A new code of <paramref name="user"/>'s, made by the store as that user, since the
    /// tests hold no token of anyone but the fixture's user.</summary>
    public static async Task<string> CodeOf(Routes routes, Guid user) =>
        (await routes.App.Services.GetRequiredService<MachineEnrollments>().Make(user, Ct)).Code;

    public static string Enrollment(string? code, string name = "mac-mini", string os = "macos", string arch = "arm64") =>
        new JsonObject { ["code"] = code, ["name"] = name, ["os"] = os, ["arch"] = arch }.ToJsonString();

    /// <summary>The machine program trading a code for a key: a POST with no key, no cookie and
    /// no <c>Origin</c>.</summary>
    public static Task<HttpContext> Enroll(Routes routes, string json) =>
        Workers.Workers.AsWorker(routes.App, EnrollPath, json);

    /// <summary>A machine enrolled with a new code of the caller's, after checking it was.</summary>
    public static async Task<(Guid Machine, string Key)> Enrolled(Routes routes, string name = "mac-mini", string? code = null)
    {
        code ??= await NewCode(routes);
        var response = await Enroll(routes, Enrollment(code, name));
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var answer = await Json(response);
        return (Guid.Parse((string)answer["machine_id"]!), (string)answer["key"]!);
    }

    public static string Hello(string name = "mac-mini", string os = "macos", string arch = "arm64", string programVersion = "2026.11.28", string docker = "running") =>
        new JsonObject { ["name"] = name, ["os"] = os, ["arch"] = arch, ["program_version"] = programVersion, ["docker"] = docker }.ToJsonString();

    /// <summary>A machine's <c>hello</c> with each of <paramref name="authorization"/> as an
    /// <c>Authorization</c> header.</summary>
    public static Task<HttpContext> SayHello(Routes routes, string json, params string[] authorization) =>
        Workers.Workers.AsWorker(routes.App, HelloPath, json, null, authorization);

    /// <summary>A machine's <c>hello</c> with its key.</summary>
    public static Task<HttpContext> SayHello(Routes routes, string key) =>
        SayHello(routes, Hello(), Bearer(key));

    public static string Bearer(string key) => $"Bearer {key}";

    /// <summary>The caller's machines as the page reads them.</summary>
    public static async Task<JsonArray> List(Routes routes)
    {
        var response = await routes.App.Get(ListPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Json(response)).AsArray();
    }

    /// <summary>"Remove" as the page presses it.</summary>
    public static Task<HttpContext> Remove(Routes routes, Guid machine) =>
        routes.App.Send("DELETE", RemovePath(machine), Contract.Token, ("Origin", Linking.Origin));

    /// <summary>The SHA-256 of the key or code as written, which is all Dev flow keeps of
    /// it.</summary>
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static async Task<JsonNode> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response))!;

    /// <summary>The machines stored, read as the superuser, past row level security.</summary>
    public static async Task<List<MachineRow>> Rows(Routes routes)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<MachineRow>(new CommandDefinition(
            """
            SELECT id AS Id, user_id AS UserId, name AS Name, os AS Os, arch AS Arch, program_version AS ProgramVersion,
                docker AS Docker, first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt
            FROM machines ORDER BY first_seen_at, id
            """,
            cancellationToken: Ct))];
    }

    internal sealed record MachineRow(Guid Id, Guid UserId, string Name, string Os, string Arch, string? ProgramVersion, string? Docker, DateTime FirstSeenAt, DateTime LastSeenAt);
}
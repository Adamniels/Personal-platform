using DevFlow.Tests.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// The machine scheme, <c>MachineAuthentication</c>: a route under <c>/devflow/api/machine/</c>,
/// <c>enroll</c> aside, takes <c>Authorization: Bearer dfm_…</c> and nothing else, and refuses
/// anything else with <c>401</c> and an empty body, whatever the reason, as the worker scheme does.
/// See "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineAuthenticationTests
{
    [Fact]
    public async Task OnlyAMachineKeyOpensMachineRoutes()
    {
        await using var routes = await Routes.Start();
        var (machine, key) = await Machines.Enrolled(routes);
        var (removed, revoked) = await Machines.Enrolled(routes, "removed");
        Assert.Equal(StatusCodes.Status204NoContent, (await Machines.Remove(routes, removed)).Response.StatusCode);
        var project = await routes.Project(Machines.User);
        var workerKey = await Workers.Workers.NewKey(routes, project);

        // The key alone opens it, with no cookie and no Origin.
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, key)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, Machines.Hello(), $"bearer {key}")).Response.StatusCode);

        foreach (var (why, authorization, cookie) in new (string, string[], string?)[]
        {
            ("no key", [], null),
            ("a worker key", [Machines.Bearer(workerKey)], null),
            ("the user's token in the header", [Machines.Bearer(Contract.Token)], null),
            ("the user's token in the cookie", [], Contract.Token),
            ("a worker key with the user's cookie", [Machines.Bearer(workerKey)], Contract.Token),
            ("a second Authorization header", [Machines.Bearer(key), Machines.Bearer(key)], null),
            ("another scheme", [$"Basic {key}"], null),
            ("no space", [$"Bearer{key}"], null),
            ("a trailing space", [Machines.Bearer(key) + " "], null),
            ("a trailing newline", [Machines.Bearer(key) + "\n"], null),
            ("a key too short", [Machines.Bearer(key[..^1])], null),
            ("a key of no machine", [Machines.Bearer("dfm_" + new string('A', 43))], null),
            ("a removed machine's key", [Machines.Bearer(revoked)], null),
        })
        {
            var refused = await Workers.Workers.AsWorker(routes.App, Machines.HelloPath, Machines.Hello(), cookie, authorization);
            Assert.True(refused.Response.StatusCode == StatusCodes.Status401Unauthorized, why);
            Assert.True(await Harness.Body(refused) == "", why);
        }

        // Nor does a machine key open anything else: a worker's route, or a user's.
        var asWorker = await Workers.Workers.AsWorker(routes.App, Workers.Workers.HelloPath, Workers.Workers.Hello(Guid.NewGuid()), null, Machines.Bearer(key));
        Assert.Equal(StatusCodes.Status401Unauthorized, asWorker.Response.StatusCode);
        var asUser = await routes.App.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = Machines.ListPath;
            context.Request.Headers.Authorization = Machines.Bearer(key);
        }, Machines.Ct);
        Assert.Equal(StatusCodes.Status401Unauthorized, asUser.Response.StatusCode);

        // The machine that answered is the key's: hello changed it, and not the removed one.
        Assert.Equal([machine], (await Machines.Rows(routes)).Select(row => row.Id));
    }
}
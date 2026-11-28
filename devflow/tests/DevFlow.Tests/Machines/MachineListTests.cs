using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// <c>GET /devflow/api/machines</c>, the user's: their machines, by when each first enrolled, never
/// another user's, each <c>online</c> when seen in the last minute. See "Machines" in
/// devflow/README.md.
/// </summary>
public sealed class MachineListTests
{
    [Fact]
    public async Task AUserSeesOnlyTheirMachines()
    {
        await using var routes = await Routes.Start();
        var (mini, miniKey) = await Machines.Enrolled(routes, "mac-mini");
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        var (box, _) = await Machines.Enrolled(routes, "build-box");
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        await Machines.Enrolled(routes, "bobs-machine", await Machines.CodeOf(routes, Machines.Bob));
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, miniKey)).Response.StatusCode);
        var helloAt = routes.Time.GetUtcNow();

        var listed = await Machines.List(routes);

        // Both of the caller's, the first enrolled first, with their fields and no more; Bob's
        // is not there.
        Assert.Equal([mini.ToString(), box.ToString()], listed.Select(machine => (string)machine!["id"]!));
        var first = listed[0]!.AsObject();
        Assert.Equal(["id", "name", "os", "arch", "program_version", "docker", "online", "last_seen_at"], first.Select(pair => pair.Key));
        Assert.Equal("mac-mini", (string)first["name"]!);
        Assert.Equal("macos", (string)first["os"]!);
        Assert.Equal("arm64", (string)first["arch"]!);
        Assert.Equal("2026.11.28", (string)first["program_version"]!);
        Assert.Equal("running", (string)first["docker"]!);
        Assert.True((bool)first["online"]!);
        Assert.Equal($"{helloAt:yyyy-MM-ddTHH:mm:ss}+00:00", (string)first["last_seen_at"]!);
        // Enrolled and not yet said hello: online, with nothing reported.
        var second = listed[1]!.AsObject();
        Assert.Null(second["program_version"]);
        Assert.Null(second["docker"]);
        Assert.True((bool)second["online"]!);

        // Online while seen less than a minute ago: the build box enrolled a second before the
        // Mac mini said hello.
        routes.Time.Advance(TimeSpan.FromSeconds(58));
        Assert.Equal([true, true], (await Machines.List(routes)).Select(machine => (bool)machine!["online"]!));
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([true, false], (await Machines.List(routes)).Select(machine => (bool)machine!["online"]!));
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([false, false], (await Machines.List(routes)).Select(machine => (bool)machine!["online"]!));
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, miniKey)).Response.StatusCode);
        Assert.Equal([true, false], (await Machines.List(routes)).Select(machine => (bool)machine!["online"]!));

        // Bob's is stored, as his.
        Assert.Equal(["mac-mini", "build-box", "bobs-machine"], (await Machines.Rows(routes)).Select(row => row.Name));
        Assert.Equal(Machines.Bob, (await Machines.Rows(routes))[2].UserId);
    }

    [Fact]
    public async Task WithoutAMachineTheListIsEmpty()
    {
        await using var routes = await Routes.Start();

        Assert.Empty(await Machines.List(routes));
        // And without a token, nothing.
        var refused = await routes.App.Get(Machines.ListPath);
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
    }

    [Fact]
    public async Task TheFakeAgesAMachine()
    {
        await using var routes = await Routes.Start();
        var (mini, _) = await Machines.Enrolled(routes, "mac-mini");
        var (bobs, _) = await Machines.Enrolled(routes, "bobs-machine", await Machines.CodeOf(routes, Machines.Bob));

        // Seen five minutes ago: offline, since then.
        var aged = await routes.App.SendJson("POST", "/devflow/api/fake/machine-age", Contract.Token, $$"""{"machine_id": "{{mini}}", "minutes": 5}""");

        Assert.Equal(StatusCodes.Status204NoContent, aged.Response.StatusCode);
        var listed = Assert.Single(await Machines.List(routes))!;
        Assert.False((bool)listed["online"]!);
        Assert.Equal($"{routes.Time.GetUtcNow().AddMinutes(-5):yyyy-MM-ddTHH:mm:ss}+00:00", (string)listed["last_seen_at"]!);

        // Another user's machine, or none, is not the caller's to age.
        foreach (var id in new[] { bobs, Guid.NewGuid() })
        {
            var refused = await routes.App.SendJson("POST", "/devflow/api/fake/machine-age", Contract.Token, $$"""{"machine_id": "{{id}}", "minutes": 5}""");
            Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
        }

        var negative = await routes.App.SendJson("POST", "/devflow/api/fake/machine-age", Contract.Token, $$"""{"machine_id": "{{mini}}", "minutes": -1}""");
        Assert.Equal(StatusCodes.Status400BadRequest, negative.Response.StatusCode);
    }
}
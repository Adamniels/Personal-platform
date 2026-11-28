using System.Text.Json.Nodes;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// A machine's <c>hello</c>: what the machine program reports of itself, recorded with when it was
/// seen, answered with Dev flow's release, which a later slice's program updates itself to. See
/// "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineHelloTests
{
    [Fact]
    public async Task HelloRecordsTheMachine()
    {
        await using var routes = await Routes.Start(settings: Machines.Settings(("DEVFLOW_RELEASE", "4f2a9c1b7e30")));
        var (machine, key) = await Machines.Enrolled(routes, "mac-mini");
        var enrolled = Assert.Single(await Machines.Rows(routes));
        routes.Time.Advance(TimeSpan.FromSeconds(30));

        var response = await Machines.SayHello(routes, Machines.Hello("build-box", "linux", "x64", "4f2a9c1b7e30", "Cannot connect to the Docker daemon"), Machines.Bearer(key));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("""{"release":"4f2a9c1b7e30"}""", (await Machines.Json(response)).ToJsonString());
        // Everything it reported, and now as when it was seen, keeping when it first was.
        var row = Assert.Single(await Machines.Rows(routes));
        Assert.Equal(
            new Machines.MachineRow(machine, Machines.User, "build-box", "linux", "x64", "4f2a9c1b7e30", "Cannot connect to the Docker daemon", enrolled.FirstSeenAt, row.LastSeenAt),
            row);
        Assert.Equal(routes.Time.GetUtcNow().UtcDateTime, row.LastSeenAt, TimeSpan.FromMilliseconds(1));

        // Docker running, on the other OS and architecture.
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, Machines.Hello("mac-mini", "macos", "arm64", "4f2a9c1b7e30", "running"), Machines.Bearer(key))).Response.StatusCode);
        var mac = Assert.Single(await Machines.Rows(routes));
        Assert.Equal(("macos", "arm64", "running"), (mac.Os, mac.Arch, mac.Docker));
    }

    [Fact]
    public async Task TheReleaseIsDevUnlessSet()
    {
        await using var routes = await Routes.Start();
        var (_, key) = await Machines.Enrolled(routes);

        var response = await Machines.SayHello(routes, key);

        Assert.Equal("""{"release":"dev"}""", (await Machines.Json(response)).ToJsonString());
    }

    [Fact]
    public async Task AMissingOrTooLongFieldIs422NamingIt()
    {
        await using var routes = await Routes.Start();
        var (_, key) = await Machines.Enrolled(routes);
        var before = Assert.Single(await Machines.Rows(routes));

        static string Without(string field)
        {
            var hello = JsonNode.Parse(Machines.Hello())!.AsObject();
            hello.Remove(field);
            return hello.ToJsonString();
        }

        foreach (var (json, field) in new[]
        {
            (Without("name"), "name"),
            (Without("os"), "os"),
            (Without("arch"), "arch"),
            (Without("program_version"), "program_version"),
            (Without("docker"), "docker"),
            (Machines.Hello(name: new string('n', 256)), "name"),
            (Machines.Hello(name: " "), "name"),
            (Machines.Hello(os: "windows"), "os"),
            (Machines.Hello(arch: "amd64"), "arch"),
            (Machines.Hello(programVersion: ""), "program_version"),
            (Machines.Hello(programVersion: new string('1', 256)), "program_version"),
            (Machines.Hello(docker: ""), "docker"),
            (Machines.Hello(docker: new string('d', 256)), "docker"),
        })
        {
            var refused = await Machines.SayHello(routes, json, Machines.Bearer(key));
            Assert.True(refused.Response.StatusCode == StatusCodes.Status422UnprocessableEntity, json);
            var problem = await Machines.Json(refused);
            Assert.Equal([field], problem["errors"]!.AsObject().Select(pair => pair.Key));
        }

        // At the limit it is taken, and nothing refused changed the machine.
        Assert.Equal(before, Assert.Single(await Machines.Rows(routes)));
        var limit = Machines.Hello(name: new string('n', 255), programVersion: new string('1', 255), docker: new string('d', 255));
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, limit, Machines.Bearer(key))).Response.StatusCode);
    }
}
using System.Text.Json.Nodes;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using static DevFlow.Tests.Placements.Placements;

using MachineSupport = DevFlow.Tests.Machines.Machines;

namespace DevFlow.Tests.Placements;

/// <summary>
/// Placing a project's workers on the user's machines: <c>PUT</c> replacing the whole list and
/// <c>GET</c> answering it with each machine's name and whether it is online, only the user's own
/// machines, each once, from 0 to 8 workers. See "Workers" in devflow/README.md.
/// </summary>
public sealed class WorkerPlacementTests
{
    [Fact]
    public async Task AProjectIsPlacedOnMachines()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var (mini, _) = await MachineSupport.Enrolled(routes, "mac-mini");
        var (studio, studioKey) = await MachineSupport.Enrolled(routes, "studio");

        // Nothing placed yet.
        Assert.Empty(await List(routes, project));

        // Two minutes on, only the studio has said hello since: the Mac mini is offline.
        routes.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(StatusCodes.Status200OK, (await MachineSupport.SayHello(routes, MachineSupport.Hello(name: "studio"), MachineSupport.Bearer(studioKey))).Response.StatusCode);

        var put = await Put(routes, project, Body((mini, 2), (studio, 1)));
        Assert.Equal(StatusCodes.Status200OK, put.Response.StatusCode);
        var expected = new[] { (mini, "mac-mini", false, 2), (studio, "studio", true, 1) };
        Assert.Equal(expected, Read((await Json(put)).AsArray()));
        Assert.Equal(expected, Read(await List(routes, project)));

        // Stored as the project's user's, one row a machine.
        Assert.Equal(
            [(project, mini, Routes.User, 2), (project, studio, Routes.User, 1)],
            (await Rows(routes)).Select(row => (row.ProjectId, row.MachineId, row.UserId, row.Workers)));

        // A PUT replaces the whole list: a machine left out is no longer placed, and a count of 0
        // is stored as given.
        await Place(routes, project, (studio, 3), (mini, 0));
        Assert.Equal([(mini, "mac-mini", false, 0), (studio, "studio", true, 3)], Read(await List(routes, project)));
        await Place(routes, project, (studio, 3));
        Assert.Equal([(studio, "studio", true, 3)], Read(await List(routes, project)));

        // Removing a machine removes its placements.
        Assert.Equal(StatusCodes.Status204NoContent, (await MachineSupport.Remove(routes, studio)).Response.StatusCode);
        Assert.Empty(await List(routes, project));
        Assert.Empty(await Rows(routes));
    }

    [Fact]
    public async Task OnlyMyMachinesAndSaneCounts()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var (mini, _) = await MachineSupport.Enrolled(routes, "mac-mini");
        var bobs = (await MachineSupport.Enrolled(routes, "bobs-box", await MachineSupport.CodeOf(routes, Routes.Bob))).Machine;

        // Another user's machine and one that does not exist are refused alike, naming the field.
        Assert.Equal(["[0].machine_id"], await Refused(await Put(routes, project, Body((bobs, 1)))));
        Assert.Equal(["[1].machine_id"], await Refused(await Put(routes, project, Body((mini, 1), (Guid.CreateVersion7(), 1)))));

        // The same machine twice.
        Assert.Equal(["[1].machine_id"], await Refused(await Put(routes, project, Body((mini, 1), (mini, 2)))));

        // A count outside 0 to 8, with a fraction, or missing.
        Assert.Equal(["[0].workers"], await Refused(await Put(routes, project, Body((mini, 9)))));
        Assert.Equal(["[0].workers"], await Refused(await Put(routes, project, Body((mini, -1)))));
        Assert.Equal(["[0].workers"], await Refused(await Put(routes, project, $"[{{\"machine_id\": \"{mini}\", \"workers\": 1.5}}]")));
        Assert.Equal(["[0].workers"], await Refused(await Put(routes, project, $"[{{\"machine_id\": \"{mini}\"}}]")));
        Assert.Equal(["[0].machine_id"], await Refused(await Put(routes, project, "[{\"workers\": 1}]")));
        Assert.Equal(["placements"], await Refused(await Put(routes, project, "null")));

        // Nothing was stored by any of them.
        Assert.Empty(await Rows(routes));

        // 0 and 8 are the ends of the range.
        await Place(routes, project, (mini, 8));
        await Place(routes, project, (mini, 0));

        // Another user's project is not there for the caller, to read or to change.
        var bobsProject = await routes.Project(Routes.Bob, repository: 2, name: "bobs");
        Assert.Equal(StatusCodes.Status404NotFound, (await Get(routes, bobsProject)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Put(routes, bobsProject, Body((mini, 1)))).Response.StatusCode);
        Assert.DoesNotContain(await Rows(routes), row => row.ProjectId == bobsProject);
    }

    private static List<(Guid, string, bool, int)> Read(JsonArray list) =>
        [.. list.Select(entry => (Guid.Parse((string)entry!["machine_id"]!), (string)entry["name"]!, (bool)entry["online"]!, (int)entry["workers"]!))];
}
using System.Text.Json.Nodes;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http;

using static DevFlow.Tests.Placements.Placements;

using MachineSupport = DevFlow.Tests.Machines.Machines;

namespace DevFlow.Tests.Placements;

/// <summary>
/// The slots a project's placements and its recipe make: one per wanted worker, numbered from 1 on
/// each machine, kept when no longer wanted and reused when wanted again; and while it has no
/// recipe, one setup slot on its first placed machine and no other. See "Workers" and "Setting up a
/// project" in devflow/README.md.
/// </summary>
public sealed class WorkerSlotTests
{
    private const string Worker = "worker";
    private const string Setup = "setup";

    [Fact]
    public async Task SlotsFollowThePlacements()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        await WorkerStore.RecipeRead(routes.Door, Routes.User, project, Recipe, Ct);
        var (mini, _) = await MachineSupport.Enrolled(routes, "mac-mini");

        // Two workers on one machine make two slots, numbered 1 and 2, the project's user's.
        await Place(routes, project, (mini, 2));
        var two = await Slots(routes, project);
        Assert.Equal([(mini, Worker, 1, true), (mini, Worker, 2, true)], two.Select(slot => (slot.MachineId, slot.Kind, slot.Number, slot.Wanted)));
        Assert.All(two, slot => Assert.Equal(Routes.User, slot.UserId));

        // Lowered to 1, slot 2 is kept, no longer wanted, so a card under way on it is not cut off.
        await Place(routes, project, (mini, 1));
        var one = await Slots(routes, project);
        Assert.Equal([(mini, Worker, 1, true), (mini, Worker, 2, false)], one.Select(slot => (slot.MachineId, slot.Kind, slot.Number, slot.Wanted)));
        Assert.Equal(two.Select(slot => slot.Id), one.Select(slot => slot.Id));

        // Raised again, number 2 is the same slot, wanted again.
        await Place(routes, project, (mini, 2));
        Assert.Equal(two, await Slots(routes, project));

        // Placed nowhere, nothing is wanted, and both are kept.
        await Place(routes, project);
        Assert.Equal([false, false], (await Slots(routes, project)).Select(slot => slot.Wanted));
        Assert.Empty(await Wanted(routes, project));
    }

    [Fact]
    public async Task AProjectWithoutARecipeGetsASetupSlot()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var (mini, _) = await MachineSupport.Enrolled(routes, "mac-mini");
        var (studio, _) = await MachineSupport.Enrolled(routes, "studio");

        // With no placement it wants nothing, and its setup says it is placed nowhere.
        Assert.Empty(await Slots(routes, project));
        Assert.False(await Placed(routes, project));

        // The studio is placed first, then the Mac mini: the setup slot is on the studio, the first
        // placed, and no worker slot is wanted while there is no recipe.
        await Place(routes, project, (studio, 2));
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        await Place(routes, project, (mini, 1), (studio, 2));
        Assert.Equal([(studio, Setup, 1)], await Wanted(routes, project));
        Assert.True(await Placed(routes, project));

        // The recipe found: the setup slot is not wanted, the placed workers are.
        await WorkerStore.RecipeRead(routes.Door, Routes.User, project, Recipe, Ct);
        Assert.Equal(Sorted((mini, Worker, 1), (studio, Worker, 1), (studio, Worker, 2)), await Wanted(routes, project));
        Assert.Contains(await Slots(routes, project), slot => slot is { Kind: Setup, Wanted: false } && slot.MachineId == studio);

        // The recipe lost: the setup slot is wanted again, the same one, and the workers are not.
        var setupSlot = (await Slots(routes, project)).Single(slot => slot.Kind == Setup).Id;
        await WorkerStore.RecipeRead(routes.Door, Routes.User, project, null, Ct);
        Assert.Equal([(studio, Setup, 1)], await Wanted(routes, project));
        Assert.Equal(setupSlot, (await Slots(routes, project)).Single(slot => slot.Kind == Setup).Id);

        // A placement of 0 is no first placed machine, and one raised from 0 is placed anew, after
        // the Mac mini.
        await Place(routes, project, (studio, 0), (mini, 1));
        Assert.Equal([(mini, Setup, 1)], await Wanted(routes, project));
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        await Place(routes, project, (mini, 1), (studio, 2));
        Assert.Equal([(mini, Setup, 1)], await Wanted(routes, project));

        // The Mac mini removed: the setup slot moves to the studio, and the Mac mini's slots go
        // with it.
        Assert.Equal(StatusCodes.Status204NoContent, (await MachineSupport.Remove(routes, mini)).Response.StatusCode);
        Assert.Equal([(studio, Setup, 1)], await Wanted(routes, project));
        Assert.DoesNotContain(await Slots(routes, project), slot => slot.MachineId == mini);

        // Placed nowhere: it wants nothing again.
        await Place(routes, project);
        Assert.Empty(await Wanted(routes, project));
        Assert.False(await Placed(routes, project));
    }

    private static List<(Guid, string, int)> Sorted(params (Guid, string, int)[] slots) => [.. slots.Order()];

    /// <summary>Whether the project's setup says it has a machine with a worker placed on it.</summary>
    private static async Task<bool> Placed(Routes routes, Guid project)
    {
        var response = await routes.App.Get($"/devflow/api/projects/{project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (bool)JsonNode.Parse(await Harness.Body(response))!["placed"]!;
    }
}
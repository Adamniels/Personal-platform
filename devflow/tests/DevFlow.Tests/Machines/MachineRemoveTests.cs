using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// <c>DELETE /devflow/api/machines/{id}</c>, the user's: the machine goes, and its key with it, so
/// the next request with the key is refused. See "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineRemoveTests
{
    [Fact]
    public async Task RemovingAMachineRevokesItsKey()
    {
        await using var routes = await Routes.Start();
        var (machine, key) = await Machines.Enrolled(routes, "mac-mini");
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        var (kept, keptKey) = await Machines.Enrolled(routes, "build-box");
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        var (bobs, bobsKey) = await Machines.Enrolled(routes, "bobs-machine", await Machines.CodeOf(routes, Machines.Bob));
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, key)).Response.StatusCode);

        var removed = await Machines.Remove(routes, machine);

        Assert.Equal(StatusCodes.Status204NoContent, removed.Response.StatusCode);
        // Its key is refused at once, and nothing of it is left.
        var refused = await Machines.SayHello(routes, key);
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
        Assert.Equal("", await Harness.Body(refused));
        Assert.Equal([kept.ToString()], (await Machines.List(routes)).Select(listed => (string)listed!["id"]!));
        await using (var admin = await routes.Database.OpenAdmin(Machines.Ct))
        {
            Assert.Equal([kept, bobs], await admin.QueryAsync<Guid>(new CommandDefinition(
                "SELECT machine_id FROM machine_keys ORDER BY created_at, machine_id", cancellationToken: Machines.Ct)));
            Assert.Equal(2, await admin.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM machine_key_lookup", cancellationToken: Machines.Ct)));
        }

        // The others' keys still work.
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, keptKey)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, bobsKey)).Response.StatusCode);

        // Removed again, or another user's, or none at all: 404, and Bob's stays.
        foreach (var id in new[] { machine, bobs, Guid.NewGuid() })
        {
            var missing = await Machines.Remove(routes, id);
            Assert.Equal(StatusCodes.Status404NotFound, missing.Response.StatusCode);
        }

        Assert.Equal([kept, bobs], (await Machines.Rows(routes)).Select(row => row.Id));
        Assert.Equal(StatusCodes.Status200OK, (await Machines.SayHello(routes, bobsKey)).Response.StatusCode);
    }
}
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// Enrolling a machine: "Add a machine" makes a code, <c>dfe_</c> and the base64url of 16 random
/// bytes, valid for one hour and once, and <c>POST /devflow/api/machine/enroll</c>, the one machine
/// route without a key, trades it for a machine of the code's user's and its key, in one
/// transaction. Every refusal of a code is <c>401</c> with an empty body. See "Machines" in
/// devflow/README.md.
/// </summary>
public sealed partial class MachineEnrollTests
{
    private static CancellationToken Ct => Machines.Ct;

    [GeneratedRegex("^dfe_[A-Za-z0-9_-]{22}$")]
    private static partial Regex CodeShape();

    [Fact]
    public async Task ACodeEnrollsOnce()
    {
        await using var routes = await Routes.Start();
        var made = routes.Time.GetUtcNow();
        var code = await Machines.NewCode(routes);
        Assert.Matches(CodeShape(), code);
        Assert.Equal(16, Base64Url.DecodeFromChars(code.AsSpan(4)).Length);
        routes.Time.Advance(TimeSpan.FromMinutes(5));
        var now = routes.Time.GetUtcNow();

        var response = await Machines.Enroll(routes, Machines.Enrollment(code, "mac-mini", "macos", "arm64"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var machine = Guid.Parse((string)(await Machines.Json(response))["machine_id"]!);
        // The code's user's machine, as it named itself, seen now, with nothing of hello yet.
        var row = Assert.Single(await Machines.Rows(routes));
        Assert.Equal(new Machines.MachineRow(machine, Machines.User, "mac-mini", "macos", "arm64", null, null, row.FirstSeenAt, row.LastSeenAt), row);
        Assert.Equal(now.UtcDateTime, row.FirstSeenAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(now.UtcDateTime, row.LastSeenAt, TimeSpan.FromMilliseconds(1));
        // Its code marked used, kept as its hash alone.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            var enrollment = await admin.QuerySingleAsync<(byte[] Hash, Guid User, DateTime Created, DateTime? Used)>(new CommandDefinition(
                "SELECT code_hash, user_id, created_at, used_at FROM machine_enrollments", cancellationToken: Ct));
            Assert.Equal(Machines.Hash(code), enrollment.Hash);
            Assert.Equal(Machines.User, enrollment.User);
            Assert.Equal(made.UtcDateTime, enrollment.Created, TimeSpan.FromMilliseconds(1));
            Assert.Equal(now.UtcDateTime, enrollment.Used!.Value, TimeSpan.FromMilliseconds(1));
        }

        // The same code a second time: refused, with nothing to tell why, and no second machine.
        var again = await Machines.Enroll(routes, Machines.Enrollment(code, "another"));
        Assert.Equal(StatusCodes.Status401Unauthorized, again.Response.StatusCode);
        Assert.Equal("", await Harness.Body(again));
        Assert.Single(await Machines.Rows(routes));
    }

    [Fact]
    public async Task ANewCodeLeavesTheOlderOne()
    {
        await using var routes = await Routes.Start();
        var first = await Machines.NewCode(routes);
        var second = await Machines.NewCode(routes);
        Assert.NotEqual(first, second);

        // Each is its own, and expires by itself: a newer one ends nothing.
        await Machines.Enrolled(routes, "first", first);
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        await Machines.Enrolled(routes, "second", second);
        Assert.Equal(["first", "second"], (await Machines.Rows(routes)).Select(row => row.Name));
    }

    [Fact]
    public async Task AnOldCodeIsRefused()
    {
        await using var routes = await Routes.Start();
        var early = await Machines.NewCode(routes);
        var late = await Machines.NewCode(routes);

        // Within the hour it enrols.
        routes.Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        await Machines.Enrolled(routes, "in-time", early);

        // An hour old, it does not.
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        var old = await Machines.Enroll(routes, Machines.Enrollment(late));
        Assert.Equal(StatusCodes.Status401Unauthorized, old.Response.StatusCode);
        Assert.Equal("", await Harness.Body(old));

        // Nor does anything that is not a code of the right shape, or not one made: the same
        // answer whatever the reason.
        var unknown = "dfe_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        foreach (var json in new[]
        {
            Machines.Enrollment(unknown),
            Machines.Enrollment(null),
            Machines.Enrollment(""),
            Machines.Enrollment("dfe_short"),
            Machines.Enrollment(late + "\n"),
            Machines.Enrollment(late + "A"),
            Machines.Enrollment(late.ToUpperInvariant()),
            Machines.Enrollment("dfm_" + late[4..]),
            """{"name": "mac-mini", "os": "macos", "arch": "arm64"}""",
        })
        {
            var refused = await Machines.Enroll(routes, json);
            Assert.True(refused.Response.StatusCode == StatusCodes.Status401Unauthorized, json);
            Assert.Equal("", await Harness.Body(refused));
        }

        Assert.Equal(["in-time"], (await Machines.Rows(routes)).Select(row => row.Name));
    }

    [Fact]
    public async Task AFieldBreakingARuleIs422AndSpendsNoCode()
    {
        await using var routes = await Routes.Start();
        var code = await Machines.NewCode(routes);

        foreach (var (json, field) in new[]
        {
            (Machines.Enrollment(code, name: ""), "name"),
            (Machines.Enrollment(code, name: "   "), "name"),
            (Machines.Enrollment(code, name: new string('m', 256)), "name"),
            (Machines.Enrollment(code, os: "windows"), "os"),
            (Machines.Enrollment(code, os: "macOS"), "os"),
            (Machines.Enrollment(code, arch: "x86"), "arch"),
            ($$"""{"code": "{{code}}", "os": "macos", "arch": "arm64"}""", "name"),
        })
        {
            var refused = await Machines.Enroll(routes, json);
            Assert.True(refused.Response.StatusCode == StatusCodes.Status422UnprocessableEntity, json);
            var problem = await Machines.Json(refused);
            Assert.Equal([field], problem["errors"]!.AsObject().Select(pair => pair.Key));
        }

        // The code was not spent on any of them.
        await Machines.Enrolled(routes, "mac-mini", code);
    }
}
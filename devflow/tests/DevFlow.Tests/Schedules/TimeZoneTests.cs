using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Schedules;

/// <summary>
/// The user's time zone over HTTP: one per user, Europe/Stockholm until they change it, an IANA id
/// .NET's own zones know. Against real Postgres, through the door as the caller. See "Schedules"
/// in devflow/README.md.
/// </summary>
public sealed class TimeZoneTests
{
    private const string Path = "/devflow/api/time-zone";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheZoneIsAnIanaId()
    {
        await using var routes = await Reading.Routes.Start();

        // A new user's zone is Stockholm.
        Assert.Equal("Europe/Stockholm", await Zone(routes));

        // IANA ids are taken, UTC among them, and answered as saved.
        foreach (var id in new[] { "UTC", "America/New_York", "Europe/Stockholm" })
        {
            var taken = await Put(routes, id);
            Assert.Equal(StatusCodes.Status200OK, taken.Response.StatusCode);
            Assert.Equal(id, (string)JsonNode.Parse(await Harness.Body(taken))!["time_zone"]!);
            Assert.Equal(id, await Zone(routes));
        }

        // A Windows id .NET would convert, a zone that does not exist, an empty one, a path and none
        // at all are refused, naming the field, and the zone stays.
        await Put(routes, "UTC");
        foreach (var id in new[] { "W. Europe Standard Time", "Mars/Olympus", "", "../../etc/localtime", "/etc/localtime" })
        {
            var refused = await Put(routes, id);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
            Assert.True(JsonNode.Parse(await Harness.Body(refused))!["errors"]!.AsObject().ContainsKey("time_zone"), id);
        }

        var missing = await routes.App.SendJson("PUT", Path, Contract.Token, "{}");
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, missing.Response.StatusCode);
        Assert.Equal("UTC", await Zone(routes));

        // Another user's zone is their own.
        Assert.Equal("Europe/Stockholm", (await DevFlowTime.ZoneOf(routes.Door, Reading.Routes.Bob, Ct)).Id);

        // A change from another origin is refused, and without a token there is nothing to read.
        Assert.Equal(StatusCodes.Status403Forbidden, (await routes.App.Send("PUT", Path, Contract.Token, ("Origin", "https://evil.test"))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await routes.App.Get(Path)).Response.StatusCode);
    }

    private static Task<HttpContext> Put(Reading.Routes routes, string id) =>
        routes.App.SendJson("PUT", Path, Contract.Token, new JsonObject { ["time_zone"] = id }.ToJsonString());

    private static async Task<string> Zone(Reading.Routes routes)
    {
        var response = await routes.App.Get(Path, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (string)JsonNode.Parse(await Harness.Body(response))!["time_zone"]!;
    }
}
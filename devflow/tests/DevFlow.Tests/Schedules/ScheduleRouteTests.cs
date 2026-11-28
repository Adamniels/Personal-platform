using System.Text.Json.Nodes;

using DevFlow.Tests.Jobs;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Schedules;

/// <summary>
/// A project's schedules over HTTP: <c>GET</c> answers the mode, the user's zone, the windows and
/// the next start; <c>PUT</c> replaces the mode and the list whole, in one transaction, and refuses
/// a list that breaks a rule, naming the field, leaving what was saved. Against real Postgres,
/// through the door as the caller. See "Schedules" in devflow/README.md.
/// </summary>
public sealed class ScheduleRouteTests
{
    internal static string Path(Guid project) => $"/devflow/api/projects/{project}/schedules";

    private static JsonObject Window(string name, JsonArray days, string from, string to, bool active = true) => new()
    {
        ["name"] = name,
        ["days"] = days,
        ["from"] = from,
        ["to"] = to,
        ["active"] = active,
    };

    private static string Body(string runsBy, params JsonObject[] windows) =>
        new JsonObject { ["runs_by"] = runsBy, ["schedules"] = new JsonArray([.. windows]) }.ToJsonString();

    [Fact]
    public async Task TheListIsSavedWhole()
    {
        await using var jobs = await Jobs.Jobs.Start();

        // A new project runs always, with no windows, in the user's zone.
        var fresh = await Schedules(jobs);
        Assert.Equal("always", (string)fresh["runs_by"]!);
        Assert.Equal("Europe/Stockholm", (string)fresh["time_zone"]!);
        Assert.Empty(fresh["schedules"]!.AsArray());

        // Two windows: the name trimmed, the days a set in the week's order, each with an id.
        var saved = await Put(jobs, Body("schedule",
            Window("  Night ", ["fri", "mon", "mon", "tue", "wed", "thu"], "22:00", "06:00"),
            Window("Weekend", ["sat", "sun"], "10:00", "14:00", active: false)));
        Assert.Equal("schedule", (string)saved["runs_by"]!);
        var windows = saved["schedules"]!.AsArray();
        Assert.Equal(2, windows.Count);
        Assert.Equal("Night", (string)windows[0]!["name"]!);
        Assert.Equal(["mon", "tue", "wed", "thu", "fri"], windows[0]!["days"]!.AsArray().Select(day => (string)day!));
        Assert.Equal(("22:00", "06:00", true), ((string)windows[0]!["from"]!, (string)windows[0]!["to"]!, (bool)windows[0]!["active"]!));
        Assert.Equal(("Weekend", false), ((string)windows[1]!["name"]!, (bool)windows[1]!["active"]!));
        var nightId = (Guid)windows[0]!["id"]!;
        Assert.NotEqual(nightId, (Guid)windows[1]!["id"]!);
        Assert.Equal(saved.ToJsonString(), (await Schedules(jobs)).ToJsonString());

        // Saved again with one, keeping its id and switching it off, it replaces the list and the
        // mode whole.
        var night = Window("Night", ["mon"], "22:00", "06:00", active: false);
        night["id"] = nightId;
        var replaced = await Put(jobs, Body("always", night));
        Assert.Equal("always", (string)replaced["runs_by"]!);
        var kept = Assert.Single(replaced["schedules"]!.AsArray())!;
        Assert.Equal((nightId, false), ((Guid)kept["id"]!, (bool)kept["active"]!));
        Assert.Null(replaced["next_start"]);

        // Each rule broken is 422 naming its field, and leaves what was saved.
        var tooMany = Enumerable.Range(1, 21).Select(n => Window($"W{n}", ["mon"], "09:00", "10:00")).ToArray();
        var twice = Window("Twice", ["mon"], "09:00", "10:00");
        twice["id"] = nightId;
        var twiceAgain = Window("Twice again", ["tue"], "09:00", "10:00");
        twiceAgain["id"] = nightId;
        foreach (var (body, field) in new[]
        {
            ("""{"schedules": []}""", "runs_by"),
            (Body("sometimes"), "runs_by"),
            ("""{"runs_by": "schedule"}""", "schedules"),
            (Body("schedule", tooMany), "schedules"),
            (Body("schedule", Window("", ["mon"], "09:00", "10:00")), "schedules[0].name"),
            (Body("schedule", Window("   ", ["mon"], "09:00", "10:00")), "schedules[0].name"),
            (Body("schedule", Window(new string('n', 61), ["mon"], "09:00", "10:00")), "schedules[0].name"),
            (Body("schedule", Window("Day", ["mon"], "09:00", "10:00"), Window(" day", ["tue"], "11:00", "12:00")), "schedules[1].name"),
            (Body("schedule", Window("Day", [], "09:00", "10:00")), "schedules[0].days"),
            (Body("schedule", Window("Day", ["monday"], "09:00", "10:00")), "schedules[0].days"),
            (Body("schedule", Window("Day", ["mon"], "9:00", "10:00")), "schedules[0].from"),
            (Body("schedule", Window("Day", ["mon"], "09:00", "24:00")), "schedules[0].to"),
            (Body("schedule", Window("Day", ["mon"], "09:00", "09:00")), "schedules[0].to"),
            (Body("schedule", twice, twiceAgain), "schedules[1].id"),
            ("""{"runs_by": "schedule", "schedules": [{"name": "Day", "days": ["mon"], "from": "09:00", "to": "10:00"}]}""", "schedules[0].active"),
        })
        {
            var refused = await jobs.Routes.App.SendJson("PUT", Path(jobs.Project), Contract.Token, body);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
            Assert.True(JsonNode.Parse(await Harness.Body(refused))!["errors"]!.AsObject().ContainsKey(field), $"{body} names {field}");
        }

        Assert.Equal(replaced.ToJsonString(), (await Schedules(jobs)).ToJsonString());

        // Another user's project, and one never made, are none of mine.
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, Reading.Routes.Bob, repository: 4242, name: "bobs-repository");
        foreach (var project in new[] { bobs, Guid.CreateVersion7() })
        {
            Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get(Path(project), Contract.Token)).Response.StatusCode);
            Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.SendJson("PUT", Path(project), Contract.Token, Body("schedule"))).Response.StatusCode);
        }

        // A change from another origin is refused, and without a token there is nothing to read.
        Assert.Equal(StatusCodes.Status403Forbidden, (await jobs.Routes.App.Send("PUT", Path(jobs.Project), Contract.Token, ("Origin", "https://evil.test"))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await jobs.Routes.App.Get(Path(jobs.Project))).Response.StatusCode);
        Assert.Equal(replaced.ToJsonString(), (await Schedules(jobs)).ToJsonString());
    }

    /// <summary>The project's schedules as its route answers them.</summary>
    internal static async Task<JsonNode> Schedules(Jobs.Jobs jobs)
    {
        var response = await jobs.Routes.App.Get(Path(jobs.Project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!;
    }

    private static async Task<JsonNode> Put(Jobs.Jobs jobs, string body)
    {
        var response = await jobs.Routes.App.SendJson("PUT", Path(jobs.Project), Contract.Token, body);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!;
    }
}
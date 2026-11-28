using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Schedules;

/// <summary>What <c>PUT /devflow/api/time-zone</c> takes.</summary>
/// <param name="TimeZone">An IANA id, such as <c>Europe/Stockholm</c> or <c>UTC</c>.</param>
public sealed record TimeZoneRequest(string? TimeZone);

/// <summary>The caller's time zone, as <c>/devflow/api/time-zone</c> answers it.</summary>
/// <param name="TimeZone">Its IANA id.</param>
public sealed record TimeZoneAnswer(string TimeZone);

/// <summary>
/// A project's schedules and the caller's time zone over HTTP, every route under the <c>Api</c>
/// policy and the origin check, through the door as the caller, so another user's project is none.
/// Each saves at once, never through the settings page's Save. See "Schedules" in
/// devflow/README.md.
/// </summary>
public static class ScheduleEndpoints
{
    private const string Rest = " Without a valid token, 401 with an empty body; a change from another origin, 403.";

    public static IEndpointRouteBuilder MapSchedules(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/schedules", async Task<Results<Ok<ProjectSchedules>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                await ScheduleStore.Read(database, context.UserId(), id, time.GetUtcNow(), context.RequestAborted) is { } schedules
                    ? TypedResults.Ok(schedules)
                    : NoSuchProject())
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getSchedules")
            .WithSummary("When a project runs")
            .WithDescription("runs_by, always or schedule; time_zone, the caller's, which every window is wall clock in; and schedules, its windows in their order, each with id, name, days, the days it starts on, mon to sun in the week's order, from and to, HH:MM, a to earlier than from ending the next day, and active, whether it is on; next_start, when a window that is on next starts, after the one it runs in ends, looked for over the next 8 days, null with none and for a project that runs always; and running_until, when it runs by schedule and is inside a window that is on, when that ends, windows that meet or overlap taken as one, null otherwise. Both are instants with the zone's own offset. 404 for a project the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPut("/devflow/api/projects/{id:guid}/schedules", Replace)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("replaceSchedules")
            .WithSummary("Replace when a project runs, as the caller")
            .WithDescription("Takes runs_by and schedules, the whole list, and replaces both in one transaction, answering what it left as getSchedules does. A window without an id gets a new one, and one with an id keeps it. The rules: runs_by is always or schedule; schedules is a list, empty for none, of at most 20 windows; a name is 1 to 60 characters once trimmed, unique in the project whatever its case; days is a non-empty set of mon, tue, wed, thu, fri, sat and sun, kept once each in the week's order; from and to are HH:MM in 24 hours, different; active is true or false; an id is given at most once. In order: 404 for a project the caller does not have; 422 naming each field that breaks a rule, runs_by, schedules or schedules[i].<field>, and nothing is saved." + Rest)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/devflow/api/time-zone", async (HttpContext context, [FromServices] UserDb database) =>
                TypedResults.Ok(new TimeZoneAnswer((await DevFlowTime.ZoneOf(database, context.UserId(), context.RequestAborted)).Id)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getTimeZone")
            .WithSummary("The caller's time zone")
            .WithDescription("time_zone, the IANA id every schedule of the caller's and every day Dev flow counts for them is read in; Europe/Stockholm until they set another. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapPut("/devflow/api/time-zone", SetZone)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("setTimeZone")
            .WithSummary("Set the caller's time zone")
            .WithDescription("Takes time_zone, an IANA id: UTC, or an area and a place joined by /, of letters, digits, _, + and -, at most 64 characters, that .NET's own zones know, such as Europe/Stockholm; a Windows id such as W. Europe Standard Time is refused. Used by every project of the caller's, its schedules and its days, at once. 200 with the zone as saved; 422 naming time_zone for anything else." + Rest)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<Ok<ProjectSchedules>, ProblemHttpResult>> Replace(Guid id, ScheduleRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var now = time.GetUtcNow();
        if (await ScheduleStore.Read(database, userId, id, now, context.RequestAborted) is null)
        {
            return NoSuchProject();
        }

        if (ScheduleRules.Check(request, out var errors) is not { } checkedRequest)
        {
            return Refused(errors);
        }

        try
        {
            return await ScheduleStore.Replace(database, userId, id, checkedRequest.RunsBy, checkedRequest.Windows, now, context.RequestAborted) is { } saved
                ? TypedResults.Ok(saved)
                : NoSuchProject();
        }
        catch (PostgresException unique) when (unique is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "project_schedules_name" })
        {
            // Two names the rules found different that Postgres's lower() finds the same.
            return Refused(new() { ["schedules"] = ["holds two windows whose names differ only in their case"] });
        }
    }

    private static async Task<Results<Ok<TimeZoneAnswer>, ProblemHttpResult>> SetZone(TimeZoneRequest request, HttpContext context, [FromServices] UserDb database)
    {
        if (DevFlowTime.FindIana(request.TimeZone) is null)
        {
            return Refused(new() { ["time_zone"] = ["must be an IANA time zone, such as Europe/Stockholm or UTC"] });
        }

        await DevFlowTime.SetZone(database, context.UserId(), request.TimeZone!, context.RequestAborted);
        return TypedResults.Ok(new TimeZoneAnswer(request.TimeZone!));
    }

    private static ProblemHttpResult Refused(Dictionary<string, string[]> errors) =>
        TypedResults.Problem(new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = string.Join("; ", errors.Select(error => $"{error.Key} {error.Value[0]}")),
        });

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");
}
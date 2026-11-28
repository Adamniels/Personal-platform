using DevFlow.Api;
using DevFlow.Protocol;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Machines;

/// <summary>What "Add a machine" answers, the one time its code is sent.</summary>
/// <param name="Code">The code, <c>dfe_</c> and the base64url of 16 random bytes.</param>
/// <param name="ExpiresAt">When it stops enrolling, an hour from now.</param>
/// <param name="Command">The command that installs the machine program on any machine, at the
/// public origin, with the code in it.</param>
/// <param name="LocalCommand">The same for the machine the platform runs on, at
/// <c>DEVFLOW_LOCAL_URL</c>.</param>
public sealed record MachineEnrollment(string Code, DateTimeOffset ExpiresAt, string Command, string LocalCommand);

/// <summary>
/// Machines over HTTP. The user's three, under the <c>Api</c> policy and the origin check: the
/// list, a new code with its commands, and removing one. The machine's, under
/// <see cref="MachineAuthentication.Policy"/> and outside the origin check: <c>enroll</c>, which
/// alone takes no key, since it is how a machine gets one, and <c>hello</c>. The machine's take and
/// answer <c>DevFlow.Protocol</c>'s types at <see cref="MachineApi"/>'s paths, which the machine
/// program sends. See "Machines" in devflow/README.md.
/// </summary>
public static class MachineEndpoints
{
    /// <summary>The most a machine's text may hold, as the table's checks say.</summary>
    private const int MaxText = 255;

    /// <param name="release">Dev flow's release, <c>DEVFLOW_RELEASE</c>, which <c>hello</c>
    /// answers.</param>
    /// <param name="install">The commands "Add a machine" shows.</param>
    public static IEndpointRouteBuilder MapMachines(this IEndpointRouteBuilder routes, string release, MachineInstall install)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrEmpty(release);
        ArgumentNullException.ThrowIfNull(install);

        routes.MapGet("/devflow/api/machines", async (HttpContext context, [FromServices] MachineStore store) =>
                TypedResults.Ok(await store.List(context.UserId(), context.RequestAborted)))
            .UserRoute("listMachines", "The caller's machines",
                "Every machine the caller enrolled, by when it did, with its name, os, arch, program_version and docker as it last said hello, both null before its first, last_seen_at, when it enrolled or last said hello, and online, whether that was less than 60 seconds ago. Never its key, and never another user's machine.");

        routes.MapPost("/devflow/api/machines/enrollment", async (HttpContext context, [FromServices] MachineEnrollments enrollments) =>
            {
                var code = await enrollments.Make(context.UserId(), context.RequestAborted);
                // Sent once, and kept by no cache on the way.
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new MachineEnrollment(code.Code, code.ExpiresAt, install.Command(code.Code), install.LocalCommand(code.Code)));
            })
            .UserRoute("newMachineEnrollment", "A code that enrols a machine",
                "A new code, dfe_ and the base64url of 16 random bytes, answered this once with Cache-Control: no-store, with expires_at, an hour from now, and the two commands that install the machine program with it: command, at the public origin, for any machine, and local_command, at DEVFLOW_LOCAL_URL, for the machine the platform runs on, which cannot reach its own public address. Dev flow keeps only the code's SHA-256. It enrols one machine, once, within the hour; a newer code ends no older one.");

        routes.MapDelete("/devflow/api/machines/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] MachineStore store) =>
                await store.Remove(context.UserId(), id, context.RequestAborted)
                    ? TypedResults.NoContent()
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such machine", detail: "the machine does not exist"))
            .UserRoute("removeMachine", "Remove a machine",
                "Removes the machine and its key, so the key is refused from its next request. 204. 404 for a machine the caller does not have, another user's among them.",
                StatusCodes.Status404NotFound);

        routes.MapPost(MachineApi.EnrollPath, Enroll)
            .AllowAnonymous()
            .WithName("machineEnroll")
            .WithSummary("A machine trades a code for its key")
            .WithDescription("The code \"Add a machine\" showed, and the machine's name, os, macos or linux, and arch, arm64 or x64. Marks the code used, records the machine as the code's user's, seen now, and answers its machine_id and its key, dfm_ and the base64url of 32 random bytes, with Cache-Control: no-store, the one time the key is sent: Dev flow keeps only its SHA-256. The one machine route without a key, no Origin needed, and no cookie read or set. 401 with an empty body for a code that is missing, not of the shape dfe_ and 22 base64url characters, not made, used already or made an hour ago or more, the same whatever the reason; 422 naming a field that breaks a rule, which spends no code.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        routes.MapPost(MachineApi.HelloPath, (MachineHelloRequest request, HttpContext context, [FromServices] MachineStore store) => Hello(request, context, store, release))
            .MachineRoute("machineHello", "A machine says what it is and runs",
                "The machine's name, os, macos or linux, arch, arm64 or x64, program_version, the machine program's, and docker, running or why Docker is not. Records them and marks the machine seen now, and answers release, Dev flow's release, DEVFLOW_RELEASE, which the program updates itself to. 422 naming a field that is missing or breaks a rule: every one is required, and each text is 1 to 255 characters, not only spaces.",
                StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<Ok<EnrollAnswer>, UnauthorizedHttpResult, ProblemHttpResult>> Enroll(EnrollRequest request, HttpContext context, [FromServices] MachineEnrollments enrollments)
    {
        // The fields first, so a request that breaks a rule spends no code.
        var errors = new Dictionary<string, string[]>();
        CheckText("name", request.Name, errors);
        CheckOs(request.Os, errors);
        CheckArch(request.Arch, errors);
        if (errors.Count > 0)
        {
            return WorkerEndpoints.Invalid(errors);
        }

        if (await enrollments.Enroll(request.Code, new MachineIntro(request.Name!, request.Os!, request.Arch!), context.RequestAborted) is not { } enrolled)
        {
            return TypedResults.Unauthorized();
        }

        // Sent once, and kept by no cache on the way.
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new EnrollAnswer(enrolled.MachineId, enrolled.Key));
    }

    private static async Task<Results<Ok<MachineHelloAnswer>, UnauthorizedHttpResult, ProblemHttpResult>> Hello(MachineHelloRequest request, HttpContext context, MachineStore store, string release)
    {
        var errors = new Dictionary<string, string[]>();
        CheckText("name", request.Name, errors);
        CheckOs(request.Os, errors);
        CheckArch(request.Arch, errors);
        CheckText("program_version", request.ProgramVersion, errors);
        CheckText("docker", request.Docker, errors);
        if (errors.Count > 0)
        {
            return WorkerEndpoints.Invalid(errors);
        }

        var report = new MachineReport(request.Name!, request.Os!, request.Arch!, request.ProgramVersion!, request.Docker!);
        // The machine was removed between finding the key and now, and the key with it.
        return await store.Hello(context.Machine(), report, context.RequestAborted)
            ? TypedResults.Ok(new MachineHelloAnswer(release))
            : TypedResults.Unauthorized();
    }

    private static void CheckText(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxText)
        {
            errors[field] = [$"must be text of 1 to {MaxText} characters, not only spaces"];
        }
    }

    private static void CheckOs(string? value, Dictionary<string, string[]> errors)
    {
        if (value is not ("macos" or "linux"))
        {
            errors["os"] = ["must be macos or linux"];
        }
    }

    private static void CheckArch(string? value, Dictionary<string, string[]> errors)
    {
        if (value is not ("arm64" or "x64"))
        {
            errors["arch"] = ["must be arm64 or x64"];
        }
    }

    /// <summary>A user's route, under the <c>Api</c> policy, named in the schema with the problems
    /// it may answer, as the workers' are.</summary>
    private static RouteHandlerBuilder UserRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            route.ProducesProblem(status);
        }

        return route;
    }

    /// <summary>A machine's route, under <see cref="MachineAuthentication.Policy"/> alone, named in
    /// the schema with the problems it may answer.</summary>
    private static RouteHandlerBuilder MachineRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(MachineAuthentication.Policy)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Takes the machine's key as Authorization: Bearer dfm_…, and nothing else: without a key a machine has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            if (status == StatusCodes.Status422UnprocessableEntity)
            {
                route.ProducesValidationProblem(status);
            }
            else
            {
                route.ProducesProblem(status);
            }
        }

        return route;
    }
}
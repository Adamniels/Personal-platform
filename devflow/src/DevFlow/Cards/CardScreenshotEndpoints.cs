using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Reading;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Cards;

/// <summary>
/// One of the pictures the worker took of a card's head commit, under the <c>Api</c> policy, served
/// through part 1's checks, <see cref="ScreenshotEndpoints.Guard"/> and
/// <see cref="ScreenshotEndpoints.FromHost"/>: the name can only be one the card's newest build
/// listed, which the worker's result was checked to name by the rules, and a name not listed is
/// refused before the code host is asked; the path is built here, the bytes are read as the app,
/// and they are served only as a PNG of at most 5 MB, with <c>nosniff</c> and
/// <c>default-src 'none'</c>. See "Screenshots" in devflow/README.md and "A code host's bytes
/// handed to the browser" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class CardScreenshotEndpoints
{
    public static IEndpointRouteBuilder MapCardScreenshots(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/cards/{number:int}/screenshots/{name}", Serve)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getCardScreenshot")
            .WithSummary("One of the pictures the worker took of a card's head commit, read from GitHub as the app")
            .WithDescription("The PNG the card's newest build that succeeded listed as name, read from the branch pr-screenshots of the project's own repository, at devflow/<number>/<the head commit's first seven characters>/<name>.png, as the app. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for a project or card the caller does not have, a card with no such build, or a name the build did not list, without asking GitHub; 404 when GitHub has no such file; 502 when what GitHub has is not a PNG or is over 5 MB; 503 when GitHub did not answer. Without a valid token, 401 with an empty body.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/png")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> Serve(
        Guid id, int number, string name, HttpContext context, [FromServices] UserDb database, [FromServices] ICodeHost host, [FromServices] TimeProvider time)
    {
        ScreenshotEndpoints.Guard(context);
        if (await CardStory.ScreenshotsOf(database, context.UserId(), id, number, context.RequestAborted) is not { } source)
        {
            return ScreenshotEndpoints.NotFound("You have no card with that number in that project, or it has not been built.");
        }

        // Compared with the names the build listed, so the path below is built only from one of
        // them, which holds nothing but lowercase letters, digits and hyphens.
        if (!source.Names.Contains(name, StringComparer.Ordinal))
        {
            return ScreenshotEndpoints.NotFound("The card's build took no picture by that name.");
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"{source.Folder}/{name}.png");
        return await ScreenshotEndpoints.FromHost(context, host, time, source.Repository, path, "GitHub has no picture by that name for this card.");
    }
}
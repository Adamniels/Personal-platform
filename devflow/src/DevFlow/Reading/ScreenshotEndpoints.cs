using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Runner;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Reading;

/// <summary>
/// One of a waiting item's screenshots, under the <c>Api</c> policy: the one place Dev flow hands
/// the browser bytes that came from a code host. So the name can only be one the reader already
/// accepted, the path is built here, the bytes are read as the app, and they are served only as a
/// PNG they are checked to be, with a content type that is fixed and never the host's, in a
/// response that cannot be sniffed as anything else or run anything. See "What waits" in
/// devflow/README.md and "A code host's bytes handed to the browser" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class ScreenshotEndpoints
{
    /// <summary>The most bytes a screenshot may have, 5 MB.</summary>
    public const int MostBytes = 5 * 1024 * 1024;

    /// <summary>How long the browser keeps one, an hour: a screenshot of a pull request's head is
    /// pushed again under the same name only when the agent changes the page.</summary>
    private const string Kept = "private, max-age=3600";

    /// <summary>What every PNG begins with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static IEndpointRouteBuilder MapScreenshots(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/waiting/{id:guid}/screenshots/{name}", Serve)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getWaitingScreenshot")
            .WithSummary("One of a waiting item's screenshots, read from GitHub as the app")
            .WithDescription("The PNG the item's pull request body showed as name, read from the branch pr-screenshots of the item's own repository, at <number>/<name>.png, as the app. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for an item the caller does not have, or a name its body did not show, without asking GitHub; 404 when GitHub has no such file; 502 when what GitHub has is not a PNG or is over 5 MB; 503 when GitHub did not answer. Without a valid token, 401 with an empty body.")
            .Produces<Stream>(StatusCodes.Status200OK, "image/png")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> Serve(
        Guid id, string name, HttpContext context, [FromServices] WaitingStore store, [FromServices] ICodeHost host, [FromServices] TimeProvider time)
    {
        Guard(context);
        var userId = context.UserId();
        if (await store.ScreenshotsOf(userId, id, context.RequestAborted) is not { } source)
        {
            return NotFound("Nothing waits for you with that id.");
        }

        // Compared with the names the reader accepted, so the path below is built only from one
        // of them, which holds nothing but lowercase letters, digits and hyphens.
        if (!source.Names.Contains(name, StringComparer.Ordinal))
        {
            return NotFound("The pull request does not show a screenshot by that name.");
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"{source.Number}/{name}.png");
        return await FromHost(context, host, time, source.Repository, path, "GitHub has no screenshot by that name for this pull request.");
    }

    /// <summary>The headers every answer of a screenshot route carries, set before anything is
    /// read, so whatever it answers is never sniffed, framed or run, and only a PNG served is
    /// kept.</summary>
    internal static void Guard(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'";
        headers.CacheControl = "no-store";
    }

    /// <summary>
    /// The file at <paramref name="path"/> on <c>pr-screenshots</c> of <paramref name="repository"/>,
    /// which the caller built from a name it listed, read as the app and served only as a PNG of at
    /// most 5 MB, fixed as <c>image/png</c>, kept an hour; 404 with <paramref name="missing"/> when
    /// the code host has none, 502 when it is not such a PNG, 503 when the host did not answer. The
    /// one place a code host's bytes reach the browser, for part 1's items and Dev flow's cards
    /// alike.
    /// </summary>
    internal static async Task<Results<FileContentHttpResult, ProblemHttpResult>> FromHost(
        HttpContext context, ICodeHost host, TimeProvider time, Repository repository, string path, string missing)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(time);
        var headers = context.Response.Headers;
        switch (await host.File(context.UserId(), repository, path, PullRequestBody.ScreenshotBranch, context.RequestAborted))
        {
            case CodeHostResult<byte[]>.Found { Value: var bytes }:
                if (bytes.Length > MostBytes || !bytes.AsSpan().StartsWith(PngSignature))
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status502BadGateway,
                        title: "The screenshot is not one Dev flow can show",
                        detail: "What GitHub has under that name is not a PNG of at most 5 MB.");
                }

                headers.CacheControl = Kept;
                return TypedResults.File(bytes, "image/png");
            case CodeHostResult<byte[]>.NotFound:
                return NotFound(missing);
            case CodeHostResult<byte[]>.Unavailable { RetryAt: var retryAt }:
                if (retryAt is { } at)
                {
                    var seconds = Math.Max(0, (long)Math.Ceiling((at - time.GetUtcNow()).TotalSeconds));
                    headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: "Try again in a moment.");
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }
    }

    internal static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such screenshot", detail: detail);
}
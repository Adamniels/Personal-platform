using System.Globalization;
using System.Text;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DevFlow.Recipes;

/// <summary>
/// The recipe route, under <see cref="WorkerAuthentication.Policy"/>: the project's
/// <c>.devflow/Dockerfile</c> from its default branch, read as the app, handed to whoever holds the
/// project's key, which is the command on the project's page building a worker. Served as text
/// whose type is fixed and never the host's, never sniffed or kept, with the commit it was read at
/// in <see cref="VersionHeader"/>, which the build turns into the version the worker reports. A
/// recipe that would copy files into the image is refused naming the line. See "Starting a worker"
/// in devflow/README.md, and the screenshot route, the other place a code host's bytes are handed
/// on, in <c>Reading/ScreenshotEndpoints.cs</c>.
/// </summary>
public static class RecipeEndpoints
{
    /// <summary>Where the recipe is.</summary>
    public const string Path = WorkerApi.Prefix + "/recipe";

    /// <summary>The recipe's path in the project's repository.</summary>
    public const string RecipePath = ".devflow/Dockerfile";

    /// <summary>The header the commit the recipe was read at is in.</summary>
    public const string VersionHeader = "X-Devflow-Recipe-Version";

    /// <summary>The most bytes a recipe may have, 64 KB, far more than one needs.</summary>
    public const int MostBytes = 64 * 1024;

    public const string Missing = "This repository has no .devflow/Dockerfile";

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IEndpointRouteBuilder MapRecipe(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, Serve)
            .RequireAuthorization(WorkerAuthentication.Policy)
            .WithName("workerRecipe")
            .WithSummary("The project's recipe, to build a worker from")
            .WithDescription($"The file {RecipePath} of the key's project, from its repository's default branch, read as the app at the newest commit that changed it: text/plain, with that commit's id in {VersionHeader}, X-Content-Type-Options: nosniff and Cache-Control: no-store. 404 when the repository has none; 422 naming the line when it copies files into the image, a COPY or an ADD in any case, after continued lines are joined, or when it is not UTF-8 text of at most 64 KB; 503 when GitHub did not answer. Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> Serve(
        HttpContext context, [FromServices] WorkerStore store, [FromServices] ICodeHost host, [FromServices] TimeProvider time)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-store";

        var caller = context.Worker();
        var cancellationToken = context.RequestAborted;
        if (await store.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            // The project went between finding the key and now.
            return NotFound("The key's project is gone.");
        }

        switch (await host.DefaultBranchFile(caller.UserId, repository, RecipePath, cancellationToken))
        {
            case CodeHostResult<FileAtCommit>.Found { Value: var file }:
                if (file.Content.Length > MostBytes || Text(file.Content) is not { } text)
                {
                    return Refuse("The recipe is not one Dev flow can hand out", $"{RecipePath} must be UTF-8 text of at most 64 KB.", line: null);
                }

                if (Recipe.FirstRefused(text) is { } refused)
                {
                    return Refuse(
                        "The recipe copies files into the image",
                        string.Create(CultureInfo.InvariantCulture, $"Line {refused.Line} of {RecipePath} is {refused.Instruction}. A worker's image is built with no files of the project, so a recipe cannot COPY or ADD them: install the project's packages in its check instead."),
                        refused.Line);
                }

                await store.RecipeRead(caller, file.Commit, cancellationToken);
                headers[VersionHeader] = file.Commit;
                return TypedResults.Text(text, "text/plain", Encoding.UTF8);
            case CodeHostResult<FileAtCommit>.NotFound:
                return NotFound(Missing);
            case CodeHostResult<FileAtCommit>.Unavailable { RetryAt: var retryAt }:
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

    /// <summary>The bytes as text, or null when they are not UTF-8.</summary>
    private static string? Text(byte[] bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static ProblemHttpResult Refuse(string title, string detail, int? line) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title,
            detail: detail,
            extensions: line is { } at ? new Dictionary<string, object?> { ["line"] = at } : null);

    private static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No recipe", detail: detail);
}
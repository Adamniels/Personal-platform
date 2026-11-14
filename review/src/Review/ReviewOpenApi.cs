namespace Review;

/// <summary>
/// The OpenAPI document of review's API, which contracts/review/openapi.json holds and the page's
/// types are generated from. It is generated only for the contract test and for regenerating the
/// file, with <c>REVIEW_EXPOSE_OPENAPI=1</c>; the running service serves it nowhere. See
/// contracts/README.md and review/README.md.
/// </summary>
public static class ReviewOpenApi
{
    public const string Path = "/review/api/openapi.json";

    public static IServiceCollection AddReviewOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "review";
            document.Info.Version = "0.1.0";
            // The server list is the address the document was asked at, which differs between a
            // test and a running service, and callers reach review through the proxy anyway.
            document.Servers?.Clear();
            return Task.CompletedTask;
        }));
}
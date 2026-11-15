namespace DevFlow;

/// <summary>
/// The OpenAPI document of Dev flow's API, which contracts/devflow/openapi.json holds and the page's
/// types are generated from. It is generated only for the contract test and for regenerating the
/// file, with <c>DEVFLOW_EXPOSE_OPENAPI=1</c>; the running service serves it nowhere. See
/// contracts/README.md and devflow/README.md.
/// </summary>
public static class DevFlowOpenApi
{
    public const string Path = "/devflow/api/openapi.json";

    public static IServiceCollection AddDevFlowOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "devflow";
            document.Info.Version = "0.1.0";
            // The server list is the address the document was asked at, which differs between a
            // test and a running service, and callers reach Dev flow through the proxy anyway.
            document.Servers?.Clear();
            return Task.CompletedTask;
        }));
}
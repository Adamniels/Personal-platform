using System.Text.Json.Nodes;

using DevFlow.Projects;

using Microsoft.OpenApi;

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
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "devflow";
                document.Info.Version = "0.1.0";
                // The server list is the address the document was asked at, which differs between a
                // test and a running service, and callers reach Dev flow through the proxy anyway.
                document.Servers?.Clear();
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // A project's preset is computed from its settings rather than given to the
                // constructor, so the generator takes it as optional and nullable; it is always
                // answered, and one of four names.
                if (context.JsonTypeInfo.Type == typeof(ProjectSettings) && schema.Properties?.ContainsKey("preset") == true)
                {
                    schema.Properties["preset"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Enum = [.. new[] { Presets.FullyAutomatic, Presets.Guided, Presets.OnlyIMerge, Presets.Custom }.Select(name => (JsonNode)name)],
                    };
                    schema.Required?.Add("preset");
                }

                return Task.CompletedTask;
            });
        });
}
using Microsoft.Extensions.FileProviders;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Pages;

/// <summary>
/// Dev flow's page: one application, web/, built into web/dist. Its index.html answers each
/// section's address, which the application reads to decide what to show, and its built files are
/// served under /devflow/assets/. Review's page, served the same way. See "What it serves" in
/// devflow/README.md.
/// </summary>
public static class PageEndpoints
{
    /// <summary>The addresses of the application, as route templates: one per tab, as
    /// web/src/sections.ts lists them, a project's page under the Projects tab, by its id, its
    /// settings page under it, and a card's page and a feature's page under its project, by their
    /// numbers; the two change together. Anything else under /devflow is 404.</summary>
    public static readonly IReadOnlyList<string> Addresses = ["/devflow", "/devflow/running", "/devflow/projects", "/devflow/usage", "/devflow/machines", "/devflow/projects/{id:guid}", "/devflow/projects/{id:guid}/settings", "/devflow/projects/{id:guid}/cards/{number:int}", "/devflow/projects/{id:guid}/features/{number:int}"];

    /// <summary>
    /// Stops Dev flow at start when <paramref name="webRoot"/> holds no build, rather than serving a
    /// page that is not there. CI and the runner build every frontend before starting services.
    /// </summary>
    /// <exception cref="StartupException">index.html or assets/ is missing, named in full.</exception>
    public static void CheckBuild(string webRoot)
    {
        ArgumentNullException.ThrowIfNull(webRoot);
        var index = Path.Combine(webRoot, "index.html");
        if (!File.Exists(index))
        {
            throw new StartupException($"{index} is missing: build the page with npm ci and npm run build in devflow/web, see devflow/README.md");
        }

        var assets = Path.Combine(webRoot, "assets");
        if (!Directory.Exists(assets))
        {
            throw new StartupException($"{assets} is missing: build the page with npm ci and npm run build in devflow/web, see devflow/README.md");
        }
    }

    /// <summary>
    /// The built files, /devflow/assets/*, with no token, as review and the brain serve their own:
    /// they are the same for everyone and hold no data. Runs before routing, so a file that is not
    /// there falls through to a 404.
    /// </summary>
    public static IApplicationBuilder UseBuiltFiles(this IApplicationBuilder app, string webRoot)
    {
        ArgumentNullException.ThrowIfNull(webRoot);
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(webRoot, "assets")),
            RequestPath = "/devflow/assets",
        });
    }

    /// <summary>index.html for each address in <see cref="Addresses"/>, with any query, behind
    /// the <see cref="PlatformAuthPolicies.Page"/> policy.</summary>
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder routes, string webRoot)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(webRoot);
        var index = Path.Combine(webRoot, "index.html");
        foreach (var address in Addresses)
        {
            routes.MapGet(address, (HttpContext context) =>
                {
                    // Revalidated on every load, so a new build is seen at once.
                    context.Response.Headers.CacheControl = "no-cache";
                    return TypedResults.PhysicalFile(index, "text/html; charset=utf-8");
                })
                .RequireAuthorization(PlatformAuthPolicies.Page)
                .ExcludeFromDescription();
        }

        return routes;
    }
}
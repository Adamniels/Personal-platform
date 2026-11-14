using Microsoft.Extensions.FileProviders;

using PlatformAuth;

using Review.Settings;

namespace Review.Pages;

/// <summary>
/// Review's page: one application, web/, built into web/dist. Its index.html answers each
/// section's address, which the application reads to decide what to show, and its built files are
/// served under /review/assets/. See "What it serves" in review/README.md.
/// </summary>
public static class PageEndpoints
{
    /// <summary>The addresses of the application. /review/study is the study session's, from a
    /// later slice, and shows the Study tab until then. Anything else under /review is 404.</summary>
    public static readonly IReadOnlyList<string> Addresses = ["/review", "/review/cards", "/review/decks", "/review/study"];

    /// <summary>
    /// Stops review at start when <paramref name="webRoot"/> holds no build, rather than serving a
    /// page that is not there. CI and the runner build every frontend before starting services.
    /// </summary>
    /// <exception cref="StartupException">index.html or assets/ is missing, named in full.</exception>
    public static void CheckBuild(string webRoot)
    {
        ArgumentNullException.ThrowIfNull(webRoot);
        var index = Path.Combine(webRoot, "index.html");
        if (!File.Exists(index))
        {
            throw new StartupException($"{index} is missing: build the page with npm ci and npm run build in review/web, see review/README.md");
        }

        var assets = Path.Combine(webRoot, "assets");
        if (!Directory.Exists(assets))
        {
            throw new StartupException($"{assets} is missing: build the page with npm ci and npm run build in review/web, see review/README.md");
        }
    }

    /// <summary>
    /// The built files, /review/assets/*, with no token, as the brain serves its own: they are
    /// the same for everyone and hold no data. Runs before routing, so a file that is not there
    /// falls through to a 404.
    /// </summary>
    public static IApplicationBuilder UseBuiltFiles(this IApplicationBuilder app, string webRoot)
    {
        ArgumentNullException.ThrowIfNull(webRoot);
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(webRoot, "assets")),
            RequestPath = "/review/assets",
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
using Microsoft.AspNetCore.Http;

using Review.Settings;

namespace Review.Tests;

/// <summary>
/// Review's page: the built index.html for each section's address, the built files under
/// /review/assets/, and nothing else. See "What it serves" in review/README.md.
/// </summary>
public sealed class PageTests : IAsyncLifetime
{
    private Harness? _review;

    private Harness Review => _review!;

    public async ValueTask InitializeAsync() => _review = await Harness.Start();

    public async ValueTask DisposeAsync() => await Review.DisposeAsync();

    [Theory]
    [InlineData("/review")]
    [InlineData("/review/cards")]
    [InlineData("/review/decks")]
    [InlineData("/review/study")]
    [InlineData("/review/cards?deck=1")]
    public async Task EachSectionAnswersTheAppWithAToken(string address)
    {
        var response = await Review.Get(address, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("text/html", response.Response.ContentType, StringComparison.Ordinal);
        // Revalidated on every load, so a new build is seen at once.
        Assert.Equal("no-cache", response.Response.Headers.CacheControl.ToString());
        Assert.Equal(Harness.IndexHtml, await Harness.Body(response));
    }

    [Fact]
    public async Task APageWithoutATokenRedirectsToRefresh()
    {
        var response = await Review.Get("/review/cards?x=1");

        Assert.Equal(StatusCodes.Status303SeeOther, response.Response.StatusCode);
        Assert.Equal("/auth/refresh?return_to=%2Freview%2Fcards%3Fx%3D1", response.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/review/other/deeper")]
    [InlineData("/review/api/nope")]
    [InlineData("/reviewx")]
    [InlineData("/review/other")]
    [InlineData("/review/cards/deeper")]
    [InlineData("/review/assets/missing.js")]
    public async Task AnythingElseIs404(string address)
    {
        // The same with a token and without, so a 404 says nothing about the session.
        foreach (var token in new[] { Contract.Token, null })
        {
            var response = await Review.Get(address, token);

            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        }
    }

    [Fact]
    public async Task TheBuiltFilesNeedNoToken()
    {
        var response = await Review.Get($"/review/assets/{Harness.AssetName}");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(Harness.Asset, await Harness.Body(response));
    }

    [Fact]
    public async Task TheServiceRefusesToStartWithoutABuild()
    {
        var empty = Directory.CreateTempSubdirectory("review-web-").FullName;
        try
        {
            var refused = Assert.Throws<StartupException>(() => ReviewApp.Build(Harness.Defaults, empty));

            Assert.Contains(Path.Combine(empty, "index.html"), refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
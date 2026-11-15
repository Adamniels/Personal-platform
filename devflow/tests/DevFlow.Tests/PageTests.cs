using DevFlow.Settings;

using Microsoft.AspNetCore.Http;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>
/// Dev flow's page: the built index.html for each section's address, the built files under
/// /devflow/assets/, and nothing else. See "What it serves" in devflow/README.md.
/// </summary>
public sealed class PageTests : IAsyncLifetime
{
    private Harness? _devflow;

    private Harness DevFlow => _devflow!;

    public async ValueTask InitializeAsync() => _devflow = await Harness.Start();

    public async ValueTask DisposeAsync() => await DevFlow.DisposeAsync();

    [Theory]
    [InlineData("/devflow")]
    [InlineData("/devflow/projects")]
    [InlineData("/devflow?tab=1")]
    [InlineData("/devflow/projects?x=1")]
    public async Task EachSectionAnswersTheAppWithAToken(string address)
    {
        var response = await DevFlow.Get(address, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("text/html", response.Response.ContentType, StringComparison.Ordinal);
        // Revalidated on every load, so a new build is seen at once.
        Assert.Equal("no-cache", response.Response.Headers.CacheControl.ToString());
        Assert.Equal(Harness.IndexHtml, await Harness.Body(response));
    }

    [Fact]
    public async Task APageWithoutATokenRedirectsToRefresh()
    {
        var response = await DevFlow.Get("/devflow/projects?x=1");

        Assert.Equal(StatusCodes.Status303SeeOther, response.Response.StatusCode);
        Assert.Equal("/auth/refresh?return_to=%2Fdevflow%2Fprojects%3Fx%3D1", response.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/devflow/other/deeper")]
    [InlineData("/devflow/api/nope")]
    [InlineData("/devflowx")]
    [InlineData("/devflow/other")]
    [InlineData("/devflow/projects/deeper")]
    [InlineData("/devflow/assets/missing.js")]
    public async Task AnythingElseIs404(string address)
    {
        // The same with a token and without, so a 404 says nothing about the session.
        foreach (var token in new[] { Contract.Token, null })
        {
            var response = await DevFlow.Get(address, token);

            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        }
    }

    [Fact]
    public async Task TheBuiltFilesNeedNoToken()
    {
        var response = await DevFlow.Get($"/devflow/assets/{Harness.AssetName}");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal(Harness.Asset, await Harness.Body(response));
    }

    [Fact]
    public async Task TheServiceRefusesToStartWithoutABuild()
    {
        var empty = Directory.CreateTempSubdirectory("devflow-web-").FullName;
        try
        {
            var settings = Harness.Defaults;
            await using var database = UserDb.Unopened(DevFlowSettings.Service, settings.AppDatabase);
            var refused = Assert.Throws<StartupException>(() => DevFlowApp.Build(settings, database, empty));

            Assert.Contains(Path.Combine(empty, "index.html"), refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
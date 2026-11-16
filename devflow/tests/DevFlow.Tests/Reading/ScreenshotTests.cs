using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <c>GET /devflow/api/waiting/{id}/screenshots/{name}</c>: one of the screenshots an item's pull
/// request body names, read from the code host as the app when it is asked for, and handed to the
/// browser only as a PNG it cannot sniff as anything else. See "What waits" in devflow/README.md.
/// </summary>
public sealed class ScreenshotTests
{
    private const string Desktop = "devflow-item-desktop";
    private const string Phone = "devflow-item-phone";
    private const string Branch = "pr-screenshots";

    /// <summary>The smallest PNG: its eight byte signature and a few bytes after it, which is all
    /// the route checks.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    /// <summary>The waiting pull request, 42, its body showing a desktop and a phone screenshot,
    /// as <c>scripts/agent/screenshots.sh</c> prints them.</summary>
    private static readonly World WithScreenshots = Acting.Acting.Waiting with
    {
        Pull = Acting.Acting.Waiting.Pull with
        {
            Body = World.Body + $"""


                Screenshots, desktop and 390px phone width:

                ![{Desktop}](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/42/{Desktop}.png?raw=true)
                ![{Phone}](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/42/{Phone}.png?raw=true)
                """,
        },
    };

    /// <summary>Dev flow with <see cref="WithScreenshots"/> read into the user's list, and
    /// <paramref name="files"/> staged in the repository, as screenshots.sh commits them.</summary>
    private static async Task<Acting.Acting> Start(bool link, params FakeFile[] files)
    {
        var acting = await Acting.Acting.Start(link, WithScreenshots);
        var world = acting.Fake.World(Acting.Acting.User);
        var installation = world.Installations[0];
        var repository = installation.Repositories[0] with { Files = files };
        acting.Fake.Replace(Acting.Acting.User, world with { Installations = [installation with { Repositories = [repository] }] });
        return acting;
    }

    private static FakeFile Shot(string name, byte[] content) => new($"42/{name}.png", Branch, content);

    private static Task<HttpContext> Get(Acting.Acting acting, Guid item, string name) =>
        acting.App.Get($"/devflow/api/waiting/{item}/screenshots/{name}", Contract.Token);

    private static async Task<byte[]> Bytes(HttpContext response)
    {
        using var buffer = new MemoryStream();
        await response.Response.Body.CopyToAsync(buffer, Reading.Ct);
        return buffer.ToArray();
    }

    private static int FileReads(Acting.Acting acting) => acting.Host.Calls.Count(call => call.Operation == "File");

    [Fact]
    public async Task AnItemsScreenshotIsServed()
    {
        await using var acting = await Start(link: true, Shot(Desktop, Png), Shot(Phone, [.. Png, 0x01]));
        var item = await acting.Item(World.Number);

        var desktop = await Get(acting, item.Id, Desktop);
        var phone = await Get(acting, item.Id, Phone);

        Assert.Equal(StatusCodes.Status200OK, desktop.Response.StatusCode);
        Assert.Equal("image/png", desktop.Response.ContentType);
        Assert.Equal(Png, await Bytes(desktop));
        Assert.Equal(StatusCodes.Status200OK, phone.Response.StatusCode);
        Assert.Equal([.. Png, 0x01], await Bytes(phone));
    }

    [Fact]
    public async Task ANameTheItemDoesNotListIs404()
    {
        // The file is there, but the body does not name it, so it is never asked for.
        await using var acting = await Start(link: true, Shot(Desktop, Png), Shot("devflow-other-desktop", Png));
        var item = await acting.Item(World.Number);
        var reads = FileReads(acting);

        foreach (var name in new[] { "devflow-other-desktop", "DEVFLOW-ITEM-DESKTOP", $"{Desktop}.png", "..", "%2e%2e", "x" })
        {
            var response = await Get(acting, item.Id, name);
            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        }

        Assert.Equal(reads, FileReads(acting));
    }

    [Fact]
    public async Task AnotherUsersItemIs404()
    {
        await using var acting = await Start(link: true, Shot(Desktop, Png));
        var bobs = await acting.BobsItem();
        var reads = FileReads(acting);

        var response = await Get(acting, bobs, Desktop);

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal(reads, FileReads(acting));
        Assert.Equal(StatusCodes.Status404NotFound, (await Get(acting, Guid.CreateVersion7(), Desktop)).Response.StatusCode);
    }

    [Fact]
    public async Task AFileThatIsNotAPngOrIsTooLargeIsRefused()
    {
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        var tooLarge = new byte[(5 * 1024 * 1024) + 1];
        Png.CopyTo(tooLarge, 0);
        var atTheLimit = new byte[5 * 1024 * 1024];
        Png.CopyTo(atTheLimit, 0);
        await using var acting = await Start(link: true, Shot(Desktop, html), Shot(Phone, tooLarge));
        var item = await acting.Item(World.Number);

        // Not a PNG by its first bytes, whatever its name says, and one byte over 5 MB.
        foreach (var name in new[] { Desktop, Phone })
        {
            var refused = await Acting.Acting.Problem(await Get(acting, item.Id, name), StatusCodes.Status502BadGateway);
            Assert.Equal("The screenshot is not one Dev flow can show", (string)refused["title"]!);
        }

        // Too short to hold the signature.
        acting.Fake.Replace(Acting.Acting.User, WithFile(acting, Shot(Desktop, Png[..7])));
        await Acting.Acting.Problem(await Get(acting, item.Id, Desktop), StatusCodes.Status502BadGateway);

        // At 5 MB exactly it is served.
        acting.Fake.Replace(Acting.Acting.User, WithFile(acting, Shot(Desktop, atTheLimit)));
        var served = await Get(acting, item.Id, Desktop);
        Assert.Equal(StatusCodes.Status200OK, served.Response.StatusCode);
        Assert.Equal(atTheLimit.Length, (await Bytes(served)).Length);
    }

    [Fact]
    public async Task AMissingFileIs404AndAnUnavailableHostIs503()
    {
        await using var acting = await Start(link: true);
        var item = await acting.Item(World.Number);

        var missing = await Acting.Acting.Problem(await Get(acting, item.Id, Desktop), StatusCodes.Status404NotFound);
        Assert.Equal("No such screenshot", (string)missing["title"]!);

        acting.Fake.SetUnavailable(Acting.Acting.User, unavailable: true);
        var unavailable = await Acting.Acting.Problem(await Get(acting, item.Id, Desktop), StatusCodes.Status503ServiceUnavailable);
        Assert.Equal("GitHub did not answer", (string)unavailable["title"]!);
    }

    [Fact]
    public async Task ItIsReadAsTheAppWithAReadOnlyKey()
    {
        // GitHub is not linked, so there is no user's token to read with, and it is served all
        // the same: the read is the app's, which the GitHub adapter makes with an installation
        // token narrowed to reading, see InstallationTokenTests.
        await using var acting = await Start(link: false, Shot(Desktop, Png));
        var item = await acting.Item(World.Number);

        var response = await Get(acting, item.Id, Desktop);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var read = Assert.Single(acting.Host.Calls, call => call.Operation == "File");
        Assert.Equal(new CountingCodeHost.Call("File", Acting.Acting.User, Reading.RepositoryId, $"42/{Desktop}.png {Branch}"), read);
        Assert.Empty(acting.Host.WriteTokens);
        Assert.Equal(0, acting.Host.Writes);
        Assert.Empty(acting.Fake.Writes(Acting.Acting.User));
    }

    [Fact]
    public async Task TheResponseCannotBeFramedOrSniffed()
    {
        await using var acting = await Start(link: true, Shot(Desktop, Png));
        var item = await acting.Item(World.Number);

        var response = await Get(acting, item.Id, Desktop);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var headers = response.Response.Headers;
        Assert.Equal("image/png", response.Response.ContentType);
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("default-src 'none'", headers.ContentSecurityPolicy.ToString());
        Assert.Equal("private, max-age=3600", headers.CacheControl.ToString());

        // A refusal is not sniffed or kept either.
        var missing = await Get(acting, item.Id, Phone);
        Assert.Equal(StatusCodes.Status404NotFound, missing.Response.StatusCode);
        Assert.Equal("nosniff", missing.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("default-src 'none'", missing.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.NotEqual("private, max-age=3600", missing.Response.Headers.CacheControl.ToString());
    }

    /// <summary>The user's world with its repository's files replaced by <paramref name="file"/>.</summary>
    private static FakeWorld WithFile(Acting.Acting acting, FakeFile file)
    {
        var world = acting.Fake.World(Acting.Acting.User);
        var installation = world.Installations[0];
        return world with { Installations = [installation with { Repositories = [installation.Repositories[0] with { Files = [file] }] }] };
    }
}
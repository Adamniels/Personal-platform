using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Cards;

/// <summary>
/// <c>GET /devflow/api/projects/{id}/cards/{number}/screenshots/{name}</c>: one of the pictures the
/// worker took of a card's head commit, listed on the card's page and on its item on "Waiting for
/// you", and served through the same checks as part 1's screenshot route. See "Screenshots" in
/// devflow/README.md.
/// </summary>
public sealed class CardScreenshotTests
{
    private const int Card = 4;

    /// <summary>The start of a PNG, all the route checks.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static CodeHost.Repository Repository => new(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");

    private static string Path(Guid project, string name, int card = Card) => $"/devflow/api/projects/{project}/cards/{card}/screenshots/{name}";

    private static int FileReads(Jobs.Jobs jobs) => jobs.Routes.Host.Calls.Count(call => call.Operation == "File");

    private static async Task<byte[]> Bytes(HttpContext response)
    {
        using var buffer = new MemoryStream();
        await response.Response.Body.CopyToAsync(buffer, Built.Ct);
        return buffer.ToArray();
    }

    /// <summary>Card 4 built by the fake worker, its result listing <paramref name="names"/>, each
    /// with the bytes <paramref name="pictures"/> gives it; the head commit it left.</summary>
    private static async Task<string> Build(Jobs.Jobs jobs, Dictionary<string, byte[]> pictures, string? note = null, params string[] names)
    {
        var ran = await Built.Run(jobs, Built.WithPictures(note, names), pictures);
        return (string)ran["head_commit"]!;
    }

    [Fact]
    public async Task TheyAreServedLikePartOnes()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(Card)]);
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        var tooLarge = new byte[(5 * 1024 * 1024) + 1];
        Png.CopyTo(tooLarge, 0);
        var head = await Build(
            jobs,
            new() { ["habits-desktop"] = Png, ["habits-phone"] = [.. Png, 0x01], ["html-desktop"] = html, ["big-desktop"] = tooLarge },
            "Screenshots could not be taken: /gone at phone size: it took longer than 60 seconds",
            "habits-desktop", "habits-phone", "html-desktop", "big-desktop", "gone-desktop");

        // The card's route lists them for its head commit, paired by page, with the note.
        var (status, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/{Card}");
        Assert.Equal(StatusCodes.Status200OK, status);
        var screenshots = story!["screenshots"]!;
        Assert.Equal(head, (string)screenshots["commit"]!);
        Assert.Equal("Screenshots could not be taken: /gone at phone size: it took longer than 60 seconds", (string)screenshots["note"]!);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""
                [{"name": "habits", "images": ["habits-desktop", "habits-phone"]}, {"name": "html", "images": ["html-desktop"]},
                 {"name": "big", "images": ["big-desktop"]}, {"name": "gone", "images": ["gone-desktop"]}]
                """),
            screenshots["groups"]), screenshots["groups"]!.ToJsonString());

        // Each served as part 1's are: the bytes on pr-screenshots under the commit's folder, only
        // as a PNG, never sniffed, framed or run, kept an hour.
        foreach (var (name, bytes) in new[] { ("habits-desktop", Png), ("habits-phone", (byte[])[.. Png, 0x01]) })
        {
            var served = await jobs.Routes.App.Get(Path(jobs.Project, name), Contract.Token);
            Assert.Equal(StatusCodes.Status200OK, served.Response.StatusCode);
            Assert.Equal("image/png", served.Response.ContentType);
            Assert.Equal("nosniff", served.Response.Headers.XContentTypeOptions);
            Assert.Equal("default-src 'none'", served.Response.Headers.ContentSecurityPolicy);
            Assert.Equal("private, max-age=3600", served.Response.Headers.CacheControl);
            Assert.Equal(bytes, await Bytes(served));
        }

        Assert.Contains(jobs.Routes.Host.Calls, call => call.Operation == "File");
        Assert.Equal(Png, Assert.IsType<CodeHostResult<byte[]>.Found>(
            await jobs.Fake.File(Jobs.Jobs.User, Repository, $"{PageShots.Folder(Card, head)}/habits-desktop.png", PageShots.Branch, Built.Ct)).Value);

        // Not a PNG, or over 5 MB, is 502; listed but not on GitHub is 404; each with the same
        // headers.
        foreach (var name in new[] { "html-desktop", "big-desktop" })
        {
            var refused = await jobs.Routes.App.Get(Path(jobs.Project, name), Contract.Token);
            Assert.Equal(StatusCodes.Status502BadGateway, refused.Response.StatusCode);
            Assert.Equal("nosniff", refused.Response.Headers.XContentTypeOptions);
            Assert.Equal("no-store", refused.Response.Headers.CacheControl);
        }

        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get(Path(jobs.Project, "gone-desktop"), Contract.Token)).Response.StatusCode);

        // GitHub not answering is 503.
        jobs.Fake.SetUnavailable(Jobs.Jobs.User, true);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await jobs.Routes.App.Get(Path(jobs.Project, "habits-desktop"), Contract.Token)).Response.StatusCode);
        jobs.Fake.SetUnavailable(Jobs.Jobs.User, false);
    }

    [Fact]
    public async Task ANameNotListedIsRefusedBeforeTheCodeHostIsAsked()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(Card)]);
        var head = await Build(jobs, new() { ["habits-desktop"] = Png }, null, "habits-desktop");
        // A file there under a name the build did not list, which is still never read.
        jobs.Fake.PutFile(Jobs.Jobs.User, Repository, $"{PageShots.Folder(Card, head)}/other-desktop.png", PageShots.Branch, Png);
        var reads = FileReads(jobs);

        foreach (var name in new[] { "other-desktop", "HABITS-DESKTOP", "habits-desktop.png", "..", "%2e%2e", "habits" })
        {
            var response = await jobs.Routes.App.Get(Path(jobs.Project, name), Contract.Token);
            Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
            Assert.Equal("nosniff", response.Response.Headers.XContentTypeOptions);
        }

        // Nor for a card with no such build, another project, or none of the caller's.
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get(Path(jobs.Project, "habits-desktop", card: 5), Contract.Token)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get(Path(Guid.CreateVersion7(), "habits-desktop"), Contract.Token)).Response.StatusCode);
        Assert.Equal(reads, FileReads(jobs));

        // Without a token, 401 with an empty body.
        var anonymous = await jobs.Routes.App.Get(Path(jobs.Project, "habits-desktop"), null);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Equal(reads, FileReads(jobs));
    }

    [Fact]
    public async Task ACardWhoseBuildTookNoneHasNone()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(Card)]);
        await Built.Run(jobs, Built.Prepared());

        var (_, story) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/{Card}");

        Assert.Null(story!["screenshots"]);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get(Path(jobs.Project, "habits-desktop"), Contract.Token)).Response.StatusCode);
    }
}
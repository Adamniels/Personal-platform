using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Cards;

/// <summary>
/// <c>GET /devflow/api/projects/{id}/cards</c>: the project's cards as last read, each with its
/// number, title, state and reason, newest change first, for the project's user only. See "Cards"
/// in devflow/README.md.
/// </summary>
public sealed class CardListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/cards";

    private static FakeIssue Card(int number, string author = "octo-owner", IReadOnlyList<string>? labels = null) => new(
        number, $"Card {number}", author, labels ?? ["devflow"], "Build it.",
        new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"),
        new DateTimeOffset(2026, 11, 17, 8, number, 0, TimeSpan.Zero), true, [], []);

    private static void Stage(Routes routes, Guid user, params FakeIssue[] issues) =>
        routes.Fake.Replace(user, new FakeWorld([
            new FakeInstallation(Reading.Reading.Installation, "Adamniels",
            [
                new FakeRepository(Reading.Reading.RepositoryId, "Adamniels", "Personal-platform", [], [], [], [], issues),
            ]),
        ]));

    [Fact]
    public async Task TheCardsAreListedNewestChangeFirst()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        await Reading.Reading.Link(routes.Door, Routes.User, "octo-owner");
        Stage(routes, Routes.User, Card(1), Card(2, author: "octo-stranger"));
        await routes.Read(project, Routes.User);
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        // 1 is held a minute later, so it changed last.
        Stage(routes, Routes.User, Card(1, labels: ["devflow", "devflow-hold"]), Card(2, author: "octo-stranger"));
        await routes.Read(project, Routes.User);

        var answer = await routes.App.Get(PathOf(project), Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        var cards = JsonNode.Parse(await Harness.Body(answer))!["cards"]!.AsArray();
        Assert.Equal([1, 2], cards.Select(card => (int)card!["number"]!));
        var held = cards[0]!.AsObject();
        Assert.Equal(
            ["changed_at", "number", "priority", "reason", "state", "title", "waits_for", "web_url"],
            held.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Card 1", (string)held["title"]!);
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/1", (string)held["web_url"]!);
        Assert.Equal("held", (string)held["state"]!);
        Assert.Equal("Held by the label devflow-hold", (string)held["reason"]!);
        Assert.Empty(held["waits_for"]!.AsArray());
        Assert.Equal("none", (string)held["priority"]!);
        Assert.Equal(routes.Time.GetUtcNow(), DateTimeOffset.Parse((string)held["changed_at"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("ignored", (string)cards[1]!["state"]!);
        Assert.Equal("Written by octo-stranger: only your own cards are picked", (string)cards[1]!["reason"]!);
    }

    [Fact]
    public async Task OnlyTheProjectsUserSeesThem()
    {
        await using var routes = await Routes.Start();
        var bobs = await routes.Project(Routes.Bob);
        await Reading.Reading.Link(routes.Door, Routes.Bob, "octo-owner");
        Stage(routes, Routes.Bob, Card(1));
        await routes.Read(bobs, Routes.Bob);
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            Assert.Equal(1, await admin.QuerySingleAsync<int>(new CommandDefinition("SELECT count(*)::int FROM cards", cancellationToken: Ct)));
        }

        Assert.Equal(StatusCodes.Status404NotFound, (await routes.App.Get(PathOf(bobs), Contract.Token)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await routes.App.Get(PathOf(bobs))).Response.StatusCode);

        // A project of the caller's with no cards is an empty list.
        var mine = await routes.Project(Routes.User, repository: 9002, name: "other");
        var empty = await routes.App.Get(PathOf(mine), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, empty.Response.StatusCode);
        Assert.Empty(JsonNode.Parse(await Harness.Body(empty))!["cards"]!.AsArray());
    }
}
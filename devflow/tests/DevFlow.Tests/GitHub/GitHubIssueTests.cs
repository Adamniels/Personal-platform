using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The adapter's reads of a project's cards, against GitHub's real answers recorded in
/// contracts/github/: the open issues carrying a label, which GitHub lists with pull requests among
/// them; what an issue is blocked by; and who edited an issue's body and title, which only
/// GraphQL says. Each field of the interface's records is what the file holds, read here by the
/// test on its own. See "Cards" in devflow/docs/devflow-architecture.md and contracts/README.md.
/// </summary>
public sealed class GitHubIssueTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static CancellationToken Ct => GitHubHost.Ct;

    /// <summary>Answers each path with its fixture, and anything else 404.</summary>
    private static Func<Received, HttpResponseMessage> Serve(params (string Path, string Fixture)[] answers) =>
        request => answers.FirstOrDefault(answer => answer.Path == request.Path) is { Fixture: not null } found
            ? StubGitHub.Json(Fixture.Text(found.Fixture))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public async Task ReadsMatchGitHubsDocumentedForm()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(
            ($"{Repo}/issues", "issues-labelled.json"),
            ($"{Repo}/issues/339/dependencies/blocked_by", "issue-blocked-by.json"),
            ("/graphql", "issue-edits.json"));

        // Issues by label: open ones with the label, a hundred a page; pull requests among them,
        // marked, as GitHub lists them.
        var issues = Results.Value(await host.CodeHost.LabelledIssues(GitHubHost.User, GitHubHost.Platform, "agent: go", Ct));
        var listed = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, listed.Method);
        Assert.Equal("open", listed.Query["state"]);
        Assert.Equal("agent: go", listed.Query["labels"]);
        Assert.Equal("100", listed.Query["per_page"]);
        var expected = Fixture.Node("issues-labelled.json").AsArray();
        Assert.Equal(expected.Count, issues.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var file = expected[i]!;
            var issue = issues[i];
            Assert.Equal((int)file["number"]!, issue.Number);
            Assert.Equal((string)file["title"]!, issue.Title);
            Assert.Equal((string)file["user"]!["login"]!, issue.Author);
            Assert.Equal(file["labels"]!.AsArray().Select(label => (string)label!["name"]!), issue.Labels);
            Assert.Equal((string?)file["body"] ?? "", issue.Body);
            Assert.Equal(new Uri((string)file["html_url"]!), issue.Url);
            Assert.Equal(Time(file["created_at"]), issue.CreatedAt);
            Assert.Equal(file.AsObject().ContainsKey("pull_request"), issue.IsPullRequest);
        }

        // The recording holds one of each.
        Assert.Equal([true, false], issues.Select(issue => issue.IsPullRequest));

        // Blocked by: each issue, wherever it is, open or closed.
        var dependencies = Results.Value(await host.CodeHost.BlockedBy(GitHubHost.User, GitHubHost.Platform, 339, Ct));
        var blockers = Fixture.Node("issue-blocked-by.json").AsArray();
        Assert.Equal(blockers.Count, dependencies.Count);
        for (var i = 0; i < blockers.Count; i++)
        {
            var file = blockers[i]!;
            var dependency = dependencies[i];
            Assert.Equal((string)file["repository"]!["owner"]!["login"]!, dependency.Owner);
            Assert.Equal((string)file["repository"]!["name"]!, dependency.Name);
            Assert.Equal((int)file["number"]!, dependency.Number);
            Assert.Equal((string)file["state"]! == "open", dependency.Open);
            Assert.Equal(new Uri((string)file["html_url"]!), dependency.Url);
        }

        Assert.Contains(dependencies, dependency => dependency.Open);
        Assert.Contains(dependencies, dependency => !dependency.Open);
        var blockedBy = host.Stub.Reads[1];
        Assert.Equal("100", blockedBy.Query["per_page"]);

        // Who edited: one POST to GraphQL, as the app, naming the repository and the issue; every
        // body edit and every change of title, each with its editor.
        var edits = Results.Value(await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 4, Ct));
        var asked = host.Stub.Reads[2];
        Assert.Equal(HttpMethod.Post, asked.Method);
        Assert.Equal("https://api.github.com/graphql", asked.Uri.ToString());
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", asked.Header("Authorization"));
        var sent = JsonNode.Parse(asked.Body!)!.AsObject();
        Assert.Contains("userContentEdits", (string)sent["query"]!, StringComparison.Ordinal);
        Assert.Contains("RENAMED_TITLE_EVENT", (string)sent["query"]!, StringComparison.Ordinal);
        var variables = sent["variables"]!.AsObject();
        Assert.Equal("Adamniels", (string)variables["owner"]!);
        Assert.Equal("Personal-platform", (string)variables["name"]!);
        Assert.Equal(4, (int)variables["number"]!);
        Assert.True((bool)variables["withEdits"]!);
        Assert.True((bool)variables["withRenames"]!);

        var issueNode = Fixture.Node("issue-edits.json")["data"]!["repository"]!["issue"]!;
        var bodyEdits = issueNode["userContentEdits"]!["nodes"]!.AsArray()
            .Select(node => new IssueEdit((string)node!["id"]!, (string)node["editor"]!["login"]!, Time(node["editedAt"]), EditKind.Body));
        var renames = issueNode["timelineItems"]!["nodes"]!.AsArray()
            .Select(node => new IssueEdit((string)node!["id"]!, (string)node["actor"]!["login"]!, Time(node["createdAt"]), EditKind.Title));
        Assert.Equal([.. bodyEdits, .. renames], edits);
        Assert.Equal(3, edits.Count);
    }

    [Fact]
    public async Task AnIssueGitHubDoesNotShowIsNotFound()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(("/graphql", "issue-edits-not-found.json"));

        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound>(
            await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 99999, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Dependency>>.NotFound>(
            await host.CodeHost.BlockedBy(GitHubHost.User, GitHubHost.Platform, 99999, Ct));
    }

    [Fact]
    public async Task EditsAreReadAcrossPages()
    {
        using var host = new GitHubHost();
        var first = Fixture.Node("issue-edits.json");
        var issue = first["data"]!["repository"]!["issue"]!;
        issue["userContentEdits"]!["pageInfo"]!["hasNextPage"] = true;
        issue["userContentEdits"]!["pageInfo"]!["endCursor"] = "edits-cursor";
        var second = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["repository"] = new JsonObject
                {
                    ["issue"] = new JsonObject
                    {
                        ["userContentEdits"] = new JsonObject
                        {
                            ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = "edits-end" },
                            ["nodes"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "UCE_older",
                                ["editedAt"] = "2026-09-01T10:00:00Z",
                                ["editor"] = null,
                            }),
                        },
                    },
                },
            },
        };
        var answers = new Queue<string>([first.ToJsonString(), second.ToJsonString()]);
        host.Stub.Answer = _ => StubGitHub.Json(answers.Dequeue());

        var edits = Results.Value(await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 4, Ct));

        // The second page asks for the edits after the cursor, and not the titles again.
        Assert.Equal(2, host.Stub.Reads.Count);
        var again = JsonNode.Parse(host.Stub.Reads[1].Body!)!["variables"]!.AsObject();
        Assert.Equal("edits-cursor", (string)again["edits"]!);
        Assert.True((bool)again["withEdits"]!);
        Assert.False((bool)again["withRenames"]!);

        // Four: two edits and the title from the first page, and an edit by a deleted account, which
        // GitHub shows as ghost.
        Assert.Equal(4, edits.Count);
        Assert.Equal(new IssueEdit("UCE_older", "ghost", Time(JsonValue.Create("2026-09-01T10:00:00Z")), EditKind.Body), edits[2]);
    }

    [Fact]
    public async Task WhatGitHubCannotAnswerIsUnavailable()
    {
        using var host = new GitHubHost();

        // A GraphQL error that is not a missing issue, such as a rate limit, answered with 200.
        host.Stub.Answer = _ => StubGitHub.Json("""{"data": null, "errors": [{"type": "RATE_LIMITED", "message": "API rate limit exceeded"}]}""");
        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable>(
            await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 4, Ct));

        // An answer that does not parse.
        host.Stub.Answer = _ => StubGitHub.Json("""{"data": {"repository": {"issue": {"userContentEdits": {"nodes": []}}}}}""");
        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable>(
            await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 4, Ct));

        // GitHub down, for each read.
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.IsType<CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable>(
            await host.CodeHost.Edits(GitHubHost.User, GitHubHost.Platform, 4, Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Issue>>.Unavailable>(
            await host.CodeHost.LabelledIssues(GitHubHost.User, GitHubHost.Platform, "devflow", Ct));
        Assert.IsType<CodeHostResult<IReadOnlyList<Dependency>>.Unavailable>(
            await host.CodeHost.BlockedBy(GitHubHost.User, GitHubHost.Platform, 339, Ct));
    }

    [Fact]
    public async Task LabelEventsMatchGitHubsRecordedAnswer()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(("/graphql", "issue-label-events.json"));

        var events = Results.Value(await host.CodeHost.LabelEvents(GitHubHost.User, GitHubHost.Platform, 15122, Ct));

        // One POST to GraphQL, as the app, with the query exactly as recorded, naming the
        // repository and the issue, from the first page.
        var asked = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Post, asked.Method);
        Assert.Equal("https://api.github.com/graphql", asked.Uri.ToString());
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", asked.Header("Authorization"));
        var sent = JsonNode.Parse(asked.Body!)!.AsObject();
        Assert.Equal(DevFlow.CodeHost.GitHub.GitHubCodeHost.LabelEventsQuery, (string)sent["query"]!);
        var variables = sent["variables"]!.AsObject();
        Assert.Equal("Adamniels", (string)variables["owner"]!);
        Assert.Equal("Personal-platform", (string)variables["name"]!);
        Assert.Equal(15122, (int)variables["number"]!);
        Assert.Null(variables["after"]);

        // Each event as the file holds it, oldest first, a label put on or taken off.
        var nodes = Fixture.Node("issue-label-events.json")["data"]!["repository"]!["issue"]!["timelineItems"]!["nodes"]!.AsArray();
        Assert.Equal(
            nodes.Select(node => new LabelEvent(
                (string)node!["label"]!["name"]!,
                (string)node["__typename"]! == "LabeledEvent" ? LabelChange.Added : LabelChange.Removed,
                (string)node["actor"]!["login"]!,
                Time(node["createdAt"]))),
            events);
        Assert.Equal([LabelChange.Added, LabelChange.Added, LabelChange.Removed], events.Select(change => change.Change));
    }

    [Fact]
    public async Task LabelEventsAreReadAcrossPages()
    {
        using var host = new GitHubHost();
        var first = Fixture.Node("issue-label-events.json");
        var page = first["data"]!["repository"]!["issue"]!["timelineItems"]!["pageInfo"]!;
        page["hasNextPage"] = true;
        page["endCursor"] = "events-cursor";
        var second = JsonNode.Parse("""
            {"data": {"repository": {"issue": {"timelineItems": {
              "pageInfo": {"hasNextPage": false, "endCursor": "events-end"},
              "nodes": [{"__typename": "UnlabeledEvent", "createdAt": "2026-11-22T10:00:00Z", "actor": null, "label": {"name": "suspected-spam"}}]
            }}}}}
            """)!;
        var answers = new Queue<string>([first.ToJsonString(), second.ToJsonString()]);
        host.Stub.Answer = _ => StubGitHub.Json(answers.Dequeue());

        var events = Results.Value(await host.CodeHost.LabelEvents(GitHubHost.User, GitHubHost.Platform, 15122, Ct));

        // The second page asks from the cursor; the event of a deleted account is ghost's.
        Assert.Equal(2, host.Stub.Reads.Count);
        Assert.Equal("events-cursor", (string)JsonNode.Parse(host.Stub.Reads[1].Body!)!["variables"]!["after"]!);
        Assert.Equal(4, events.Count);
        Assert.Equal(new LabelEvent("suspected-spam", LabelChange.Removed, "ghost", Time(JsonValue.Create("2026-11-22T10:00:00Z"))), events[3]);
    }

    [Fact]
    public async Task LabelEventsOfAnIssueGitHubDoesNotShowAreNotFound()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(("/graphql", "issue-edits-not-found.json"));

        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound>(
            await host.CodeHost.LabelEvents(GitHubHost.User, GitHubHost.Platform, 99999, Ct));
    }

    /// <summary>Anything but a whole history is unavailable, never part of one answered as
    /// all of it: an event the adapter cannot name might be the one that decides.</summary>
    [Theory]
    [InlineData("""{"data": null, "errors": [{"type": "RATE_LIMITED", "message": "API rate limit exceeded"}]}""")]
    [InlineData("""{"data": {"repository": {"issue": {}}}}""")]
    [InlineData("""{"data": {"repository": {"issue": {"timelineItems": {"pageInfo": {"hasNextPage": true, "endCursor": null}, "nodes": []}}}}}""")]
    [InlineData("""{"data": {"repository": {"issue": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": [{"__typename": "RenamedTitleEvent", "createdAt": "2026-11-22T10:00:00Z", "actor": null}]}}}}}""")]
    [InlineData("""{"data": {"repository": {"issue": {"timelineItems": {"pageInfo": {"hasNextPage": false, "endCursor": null}, "nodes": [{"__typename": "LabeledEvent", "createdAt": "2026-11-22T10:00:00Z", "actor": null, "label": null}]}}}}}""")]
    public async Task LabelEventsGitHubCannotAnswerWhollyAreUnavailable(string answer)
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => StubGitHub.Json(answer);

        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable>(
            await host.CodeHost.LabelEvents(GitHubHost.User, GitHubHost.Platform, 15122, Ct));

        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.IsType<CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable>(
            await host.CodeHost.LabelEvents(GitHubHost.User, GitHubHost.Platform, 15122, Ct));
    }

    private static DateTimeOffset Time(JsonNode? value) =>
        DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
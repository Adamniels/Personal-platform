using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The reads picking cards needs, as the app: the open issues carrying a label, an issue's
/// sub-issues and what an issue is blocked by, through REST like every other read; and who edited an issue's body and title,
/// which REST does not say, through one GraphQL query, <see cref="EditsQuery"/>, posted by hand
/// with the same installation token. See "Cards" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>
    /// Who edited an issue: its body's edits, which GitHub lists newest first with the first
    /// version among them once there is an edit, and its changes of title, from its timeline. Each
    /// list is a hundred a page, and a list that has more is asked for again from its cursor, the
    /// other left out by its <c>with</c> variable. contracts/github/issue-edits.json was recorded
    /// with exactly this query.
    /// </summary>
    public const string EditsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $edits: String, $renames: String, $withEdits: Boolean!, $withRenames: Boolean!) {
          repository(owner: $owner, name: $name) {
            issue(number: $number) {
              userContentEdits(first: 100, after: $edits) @include(if: $withEdits) {
                pageInfo { hasNextPage endCursor }
                nodes { id editedAt editor { login } }
              }
              timelineItems(first: 100, after: $renames, itemTypes: [RENAMED_TITLE_EVENT]) @include(if: $withRenames) {
                pageInfo { hasNextPage endCursor }
                nodes { ... on RenamedTitleEvent { id createdAt actor { login } } }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Every label put on an issue or taken off it, from its timeline, oldest first as GitHub lists
    /// it, each with the label's name and who did it; a hundred a page, asked for again from the
    /// cursor while there are more. contracts/github/issue-label-events.json was recorded with
    /// exactly this query.
    /// </summary>
    public const string LabelEventsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            issue(number: $number) {
              timelineItems(first: 100, after: $after, itemTypes: [LABELED_EVENT, UNLABELED_EVENT]) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on LabeledEvent { createdAt actor { login } label { name } }
                  ... on UnlabeledEvent { createdAt actor { login } label { name } }
                }
              }
            }
          }
        }
        """;

    /// <summary>GitHub's GraphQL address, on its API.</summary>
    private static readonly Uri GraphQlAddress = new(GitHubHttp.BaseAddress, "graphql");

    /// <summary>GraphQL's answers are camel case, unlike REST's.</summary>
    private static readonly JsonSerializerOptions GraphQlJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary><c>GET /repos/{owner}/{repo}/issues?state=open&amp;labels=…</c>, which lists pull
    /// requests too, each with a <c>pull_request</c> field.</summary>
    public async Task<CodeHostResult<IReadOnlyList<Issue>>> LabelledIssues(Guid userId, Repository repository, string label, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<IssueAnswer>, IssueAnswer>($"{Repo(repository)}/issues?state=open&labels={Escape(label)}&per_page=100", page => page, AsApp(repository, cancellationToken)),
            issues => (IReadOnlyList<Issue>)[.. issues.Select(ToIssue)]);

    /// <summary>An issue as GitHub answers it, listed or just opened.</summary>
    private static Issue ToIssue(IssueAnswer issue) => new(
        issue.Number,
        issue.Title,
        Login(issue.User),
        [.. issue.Labels.Select(found => found.Name)],
        issue.Body ?? "",
        issue.HtmlUrl,
        issue.CreatedAt,
        issue.PullRequest is not null);

    /// <summary><c>GET /repos/{owner}/{repo}/issues/{number}/sub_issues</c>, a hundred a page, in
    /// GitHub's order; each names its own repository, which may be another.</summary>
    public async Task<CodeHostResult<IReadOnlyList<SubIssue>>> SubIssues(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<SubIssueAnswer>, SubIssueAnswer>(Invariant($"{Repo(repository)}/issues/{number}/sub_issues?per_page=100"), page => page, AsApp(repository, cancellationToken)),
            subIssues => (IReadOnlyList<SubIssue>)[.. subIssues.Select(subIssue => new SubIssue(
                subIssue.Number,
                subIssue.Title,
                subIssue.Body ?? "",
                subIssue.State switch
                {
                    "open" => true,
                    "closed" => false,
                    _ => throw Unknown("an issue's state"),
                },
                Login(subIssue.User),
                [.. subIssue.Labels.Select(found => found.Name)],
                subIssue.CreatedAt,
                subIssue.Repository.Owner.Login,
                subIssue.Repository.Name,
                subIssue.HtmlUrl,
                subIssue.PullRequest is not null))]);

    /// <summary><c>GET /repos/{owner}/{repo}/issues/{number}/dependencies/blocked_by</c>.</summary>
    public async Task<CodeHostResult<IReadOnlyList<Dependency>>> BlockedBy(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<DependencyAnswer>, DependencyAnswer>(Invariant($"{Repo(repository)}/issues/{number}/dependencies/blocked_by?per_page=100"), page => page, AsApp(repository, cancellationToken)),
            dependencies => (IReadOnlyList<Dependency>)[.. dependencies.Select(dependency => new Dependency(
                dependency.Repository.Owner.Login,
                dependency.Repository.Name,
                dependency.Number,
                dependency.State switch
                {
                    "open" => true,
                    "closed" => false,
                    _ => throw Unknown("an issue's state"),
                },
                dependency.HtmlUrl))]);

    /// <summary>
    /// <see cref="EditsQuery"/>, posted to GraphQL as many times as its two lists have pages, at
    /// most <see cref="MostPages"/>. GraphQL answers <c>200</c> with an error of type
    /// <c>NOT_FOUND</c> for an issue that is not there, which is not found; any other error, such
    /// as a rate limit, or an answer that does not parse, is unavailable, and so is any page that
    /// is: half a history is never answered as the whole.
    /// </summary>
    public async Task<CodeHostResult<IReadOnlyList<IssueEdit>>> Edits(Guid userId, Repository repository, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var bodyEdits = new List<IssueEdit>();
        var renames = new List<IssueEdit>();
        string? editsAfter = null;
        string? renamesAfter = null;
        var (withEdits, withRenames) = (true, true);
        for (var pages = 0; withEdits || withRenames; pages++)
        {
            if (pages == MostPages)
            {
                return new CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable(RetryAt: null);
            }

            var variables = new JsonObject
            {
                ["owner"] = repository.Owner,
                ["name"] = repository.Name,
                ["number"] = number,
                ["edits"] = editsAfter,
                ["renames"] = renamesAfter,
                ["withEdits"] = withEdits,
                ["withRenames"] = withRenames,
            };
            var answer = await GraphQl<EditsData>(repository, EditsQuery, variables, cancellationToken);
            if (answer is not CodeHostResult<EditsData>.Found { Value: var data })
            {
                return Fail<EditsData, IReadOnlyList<IssueEdit>>(answer);
            }

            if (data.Repository?.Issue is not { } issue)
            {
                return new CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound();
            }

            // A list asked for and not answered, or a next page without a cursor to ask for it
            // by, is an answer that does not parse.
            if ((withEdits && (issue.UserContentEdits is null || Broken(issue.UserContentEdits.PageInfo)))
                || (withRenames && (issue.TimelineItems is null || Broken(issue.TimelineItems.PageInfo))))
            {
                return new CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable(RetryAt: null);
            }

            if (withEdits)
            {
                bodyEdits.AddRange(issue.UserContentEdits!.Nodes.Select(edit => new IssueEdit(edit.Id, Login(edit.Editor), edit.EditedAt, EditKind.Body)));
                (withEdits, editsAfter) = Next(issue.UserContentEdits.PageInfo);
            }

            if (withRenames)
            {
                renames.AddRange(issue.TimelineItems!.Nodes.Select(rename => new IssueEdit(rename.Id, Login(rename.Actor), rename.CreatedAt, EditKind.Title)));
                (withRenames, renamesAfter) = Next(issue.TimelineItems.PageInfo);
            }
        }

        return new CodeHostResult<IReadOnlyList<IssueEdit>>.Found([.. bodyEdits, .. renames]);
    }

    /// <summary>
    /// <see cref="LabelEventsQuery"/>, posted to GraphQL as many times as the timeline has pages,
    /// at most <see cref="MostPages"/>, failing as <see cref="Edits"/> does: not found for an issue
    /// that is not there, and unavailable for any other error, an answer that does not parse, or
    /// any page that is, so half a history is never answered as the whole. An event of a type the
    /// query did not ask for, or one without its label, does not parse either: an event the
    /// adapter cannot name could be the one that decides.
    /// </summary>
    public async Task<CodeHostResult<IReadOnlyList<LabelEvent>>> LabelEvents(Guid userId, Repository repository, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var events = new List<LabelEvent>();
        string? after = null;
        for (var pages = 0; ; pages++)
        {
            if (pages == MostPages)
            {
                return new CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable(RetryAt: null);
            }

            var variables = new JsonObject
            {
                ["owner"] = repository.Owner,
                ["name"] = repository.Name,
                ["number"] = number,
                ["after"] = after,
            };
            var answer = await GraphQl<LabelEventsData>(repository, LabelEventsQuery, variables, cancellationToken);
            if (answer is not CodeHostResult<LabelEventsData>.Found { Value: var data })
            {
                return Fail<LabelEventsData, IReadOnlyList<LabelEvent>>(answer);
            }

            if (data.Repository?.Issue is not { } issue)
            {
                return new CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound();
            }

            if (issue.TimelineItems is not { } timeline || Broken(timeline.PageInfo))
            {
                return new CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable(RetryAt: null);
            }

            foreach (var node in timeline.Nodes)
            {
                LabelChange? change = node.Typename switch
                {
                    "LabeledEvent" => LabelChange.Added,
                    "UnlabeledEvent" => LabelChange.Removed,
                    _ => null,
                };
                if (change is not { } known || node.Label is not { } label || node.CreatedAt is not { } at)
                {
                    return new CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable(RetryAt: null);
                }

                events.Add(new LabelEvent(label.Name, known, Login(node.Actor), at));
            }

            bool more;
            (more, after) = Next(timeline.PageInfo);
            if (!more)
            {
                return new CodeHostResult<IReadOnlyList<LabelEvent>>.Found(events);
            }
        }
    }

    /// <summary>Whether a list has another page, and the cursor it starts after.</summary>
    private static (bool More, string? After) Next(PageInfoAnswer page) =>
        page.HasNextPage ? (true, page.EndCursor) : (false, null);

    /// <summary>A next page without a cursor, which could not be asked for.</summary>
    private static bool Broken(PageInfoAnswer page) => page.HasNextPage && page.EndCursor is null;

    /// <summary>
    /// Posts <paramref name="query"/> with <paramref name="variables"/> to GraphQL with a token for
    /// the repository, and answers its <c>data</c>. Never remembered, since a POST has no
    /// <c>ETag</c>. Not found when the token cannot be had for the repository, or the only errors
    /// are GraphQL's <c>NOT_FOUND</c>; unavailable for anything else that is not a success.
    /// </summary>
    private async Task<CodeHostResult<TData>> GraphQl<TData>(Repository repository, string query, JsonObject variables, CancellationToken cancellationToken)
        where TData : class
    {
        var token = await _tokens.For(repository, cancellationToken);
        if (token is not CodeHostResult<string>.Found found)
        {
            return Fail<string, TData>(token);
        }

        using var request = GitHubHttp.Request(HttpMethod.Post, GraphQlAddress, GitHubHttp.PlainJsonMediaType);
        request.Headers.Authorization = new("Bearer", found.Value);
        request.Content = new StringContent(
            new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(),
            Encoding.UTF8,
            GitHubHttp.PlainJsonMediaType);
        var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
        if (answer is null)
        {
            return new CodeHostResult<TData>.Unavailable(RetryAt: null);
        }

        if (answer.Status != HttpStatusCode.OK)
        {
            return GitHubHttp.Failure<TData>(answer, _time);
        }

        GraphQlAnswer<TData>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GraphQlAnswer<TData>>(answer.Body, GraphQlJson);
        }
        catch (JsonException)
        {
            return new CodeHostResult<TData>.Unavailable(RetryAt: null);
        }

        if (parsed is null)
        {
            return new CodeHostResult<TData>.Unavailable(RetryAt: null);
        }

        if (parsed.Errors is { Count: > 0 } errors)
        {
            return errors.All(error => error.Type == "NOT_FOUND")
                ? new CodeHostResult<TData>.NotFound()
                : new CodeHostResult<TData>.Unavailable(RetryAt: null);
        }

        return parsed.Data is { } data
            ? new CodeHostResult<TData>.Found(data)
            : new CodeHostResult<TData>.Unavailable(RetryAt: null);
    }

    /// <summary>An issue, in a list of issues or as opening one answers it; <c>pull_request</c> is
    /// there only for a pull request.</summary>
    private sealed record IssueAnswer(
        int Number,
        string Title,
        UserAnswer? User,
        IReadOnlyList<LabelAnswer> Labels,
        string? Body,
        Uri HtmlUrl,
        DateTimeOffset CreatedAt,
        JsonElement? PullRequest = null);

    private sealed record DependencyRepositoryAnswer(string Name, OwnerAnswer Owner);

    /// <summary>A sub-issue as GitHub lists it, with its repository; <c>pull_request</c> is there
    /// only for a pull request.</summary>
    private sealed record SubIssueAnswer(
        int Number,
        string Title,
        string? Body,
        string State,
        UserAnswer? User,
        IReadOnlyList<LabelAnswer> Labels,
        DateTimeOffset CreatedAt,
        Uri HtmlUrl,
        DependencyRepositoryAnswer Repository,
        JsonElement? PullRequest = null);

    private sealed record DependencyAnswer(int Number, string State, Uri HtmlUrl, DependencyRepositoryAnswer Repository);

    /// <summary>A GraphQL answer: its data, null when the query failed as a whole, and its
    /// errors, when there are any.</summary>
    private sealed record GraphQlAnswer<TData>(TData? Data, IReadOnlyList<GraphQlError>? Errors = null)
        where TData : class;

    /// <summary>A GraphQL error, of which the adapter reads its type, such as <c>NOT_FOUND</c>.
    /// Its message is GitHub's, and never kept.</summary>
    private sealed record GraphQlError(string? Type = null);

    private sealed record PageInfoAnswer(bool HasNextPage, string? EndCursor);

    private sealed record EditNodeAnswer(string Id, DateTimeOffset EditedAt, UserAnswer? Editor);

    private sealed record RenameNodeAnswer(string Id, DateTimeOffset CreatedAt, UserAnswer? Actor);

    private sealed record EditsConnectionAnswer(PageInfoAnswer PageInfo, IReadOnlyList<EditNodeAnswer> Nodes);

    private sealed record RenamesConnectionAnswer(PageInfoAnswer PageInfo, IReadOnlyList<RenameNodeAnswer> Nodes);

    /// <summary>The issue, each list there only when it was asked for.</summary>
    private sealed record EditsIssueAnswer(EditsConnectionAnswer? UserContentEdits = null, RenamesConnectionAnswer? TimelineItems = null);

    private sealed record EditsRepositoryAnswer(EditsIssueAnswer? Issue);

    private sealed record EditsData(EditsRepositoryAnswer? Repository);

    private sealed record LabelNameAnswer(string Name);

    /// <summary>A label event; every field may be left out by a node of another type, which the
    /// read refuses rather than this record.</summary>
    private sealed record LabelEventNodeAnswer(
        [property: JsonPropertyName("__typename")] string? Typename = null,
        DateTimeOffset? CreatedAt = null,
        UserAnswer? Actor = null,
        LabelNameAnswer? Label = null);

    private sealed record LabelEventsConnectionAnswer(PageInfoAnswer PageInfo, IReadOnlyList<LabelEventNodeAnswer> Nodes);

    private sealed record LabelEventsIssueAnswer(LabelEventsConnectionAnswer? TimelineItems = null);

    private sealed record LabelEventsRepositoryAnswer(LabelEventsIssueAnswer? Issue);

    private sealed record LabelEventsData(LabelEventsRepositoryAnswer? Repository);
}
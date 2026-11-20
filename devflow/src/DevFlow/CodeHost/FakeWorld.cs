using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevFlow.CodeHost;

/// <summary>
/// One account's world in the fake code host: the installations it reaches, their repositories,
/// and what each holds. Its JSON is the document <c>PUT /devflow/api/fake/world</c> takes and
/// <c>GET</c> gives back, shaped as <see cref="ICodeHost"/>'s records, so staging something is
/// writing what the interface would answer. See "The code host" in devflow/README.md.
/// </summary>
/// <param name="Installations">The installations of Dev flow's app the account reaches.</param>
/// <param name="User">Who the account is at the fake code host, the one person its sign in page
/// shows and its tokens act as; <see cref="FakeUser.Default"/> when it is left out, the one field
/// a world may leave out, and left out when it is written back.</param>
public sealed record FakeWorld(
    IReadOnlyList<FakeInstallation> Installations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FakeUser? User = null)
{
    /// <summary>The world of an account that has staged nothing.</summary>
    public static FakeWorld Empty { get; } = new([]);

    /// <summary>Who the account is at the fake code host. Not part of the document.</summary>
    [JsonIgnore]
    public FakeUser SignedInUser => User ?? FakeUser.Default;
}

/// <summary>A person at the fake code host, as its sign in page shows them and as
/// <see cref="ICodeHostSignIn.WhoIs"/> answers.</summary>
public sealed record FakeUser(string Login, long Id)
{
    /// <summary>Who an account is when its world does not say: <c>octo-owner</c>.</summary>
    public static FakeUser Default { get; } = new("octo-owner", 70000001);
}

/// <summary>An installation of Dev flow's app, the account it is on, and the repositories it
/// reaches.</summary>
public sealed record FakeInstallation(long Id, string Account, IReadOnlyList<FakeRepository> Repositories);

/// <summary>A repository, by its id within its installation, and what it holds.</summary>
/// <param name="Conversations">The comments of an issue or pull request, by its number. A pull
/// request without one has an empty conversation.</param>
/// <param name="Commits">The statuses and check runs posted on a commit, by its id. A commit not
/// listed is not found.</param>
/// <param name="Issues">Its issues, by number, which pull requests' numbers are not.</param>
/// <param name="DefaultBranch">Its default branch, <see cref="FakeCodeHost.DefaultBranchName"/> when it
/// is left out; one of the fields a repository may leave out, and left out when it is written
/// back.</param>
/// <param name="Activity">What changed its branches, oldest first, as
/// <see cref="ICodeHost.Activity"/> answers it newest first; none when it is left out, as it may
/// be. A merge the fake takes adds one.</param>
/// <param name="Conflicts">The branches that bringing another branch into answers a conflict, see
/// <see cref="FakeCodeHost.BringIn"/>; none when it is left out, as it may be.</param>
public sealed record FakeRepository(
    long Id,
    string Owner,
    string Name,
    IReadOnlyList<PullRequest> PullRequests,
    IReadOnlyList<FakeConversation> Conversations,
    IReadOnlyList<FakeCommit> Commits,
    IReadOnlyList<FakeFile> Files,
    IReadOnlyList<FakeIssue> Issues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultBranch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FakeActivity>? Activity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Conflicts = null)
{
    /// <summary>Its default branch, as the world says or the fake's own.</summary>
    [JsonIgnore]
    public string Default => DefaultBranch ?? FakeCodeHost.DefaultBranchName;
}

/// <summary>One change to a branch of a repository in the fake, a
/// <see cref="BranchActivity"/> and the branch it changed.</summary>
public sealed record FakeActivity(long Id, string Branch, string Before, string After, DateTimeOffset At, ActivityKind Kind, string? Actor);

/// <summary>An issue, as picking cards reads it.</summary>
/// <param name="Open">Whether it is open; a closed one is never listed by label, and no longer
/// holds back what it blocks.</param>
/// <param name="BlockedBy">The numbers of the issues in the same repository it is blocked by,
/// each listed among the repository's issues.</param>
/// <param name="Edits">Its edits, as <see cref="ICodeHost.Edits"/> answers them; empty when it was
/// never edited.</param>
/// <param name="SubIssues">Its sub-issues, in their order, each an issue or a pull request listed
/// in this repository or another of the same installation; none when it is left out, the one field
/// an issue may leave out, and left out when it is written back.</param>
public sealed record FakeIssue(
    int Number,
    string Title,
    string Author,
    IReadOnlyList<string> Labels,
    string Body,
    Uri Url,
    DateTimeOffset CreatedAt,
    bool Open,
    IReadOnlyList<int> BlockedBy,
    IReadOnlyList<IssueEdit> Edits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FakeSubIssue>? SubIssues = null);

/// <summary>A sub-issue, by its number in <paramref name="Repository"/>, the id of another
/// repository of the same installation, or this one when null.</summary>
public sealed record FakeSubIssue(int Number, long? Repository);

/// <summary>The conversation of the issue or pull request with the number, oldest first.</summary>
public sealed record FakeConversation(int Number, IReadOnlyList<Comment> Comments);

/// <summary>A commit, by its full id, with what is posted on it.</summary>
public sealed record FakeCommit(string Id, IReadOnlyList<CommitStatus> Statuses, IReadOnlyList<CheckRun> CheckRuns);

/// <summary>A file on a branch; its content is base64 in the JSON.</summary>
public sealed record FakeFile(string Path, string Branch, byte[] Content);

/// <summary>A world, or a switch, that does not parse, and the field that is wrong.</summary>
/// <param name="Field">Where it is, such as <c>installations[0].repositories[0].name</c>, or
/// <c>the body</c> when it is not JSON at all.</param>
/// <param name="Reason">What is wrong with it, in words that name no .NET type.</param>
public sealed record Malformed(string Field, string Reason)
{
    public override string ToString() => $"{Field}: {Reason}";
}

/// <summary>
/// Reads the fake's documents strictly: every field present, nothing null that the records do
/// not allow, no field the records do not have, numbers as numbers and enumerations by their
/// snake case words. So a typo in a test's world is refused naming it rather than staged as
/// something else, and a world read back is the document that was put.
/// </summary>
public static partial class FakeJson
{
    /// <summary>The options a world is read and written with, snake case like the rest of the
    /// API.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>The document in <paramref name="body"/>, or what is wrong with it. JSON that does
    /// not parse is the body's fault; JSON that parses and does not fit is a field's.</summary>
    public static async Task<(T? Value, Malformed? Error)> Read<T>(Stream body, CancellationToken cancellationToken)
        where T : class
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return (null, new Malformed("the body", "is not JSON"));
        }

        using (document)
        {
            try
            {
                var value = document.Deserialize<T>(Options);
                return value is null ? (null, new Malformed("the body", "is null, not a document")) : (value, null);
            }
            catch (JsonException refused)
            {
                return (null, Describe(refused));
            }
        }
    }

    /// <summary>The world's own rules, beyond its shape: what identifies a thing is listed once
    /// where it is looked up by it, so the fake never has to choose between two.</summary>
    public static Malformed? Check(FakeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Duplicate(world.Installations, installation => installation.Id, i => $"installations[{i}].id")
            ?? world.Installations.Select((installation, i) => Check(installation, $"installations[{i}]")).FirstOrDefault(error => error is not null);
    }

    private static Malformed? Check(FakeInstallation installation, string at) =>
        Duplicate(installation.Repositories, repository => repository.Id, i => $"{at}.repositories[{i}].id")
        ?? installation.Repositories.Select((repository, i) => Check(repository, $"{at}.repositories[{i}]")).FirstOrDefault(error => error is not null)
        ?? installation.Repositories.Select((repository, i) => CheckSubIssues(installation, repository, $"{at}.repositories[{i}]")).FirstOrDefault(error => error is not null);

    /// <summary>Each sub-issue names an issue or a pull request listed in its repository, this one
    /// or another of the installation, and never the issue itself.</summary>
    private static Malformed? CheckSubIssues(FakeInstallation installation, FakeRepository repository, string at) =>
        repository.Issues.Select((issue, i) => (issue.SubIssues ?? []).Select((sub, j) =>
                installation.Repositories.FirstOrDefault(candidate => candidate.Id == (sub.Repository ?? repository.Id)) is not { } holder
                    ? new Malformed($"{at}.issues[{i}].sub_issues[{j}].repository", "names no repository of this installation")
                    : !holder.Issues.Any(other => other.Number == sub.Number) && !holder.PullRequests.Any(pull => pull.Number == sub.Number)
                        ? new Malformed($"{at}.issues[{i}].sub_issues[{j}].number", "names no issue or pull request listed in its repository")
                        : holder.Id == repository.Id && sub.Number == issue.Number
                            ? new Malformed($"{at}.issues[{i}].sub_issues[{j}].number", "is the issue itself")
                            : null)
                .FirstOrDefault(error => error is not null))
            .FirstOrDefault(error => error is not null);

    private static Malformed? Check(FakeRepository repository, string at) =>
        Duplicate(repository.PullRequests, pullRequest => pullRequest.Number, i => $"{at}.pull_requests[{i}].number")
        ?? Duplicate(repository.Conversations, conversation => conversation.Number, i => $"{at}.conversations[{i}].number")
        ?? Duplicate(repository.Commits, commit => commit.Id, i => $"{at}.commits[{i}].id")
        ?? Duplicate(repository.Files, file => (file.Path, file.Branch), i => $"{at}.files[{i}]")
        ?? Duplicate(repository.Issues, issue => issue.Number, i => $"{at}.issues[{i}].number")
        ?? Duplicate(repository.Activity ?? [], activity => activity.Id, i => $"{at}.activity[{i}].id")
        ?? (repository.DefaultBranch is "" ? new Malformed($"{at}.default_branch", "is empty; leave it out for main") : null)
        ?? repository.Issues.Select((issue, i) => repository.PullRequests.Any(pull => pull.Number == issue.Number)
                ? new Malformed($"{at}.issues[{i}].number", "is a pull request's number; an issue and a pull request never share one")
                : issue.BlockedBy.Select((number, j) => repository.Issues.Any(other => other.Number == number)
                        ? null
                        : new Malformed($"{at}.issues[{i}].blocked_by[{j}]", "names no issue listed in this repository"))
                    .FirstOrDefault(error => error is not null))
            .FirstOrDefault(error => error is not null);

    /// <summary>The first item whose key an earlier one has, named by <paramref name="field"/>.</summary>
    private static Malformed? Duplicate<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, Func<int, string> field)
    {
        var seen = new HashSet<TKey>();
        for (var i = 0; i < items.Count; i++)
        {
            if (!seen.Add(key(items[i])))
            {
                return new Malformed(field(i), "is listed twice; an earlier entry has the same");
            }
        }

        return null;
    }

    /// <summary>
    /// The field a <see cref="JsonException"/> is about, from its path, and the reason in plain
    /// words. A missing field's path is the object holding it, so its name is taken from the
    /// message, the one place System.Text.Json gives it.
    /// </summary>
    private static Malformed Describe(JsonException refused)
    {
        var path = refused.Path is null or "$" ? "" : refused.Path.TrimStart('$').TrimStart('.');
        var message = refused.Message;
        if (MissingProperty().Match(message) is { Success: true } missing)
        {
            var name = missing.Groups["name"].Value;
            return new Malformed(path.Length == 0 ? name : $"{path}.{name}", "is missing; every field is written, null where it may be null");
        }

        if (path.Length == 0)
        {
            return new Malformed("the body", "is not a JSON document of the expected shape");
        }

        if (message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal))
        {
            return new Malformed(path, "is not a field here");
        }

        if (message.Contains("doesn't allow null values", StringComparison.Ordinal))
        {
            return new Malformed(path, "may not be null");
        }

        return new Malformed(path, "is not a valid value here");
    }

    [GeneratedRegex("missing required properties including: '(?<name>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex MissingProperty();
}
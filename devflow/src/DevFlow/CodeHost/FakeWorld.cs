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
public sealed record FakeWorld(IReadOnlyList<FakeInstallation> Installations)
{
    /// <summary>The world of an account that has staged nothing.</summary>
    public static FakeWorld Empty { get; } = new([]);
}

/// <summary>An installation of Dev flow's app, and the repositories it reaches.</summary>
public sealed record FakeInstallation(long Id, IReadOnlyList<FakeRepository> Repositories);

/// <summary>A repository, by its id within its installation, and what it holds.</summary>
/// <param name="Conversations">The comments of an issue or pull request, by its number. A pull
/// request without one has an empty conversation.</param>
/// <param name="Commits">The statuses and check runs posted on a commit, by its id. A commit not
/// listed is not found.</param>
public sealed record FakeRepository(
    long Id,
    string Owner,
    string Name,
    IReadOnlyList<PullRequest> PullRequests,
    IReadOnlyList<FakeConversation> Conversations,
    IReadOnlyList<FakeCommit> Commits,
    IReadOnlyList<FakeFile> Files);

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
        ?? installation.Repositories.Select((repository, i) => Check(repository, $"{at}.repositories[{i}]")).FirstOrDefault(error => error is not null);

    private static Malformed? Check(FakeRepository repository, string at) =>
        Duplicate(repository.PullRequests, pullRequest => pullRequest.Number, i => $"{at}.pull_requests[{i}].number")
        ?? Duplicate(repository.Conversations, conversation => conversation.Number, i => $"{at}.conversations[{i}].number")
        ?? Duplicate(repository.Commits, commit => commit.Id, i => $"{at}.commits[{i}].id")
        ?? Duplicate(repository.Files, file => (file.Path, file.Branch), i => $"{at}.files[{i}]");

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
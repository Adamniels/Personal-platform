using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DevFlow.CodeHost;

/// <summary>A write the fake took, as GitHub records one: what was done, to which issue or pull
/// request, and who did it, a person by their access token or the app by a key it made for a
/// purpose. <c>GET /devflow/api/fake/writes</c> lists them, so a test asserts who wrote. The fields
/// after <paramref name="By"/> are left out of the JSON when null, so a person's write reads as it
/// always has.</summary>
/// <param name="Kind"><c>merge</c> or <c>comment</c>, by a person or the app; or, by the app
/// alone, <c>pull_request</c>, <c>issue</c>, <c>label_added</c> or <c>label_removed</c>.</param>
/// <param name="Repository">The repository's id.</param>
/// <param name="Number">The issue's or pull request's number, the new one for what was
/// opened.</param>
/// <param name="Commit">For a merge, the commit its head had to be; for a pull request opened, its
/// head commit; otherwise null.</param>
/// <param name="Method">For a merge, how it merged, always <c>merge</c>, a merge commit; otherwise
/// null.</param>
/// <param name="Body">For a comment, a pull request or an issue, its text exactly; otherwise
/// null.</param>
/// <param name="By">The login GitHub would show: the person's whose access token made it, or the
/// app's, <see cref="FakeCodeHost.AppLogin"/>.</param>
/// <param name="Purpose">For the app's write, the purpose of the key that made it, <c>post</c> or
/// <c>merge</c>; null for a person's, which is how a test tells the two apart.</param>
/// <param name="Title">For a pull request or an issue opened, its title.</param>
/// <param name="Head">For a pull request opened, the branch it merges from.</param>
/// <param name="Base">For a pull request opened, the branch it merges into.</param>
/// <param name="Label">For a label added or removed, its name.</param>
public sealed record FakeWrite(
    string Kind,
    long Repository,
    int Number,
    string? Commit,
    string? Method,
    string? Body,
    string By,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Head = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

/// <summary>
/// The fake's writes, <see cref="ICodeHost.Merge"/> and <see cref="ICodeHost.Comment"/>, answered
/// as GitHub answers them and kept in the account's world: a merged pull request is merged there,
/// and a comment is in its conversation by the person, so the next read sees both. Each takes only
/// the account's current access token, from its sign in, and is refused as GitHub refuses a token
/// it does not take otherwise. Every write taken is recorded, see <see cref="Writes"/>. See "The
/// fake" in devflow/README.md.
/// </summary>
public sealed partial class FakeCodeHost
{
    /// <summary>What GitHub says for a pull request it will not merge, closed among them.</summary>
    public const string NotMergeableMessage = "Pull Request is not mergeable";

    /// <summary>What GitHub says for a token it does not take, with <c>401</c>.</summary>
    public const string BadCredentials = "Bad credentials";

    /// <summary>What GitHub says with <c>404</c>.</summary>
    public const string NotFoundMessage = "Not Found";

    /// <summary>The writes the fake took for the account, oldest first, kept when a world is
    /// staged again.</summary>
    public IReadOnlyList<FakeWrite> Writes(Guid userId) => Of(userId).Writes;

    /// <summary>Not found for a repository or pull request the world does not hold, not
    /// mergeable unless it is open, head moved unless its head is <paramref name="commit"/>, and
    /// otherwise merged, as the person.</summary>
    public Task<MergeResult> Merge(Guid userId, UserAccessToken token, Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(commit);
        lock (_changing)
        {
            var account = Of(userId);
            MergeResult result;
            if (account.Unavailable)
            {
                result = new MergeResult.Unavailable(RetryAt: null);
            }
            else if (ActingAs(userId, token.Value) is not { } person)
            {
                result = new MergeResult.Refused(StatusCodes.Status401Unauthorized, BadCredentials);
            }
            else if (Find(account.World, repository) is not { } found
                || found.PullRequests.FirstOrDefault(pull => pull.Number == number) is not { } pull)
            {
                result = new MergeResult.Refused(StatusCodes.Status404NotFound, NotFoundMessage);
            }
            else if (pull.State != PullRequestState.Open)
            {
                result = new MergeResult.NotMergeable(NotMergeableMessage);
            }
            else if (!string.Equals(pull.HeadCommit, commit, StringComparison.Ordinal))
            {
                result = new MergeResult.HeadMoved();
            }
            else
            {
                var (changed, mergeCommit) = WithMerge(found, number, person.Login);
                _accounts[userId] = account with
                {
                    World = WithRepository(account.World, repository, changed),
                    Writes = [.. account.Writes, new FakeWrite("merge", repository.Id, number, commit, "merge", null, person.Login)],
                };
                result = new MergeResult.Merged(mergeCommit);
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>Not found for a repository the world does not hold, or a number with neither a
    /// pull request, an issue nor a conversation; otherwise posted, as the person, at the end of the
    /// conversation, with an id after every comment in the repository.</summary>
    public Task<CommentResult> Comment(Guid userId, UserAccessToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(body);
        lock (_changing)
        {
            var account = Of(userId);
            CommentResult result;
            if (account.Unavailable)
            {
                result = new CommentResult.Unavailable(RetryAt: null);
            }
            else if (ActingAs(userId, token.Value) is not { } person)
            {
                result = new CommentResult.Refused(StatusCodes.Status401Unauthorized, BadCredentials);
            }
            else if (Find(account.World, repository) is not { } found || !Holds(found, number))
            {
                result = new CommentResult.Refused(StatusCodes.Status404NotFound, NotFoundMessage);
            }
            else
            {
                var (changed, _) = WithComment(found, number, person.Login, body);
                _accounts[userId] = account with
                {
                    World = WithRepository(account.World, repository, changed),
                    Writes = [.. account.Writes, new FakeWrite("comment", repository.Id, number, null, null, body, person.Login)],
                };
                result = new CommentResult.Posted();
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>The repository with a comment by <paramref name="author"/> at the end of the
    /// conversation with the number, made when there is none, at the fake's now, with an id after
    /// every comment in the repository; and the comment.</summary>
    private (FakeRepository Changed, Comment Comment) WithComment(FakeRepository found, int number, string author, string body)
    {
        var now = _time.GetUtcNow();
        var id = found.Conversations.SelectMany(conversation => conversation.Comments).Select(comment => comment.Id).DefaultIfEmpty(0).Max() + 1;
        var comment = new Comment(id, author, body, now, now);
        var conversations = found.Conversations.Any(conversation => conversation.Number == number)
            ? found.Conversations.Select(conversation => conversation.Number == number
                ? conversation with { Comments = [.. conversation.Comments, comment] }
                : conversation)
            : [.. found.Conversations, new FakeConversation(number, [comment])];
        return (found with { Conversations = [.. conversations] }, comment);
    }

    /// <summary>
    /// The repository with the pull request merged, now, and the merge landed on its default
    /// branch as a change by <paramref name="actor"/>, as GitHub lists one: after the branch's last
    /// change, from the commit that left it at, or forty zeros before any, to a new merge commit,
    /// random; and that commit. Its git repository is not changed.
    /// </summary>
    private (FakeRepository Changed, string Commit) WithMerge(FakeRepository found, int number, string actor)
    {
        var now = _time.GetUtcNow();
        var activity = found.Activity ?? [];
        var before = activity.LastOrDefault(each => each.Branch == found.Default)?.After ?? new string('0', 40);
        var commit = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var id = activity.Select(each => each.Id).DefaultIfEmpty(0).Max() + 1;
        return (found with
        {
            PullRequests = [.. found.PullRequests.Select(each => each.Number == number ? each with { State = PullRequestState.Merged, UpdatedAt = now } : each)],
            Activity = [.. activity, new FakeActivity(id, found.Default, before, commit, now, ActivityKind.PullRequestMerge, actor)],
        }, commit);
    }

    /// <summary>The world with <paramref name="repository"/>'s entry, found by installation and
    /// id, replaced by <paramref name="changed"/>.</summary>
    private static FakeWorld WithRepository(FakeWorld world, Repository repository, FakeRepository changed) =>
        world with
        {
            Installations = [.. world.Installations.Select(installation => installation.Id != repository.Installation
                ? installation
                : installation with
                {
                    Repositories = [.. installation.Repositories.Select(each => each.Id == repository.Id ? changed : each)],
                })],
        };
}
namespace DevFlow.CodeHost;

/// <summary>A write the fake took, as GitHub records one: what was done, to which pull request, and
/// whose token did it. <c>GET /devflow/api/fake/writes</c> lists them, so a test asserts who
/// wrote.</summary>
/// <param name="Kind"><c>merge</c> or <c>comment</c>.</param>
/// <param name="Repository">The repository's id.</param>
/// <param name="Number">The pull request's number.</param>
/// <param name="Commit">For a merge, the commit its head had to be; null for a comment.</param>
/// <param name="Method">For a merge, how it merged, always <c>merge</c>, a merge commit; null for a
/// comment.</param>
/// <param name="Body">For a comment, its text exactly; null for a merge.</param>
/// <param name="By">The login of the person whose access token made it. The fake takes a write
/// with nothing else, so there is no write by the app to record.</param>
public sealed record FakeWrite(string Kind, long Repository, int Number, string? Commit, string? Method, string? Body, string By);

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
                var merged = pull with { State = PullRequestState.Merged, UpdatedAt = _time.GetUtcNow() };
                var changed = found with { PullRequests = [.. found.PullRequests.Select(each => each.Number == number ? merged : each)] };
                _accounts[userId] = account with
                {
                    World = WithRepository(account.World, repository, changed),
                    Writes = [.. account.Writes, new FakeWrite("merge", repository.Id, number, commit, "merge", null, person.Login)],
                };
                result = new MergeResult.Merged();
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>Not found for a repository the world does not hold, or a number with neither a
    /// pull request nor a conversation; otherwise posted, as the person, at the end of the
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
            else if (Find(account.World, repository) is not { } found
                || (!found.PullRequests.Any(pull => pull.Number == number) && !found.Conversations.Any(conversation => conversation.Number == number)))
            {
                result = new CommentResult.Refused(StatusCodes.Status404NotFound, NotFoundMessage);
            }
            else
            {
                var now = _time.GetUtcNow();
                var id = found.Conversations.SelectMany(conversation => conversation.Comments).Select(comment => comment.Id).DefaultIfEmpty(0).Max() + 1;
                var comment = new Comment(id, person.Login, body, now, now);
                var conversations = found.Conversations.Any(conversation => conversation.Number == number)
                    ? found.Conversations.Select(conversation => conversation.Number == number
                        ? conversation with { Comments = [.. conversation.Comments, comment] }
                        : conversation)
                    : [.. found.Conversations, new FakeConversation(number, [comment])];
                _accounts[userId] = account with
                {
                    World = WithRepository(account.World, repository, found with { Conversations = [.. conversations] }),
                    Writes = [.. account.Writes, new FakeWrite("comment", repository.Id, number, null, null, body, person.Login)],
                };
                result = new CommentResult.Posted();
            }

            return Task.FromResult(result);
        }
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
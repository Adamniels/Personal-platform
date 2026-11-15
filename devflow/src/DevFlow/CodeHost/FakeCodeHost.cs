using System.Collections.Concurrent;

namespace DevFlow.CodeHost;

/// <summary>
/// The code host every test, CI and the runner's stack use: a world per platform account, kept in
/// memory and lost at a restart, which a test stages through <c>/devflow/api/fake/</c>. Per account,
/// because every acceptance test registers its own account and they all run against one Dev flow.
/// It sits at <see cref="ICodeHost"/> rather than standing in for GitHub's server, so what is
/// staged is what the interface answers, and the GitHub adapter is tested on its own. Chosen by
/// <c>DEVFLOW_CODE_HOST=fake</c>, the default; it must never be on where real data is. See "The
/// code host seam" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class FakeCodeHost : ICodeHost
{
    /// <summary>An account's world, and whether every operation answers unavailable. Replaced
    /// whole, never changed in place, so a reader sees one or the other.</summary>
    private sealed record Account(FakeWorld World, bool Unavailable);

    private static readonly Account Nothing = new(FakeWorld.Empty, Unavailable: false);

    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    /// <summary>The account's world, empty until one is staged.</summary>
    public FakeWorld World(Guid userId) => Of(userId).World;

    /// <summary>Replaces the account's world whole. Whether it is unavailable is kept.</summary>
    public void Replace(Guid userId, FakeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _accounts.AddOrUpdate(userId, _ => Nothing with { World = world }, (_, account) => account with { World = world });
    }

    /// <summary>Makes every operation for the account answer unavailable, or answer again. The
    /// world is kept either way.</summary>
    public void SetUnavailable(Guid userId, bool unavailable) =>
        _accounts.AddOrUpdate(userId, _ => Nothing with { Unavailable = unavailable }, (_, account) => account with { Unavailable = unavailable });

    public Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<PullRequest>>(userId, repository, found => [.. found.PullRequests.Where(pullRequest => pullRequest.State == PullRequestState.Open)]);

    public Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.PullRequests.FirstOrDefault(pullRequest => pullRequest.Number == number));

    public Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<Comment>>(userId, repository, found =>
            found.Conversations.FirstOrDefault(conversation => conversation.Number == number)?.Comments
            ?? (found.PullRequests.Any(pullRequest => pullRequest.Number == number) ? [] : null));

    public Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => Commit(found, commit)?.Statuses);

    public Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => Commit(found, commit)?.CheckRuns);

    public Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.Files.FirstOrDefault(file => file.Path == path && file.Branch == branch)?.Content.ToArray());

    private Account Of(Guid userId) => _accounts.GetValueOrDefault(userId, Nothing);

    private static FakeCommit? Commit(FakeRepository repository, string commit) =>
        repository.Commits.FirstOrDefault(candidate => candidate.Id == commit);

    /// <summary>Unavailable when the account says so; otherwise what <paramref name="read"/>
    /// finds in the repository, and not found when the repository or that is missing.</summary>
    private Task<CodeHostResult<T>> Answer<T>(Guid userId, Repository repository, Func<FakeRepository, T?> read)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var account = Of(userId);
        CodeHostResult<T> result = account.Unavailable
            ? new CodeHostResult<T>.Unavailable(RetryAt: null)
            : Find(account.World, repository) is { } found && read(found) is { } value
                ? new CodeHostResult<T>.Found(value)
                : new CodeHostResult<T>.NotFound();
        return Task.FromResult(result);
    }

    /// <summary>The repository by its installation and id; its owner and name are display.</summary>
    private static FakeRepository? Find(FakeWorld world, Repository repository) =>
        world.Installations
            .Where(installation => installation.Id == repository.Installation)
            .SelectMany(installation => installation.Repositories)
            .FirstOrDefault(candidate => candidate.Id == repository.Id);
}
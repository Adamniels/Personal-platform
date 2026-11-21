using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

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
public sealed partial class FakeCodeHost : ICodeHost, ICodeHostSignIn
{
    /// <summary>A repository's default branch in the fake when its world does not name one.</summary>
    public const string DefaultBranchName = "main";

    private readonly TimeProvider _time;

    /// <summary>A fake on the system's clock.</summary>
    public FakeCodeHost()
        : this(TimeProvider.System)
    {
    }

    /// <param name="time">Decides when its codes and tokens expire, a test's own clock in the
    /// tests.</param>
    public FakeCodeHost(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>An account's world, whether every operation answers unavailable, the writes
    /// made to it, oldest first, and the keys the fake issued for the app's writes in it, by their
    /// value. Replaced whole, never changed in place, so a reader sees one or the other.</summary>
    private sealed record Account(FakeWorld World, bool Unavailable, IReadOnlyList<FakeWrite> Writes, ImmutableDictionary<string, IssuedKey> Keys);

    private static readonly Account Nothing = new(FakeWorld.Empty, Unavailable: false, Writes: [], Keys: ImmutableDictionary.Create<string, IssuedKey>(StringComparer.Ordinal));

    private readonly ConcurrentDictionary<Guid, Account> _accounts = new();

    /// <summary>Held while an account is changed, so a write and a test staging a world never
    /// lose each other's change.</summary>
    private readonly Lock _changing = new();

    /// <summary>The account's world, empty until one is staged.</summary>
    public FakeWorld World(Guid userId) => Of(userId).World;

    /// <summary>Replaces the account's world whole. Whether it is unavailable, and the writes
    /// made, are kept.</summary>
    public void Replace(Guid userId, FakeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Change(userId, account => account with { World = world });
    }

    /// <summary>Makes every operation for the account answer unavailable, or answer again. The
    /// world is kept either way.</summary>
    public void SetUnavailable(Guid userId, bool unavailable) =>
        Change(userId, account => account with { Unavailable = unavailable });

    /// <summary>The account changed by <paramref name="change"/>, under the lock.</summary>
    private void Change(Guid userId, Func<Account, Account> change)
    {
        lock (_changing)
        {
            _accounts[userId] = change(Of(userId));
        }
    }

    public Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<PullRequest>>(userId, repository, found => [.. found.PullRequests.Where(pullRequest => pullRequest.State == PullRequestState.Open)]);

    public Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.PullRequests.FirstOrDefault(pullRequest => pullRequest.Number == number));

    public Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<Comment>>(userId, repository, found =>
            found.Conversations.FirstOrDefault(conversation => conversation.Number == number)?.Comments
            ?? (found.PullRequests.Any(pullRequest => pullRequest.Number == number) ? [] : null));

    /// <summary>The statuses the world lists on the commit; none for a commit its git repository
    /// holds and the world does not list, such as one a worker pushed, as GitHub answers for a
    /// commit nothing was posted on; and not found for any other.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        await OrInGit(userId, repository, commit, await Answer(userId, repository, found => Commit(found, commit)?.Statuses), cancellationToken);

    /// <summary>The check runs the world lists on the commit, or none for one only its git
    /// repository holds, as <see cref="Statuses"/>.</summary>
    public async Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, Repository repository, string commit, CancellationToken cancellationToken) =>
        await OrInGit(userId, repository, commit, await Answer(userId, repository, found => Commit(found, commit)?.CheckRuns), cancellationToken);

    /// <summary><paramref name="listed"/>, or nothing posted when it is not found and the
    /// repository's git repository holds the commit.</summary>
    private async Task<CodeHostResult<IReadOnlyList<T>>> OrInGit<T>(Guid userId, Repository repository, string commit, CodeHostResult<IReadOnlyList<T>> listed, CancellationToken cancellationToken) =>
        listed is CodeHostResult<IReadOnlyList<T>>.NotFound && Find(Of(userId).World, repository) is not null && await HasCommit(userId, repository, commit, cancellationToken)
            ? new CodeHostResult<IReadOnlyList<T>>.Found([])
            : listed;

    public Task<CodeHostResult<byte[]>> File(Guid userId, Repository repository, string path, string branch, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.Files.FirstOrDefault(file => file.Path == path && file.Branch == branch)?.Content.ToArray());

    /// <summary>
    /// The file on the repository's default branch. The fake keeps no history, so the commit it answers
    /// is the SHA-256 of the file's content, in hex, the shape of a commit id in a SHA-256
    /// repository: like the newest commit that changed the file, it changes when the content does
    /// and only then.
    /// </summary>
    public Task<CodeHostResult<FileAtCommit>> DefaultBranchFile(Guid userId, Repository repository, string path, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.Files.FirstOrDefault(file => file.Path == path && file.Branch == found.Default) is { } file
            ? new FileAtCommit(file.Content.ToArray(), Convert.ToHexStringLower(SHA256.HashData(file.Content)))
            : null);

    /// <summary>The open issues carrying the label, whatever its case as GitHub matches it, and the
    /// open pull requests carrying it, marked, as GitHub lists them; a pull request was made when
    /// it was last updated, since the fake keeps no other time for one.</summary>
    public Task<CodeHostResult<IReadOnlyList<Issue>>> LabelledIssues(Guid userId, Repository repository, string label, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<Issue>>(userId, repository, found =>
        [
            .. found.Issues
                .Where(issue => issue.Open && issue.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                .Select(issue => new Issue(issue.Number, issue.Title, issue.Author, issue.Labels, issue.Body, issue.Url, issue.CreatedAt, IsPullRequest: false)),
            .. found.PullRequests
                .Where(pull => pull.State == PullRequestState.Open && pull.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                .Select(pull => new Issue(pull.Number, pull.Title, pull.Author, pull.Labels, pull.Body, pull.Url, pull.UpdatedAt, IsPullRequest: true)),
        ]);

    /// <summary>The issue's sub-issues as staged, in their order, each as its own repository holds
    /// it: an issue there, or a pull request, marked, made when it was last updated.</summary>
    public Task<CodeHostResult<IReadOnlyList<SubIssue>>> SubIssues(Guid userId, Repository repository, int number, CancellationToken cancellationToken)
    {
        var installation = Of(userId).World.Installations.FirstOrDefault(candidate => candidate.Id == repository.Installation);
        return Answer<IReadOnlyList<SubIssue>>(userId, repository, found =>
            found.Issues.FirstOrDefault(issue => issue.Number == number) is { } parent
                ? [.. (parent.SubIssues ?? []).Select(sub =>
                {
                    var holder = sub.Repository is { } id ? installation!.Repositories.First(candidate => candidate.Id == id) : found;
                    return holder.Issues.FirstOrDefault(issue => issue.Number == sub.Number) is { } issue
                        ? new SubIssue(issue.Number, issue.Title, issue.Body, issue.Open, issue.Author, issue.Labels, issue.CreatedAt, holder.Owner, holder.Name, issue.Url, IsPullRequest: false)
                        : AsSubIssue(holder.PullRequests.First(pull => pull.Number == sub.Number), holder);
                })]
                : null);
    }

    /// <summary>A pull request listed among an issue's sub-issues, as GitHub would list it.</summary>
    private static SubIssue AsSubIssue(PullRequest pull, FakeRepository holder) =>
        new(pull.Number, pull.Title, pull.Body, pull.State == PullRequestState.Open, pull.Author, pull.Labels, pull.UpdatedAt, holder.Owner, holder.Name, pull.Url, IsPullRequest: true);

    /// <summary>The issues it is blocked by, each in the same repository, as the world holds
    /// them.</summary>
    public Task<CodeHostResult<IReadOnlyList<Dependency>>> BlockedBy(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<Dependency>>(userId, repository, found =>
            found.Issues.FirstOrDefault(issue => issue.Number == number) is { } blocked
                ? [.. blocked.BlockedBy
                    .Select(blocker => found.Issues.First(issue => issue.Number == blocker))
                    .Select(blocker => new Dependency(found.Owner, found.Name, blocker.Number, blocker.Open, blocker.Url))]
                : null);

    /// <summary>The issue's edits as staged, its body's first and then its title's.</summary>
    public Task<CodeHostResult<IReadOnlyList<IssueEdit>>> Edits(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<IssueEdit>>(userId, repository, found =>
            found.Issues.FirstOrDefault(issue => issue.Number == number) is { } edited
                ? [.. edited.Edits.Where(edit => edit.Kind == EditKind.Body), .. edited.Edits.Where(edit => edit.Kind == EditKind.Title)]
                : null);

    /// <summary>The issue's label events, as <see cref="FakeIssue.LabelHistory"/> answers
    /// them.</summary>
    public Task<CodeHostResult<IReadOnlyList<LabelEvent>>> LabelEvents(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<LabelEvent>>(userId, repository, found => found.Issues.FirstOrDefault(issue => issue.Number == number)?.LabelHistory());

    /// <summary>The files the pull request changes, as its world lists them, none when it lists
    /// nothing for it; not found for a pull request the world does not hold.</summary>
    public Task<CodeHostResult<IReadOnlyList<ChangedFile>>> PullRequestFiles(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<ChangedFile>>(userId, repository, found => found.PullRequests.Any(pull => pull.Number == number)
            ? found.PullRequestFiles?.FirstOrDefault(files => files.Number == number)?.Files ?? []
            : null);

    /// <summary>The repository's default branch, as its world says, <see cref="DefaultBranchName"/>
    /// when it does not.</summary>
    public Task<CodeHostResult<string>> DefaultBranch(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Answer(userId, repository, found => found.Default);

    /// <summary>As GitHub compares two commits: unavailable when the account says so, not found
    /// for a repository the world does not hold or a commit its git repository does not; and
    /// otherwise the files <paramref name="head"/> changed since its merge base with
    /// <paramref name="from"/> in its git repository, by path, sorted, at most 300.</summary>
    public async Task<CodeHostResult<IReadOnlyList<string>>> ChangedFiles(Guid userId, Repository repository, string from, string head, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var account = Of(userId);
        if (account.Unavailable)
        {
            return new CodeHostResult<IReadOnlyList<string>>.Unavailable(RetryAt: null);
        }

        if (Find(account.World, repository) is null
            || !await HasCommit(userId, repository, from, cancellationToken)
            || !await HasCommit(userId, repository, head, cancellationToken))
        {
            return new CodeHostResult<IReadOnlyList<string>>.NotFound();
        }

        return await DiffNames(userId, repository, from, head, cancellationToken) is { } names
            ? new CodeHostResult<IReadOnlyList<string>>.Found([.. names.Take(ICodeHost.MostChangedFiles)])
            : new CodeHostResult<IReadOnlyList<string>>.Unavailable(RetryAt: null);
    }

    /// <summary>The branch's changes as the world lists them, newest first: with
    /// <paramref name="since"/> null at most the newest hundred, GitHub's one page; otherwise those
    /// after it, or all when it is not listed.</summary>
    public Task<CodeHostResult<IReadOnlyList<BranchActivity>>> Activity(Guid userId, Repository repository, string branch, long? since, CancellationToken cancellationToken) =>
        Answer<IReadOnlyList<BranchActivity>>(userId, repository, found =>
        {
            var newest = (found.Activity ?? [])
                .Where(activity => activity.Branch == branch)
                .Reverse()
                .Select(activity => new BranchActivity(activity.Id, activity.Before, activity.After, activity.At, activity.Kind, activity.Actor));
            return since is { } id ? [.. newest.TakeWhile(activity => activity.Id != id)] : [.. newest.Take(100)];
        });

    /// <summary>The app's bot, <see cref="AppLogin"/> with <see cref="AppEmail"/>, for a repository
    /// the world holds.</summary>
    public Task<CodeHostResult<Committer>> AppCommitter(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Answer(userId, repository, _ => new Committer(AppLogin, AppEmail));

    /// <summary>The world's installations, with the account's access token only, see
    /// <see cref="AsPerson{T}"/>.</summary>
    public Task<CodeHostResult<IReadOnlyList<Installation>>> Installations(Guid userId, string userToken, CancellationToken cancellationToken) =>
        AsPerson<IReadOnlyList<Installation>>(userId, userToken, world =>
            [.. world.Installations.Select(installation => new Installation(installation.Id, installation.Account))]);

    /// <summary>The installation's repositories, with the account's access token only; not found
    /// when the world holds no such installation.</summary>
    public Task<CodeHostResult<IReadOnlyList<Repository>>> Repositories(Guid userId, string userToken, long installation, CancellationToken cancellationToken) =>
        AsPerson<IReadOnlyList<Repository>>(userId, userToken, world =>
            world.Installations.FirstOrDefault(candidate => candidate.Id == installation) is { } found
                ? [.. found.Repositories.Select(repository => new Repository(found.Id, repository.Id, repository.Owner, repository.Name))]
                : null);

    /// <summary>The fake's own page, <see cref="InstallPath"/>, under the platform's origin.</summary>
    public Uri InstallAddress(Uri origin) => new(origin, InstallPath);

    /// <summary>
    /// Unavailable when the account says so, or when <paramref name="userToken"/> is not the access
    /// token its sign in gave and still works, which GitHub answers with <c>401</c> and the adapter
    /// as unavailable; otherwise what <paramref name="read"/> finds in the world, and not found
    /// when that is null. So a read as a person works only with that person's own token.
    /// </summary>
    private Task<CodeHostResult<T>> AsPerson<T>(Guid userId, string userToken, Func<FakeWorld, T?> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(userToken);
        var account = Of(userId);
        CodeHostResult<T> result = account.Unavailable || !TakesAccessToken(userId, userToken)
            ? new CodeHostResult<T>.Unavailable(RetryAt: null)
            : read(account.World) is { } value
                ? new CodeHostResult<T>.Found(value)
                : new CodeHostResult<T>.NotFound();
        return Task.FromResult(result);
    }

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

    /// <summary>The repository by its installation and id; its owner and name are display. A
    /// write finds it the same way, so a name that changed still reaches it.</summary>
    private static FakeRepository? Find(FakeWorld world, Repository repository) =>
        world.Installations
            .Where(installation => installation.Id == repository.Installation)
            .SelectMany(installation => installation.Repositories)
            .FirstOrDefault(candidate => candidate.Id == repository.Id);
}
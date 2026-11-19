using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace DevFlow.CodeHost;

/// <summary>
/// The fake's side of the app's writes, answered as GitHub answers them and kept in the account's
/// world, so the next read sees them. It issues its own keys, random, an hour long and recorded per
/// account, and takes a write only with one it issued for that account and repository and still
/// within its hour, refusing another as GitHub refuses a key it does not take; a key for another
/// purpose or repository is refused before anything is done, as the adapter refuses it. Every write
/// is recorded as the app's, by <see cref="AppLogin"/> with its key's purpose, see
/// <see cref="Writes"/>. Each repository also has a bare git repository of its own, in a temporary
/// folder, made when its clone address is first asked for, which a worker in a test fetches from
/// and pushes to for real, and which opening a pull request reads its branches from. See "The
/// fake" in devflow/README.md.
/// </summary>
public sealed partial class FakeCodeHost : IDisposable
{
    /// <summary>Who GitHub shows as the author of what the app writes, its slug and
    /// <c>[bot]</c>.</summary>
    public const string AppLogin = "devflow-fake[bot]";

    /// <summary>The no-reply address a worker commits as in the fake, in the form GitHub gives an
    /// app's bot, its id and its login.</summary>
    public const string AppEmail = "70000099+devflow-fake[bot]@users.noreply.github.com";

    /// <summary>What GitHub says with <c>404</c> for removing a label the issue does not carry.</summary>
    public const string LabelMissing = "Label does not exist";

    /// <summary>What GitHub says with <c>422</c> for a pull request it will not open.</summary>
    public const string ValidationFailed = "Validation Failed";

    /// <summary>How long a key the fake issues lasts, as GitHub's installation tokens do.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);

    /// <summary>How long one git command may take before the fake gives up on it.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A key the fake issued: the repository and purpose it is for, and when it ends.</summary>
    private sealed record IssuedKey(long Installation, long Repository, WritePurpose Purpose, DateTimeOffset ExpiresAt);

    /// <summary>Held while a bare repository is made, so two requests never make one twice.</summary>
    private readonly SemaphoreSlim _git = new(1, 1);

    /// <summary>The folder every bare repository of this fake is under, made with the first.</summary>
    private string? _gitRoot;

    /// <summary>Deletes the bare repositories with their folder.</summary>
    public void Dispose()
    {
        _git.Dispose();
        if (_gitRoot is { } root && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Unavailable when the account says so, not found for a repository the world does
    /// not hold, and otherwise a new key, recorded for the account and never given again.</summary>
    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        lock (_changing)
        {
            var account = Of(userId);
            CodeHostResult<AppWriteToken> result;
            if (account.Unavailable)
            {
                result = new CodeHostResult<AppWriteToken>.Unavailable(RetryAt: null);
            }
            else if (Find(account.World, repository) is null)
            {
                result = new CodeHostResult<AppWriteToken>.NotFound();
            }
            else
            {
                var value = $"fake_app_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20))}";
                var expiresAt = _time.GetUtcNow() + KeyLifetime;
                _accounts[userId] = account with
                {
                    Keys = account.Keys.SetItem(value, new IssuedKey(repository.Installation, repository.Id, purpose, expiresAt)),
                };
                result = new CodeHostResult<AppWriteToken>.Found(new AppWriteToken(value, repository, purpose, expiresAt));
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>The repository and purpose the fake issued <paramref name="key"/> for to the
    /// account, or null when it issued no such key; for a test to check what a key handed out
    /// reaches.</summary>
    internal (long Installation, long Repository, WritePurpose Purpose)? Issued(Guid userId, string key) =>
        Of(userId).Keys.TryGetValue(key, out var issued) ? (issued.Installation, issued.Repository, issued.Purpose) : null;

    /// <summary>The <c>file://</c> address of the repository's bare git repository, made empty,
    /// with <c>main</c> as its default branch, the first time it is asked for, and the same one
    /// after, kept when a world is staged again. Not found for a repository the world does not
    /// hold, and unavailable when the account says so or git fails.</summary>
    public async Task<CodeHostResult<Uri>> CloneAddress(Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var account = Of(userId);
        if (account.Unavailable)
        {
            return new CodeHostResult<Uri>.Unavailable(RetryAt: null);
        }

        if (Find(account.World, repository) is null)
        {
            return new CodeHostResult<Uri>.NotFound();
        }

        await _git.WaitAsync(cancellationToken);
        try
        {
            _gitRoot ??= Directory.CreateTempSubdirectory("devflow-fake-git-").FullName;
            var path = GitPath(userId, repository);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if ((await Git(cancellationToken, "init", "--quiet", "--bare", "--initial-branch=main", path)).Exit != 0)
                {
                    return new CodeHostResult<Uri>.Unavailable(RetryAt: null);
                }
            }

            return new CodeHostResult<Uri>.Found(new Uri(path));
        }
        finally
        {
            _git.Release();
        }
    }

    /// <summary>
    /// Makes a commit on <paramref name="branch"/> in the repository's git repository, as a worker's
    /// push would leave it, by <paramref name="committer"/>, on top of <paramref name="baseBranch"/>
    /// when it has a commit and as the first commit otherwise, with its tree unchanged; and points
    /// the branch at it, and any open pull request from the branch at it too, as a push would. The
    /// fake worker's, so a test's chain has a branch to open a pull request from. Its id, or null when the repository is not the world's or git fails.
    /// </summary>
    internal async Task<string?> CommitOnBranch(Guid userId, Repository repository, string branch, string baseBranch, string message, Committer committer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentNullException.ThrowIfNull(committer);
        if (await CloneAddress(userId, repository, cancellationToken) is not CodeHostResult<Uri>.Found)
        {
            return null;
        }

        await _git.WaitAsync(cancellationToken);
        try
        {
            string[] who = ["-c", $"user.name={committer.Name}", "-c", $"user.email={committer.Email}", "--git-dir", GitPath(userId, repository)];
            var parent = await BranchCommit(userId, repository, baseBranch, cancellationToken);
            var tree = parent is null
                ? await Git(cancellationToken, [.. who, "hash-object", "-t", "tree", "-w", "/dev/null"])
                : await Git(cancellationToken, [.. who, "rev-parse", $"{parent}^{{tree}}"]);
            if (tree.Exit != 0)
            {
                return null;
            }

            string[] commitTree = parent is null
                ? [.. who, "commit-tree", tree.Output.Trim(), "-m", message]
                : [.. who, "commit-tree", tree.Output.Trim(), "-p", parent, "-m", message];
            var commit = await Git(cancellationToken, commitTree);
            if (commit.Exit != 0)
            {
                return null;
            }

            var id = commit.Output.Trim();
            if ((await Git(cancellationToken, [.. who, "update-ref", $"refs/heads/{branch}", id])).Exit != 0)
            {
                return null;
            }

            // As GitHub does, an open pull request from the branch now has the commit as its head.
            Change(userId, account => Find(account.World, repository) is { } found
                ? account with
                {
                    World = WithRepository(account.World, repository, found with
                    {
                        PullRequests = [.. found.PullRequests.Select(pull => pull.HeadBranch == branch && pull.State == PullRequestState.Open ? pull with { HeadCommit = id } : pull)],
                    }),
                }
                : account);
            return id;
        }
        finally
        {
            _git.Release();
        }
    }

    /// <summary>Whether the repository's git repository holds the commit with the full id
    /// <paramref name="commit"/>; false before its git repository is made, and for anything that is
    /// not a full id, so nothing else reaches git as an argument.</summary>
    internal async Task<bool> HasCommit(Guid userId, Repository repository, string commit, CancellationToken cancellationToken)
    {
        if (_gitRoot is null || !FullId().IsMatch(commit) || !Directory.Exists(GitPath(userId, repository)))
        {
            return false;
        }

        return (await Git(cancellationToken, "--git-dir", GitPath(userId, repository), "cat-file", "-e", $"{commit}^{{commit}}")).Exit == 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FullId();

    /// <summary>
    /// Puts <paramref name="content"/> at <paramref name="path"/> on <paramref name="branch"/> in
    /// the repository's world, replacing a file there, so <see cref="File"/> answers it: what a
    /// worker's push of its pictures to <c>pr-screenshots</c> leaves for the page to read, for the
    /// fake worker. False when the world does not hold the repository.
    /// </summary>
    public bool PutFile(Guid userId, Repository repository, string path, string branch, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(content);
        lock (_changing)
        {
            var account = Of(userId);
            if (Find(account.World, repository) is not { } found)
            {
                return false;
            }

            var files = found.Files.Where(file => file.Path != path || file.Branch != branch).Append(new FakeFile(path, branch, [.. content])).ToList();
            _accounts[userId] = account with { World = WithRepository(account.World, repository, found with { Files = files }) };
            return true;
        }
    }

    /// <summary>Whether the repository's git repository has <paramref name="branch"/>, with a
    /// commit; false before its git repository is made.</summary>
    internal async Task<bool> HasBranch(Guid userId, Repository repository, string branch, CancellationToken cancellationToken) =>
        await BranchCommit(userId, repository, branch, cancellationToken) is not null;

    /// <summary>Refused with <c>422</c> unless both branches are in the repository's git
    /// repository and no open pull request is already from the head into the base, as GitHub
    /// refuses; otherwise opened by the app with the next number, its head the branch's commit.</summary>
    public async Task<WriteResult<PullRequest>> OpenPullRequest(Guid userId, AppWriteToken token, Repository repository, string headBranch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(WritePurpose.Post, repository);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        var head = await BranchCommit(userId, repository, headBranch, cancellationToken);
        var hasBase = await BranchCommit(userId, repository, baseBranch, cancellationToken) is not null;
        return AsApp<WriteResult<PullRequest>>(userId, token, repository, Unavailable<PullRequest>(), Refused<PullRequest>, found =>
        {
            if (head is null || !hasBase || found.PullRequests.Any(pull =>
                pull.State == PullRequestState.Open && pull.HeadBranch == headBranch && pull.BaseBranch == baseBranch))
            {
                return (Refused<PullRequest>(StatusCodes.Status422UnprocessableEntity, ValidationFailed), null, null);
            }

            var number = NextNumber(found);
            var pull = new PullRequest(number, title, AppLogin, [], head, headBranch, baseBranch, Draft: false, body,
                WebAddress(found, $"pull/{number}"), _time.GetUtcNow(), PullRequestState.Open);
            return (new WriteResult<PullRequest>.Written(pull), found with { PullRequests = [.. found.PullRequests, pull] },
                new FakeWrite("pull_request", repository.Id, number, head, null, body, AppLogin, Post, Title: title, Head: headBranch, Base: baseBranch));
        });
    }

    /// <summary>Refused with <c>404</c> for a number with neither a pull request, an issue nor a
    /// conversation; otherwise posted by the app at the end of the conversation.</summary>
    public Task<WriteResult<Comment>> CommentOnIssue(Guid userId, AppWriteToken token, Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(WritePurpose.Post, repository);
        ArgumentNullException.ThrowIfNull(body);
        return Task.FromResult(AsApp<WriteResult<Comment>>(userId, token, repository, Unavailable<Comment>(), Refused<Comment>, found =>
        {
            if (!Holds(found, number))
            {
                return (Refused<Comment>(StatusCodes.Status404NotFound, NotFoundMessage), null, null);
            }

            var (changed, comment) = WithComment(found, number, AppLogin, body);
            return (new WriteResult<Comment>.Written(comment), changed,
                new FakeWrite("comment", repository.Id, number, null, null, body, AppLogin, Post));
        }));
    }

    /// <summary>Refused with <c>404</c> for a number with neither a pull request nor an issue;
    /// otherwise the label is added, unless it is there already, as GitHub adds.</summary>
    public Task<WriteResult<IReadOnlyList<string>>> AddLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken) =>
        Label(userId, token, repository, number, label, add: true);

    /// <summary>Refused with <c>404</c> for a number with neither a pull request nor an issue, or
    /// a label it does not carry, <see cref="LabelMissing"/>; otherwise the label is taken off.</summary>
    public Task<WriteResult<IReadOnlyList<string>>> RemoveLabel(Guid userId, AppWriteToken token, Repository repository, int number, string label, CancellationToken cancellationToken) =>
        Label(userId, token, repository, number, label, add: false);

    /// <summary>Opened by the app with the next number and the labels, open, blocked by nothing and
    /// never edited, in the world's issues.</summary>
    public Task<WriteResult<Issue>> OpenIssue(Guid userId, AppWriteToken token, Repository repository, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(WritePurpose.Post, repository);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);
        return Task.FromResult(AsApp<WriteResult<Issue>>(userId, token, repository, Unavailable<Issue>(), Refused<Issue>, found =>
        {
            var number = NextNumber(found);
            var issue = new FakeIssue(number, title, AppLogin, [.. labels.Distinct(StringComparer.Ordinal)], body, WebAddress(found, $"issues/{number}"), _time.GetUtcNow(), Open: true, BlockedBy: [], Edits: []);
            return (new WriteResult<Issue>.Written(new Issue(issue.Number, issue.Title, issue.Author, issue.Labels, issue.Body, issue.Url, issue.CreatedAt, IsPullRequest: false)),
                found with { Issues = [.. found.Issues, issue] },
                new FakeWrite("issue", repository.Id, number, null, null, body, AppLogin, Post, Title: title));
        }));
    }

    /// <summary>As the owner's <see cref="Merge"/>: refused with <c>404</c> for a pull request the
    /// world does not hold, not mergeable unless it is open, head moved unless its head is
    /// <paramref name="commit"/>, and otherwise merged, by the app, landing on the default branch
    /// as <see cref="WithMerge"/> says. Its git repository is not changed.</summary>
    public Task<MergeResult> MergeAsApp(Guid userId, AppWriteToken token, Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(WritePurpose.Merge, repository);
        ArgumentNullException.ThrowIfNull(commit);
        return Task.FromResult(AsApp<MergeResult>(userId, token, repository, new MergeResult.Unavailable(RetryAt: null), (status, message) => new MergeResult.Refused(status, message), found =>
        {
            if (found.PullRequests.FirstOrDefault(pull => pull.Number == number) is not { } pull)
            {
                return (new MergeResult.Refused(StatusCodes.Status404NotFound, NotFoundMessage), null, null);
            }

            if (pull.State != PullRequestState.Open)
            {
                return (new MergeResult.NotMergeable(NotMergeableMessage), null, null);
            }

            if (!string.Equals(pull.HeadCommit, commit, StringComparison.Ordinal))
            {
                return (new MergeResult.HeadMoved(), null, null);
            }

            var (changed, mergeCommit) = WithMerge(found, number, AppLogin);
            return (new MergeResult.Merged(mergeCommit), changed,
                new FakeWrite("merge", repository.Id, number, commit, "merge", null, AppLogin, "merge"));
        }));
    }

    /// <summary>The purpose a post key's writes are recorded with.</summary>
    private const string Post = "post";

    private Task<WriteResult<IReadOnlyList<string>>> Label(Guid userId, AppWriteToken token, Repository repository, int number, string label, bool add)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Require(WritePurpose.Post, repository);
        ArgumentException.ThrowIfNullOrEmpty(label);
        return Task.FromResult(AsApp<WriteResult<IReadOnlyList<string>>>(userId, token, repository, Unavailable<IReadOnlyList<string>>(), Refused<IReadOnlyList<string>>, found =>
        {
            var pull = found.PullRequests.FirstOrDefault(each => each.Number == number);
            var issue = found.Issues.FirstOrDefault(each => each.Number == number);
            if ((pull?.Labels ?? issue?.Labels) is not { } labels)
            {
                return (Refused<IReadOnlyList<string>>(StatusCodes.Status404NotFound, NotFoundMessage), null, null);
            }

            if (!add && !labels.Contains(label, StringComparer.Ordinal))
            {
                return (Refused<IReadOnlyList<string>>(StatusCodes.Status404NotFound, LabelMissing), null, null);
            }

            IReadOnlyList<string> after = add
                ? labels.Contains(label, StringComparer.Ordinal) ? labels : [.. labels, label]
                : [.. labels.Where(each => each != label)];
            var changed = pull is not null
                ? found with { PullRequests = [.. found.PullRequests.Select(each => each.Number == number ? each with { Labels = after } : each)] }
                : found with { Issues = [.. found.Issues.Select(each => each.Number == number ? each with { Labels = after } : each)] };
            return (new WriteResult<IReadOnlyList<string>>.Written(after), changed,
                new FakeWrite(add ? "label_added" : "label_removed", repository.Id, number, null, null, null, AppLogin, Post, Label: label));
        }));
    }

    /// <summary>
    /// One of the app's writes, under the lock: unavailable when the account says so, refused as
    /// GitHub refuses a key it does not take, <c>401</c>, unless the account's ledger holds
    /// <paramref name="token"/> for this repository and purpose and it has not expired, refused
    /// with <c>404</c> for a repository the world does not hold, and otherwise what
    /// <paramref name="write"/> answers. Its changed repository, when it gives one, replaces the
    /// world's, and its write is recorded.
    /// </summary>
    private TResult AsApp<TResult>(
        Guid userId,
        AppWriteToken token,
        Repository repository,
        TResult unavailable,
        Func<int, string, TResult> refused,
        Func<FakeRepository, (TResult Result, FakeRepository? Changed, FakeWrite? Write)> write)
    {
        lock (_changing)
        {
            var account = Of(userId);
            if (account.Unavailable)
            {
                return unavailable;
            }

            if (!account.Keys.TryGetValue(token.Value, out var issued)
                || issued.Installation != repository.Installation
                || issued.Repository != repository.Id
                || issued.Purpose != token.Purpose
                || _time.GetUtcNow() >= issued.ExpiresAt)
            {
                return refused(StatusCodes.Status401Unauthorized, BadCredentials);
            }

            if (Find(account.World, repository) is not { } found)
            {
                return refused(StatusCodes.Status404NotFound, NotFoundMessage);
            }

            var (result, changed, recorded) = write(found);
            if (changed is not null && recorded is not null)
            {
                _accounts[userId] = account with
                {
                    World = WithRepository(account.World, repository, changed),
                    Writes = [.. account.Writes, recorded],
                };
            }

            return result;
        }
    }

    private static WriteResult<T> Unavailable<T>() => new WriteResult<T>.Unavailable(RetryAt: null);

    private static WriteResult<T> Refused<T>(int status, string message) => new WriteResult<T>.Refused(status, message);

    /// <summary>Whether the number is a pull request, an issue or a conversation of the
    /// repository.</summary>
    private static bool Holds(FakeRepository repository, int number) =>
        repository.PullRequests.Any(pull => pull.Number == number)
        || repository.Issues.Any(issue => issue.Number == number)
        || repository.Conversations.Any(conversation => conversation.Number == number);

    /// <summary>The number after every pull request's, issue's and conversation's, which share
    /// their numbers as GitHub's do.</summary>
    private static int NextNumber(FakeRepository repository) =>
        repository.PullRequests.Select(pull => pull.Number)
            .Concat(repository.Issues.Select(issue => issue.Number))
            .Concat(repository.Conversations.Select(conversation => conversation.Number))
            .DefaultIfEmpty(0).Max() + 1;

    /// <summary>Where a person would open it on GitHub, from the repository's owner and name.</summary>
    private static Uri WebAddress(FakeRepository repository, string rest) =>
        new($"https://github.com/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/{rest}");

    /// <summary>The commit the branch points at in the repository's git repository, or null when
    /// it has none, or no git repository has been made for it.</summary>
    private async Task<string?> BranchCommit(Guid userId, Repository repository, string branch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        if (_gitRoot is null || !Directory.Exists(GitPath(userId, repository)))
        {
            return null;
        }

        var (exit, output) = await Git(cancellationToken, "--git-dir", GitPath(userId, repository), "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");
        return exit == 0 ? output.Trim() : null;
    }

    /// <summary>The bare repository's folder, per account and repository, by installation and id.</summary>
    private string GitPath(Guid userId, Repository repository) =>
        Path.Combine(_gitRoot!, userId.ToString("N"), string.Create(CultureInfo.InvariantCulture, $"{repository.Installation}-{repository.Id}.git"));

    /// <summary>git with <paramref name="arguments"/>, never through a shell, never asking for a
    /// password, and given <see cref="GitTimeout"/>: its exit code, or <c>-1</c> when it could not
    /// run or took longer, and what it printed.</summary>
    private static async Task<(int Exit, string Output)> Git(CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        try
        {
            using var git = Process.Start(start);
            if (git is null)
            {
                return (-1, "");
            }

            var output = git.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = git.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await git.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                git.Kill(entireProcessTree: true);
                return (-1, "");
            }

            await error;
            return (git.ExitCode, await output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git on the machine.
            return (-1, "");
        }
    }
}
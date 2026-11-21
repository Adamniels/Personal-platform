using System.Collections.Concurrent;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Settings;
using DevFlow.Tests.Runner;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Reading;

/// <summary>
/// A migrated database of its own, the door to it as devflow_app, and the fake code host behind a
/// <see cref="CountingCodeHost"/>, for the tests of reading a project into the snapshot. Projects
/// and links are put straight into the tables through the door, as connecting and linking would
/// leave them; what was stored is read around row level security.
/// </summary>
internal sealed class Reading : IAsyncDisposable
{
    public const long Installation = 41;
    public const long RepositoryId = 9001;

    public static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    public static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private Reading(TestDatabase database, UserDb door, FakeTimeProvider time, FakeCodeHost fake)
    {
        Database = database;
        Door = door;
        Time = time;
        Fake = fake;
        Host = new CountingCodeHost(fake);
        Reader = new ProjectReader(door, Host, time, NullLogger<ProjectReader>.Instance);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestDatabase Database { get; }

    public UserDb Door { get; }

    public FakeTimeProvider Time { get; }

    public FakeCodeHost Fake { get; }

    public CountingCodeHost Host { get; }

    public ProjectReader Reader { get; }

    public static async Task<Reading> Start()
    {
        var database = await Databases.DevFlow.Migrated(Ct);
        var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        var time = Harness.Clock();
        return new Reading(database, door, time, new FakeCodeHost(time));
    }

    /// <summary>A project of <paramref name="user"/>'s, through the door as them, as connecting
    /// stores one.</summary>
    public Task<Guid> Project(Guid user, long repository = RepositoryId, string name = "Personal-platform") =>
        InsertProject(Door, user, repository, name);

    /// <inheritdoc cref="Project"/>
    public static async Task<Guid> InsertProject(UserDb door, Guid user, long repository = RepositoryId, string name = "Personal-platform")
    {
        var id = Guid.CreateVersion7();
        await door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
            VALUES (@id, @user, 'github', @installation, @repository, 'Adamniels', @name, now())
            """,
            new { id, user, installation = Installation, repository, name },
            transaction,
            cancellationToken: Ct)), Ct);
        return id;
    }

    /// <summary>Links <paramref name="user"/>'s GitHub account as <paramref name="login"/>, in
    /// <paramref name="state"/>, as linking leaves the row; no token is needed to read.</summary>
    public Task LinkOwner(Guid user, string login, string state = "linked") => Link(Door, user, login, state);

    /// <inheritdoc cref="LinkOwner"/>
    public static async Task Link(UserDb door, Guid user, string login, string state = "linked") =>
        await door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO github_accounts (user_id, login, github_user_id, state, access_expires_at, refresh_expires_at, linked_at)
            VALUES (@user, @login, 70000001, @state, now() + interval '8 hours', now() + interval '180 days', now())
            """,
            new { user, login, state },
            transaction,
            cancellationToken: Ct)), Ct);

    /// <summary>Stages <paramref name="pulls"/> in <paramref name="user"/>'s world, in one
    /// repository, <see cref="RepositoryId"/> unless given, each with its conversation and its
    /// head's checks, and the slice issues' conversations.</summary>
    public void Stage(Guid user, IReadOnlyList<Staged> pulls, IReadOnlyList<FakeConversation>? slices = null, params long[] more) =>
        Fake.Replace(user, Staging(pulls, slices, more));

    /// <summary>A world holding <paramref name="pulls"/> in <see cref="RepositoryId"/>, and an
    /// empty repository for each of <paramref name="more"/>.</summary>
    public static FakeWorld Staging(IReadOnlyList<Staged> pulls, IReadOnlyList<FakeConversation>? slices = null, params long[] more) =>
        new([
            new FakeInstallation(Installation, "Adamniels",
            [
                new FakeRepository(
                    RepositoryId,
                    "Adamniels",
                    "Personal-platform",
                    [.. pulls.Select(p => p.Pull)],
                    [.. pulls.Select(p => new FakeConversation(p.Pull.Number, p.Comments)), .. slices ?? []],
                    [.. pulls.Where(p => p.Statuses.Count > 0 || p.CheckRuns.Count > 0).Select(p => new FakeCommit(p.Pull.HeadCommit, p.Statuses, p.CheckRuns))],
                    [],
                    []),
                .. more.Select(id => new FakeRepository(id, "Adamniels", $"repository-{id}", [], [], [], [], [])),
            ]),
        ]);

    /// <summary>The runner's world as a pull request staged at the fake.</summary>
    public static Staged From(World world) => new(world.Pull, world.Comments, world.Statuses, world.CheckRuns);

    /// <summary>The conversation of the slice <see cref="World"/>'s pull requests close.</summary>
    public static FakeConversation SliceOf(World world) => new(World.SliceNumber, world.SliceComments);

    /// <summary>The project's stored items, by number, read around row level security.</summary>
    public Task<List<ItemRow>> Items(Guid project) => StoredItems(Database, project);

    /// <inheritdoc cref="Items"/>
    public static async Task<List<ItemRow>> StoredItems(TestDatabase database, Guid project)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<ItemRow>(new CommandDefinition(
            """
            SELECT id AS Id, user_id AS UserId, number AS Number, kind AS Kind, sent_back AS SentBack, title AS Title,
                   web_url AS WebUrl, slice_number AS SliceNumber, head_commit AS HeadCommit, can_merge AS CanMerge,
                   summary AS Summary, reason AS Reason, decisions::text AS Decisions, other_decisions AS OtherDecisions,
                   reviewer_view::text AS ReviewerView, screenshots::text AS Screenshots, read_at AS ReadAt
            FROM waiting_items WHERE project_id = @project ORDER BY number
            """,
            new { project },
            cancellationToken: Ct))];
    }

    /// <summary>How the project's reads went, read around row level security, or null before
    /// any.</summary>
    public Task<ReadRow?> ReadOf(Guid project) => StoredRead(Database, project);

    /// <inheritdoc cref="ReadOf"/>
    public static async Task<ReadRow?> StoredRead(TestDatabase database, Guid project)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await admin.QuerySingleOrDefaultAsync<ReadRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, last_attempt_at AS LastAttemptAt, last_success_at AS LastSuccessAt, last_error AS LastError
            FROM project_reads WHERE project_id = @project
            """,
            new { project },
            cancellationToken: Ct));
    }

    /// <summary>Waits, in real time, until <paramref name="condition"/> holds, for at most ten
    /// seconds.</summary>
    public static async Task Until(Func<Task<bool>> condition, string what)
    {
        for (var tries = 0; tries < 200; tries++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"waited ten seconds and {what} never happened");
    }

    public async ValueTask DisposeAsync()
    {
        await Door.DisposeAsync();
        await Database.DisposeAsync();
    }

    internal sealed record ItemRow(
        Guid Id,
        Guid UserId,
        int Number,
        string Kind,
        bool SentBack,
        string Title,
        string WebUrl,
        int? SliceNumber,
        string HeadCommit,
        bool CanMerge,
        string Summary,
        string Reason,
        string Decisions,
        int OtherDecisions,
        string ReviewerView,
        string Screenshots,
        DateTime ReadAt);

    internal sealed record ReadRow(Guid UserId, DateTime LastAttemptAt, DateTime? LastSuccessAt, string? LastError);
}

/// <summary>A pull request as staged at the fake: itself, its conversation and its head's checks.</summary>
internal sealed record Staged(PullRequest Pull, IReadOnlyList<Comment> Comments, IReadOnlyList<CommitStatus> Statuses, IReadOnlyList<CheckRun> CheckRuns);

/// <summary>
/// Stands between Dev flow and the fake: records every call, as the operation, the user it was
/// asked for and what it named, and can throw for a repository, hold the list of open pull
/// requests until a test lets it go, or run a test's step as the app's committer is read.
/// </summary>
internal sealed class CountingCodeHost(ICodeHost inner) : ICodeHost
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentQueue<UserAccessToken> _writeTokens = new();
    private Func<Task>? _beforeCommitter;
    private Func<Task>? _beforeMergeAsApp;
    private Func<Task>? _beforeBringIn;
    private Func<Task>? _afterFeatureActivity;
    private int _listing;
    private int _mostAtOnce;

    /// <summary>Every call, in the order made.</summary>
    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary>The repositories whose every read throws, as a bug would.</summary>
    public ConcurrentDictionary<long, bool> Throws { get; } = new();

    /// <summary>When set, listing open pull requests says it has started, then waits for it.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completed each time a list of open pull requests starts; replaced by the test.</summary>
    public TaskCompletionSource ListStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, run once as the app's committer is next read, before it is answered: the
    /// last thing handing out a job asks the code host, so a test can change the world between
    /// the hold read first and the card being taken.</summary>
    public Func<Task>? BeforeCommitter
    {
        get => Volatile.Read(ref _beforeCommitter);
        set => Volatile.Write(ref _beforeCommitter, value);
    }

    /// <summary>The operations, by name, that answer unavailable, as GitHub not answering one read
    /// would, while the rest answer.</summary>
    public ConcurrentDictionary<string, bool> Unavailable { get; } = new(StringComparer.Ordinal);

    /// <summary>The branches whose changes answer unavailable, as GitHub not answering for one
    /// branch would, while every other read answers.</summary>
    public ConcurrentDictionary<string, bool> UnavailableBranches { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, run once as the app next merges, before the code host is asked: so a
    /// test can move a pull request's head between the merge check's read and its merge.</summary>
    public Func<Task>? BeforeMergeAsApp
    {
        get => Volatile.Read(ref _beforeMergeAsApp);
        set => Volatile.Write(ref _beforeMergeAsApp, value);
    }

    /// <summary>When set, run once after a feature's branch's changes are next listed, before they
    /// are answered: so a test can change the world between a watch listing the branch and storing
    /// what it read.</summary>
    public Func<Task>? AfterFeatureActivity
    {
        get => Volatile.Read(ref _afterFeatureActivity);
        set => Volatile.Write(ref _afterFeatureActivity, value);
    }

    /// <summary>When set, run once as the app next brings a branch in, before the code host is
    /// asked: so a test can read the project, the watch among it, while the bring in is under
    /// way.</summary>
    public Func<Task>? BeforeBringIn
    {
        get => Volatile.Read(ref _beforeBringIn);
        set => Volatile.Write(ref _beforeBringIn, value);
    }

    /// <summary>When set, opening a pull request answers that the host did not answer, and opens
    /// nothing.</summary>
    public bool OpeningUnavailable { get; set; }

    /// <summary>When set, opening an issue opens it, and then answers that the host did not answer,
    /// as an answer lost on the way would.</summary>
    public bool OpeningIssueLost { get; set; }

    /// <summary>The most lists of open pull requests that were ever under way at once.</summary>
    public int MostListsAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>How many times the open pull requests were listed for the user.</summary>
    public int Lists(Guid? user = null) => Calls.Count(call => call.Operation == "OpenPullRequests" && (user is null || call.User == user));

    public async Task<CodeHostResult<IReadOnlyList<PullRequest>>> OpenPullRequests(Guid userId, CodeHost.Repository repository, CancellationToken cancellationToken)
    {
        Record("OpenPullRequests", userId, repository, "");
        var now = Interlocked.Increment(ref _listing);
        try
        {
            int seen;
            while (now > (seen = Volatile.Read(ref _mostAtOnce)) && Interlocked.CompareExchange(ref _mostAtOnce, now, seen) != seen)
            {
            }

            ListStarted.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return await inner.OpenPullRequests(userId, repository, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _listing);
        }
    }

    public Task<CodeHostResult<PullRequest>> PullRequest(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("PullRequest", userId, repository, $"{number}");
        if (Unavailable.ContainsKey("PullRequest"))
        {
            return Task.FromResult<CodeHostResult<PullRequest>>(new CodeHostResult<PullRequest>.Unavailable(RetryAt: null));
        }

        return inner.PullRequest(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<Comment>>> Comments(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("Comments", userId, repository, $"{number}");
        return inner.Comments(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<CommitStatus>>> Statuses(Guid userId, CodeHost.Repository repository, string commit, CancellationToken cancellationToken)
    {
        Record("Statuses", userId, repository, commit);
        if (Unavailable.ContainsKey("Statuses"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<CommitStatus>>>(new CodeHostResult<IReadOnlyList<CommitStatus>>.Unavailable(RetryAt: null));
        }

        return inner.Statuses(userId, repository, commit, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<CheckRun>>> CheckRuns(Guid userId, CodeHost.Repository repository, string commit, CancellationToken cancellationToken)
    {
        Record("CheckRuns", userId, repository, commit);
        if (Unavailable.ContainsKey("CheckRuns"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<CheckRun>>>(new CodeHostResult<IReadOnlyList<CheckRun>>.Unavailable(RetryAt: null));
        }

        return inner.CheckRuns(userId, repository, commit, cancellationToken);
    }

    public Task<CodeHostResult<byte[]>> File(Guid userId, CodeHost.Repository repository, string path, string branch, CancellationToken cancellationToken)
    {
        Record("File", userId, repository, $"{path} {branch}");
        return inner.File(userId, repository, path, branch, cancellationToken);
    }

    public Task<CodeHostResult<FileAtCommit>> DefaultBranchFile(Guid userId, CodeHost.Repository repository, string path, CancellationToken cancellationToken)
    {
        Record("DefaultBranchFile", userId, repository, path);
        return inner.DefaultBranchFile(userId, repository, path, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<Issue>>> LabelledIssues(Guid userId, CodeHost.Repository repository, string label, CancellationToken cancellationToken)
    {
        Record("LabelledIssues", userId, repository, label);
        if (Unavailable.ContainsKey("LabelledIssues"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<Issue>>>(new CodeHostResult<IReadOnlyList<Issue>>.Unavailable(RetryAt: null));
        }

        return inner.LabelledIssues(userId, repository, label, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<SubIssue>>> SubIssues(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("SubIssues", userId, repository, $"{number}");
        if (Unavailable.ContainsKey("SubIssues"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<SubIssue>>>(new CodeHostResult<IReadOnlyList<SubIssue>>.Unavailable(RetryAt: null));
        }

        return inner.SubIssues(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<Dependency>>> BlockedBy(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("BlockedBy", userId, repository, $"{number}");
        return inner.BlockedBy(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<IssueEdit>>> Edits(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("Edits", userId, repository, $"{number}");
        if (Unavailable.ContainsKey("Edits"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<IssueEdit>>>(new CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable(RetryAt: null));
        }

        return inner.Edits(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<LabelEvent>>> LabelEvents(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("LabelEvents", userId, repository, $"{number}");
        if (Unavailable.ContainsKey("LabelEvents"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<LabelEvent>>>(new CodeHostResult<IReadOnlyList<LabelEvent>>.Unavailable(RetryAt: null));
        }

        return inner.LabelEvents(userId, repository, number, cancellationToken);
    }

    public Task<CodeHostResult<string>> DefaultBranch(Guid userId, CodeHost.Repository repository, CancellationToken cancellationToken)
    {
        Record("DefaultBranch", userId, repository, "");
        return inner.DefaultBranch(userId, repository, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<string>>> ChangedFiles(Guid userId, CodeHost.Repository repository, string from, string head, CancellationToken cancellationToken)
    {
        Record("ChangedFiles", userId, repository, $"{from}...{head}");
        return inner.ChangedFiles(userId, repository, from, head, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<ChangedFile>>> PullRequestFiles(Guid userId, CodeHost.Repository repository, int number, CancellationToken cancellationToken)
    {
        Record("PullRequestFiles", userId, repository, $"{number}");
        if (Unavailable.ContainsKey("PullRequestFiles"))
        {
            return Task.FromResult<CodeHostResult<IReadOnlyList<ChangedFile>>>(new CodeHostResult<IReadOnlyList<ChangedFile>>.Unavailable(RetryAt: null));
        }

        return inner.PullRequestFiles(userId, repository, number, cancellationToken);
    }

    public async Task<CodeHostResult<IReadOnlyList<BranchActivity>>> Activity(Guid userId, CodeHost.Repository repository, string branch, long? since, CancellationToken cancellationToken)
    {
        Record("Activity", userId, repository, $"{branch} {since}");
        if (UnavailableBranches.ContainsKey(branch))
        {
            return new CodeHostResult<IReadOnlyList<BranchActivity>>.Unavailable(RetryAt: null);
        }

        var listed = await inner.Activity(userId, repository, branch, since, cancellationToken);
        if (branch.StartsWith("feature-", StringComparison.Ordinal) && Interlocked.Exchange(ref _afterFeatureActivity, null) is { } step)
        {
            await step();
        }

        return listed;
    }

    public async Task<CodeHostResult<Committer>> AppCommitter(Guid userId, CodeHost.Repository repository, CancellationToken cancellationToken)
    {
        Record("AppCommitter", userId, repository, "");
        if (Interlocked.Exchange(ref _beforeCommitter, null) is { } step)
        {
            await step();
        }

        return await inner.AppCommitter(userId, repository, cancellationToken);
    }

    public Task<CodeHostResult<IReadOnlyList<Installation>>> Installations(Guid userId, string userToken, CancellationToken cancellationToken) =>
        inner.Installations(userId, userToken, cancellationToken);

    public Task<CodeHostResult<IReadOnlyList<CodeHost.Repository>>> Repositories(Guid userId, string userToken, long installation, CancellationToken cancellationToken) =>
        inner.Repositories(userId, userToken, installation, cancellationToken);

    /// <summary>How many writes, merges and comments, were asked of the code host.</summary>
    public int Writes => Calls.Count(call => call.Operation is "Merge" or "Comment");

    /// <summary>The token each write was asked with, in the order asked.</summary>
    public IReadOnlyList<UserAccessToken> WriteTokens => [.. _writeTokens];

    public Task<MergeResult> Merge(Guid userId, UserAccessToken token, CodeHost.Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        _writeTokens.Enqueue(token);
        Record("Merge", userId, repository, $"{number} {commit}");
        return inner.Merge(userId, token, repository, number, commit, cancellationToken);
    }

    public Task<CommentResult> Comment(Guid userId, UserAccessToken token, CodeHost.Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        _writeTokens.Enqueue(token);
        Record("Comment", userId, repository, $"{number}");
        return inner.Comment(userId, token, repository, number, body, cancellationToken);
    }

    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, CodeHost.Repository repository, WritePurpose purpose, CancellationToken cancellationToken)
    {
        Record("ForWrite", userId, repository, $"{purpose}");
        return inner.ForWrite(userId, repository, purpose, cancellationToken);
    }

    public Task<CodeHostResult<Uri>> CloneAddress(Guid userId, CodeHost.Repository repository, CancellationToken cancellationToken)
    {
        Record("CloneAddress", userId, repository, "");
        return inner.CloneAddress(userId, repository, cancellationToken);
    }

    public Task<BranchResult> MakeBranch(Guid userId, AppWriteToken token, CodeHost.Repository repository, string name, string from, CancellationToken cancellationToken)
    {
        Record("MakeBranch", userId, repository, $"{name} {from}");
        return inner.MakeBranch(userId, token, repository, name, from, cancellationToken);
    }

    public Task<WriteResult<PullRequest>> OpenPullRequest(Guid userId, AppWriteToken token, CodeHost.Repository repository, string headBranch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        Record("OpenPullRequest", userId, repository, $"{headBranch} {baseBranch}");
        if (OpeningUnavailable)
        {
            return Task.FromResult<WriteResult<PullRequest>>(new WriteResult<PullRequest>.Unavailable(RetryAt: null));
        }

        return inner.OpenPullRequest(userId, token, repository, headBranch, baseBranch, title, body, cancellationToken);
    }

    public Task<WriteResult<Comment>> CommentOnIssue(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        Record("CommentOnIssue", userId, repository, $"{number}");
        return inner.CommentOnIssue(userId, token, repository, number, body, cancellationToken);
    }

    public Task<WriteResult<IReadOnlyList<string>>> AddLabel(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, string label, CancellationToken cancellationToken)
    {
        Record("AddLabel", userId, repository, $"{number} {label}");
        return inner.AddLabel(userId, token, repository, number, label, cancellationToken);
    }

    public Task<WriteResult<IReadOnlyList<string>>> RemoveLabel(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, string label, CancellationToken cancellationToken)
    {
        Record("RemoveLabel", userId, repository, $"{number} {label}");
        return inner.RemoveLabel(userId, token, repository, number, label, cancellationToken);
    }

    public Task<WriteResult<Issue>> SetIssueState(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, bool open, CancellationToken cancellationToken)
    {
        Record("SetIssueState", userId, repository, $"{number} {(open ? "open" : "closed")}");
        return inner.SetIssueState(userId, token, repository, number, open, cancellationToken);
    }

    public async Task<WriteResult<Issue>> OpenIssue(Guid userId, AppWriteToken token, CodeHost.Repository repository, string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        Record("OpenIssue", userId, repository, "");
        var opened = await inner.OpenIssue(userId, token, repository, title, body, labels, cancellationToken);
        return OpeningIssueLost ? new WriteResult<Issue>.Unavailable(RetryAt: null) : opened;
    }

    public async Task<MergeResult> MergeAsApp(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, string commit, CancellationToken cancellationToken)
    {
        Record("MergeAsApp", userId, repository, $"{number} {commit}");
        if (Interlocked.Exchange(ref _beforeMergeAsApp, null) is { } step)
        {
            await step();
        }

        return await inner.MergeAsApp(userId, token, repository, number, commit, cancellationToken);
    }

    public Task<WriteResult<PullRequest>> EditPullRequestBody(Guid userId, AppWriteToken token, CodeHost.Repository repository, int number, string body, CancellationToken cancellationToken)
    {
        Record("EditPullRequestBody", userId, repository, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return inner.EditPullRequestBody(userId, token, repository, number, body, cancellationToken);
    }

    public async Task<BringInResult> BringIn(Guid userId, AppWriteToken token, CodeHost.Repository repository, string baseBranch, string head, string message, CancellationToken cancellationToken)
    {
        Record("BringIn", userId, repository, $"{baseBranch} {head}");
        if (Interlocked.Exchange(ref _beforeBringIn, null) is { } step)
        {
            await step();
        }

        return await inner.BringIn(userId, token, repository, baseBranch, head, message, cancellationToken);
    }

    public Uri InstallAddress(Uri origin) => inner.InstallAddress(origin);

    private void Record(string operation, Guid userId, CodeHost.Repository repository, string argument)
    {
        _calls.Enqueue(new Call(operation, userId, repository.Id, argument));
        if (Throws.ContainsKey(repository.Id))
        {
            throw new InvalidOperationException("a bug in reading, with the code host's text: secret-from-the-host");
        }
    }

    internal sealed record Call(string Operation, Guid User, long Repository, string Argument);
}
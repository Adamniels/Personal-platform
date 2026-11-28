using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Link;
using DevFlow.Recipes;
using DevFlow.Setup;
using DevFlow.Stops;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Projects;

/// <summary>A connected repository, as the API answers it.</summary>
/// <param name="Id">The project's own id.</param>
/// <param name="InstallationId">The installation of the app it is reached through.</param>
/// <param name="RepositoryId">The repository's id on the code host.</param>
/// <param name="Owner">Its owner, as the code host named it when it was connected.</param>
/// <param name="Name">Its name, as the code host named it when it was connected.</param>
/// <param name="CreatedAt">When it was connected.</param>
public sealed record Project(Guid Id, long InstallationId, long RepositoryId, string Owner, string Name, DateTimeOffset CreatedAt);

/// <summary>What <c>GET /devflow/api/projects</c> answers.</summary>
public sealed record ProjectList(IReadOnlyList<ListedProject> Projects);

/// <summary>A repository the person can reach, and whether it is one of their projects.</summary>
public sealed record ConnectableRepository(long Id, string Owner, string Name, bool Connected);

/// <summary>An installation of the app the person can reach, the account it is on, and its
/// repositories.</summary>
public sealed record ConnectableInstallation(long Id, string Account, IReadOnlyList<ConnectableRepository> Repositories);

/// <summary>What <c>GET /devflow/api/github/repositories</c> answers.</summary>
/// <param name="Installations">Every installation the person can reach, by account.</param>
/// <param name="InstallUrl">Where the person installs the app on another account.</param>
public sealed record Connectable(IReadOnlyList<ConnectableInstallation> Installations, Uri InstallUrl);

/// <summary>What <c>POST /devflow/api/projects</c> takes: which repository, through which
/// installation. Nothing else is the request's to say.</summary>
public sealed record ConnectRequest(long? InstallationId, long? RepositoryId);

/// <summary>
/// What a project operation answers: <see cref="Done"/> with the value, or why not, which the
/// route says as a problem document. A closed set.
/// </summary>
public abstract record ProjectOutcome<T>
{
    private ProjectOutcome()
    {
    }

    public sealed record Done(T Value) : ProjectOutcome<T>;

    /// <summary>The person has not linked GitHub.</summary>
    public sealed record NotLinked : ProjectOutcome<T>;

    /// <summary>GitHub stopped taking the person's link, and they link again.</summary>
    public sealed record NeedsRelink : ProjectOutcome<T>;

    /// <summary>The code host does not list that repository through that installation for the
    /// person.</summary>
    public sealed record NotReachable : ProjectOutcome<T>;

    /// <summary>The repository is already one of the person's projects.</summary>
    public sealed record AlreadyConnected : ProjectOutcome<T>;

    /// <summary>The code host did not answer; <paramref name="RetryAt"/> is when to try again,
    /// when it said.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : ProjectOutcome<T>;
}

/// <summary>
/// A user's projects: what they can connect, connecting, listing and disconnecting. What can be
/// connected is what the code host lists for the person, read with their own token through
/// <see cref="UserTokens.For"/>, so a repository is connected only when they can reach it; a
/// project is stored with the owner and name the code host gave. Every query goes through the
/// door as the user, and listing answers each project with its summary, from
/// <paramref name="logins"/> and <paramref name="start"/> as the workers list and the setup route
/// answer them. See "Connecting a repository" in devflow/README.md.
/// </summary>
public sealed class ProjectStore(UserDb database, UserTokens tokens, ICodeHost host, TimeProvider time, ClaudeLogins logins, WorkerStart start)
{
    /// <summary>The installations the person can reach, each with its repositories, sorted by
    /// account and by owner and name, each marked when it is already a project; and where to
    /// install the app. An installation gone between the two reads is left out.</summary>
    public async Task<ProjectOutcome<Connectable>> Connectable(Guid userId, Uri origin, CancellationToken cancellationToken)
    {
        var usable = await tokens.For(userId, cancellationToken);
        if (usable is not UserToken.Usable { Token.Value: var token })
        {
            return Refused<Connectable>(usable);
        }

        var installations = await host.Installations(userId, token, cancellationToken);
        if (installations is not CodeHostResult<IReadOnlyList<Installation>>.Found { Value: var reachable })
        {
            // Listing a person's installations is never not found; either failure is the host's.
            return new ProjectOutcome<Connectable>.Unavailable((installations as CodeHostResult<IReadOnlyList<Installation>>.Unavailable)?.RetryAt);
        }

        var listed = new List<ConnectableInstallation>();
        var connected = await ConnectedRepositories(userId, cancellationToken);
        foreach (var installation in reachable.OrderBy(i => i.Account, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id))
        {
            switch (await host.Repositories(userId, token, installation.Id, cancellationToken))
            {
                case CodeHostResult<IReadOnlyList<Repository>>.Found { Value: var repositories }:
                    listed.Add(new ConnectableInstallation(installation.Id, installation.Account, [.. repositories
                        .OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new ConnectableRepository(r.Id, r.Owner, r.Name, connected.Contains(r.Id)))]));
                    break;
                case CodeHostResult<IReadOnlyList<Repository>>.NotFound:
                    break;
                case CodeHostResult<IReadOnlyList<Repository>>.Unavailable { RetryAt: var retryAt }:
                    return new ProjectOutcome<Connectable>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        return new ProjectOutcome<Connectable>.Done(new Connectable(listed, host.InstallAddress(origin)));
    }

    /// <summary>
    /// Connects the repository, which makes it a project: only when the code host, asked as the
    /// person, lists it among those of the installation, and with the owner and name it gives.
    /// Already a project of theirs is refused, and so is one they cannot reach, storing nothing.
    /// </summary>
    public async Task<ProjectOutcome<Project>> Connect(Guid userId, long installationId, long repositoryId, CancellationToken cancellationToken)
    {
        var usable = await tokens.For(userId, cancellationToken);
        if (usable is not UserToken.Usable { Token.Value: var token })
        {
            return Refused<Project>(usable);
        }

        Repository repository;
        switch (await host.Repositories(userId, token, installationId, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Repository>>.Found { Value: var repositories }:
                if (repositories.FirstOrDefault(candidate => candidate.Id == repositoryId) is not { } found)
                {
                    return new ProjectOutcome<Project>.NotReachable();
                }

                repository = found;
                break;
            case CodeHostResult<IReadOnlyList<Repository>>.NotFound:
                return new ProjectOutcome<Project>.NotReachable();
            case CodeHostResult<IReadOnlyList<Repository>>.Unavailable { RetryAt: var retryAt }:
                return new ProjectOutcome<Project>.Unavailable(retryAt);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        // Postgres keeps microseconds, so the answer is the time as it is stored.
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        var id = Guid.CreateVersion7(now);
        var inserted = await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
            VALUES (@id, @userId, 'github', @installation, @repositoryId, @owner, @name, @now)
            ON CONFLICT (user_id, repository_id) DO NOTHING
            """,
            new { id, userId, installation = repository.Installation, repositoryId = repository.Id, owner = repository.Owner, name = repository.Name, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return inserted == 0
            ? new ProjectOutcome<Project>.AlreadyConnected()
            : new ProjectOutcome<Project>.Done(new Project(id, repository.Installation, repository.Id, repository.Owner, repository.Name, now));
    }

    /// <summary>
    /// The user's projects, by owner and name, each with its summary, under row level security:
    /// the projects with their workers' health, the cards counted by group and the open features,
    /// one query each in one transaction. <c>held</c> is the workers list's, the pauses, the project's
    /// hold and then the user's Claude login for a project on the subscription, and <c>cannot_run</c> its too; setting up is
    /// <see cref="SetupStore.Of"/>, asked only for a project not registered, since its values step
    /// is worked out from the newest setup report in C#. See "Projects" in
    /// devflow/docs/devflow-architecture.md.
    /// </summary>
    public async Task<IReadOnlyList<ListedProject>> List(Guid userId, CancellationToken cancellationToken)
    {
        var (projects, cards, features) = await database.InUserTransaction(userId, async transaction =>
        {
            var projects = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
                $"""
                SELECT p.id AS Id, p.installation_id AS InstallationId, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name, p.created_at AS CreatedAt,
                       (SELECT count(*)::int FROM workers w WHERE w.project_id = p.id) AS Workers,
                       (SELECT max(w.last_seen_at) FROM workers w WHERE w.project_id = p.id) AS LastSeenAt,
                       coalesce({Jobs.Pauses.PauseSql}, {WatchStore.HoldSql}) AS Held,
                       p.paused_at AS PausedAt,
                       {ProjectWaits.CannotRunReasonSql} AS CannotRunReason,
                       {SetupStore.RegisteredSql} AS Registered,
                       (SELECT r.last_success_at FROM project_reads r WHERE r.project_id = p.id) AS LastReadAt,
                       coalesce((SELECT s.runs_on FROM project_settings s WHERE s.project_id = p.id), '{RunsOn.Subscription}') AS RunsOn
                FROM projects p
                ORDER BY lower(p.owner), lower(p.name), p.id
                """,
                new { watchedSince = time.GetUtcNow() - BranchWatch.UnwatchedAfter },
                transaction,
                cancellationToken: cancellationToken));
            var cards = await transaction.Connection!.QueryAsync<CountsRow>(new CommandDefinition(
                """
                SELECT project_id AS ProjectId,
                       count(*) FILTER (WHERE state = ANY(@needsYou))::int AS NeedsYou,
                       count(*) FILTER (WHERE state = ANY(@inProgress))::int AS InProgress,
                       count(*) FILTER (WHERE state = ANY(@queued))::int AS Queued,
                       count(*) FILTER (WHERE state = ANY(@done))::int AS Done
                FROM cards
                GROUP BY project_id
                """,
                new
                {
                    needsYou = ProjectSummaries.Groups[ProjectSummaries.NeedsYou],
                    inProgress = ProjectSummaries.Groups[ProjectSummaries.InProgress],
                    queued = ProjectSummaries.Groups[ProjectSummaries.Queued],
                    done = ProjectSummaries.Groups[ProjectSummaries.Done],
                },
                transaction,
                cancellationToken: cancellationToken));
            // Each project's open feature with the most cards landed, the lowest number among
            // equals. Open is neither merged nor closed, and picked: an ignored parent is not his.
            var features = await transaction.Connection!.QueryAsync<FeatureRow>(new CommandDefinition(
                """
                SELECT DISTINCT ON (f.project_id) f.project_id AS ProjectId, f.number AS Number, f.title AS Title,
                       count(c.id) FILTER (WHERE c.state = ANY(@done))::int AS Landed,
                       count(c.id) FILTER (WHERE c.state = ANY(@counted))::int AS Total
                FROM features f LEFT JOIN cards c ON c.feature_id = f.id
                WHERE f.state NOT IN ('merged', 'closed', 'ignored')
                GROUP BY f.id
                ORDER BY f.project_id, landed DESC, f.number
                """,
                new { done = ProjectSummaries.Groups[ProjectSummaries.Done], counted = ProjectSummaries.Counted },
                transaction,
                cancellationToken: cancellationToken));
            return (projects.ToList(), cards.ToDictionary(row => row.ProjectId), features.ToDictionary(row => row.ProjectId));
        }, cancellationToken);

        // Asked once, and only when a project on the subscription has no hold of its own that
        // already says why.
        ClaudeLoginStatus? login = null;
        if (projects.Any(row => row.Held is null && RunsOn.NeedsClaudeLogin(row.RunsOn)))
        {
            login = await logins.Status(userId, cancellationToken);
        }

        var listed = new List<ListedProject>(projects.Count);
        foreach (var row in projects)
        {
            SetupProgress? settingUp = null;
            if (!row.Registered
                && await SetupStore.Of(database, userId, row.Id, start, cancellationToken) is { State: SetupStore.SettingUp, Steps: var steps })
            {
                settingUp = new SetupProgress(steps.Count(step => step.Done), steps.Count);
            }

            var counts = cards.TryGetValue(row.Id, out var counted)
                ? new CardCounts(counted.NeedsYou, counted.InProgress, counted.Queued, counted.Done)
                : new CardCounts(0, 0, 0, 0);
            var feature = features.TryGetValue(row.Id, out var open)
                ? new FeatureProgress(open.Number, open.Title, open.Landed, open.Total)
                : null;
            var workers = new WorkersHealth(
                row.Workers,
                row.LastSeenAt is { } seen ? GitHubLink.Utc(seen) : null,
                row.Held ?? (login is null ? null : Dispatcher.HeldFor(login, row.RunsOn)),
                row.CannotRunReason is { } reason ? ProjectWaits.CannotRun(row.Name, reason) : null);
            listed.Add(new ListedProject(row.Id, row.InstallationId, row.RepositoryId, row.Owner, row.Name, GitHubLink.Utc(row.CreatedAt),
                row.LastReadAt is { } read ? GitHubLink.Utc(read) : null,
                row.PausedAt is { } paused ? GitHubLink.Utc(paused) : null,
                new ProjectSummary(counts, workers, settingUp, feature)));
        }

        return listed;
    }

    /// <summary>Disconnects the project, and by its foreign keys everything stored for it. Nothing
    /// changes on the code host.</summary>
    /// <returns>Whether the user had such a project.</returns>
    public async Task<bool> Disconnect(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM projects WHERE id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;

    private async Task<HashSet<long>> ConnectedRepositories(Guid userId, CancellationToken cancellationToken) =>
        [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<long>(new CommandDefinition(
            "SELECT repository_id FROM projects", transaction: transaction, cancellationToken: cancellationToken)), cancellationToken)];

    /// <summary>Why there is no token to act as the person with, as an outcome.</summary>
    private static ProjectOutcome<T> Refused<T>(UserToken token) => token switch
    {
        UserToken.NotLinked => new ProjectOutcome<T>.NotLinked(),
        UserToken.NeedsRelink => new ProjectOutcome<T>.NeedsRelink(),
        UserToken.Unavailable { RetryAt: var retryAt } => new ProjectOutcome<T>.Unavailable(retryAt),
        _ => throw new ArgumentException("a usable token is not a refusal", nameof(token)),
    };

    private sealed record Row(Guid Id, long InstallationId, long RepositoryId, string Owner, string Name, DateTime CreatedAt,
        int Workers, DateTime? LastSeenAt, string? Held, DateTime? PausedAt, string? CannotRunReason, bool Registered, DateTime? LastReadAt, string RunsOn);

    private sealed record CountsRow(Guid ProjectId, int NeedsYou, int InProgress, int Queued, int Done);

    private sealed record FeatureRow(Guid ProjectId, int Number, string Title, int Landed, int Total);
}
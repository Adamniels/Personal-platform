using Dapper;

using DevFlow.CodeHost;
using DevFlow.Link;

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
public sealed record ProjectList(IReadOnlyList<Project> Projects);

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
/// door as the user. See "Connecting a repository" in devflow/README.md.
/// </summary>
public sealed class ProjectStore(UserDb database, UserTokens tokens, ICodeHost host, TimeProvider time)
{
    /// <summary>The installations the person can reach, each with its repositories, sorted by
    /// account and by owner and name, each marked when it is already a project; and where to
    /// install the app. An installation gone between the two reads is left out.</summary>
    public async Task<ProjectOutcome<Connectable>> Connectable(Guid userId, Uri origin, CancellationToken cancellationToken)
    {
        var usable = await tokens.For(userId, cancellationToken);
        if (usable is not UserToken.Usable { AccessToken: var token })
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
        if (usable is not UserToken.Usable { AccessToken: var token })
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

    /// <summary>The user's projects, by owner and name.</summary>
    public async Task<IReadOnlyList<Project>> List(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, installation_id AS InstallationId, repository_id AS RepositoryId, owner AS Owner, name AS Name, created_at AS CreatedAt
            FROM projects
            ORDER BY lower(owner), lower(name), id
            """,
            transaction: transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new Project(row.Id, row.InstallationId, row.RepositoryId, row.Owner, row.Name, GitHubLink.Utc(row.CreatedAt)))];
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

    private sealed record Row(Guid Id, long InstallationId, long RepositoryId, string Owner, string Name, DateTime CreatedAt);
}
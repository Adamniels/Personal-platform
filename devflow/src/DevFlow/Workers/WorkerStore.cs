using Dapper;

using DevFlow.Link;

using PlatformData;

namespace DevFlow.Workers;

/// <summary>A worker that has said hello, as the project's workers route answers it.</summary>
/// <param name="Id">The id the worker made when it started.</param>
/// <param name="Name">The name it sent, its container's hostname.</param>
/// <param name="RecipeVersion">The version of its project's recipe it runs.</param>
/// <param name="BaseImage">The base image it reports.</param>
/// <param name="FirstSeenAt">When it first said hello.</param>
/// <param name="LastSeenAt">When it last said hello or asked for work.</param>
public sealed record Worker(Guid Id, string Name, string RecipeVersion, string BaseImage, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

/// <summary>What <c>GET /devflow/api/projects/{id}/workers</c> answers.</summary>
/// <param name="KeyMadeAt">When the project's key was made, null before one has been; never the
/// key.</param>
/// <param name="Workers">Every worker that has said hello, by when it first did.</param>
public sealed record WorkerList(DateTimeOffset? KeyMadeAt, IReadOnlyList<Worker> Workers);

/// <summary>What a worker reports when it says hello.</summary>
public sealed record WorkerHello(Guid WorkerId, string Name, string RecipeVersion, string BaseImage);

/// <summary>
/// The workers of each project: a worker saying hello, asking for work, and the user's list of
/// them. A worker's calls run as its project's user, through the door, from the
/// <see cref="WorkerCaller"/> its key led to; every write names the project through the user's own
/// <c>projects</c>, so a worker can only ever be recorded in its own project. See "Workers" in
/// devflow/README.md.
/// </summary>
public sealed class WorkerStore(UserDb database, TimeProvider time)
{
    /// <summary>
    /// Records the worker, or brings it up to date when it has said hello before: what it reports
    /// now, and now as when it was last seen, keeping when it was first. False when the project is
    /// gone, which a key that was just found makes rare.
    /// </summary>
    public async Task<bool> Hello(WorkerCaller caller, WorkerHello hello, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(hello);
        var now = Now();
        return await database.InUserTransaction(caller.UserId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
            SELECT id, @workerId, user_id, @name, @recipeVersion, @baseImage, @now, @now FROM projects WHERE id = @projectId
            ON CONFLICT (project_id, id) DO UPDATE SET
                name = EXCLUDED.name,
                recipe_version = EXCLUDED.recipe_version,
                base_image = EXCLUDED.base_image,
                last_seen_at = EXCLUDED.last_seen_at
            """,
            new { projectId = caller.ProjectId, hello.WorkerId, hello.Name, hello.RecipeVersion, hello.BaseImage, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
    }

    /// <summary>Marks the worker seen now. False when it has not said hello in its project.</summary>
    public async Task<bool> Seen(WorkerCaller caller, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var now = Now();
        return await database.InUserTransaction(caller.UserId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE workers SET last_seen_at = @now WHERE project_id = @projectId AND id = @workerId",
            new { projectId = caller.ProjectId, workerId, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
    }

    /// <summary>The project's workers and when its key was made, or null when the user has no
    /// project by that id, another user's included.</summary>
    public async Task<WorkerList?> List(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
        {
            var found = await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM projects WHERE id = @projectId)",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (!found)
            {
                return null;
            }

            var made = await transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
                "SELECT created_at FROM worker_keys WHERE project_id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            var rows = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
                """
                SELECT id AS Id, name AS Name, recipe_version AS RecipeVersion, base_image AS BaseImage,
                       first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt
                FROM workers WHERE project_id = @projectId
                ORDER BY first_seen_at, id
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            return new WorkerList(
                made is { } at ? GitHubLink.Utc(at) : null,
                [.. rows.Select(row => new Worker(row.Id, row.Name, row.RecipeVersion, row.BaseImage, GitHubLink.Utc(row.FirstSeenAt), GitHubLink.Utc(row.LastSeenAt)))]);
        }, cancellationToken);

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed record Row(Guid Id, string Name, string RecipeVersion, string BaseImage, DateTime FirstSeenAt, DateTime LastSeenAt);
}
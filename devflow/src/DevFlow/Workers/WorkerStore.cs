using Dapper;

using DevFlow.CodeHost;

using DevFlow.Link;
using DevFlow.Recipes;

using PlatformData;

namespace DevFlow.Workers;

/// <summary>A worker that has said hello, as the project's workers route answers it.</summary>
/// <param name="Id">The id the worker made when it started.</param>
/// <param name="Name">The name it sent, its container's hostname.</param>
/// <param name="RecipeVersion">The version of its project's recipe it runs.</param>
/// <param name="BaseImage">The base image it reports.</param>
/// <param name="FirstSeenAt">When it first said hello.</param>
/// <param name="LastSeenAt">When it last said hello or asked for work.</param>
/// <param name="Rebuild">Why it needs building again, so it is given no work: <c>recipe</c> when
/// it runs another version of the recipe than Dev flow last read, <c>base_image</c> when it runs on
/// another base image than <c>DEVFLOW_WORKER_IMAGE</c>; null when it needs neither.</param>
public sealed record Worker(Guid Id, string Name, string RecipeVersion, string BaseImage, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, string? Rebuild);

/// <summary>What <c>GET /devflow/api/projects/{id}/workers</c> answers.</summary>
/// <param name="KeyMadeAt">When the project's key was made, null before one has been; never the
/// key.</param>
/// <param name="RecipeVersion">The version of the project's recipe as Dev flow last read it, the
/// commit; null before it has been read, or when the default branch has none.</param>
/// <param name="StartCommand">The command that builds and starts the project's worker, which never
/// holds the key.</param>
/// <param name="Workers">Every worker that has said hello, by when it first did.</param>
public sealed record WorkerList(DateTimeOffset? KeyMadeAt, string? RecipeVersion, string StartCommand, IReadOnlyList<Worker> Workers);

/// <summary>Where a worker stands when it asks for work.</summary>
public enum WorkerState
{
    /// <summary>It has not said hello in its key's project.</summary>
    Unknown,

    /// <summary>It runs the recipe and the base image Dev flow expects.</summary>
    Current,

    /// <summary>It runs an older recipe or another base image, and is given no work.</summary>
    NeedsRebuild,
}

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

    /// <summary>Marks the worker seen now, and says whether it needs building again, by
    /// <see cref="RebuildSql"/> with <paramref name="image"/> as the base image released.</summary>
    public async Task<WorkerState> Seen(WorkerCaller caller, Guid workerId, string image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var now = Now();
        var rebuild = await database.InUserTransaction(caller.UserId, transaction => transaction.Connection!.QueryAsync<string?>(new CommandDefinition(
            $"""
            UPDATE workers w SET last_seen_at = @now FROM projects p
            WHERE w.project_id = @projectId AND w.id = @workerId AND p.id = w.project_id
            RETURNING {RebuildSql}
            """,
            new { projectId = caller.ProjectId, workerId, now, image },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return rebuild.ToList() switch
        {
            [] => WorkerState.Unknown,
            [null] => WorkerState.Current,
            _ => WorkerState.NeedsRebuild,
        };
    }

    /// <summary>
    /// Why a worker <c>w</c> of project <c>p</c> needs building again, compared in SQL with the
    /// base image released as <c>@image</c>: <c>recipe</c> when Dev flow has read a version of the
    /// recipe and the worker's is another, then <c>base_image</c>, or null. A project whose recipe
    /// has not been read judges its workers by their base image alone, so a worker is never held
    /// back for what Dev flow does not know. Compared in the database rather than here, so no code
    /// in this namespace compares strings, see <c>TheKeyIsComparedInConstantTime</c>.
    /// </summary>
    private const string RebuildSql = """
        CASE
            WHEN p.recipe_version IS NOT NULL AND w.recipe_version <> p.recipe_version THEN 'recipe'
            WHEN w.base_image <> @image THEN 'base_image'
        END AS Rebuild
        """;

    /// <summary>The name of the repository of the user's project, or null when they have no
    /// project by that id.</summary>
    public async Task<string?> ProjectName(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT name FROM projects WHERE id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The repository of the worker's project, or null when the project is gone.</summary>
    public async Task<Repository?> RepositoryOf(WorkerCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return await database.InUserTransaction(caller.UserId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Repository>(new CommandDefinition(
            """
            SELECT installation_id AS Installation, repository_id AS Id, owner AS Owner, name AS Name
            FROM projects WHERE id = @projectId
            """,
            new { projectId = caller.ProjectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>Records <paramref name="version"/> as the version of the worker's project's recipe,
    /// the commit it was just read at.</summary>
    public async Task RecipeRead(WorkerCaller caller, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await RecipeRead(database, caller.UserId, caller.ProjectId, version, cancellationToken);
    }

    /// <summary>Records the version of the project's recipe as last read, through the door as its
    /// user: the commit it was read at, or null when its default branch has none. The loop's reader
    /// records it too, see <see cref="Reading.ProjectReader"/>.</summary>
    public static async Task RecipeRead(UserDb database, Guid userId, Guid projectId, string? version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET recipe_version = @version WHERE id = @projectId AND recipe_version IS DISTINCT FROM @version",
            new { projectId, version },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The project's workers, when its key was made, the version of its recipe and the
    /// command that starts a worker, from <paramref name="start"/>; or null when the user has no
    /// project by that id, another user's included.</summary>
    public async Task<WorkerList?> List(Guid userId, Guid projectId, WorkerStart start, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
        {
            ArgumentNullException.ThrowIfNull(start);
            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT name AS Name, recipe_version AS RecipeVersion FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project is null)
            {
                return null;
            }

            var made = await transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
                "SELECT created_at FROM worker_keys WHERE project_id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            var rows = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
                $"""
                SELECT w.id AS Id, w.name AS Name, w.recipe_version AS RecipeVersion, w.base_image AS BaseImage,
                       w.first_seen_at AS FirstSeenAt, w.last_seen_at AS LastSeenAt, {RebuildSql}
                FROM workers w JOIN projects p ON p.id = w.project_id
                WHERE w.project_id = @projectId
                ORDER BY w.first_seen_at, w.id
                """,
                new { projectId, image = start.Image },
                transaction,
                cancellationToken: cancellationToken));
            return new WorkerList(
                made is { } at ? GitHubLink.Utc(at) : null,
                project.RecipeVersion,
                start.StartCommand(project.Name),
                [.. rows.Select(row => new Worker(row.Id, row.Name, row.RecipeVersion, row.BaseImage, GitHubLink.Utc(row.FirstSeenAt), GitHubLink.Utc(row.LastSeenAt), row.Rebuild))]);
        }, cancellationToken);

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private sealed record Row(Guid Id, string Name, string RecipeVersion, string BaseImage, DateTime FirstSeenAt, DateTime LastSeenAt, string? Rebuild);

    private sealed record ProjectRow(string Name, string? RecipeVersion);
}
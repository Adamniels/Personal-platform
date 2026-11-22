using System.Globalization;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;

using Npgsql;

using PlatformData;

namespace DevFlow.Setup;

/// <summary>Where a worker's setup version stands, when it asks for work.</summary>
public enum VersionCheck
{
    /// <summary>A check of it passed: the worker takes any job.</summary>
    Checked,

    /// <summary>It has no check that passed and none under way, and its last check, if any,
    /// failed and the owner asked for another: the worker is handed its check.</summary>
    Due,

    /// <summary>A check of it is under way, or its last check failed and the owner has not asked
    /// for another: the worker gets nothing.</summary>
    Waiting,
}

/// <summary>A step of setting a project up, and whether it is done.</summary>
/// <param name="Step"><see cref="SetupStore.RecipeStep"/>, <see cref="SetupStore.WorkerStep"/> or
/// <see cref="SetupStore.CheckStep"/>.</param>
/// <param name="Done">Whether it is done.</param>
public sealed record SetupStep(string Step, bool Done);

/// <summary>A check that ran, as the project's page shows it.</summary>
/// <param name="Id">Its run's id, which its pictures' addresses name.</param>
/// <param name="Version">The setup version it checked.</param>
/// <param name="Commit">The commit it checked.</param>
/// <param name="Passed">Whether the check exited 0.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">The last 200 lines it printed, the project's text and so data.</param>
/// <param name="Pictures">The names of its pictures of the project's address, desktop then phone;
/// empty when none were taken.</param>
/// <param name="PicturesNote">Why pictures are missing, or null.</param>
/// <param name="CheckedAt">When its result came back.</param>
/// <param name="AgainAsked">Whether the owner asked for another check since it failed.</param>
public sealed record SetupCheck(Guid Id, string Version, string Commit, bool Passed, int ExitCode, string Output, IReadOnlyList<string> Pictures, string? PicturesNote, DateTimeOffset CheckedAt, bool AgainAsked);

/// <summary>What <c>GET /devflow/api/projects/{id}/setup</c> answers.</summary>
/// <param name="State"><see cref="SetupStore.SettingUp"/> until a check of any version has passed,
/// then <see cref="SetupStore.Registered"/>.</param>
/// <param name="Steps">The steps of setting it up, in their order, each with whether it is
/// done.</param>
/// <param name="Check">The newest check of the project's setup version, or, before that version is
/// known, of any; null before one has run.</param>
/// <param name="Checking">Whether a check of that version is running now.</param>
public sealed record ProjectSetup(string State, IReadOnlyList<SetupStep> Steps, SetupCheck? Check, bool Checking);

/// <summary>What asking for another check comes to.</summary>
public enum CheckAgain
{
    /// <summary>Another check is handed out to the version's next worker that asks.</summary>
    Asked,

    /// <summary>The newest check did not fail, or another was already asked for.</summary>
    NothingToCheck,

    /// <summary>The caller has no such project.</summary>
    NoProject,
}

/// <summary>A check taken to run, as its job is made from it.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Version">The setup version it checks, the worker's.</param>
/// <param name="Commit">The default branch's head it checks.</param>
public sealed record ClaimedCheck(Guid RunId, string Version, string Commit);

/// <summary>
/// The checks of each project's setup, through the door as the project's user: whether a worker's
/// version is due its check, taking one, and recording what came back. Every version of
/// <c>.devflow/</c> is checked once before its workers take any other job; a check that failed
/// holds back that version's workers until the owner asks for another or the version changes, and
/// one that failed outside the project is put back, making the project wait as any run does. See
/// "Setting up a project" in devflow/README.md, and in devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class SetupStore
{
    /// <summary>The longest a check may take, which its run keeps for being found lost.</summary>
    public const int CheckMinutes = 30;

    /// <summary>A project no version of whose setup has passed its check: it hands out no card.</summary>
    public const string SettingUp = "setting_up";

    /// <summary>A project a version of whose setup has passed its check.</summary>
    public const string Registered = "registered";

    /// <summary>The step of the recipe on the default branch: Dev flow has read a version of it.</summary>
    public const string RecipeStep = "recipe";

    /// <summary>The step of a worker on the recipe: one has said hello built from that version.</summary>
    public const string WorkerStep = "worker";

    /// <summary>The step of the check: a check of that version passed.</summary>
    public const string CheckStep = "check";

    /// <summary>The newest check of project <c>p</c>'s setup version, or of any before it is
    /// known, as SQL naming its run's id.</summary>
    private const string NewestSql = """
        (SELECT c.run_id FROM recipe_checks c
         WHERE c.project_id = p.id AND c.version = coalesce(p.recipe_version, c.version)
         ORDER BY c.checked_at DESC, c.run_id DESC LIMIT 1)
        """;

    /// <summary>
    /// The project's setup: whether it is setting up or registered, its steps in their order, the
    /// recipe on the default branch, a worker on it and its check passed, and its newest check;
    /// or null when the user has no such project. The later parts of setting up add their steps
    /// among these.
    /// </summary>
    public static async Task<ProjectSetup?> Of(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var row = await transaction.Connection!.QuerySingleOrDefaultAsync<StepsRow>(new CommandDefinition(
                $"""
                SELECT p.recipe_version IS NOT NULL AS Recipe,
                       EXISTS (SELECT 1 FROM workers w WHERE w.project_id = p.id AND w.recipe_version = p.recipe_version) AS Worker,
                       EXISTS (SELECT 1 FROM recipe_checks c WHERE c.project_id = p.id AND c.version = p.recipe_version AND c.passed) AS Passed,
                       EXISTS (SELECT 1 FROM recipe_checks c WHERE c.project_id = p.id AND c.passed) AS Registered,
                       EXISTS (SELECT 1 FROM runs r WHERE r.project_id = p.id AND r.kind = 'check' AND r.finished_at IS NULL
                               AND r.recipe_version = coalesce(p.recipe_version, r.recipe_version)) AS Checking,
                       {NewestSql} AS Newest
                FROM projects p WHERE p.id = @projectId
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (row is null)
            {
                return null;
            }

            SetupCheck? check = null;
            if (row.Newest is { } newest)
            {
                var found = await transaction.Connection!.QuerySingleAsync<CheckRow>(new CommandDefinition(
                    """
                    SELECT run_id AS Id, version AS Version, commit AS Commit, passed AS Passed, exit_code AS ExitCode, output AS Output,
                           pictures AS Pictures, pictures_note AS PicturesNote, checked_at AS CheckedAt, again_at IS NOT NULL AS AgainAsked
                    FROM recipe_checks WHERE run_id = @newest
                    """,
                    new { newest },
                    transaction,
                    cancellationToken: cancellationToken));
                check = new SetupCheck(found.Id, found.Version, found.Commit, found.Passed, found.ExitCode, found.Output, found.Pictures, found.PicturesNote, Link.GitHubLink.Utc(found.CheckedAt), found.AgainAsked);
            }

            return new ProjectSetup(
                row.Registered ? Registered : SettingUp,
                [new SetupStep(RecipeStep, row.Recipe), new SetupStep(WorkerStep, row.Worker), new SetupStep(CheckStep, row.Passed)],
                check,
                row.Checking);
        }, cancellationToken);
    }

    /// <summary>
    /// Asks for another check of the version whose newest check, the one the project's page shows,
    /// failed: its next worker that asks is handed one. Once per failed check.
    /// </summary>
    public static async Task<CheckAgain> Again(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var newest = await transaction.Connection!.QuerySingleOrDefaultAsync<(bool Found, Guid? RunId)>(new CommandDefinition(
                $"SELECT true, {NewestSql} FROM projects p WHERE p.id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (!newest.Found)
            {
                return CheckAgain.NoProject;
            }

            var asked = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE recipe_checks SET again_at = @now WHERE run_id = @runId AND project_id = @projectId AND NOT passed AND again_at IS NULL",
                new { runId = newest.RunId, projectId, now },
                transaction,
                cancellationToken: cancellationToken));
            return asked == 1 ? CheckAgain.Asked : CheckAgain.NothingToCheck;
        }, cancellationToken);
    }

    /// <summary>The bytes of a check's picture of the user's project, or null when they have no
    /// such project, check or picture.</summary>
    public static async Task<byte[]?> Picture(UserDb database, Guid userId, Guid projectId, Guid checkId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<byte[]?>(new CommandDefinition(
            "SELECT png FROM recipe_check_pictures WHERE project_id = @projectId AND run_id = @checkId AND name = @name",
            new { projectId, checkId, name },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    private sealed record StepsRow(bool Recipe, bool Worker, bool Passed, bool Registered, bool Checking, Guid? Newest);

    /// <summary>A check's row; a class rather than a positional record, since Npgsql answers a
    /// <c>text[]</c> column as an array Dapper matches only to a property.</summary>
    private sealed class CheckRow
    {
        public Guid Id { get; init; }

        public string Version { get; init; } = "";

        public string Commit { get; init; } = "";

        public bool Passed { get; init; }

        public int ExitCode { get; init; }

        public string Output { get; init; } = "";

        public string[] Pictures { get; init; } = [];

        public string? PicturesNote { get; init; }

        public DateTime CheckedAt { get; init; }

        public bool AgainAsked { get; init; }
    }

    /// <summary>
    /// Whether the version <c>@version</c> of project <c>@projectId</c> is checked, as SQL: one of
    /// its checks passed.
    /// </summary>
    internal const string PassedSql = """
        EXISTS (SELECT 1 FROM recipe_checks c WHERE c.project_id = @projectId AND c.version = @version AND c.passed)
        """;

    /// <summary>
    /// Whether a check of version <c>@version</c> may be handed out, as SQL: none passed, none is
    /// under way, and its newest check, if any, failed with the owner asking for another since.
    /// </summary>
    private const string DueSql = $"""
        NOT {PassedSql}
        AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = @projectId AND r.kind = 'check'
                        AND r.recipe_version = @version AND r.finished_at IS NULL)
        AND coalesce((SELECT c.again_at IS NOT NULL FROM recipe_checks c WHERE c.project_id = @projectId AND c.version = @version
                      ORDER BY c.checked_at DESC, c.run_id DESC LIMIT 1), true)
        """;

    /// <summary>Where the version the worker reported stands, and that version; null when the
    /// worker has not said hello in the project.</summary>
    public static async Task<(VersionCheck State, string Version)?> Of(UserDb database, Guid userId, Guid projectId, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var version = await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT recipe_version FROM workers WHERE project_id = @projectId AND id = @workerId",
                new { projectId, workerId },
                transaction,
                cancellationToken: cancellationToken));
            if (version is null)
            {
                return ((VersionCheck, string)?)null;
            }

            var (passed, due) = await transaction.Connection!.QuerySingleAsync<(bool, bool)>(new CommandDefinition(
                $"SELECT {PassedSql}, {DueSql}",
                new { projectId, version },
                transaction,
                cancellationToken: cancellationToken));
            return (passed ? VersionCheck.Checked : due ? VersionCheck.Due : VersionCheck.Waiting, version);
        }, cancellationToken);
    }

    /// <summary>
    /// Takes the check of <paramref name="version"/> for the worker, inside
    /// <paramref name="transaction"/>, which runs as the project's user under the project's lock:
    /// records a check run on <paramref name="branch"/>, the default branch, of
    /// <paramref name="commit"/>, with the check's minutes, only while it is still due. Null when
    /// it is not, another worker having taken it or a check of it having passed meanwhile.
    /// </summary>
    public static async Task<ClaimedCheck?> Claim(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, string version, string branch, string commit, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var runId = Guid.CreateVersion7(now);
        var inserted = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO runs (id, project_id, user_id, worker_id, kind, branch, base, started_at, run_minutes, recipe_version, from_commit, check_command)
            SELECT @runId, @projectId, @userId, @workerId, 'check', @branch, @branch, @now, @minutes, @version, @commit, @check
            WHERE {DueSql}
            ON CONFLICT (project_id, recipe_version) WHERE kind = 'check' AND finished_at IS NULL DO NOTHING
            """,
            new { runId, projectId, userId, workerId, branch, now, minutes = CheckMinutes, version, commit, check },
            transaction,
            cancellationToken: cancellationToken));
        return inserted == 0 ? null : new ClaimedCheck(runId, version, commit);
    }

    /// <summary>
    /// How a check's result ends its run: a failure outside the project, or a check that did not
    /// run, puts it back, counting as no verdict, and makes the project wait; a check that ran
    /// succeeded when it exited 0 and failed otherwise.
    /// </summary>
    public static RunEnding Judge(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.CheckExitCode is null)
        {
            // Every failure a check sends lies outside the project, the worker's table has it so; a
            // check cannot fail in a card it does not have.
            return new RunEnding("failed_outside_card", RunStore.Cut(result.FailureReason ?? "The check did not run"), "");
        }

        return result.CheckExitCode == 0
            ? new RunEnding("succeeded", null, "")
            : new RunEnding("check_failed", string.Create(CultureInfo.InvariantCulture, $"The check failed with exit code {result.CheckExitCode}"), "");
    }

    /// <summary>
    /// Records what a check that ran found, inside the transaction that ends its run: the version
    /// and the commit, whether it passed, its exit code and output, and its pictures, each stored
    /// whole under its name, only those the result names. Nothing is recorded for a check that did
    /// not run.
    /// </summary>
    internal static async Task Record(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid runId, string version, string commit, RunResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!result.Succeeded || result.CheckExitCode is not { } exit)
        {
            return;
        }

        var named = result.Screenshots?.Names ?? [];
        var pictures = (result.Pictures ?? [])
            .Where(picture => picture is { Name: { } name, Png: not null } && named.Contains(name, StringComparer.Ordinal))
            .ToList();
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO recipe_checks (run_id, project_id, user_id, version, commit, passed, exit_code, output, pictures, pictures_note, checked_at)
            VALUES (@runId, @projectId, @userId, @version, @commit, @passed, @exit, @output, @pictures, @note, @now)
            """,
            new
            {
                runId,
                projectId,
                userId,
                version,
                commit,
                passed = exit == 0,
                exit,
                output = RunStore.Tail(result.CheckOutput ?? ""),
                pictures = pictures.Select(picture => picture.Name!).ToArray(),
                note = result.Screenshots?.Note,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var picture in pictures)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "INSERT INTO recipe_check_pictures (run_id, project_id, user_id, name, png) VALUES (@runId, @projectId, @userId, @name, @png)",
                new { runId, projectId, userId, name = picture.Name, png = picture.Png },
                transaction,
                cancellationToken: cancellationToken));
        }
    }
}
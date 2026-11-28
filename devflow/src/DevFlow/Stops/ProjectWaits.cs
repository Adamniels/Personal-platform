using System.Globalization;

using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>A project that cannot run its cards: its last failure outside a card, and how many in
/// a row there were.</summary>
/// <param name="Failures">How many runs in a row failed outside a card or were lost.</param>
/// <param name="Reason">The last one's reason.</param>
/// <param name="Since">When the first of them failed.</param>
/// <param name="WaitUntil">Until when the project hands out no job.</param>
/// <param name="UntilReset">Whether it waits until Claude's usage limit resets, as the last
/// failure's answer said, rather than for <see cref="ProjectWaits.Wait"/>.</param>
public sealed record ProjectWait(int Failures, string Reason, DateTimeOffset Since, DateTimeOffset WaitUntil, bool UntilReset);

/// <summary>
/// A project waiting after failures outside its cards, a usage limit, the network or the machine,
/// which never count against a card: each such failure, a lost run among them, makes the project
/// hand out no job for <see cref="Wait"/>, 5 minutes doubling up to 60, or until Claude's usage
/// limit resets when its answer said when; and the next run that succeeds ends it. After
/// <see cref="Shown"/> in a row the project shows it cannot run its cards. See "Stops" in
/// devflow/README.md, and in devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class ProjectWaits
{
    /// <summary>The wait after the first failure outside a card.</summary>
    public static readonly TimeSpan First = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait the doubling reaches.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(60);

    /// <summary>The furthest a usage limit's reset is believed: Claude's longest limit is a week,
    /// so a reset later than this is a misreading, and the doubling holds instead.</summary>
    public static readonly TimeSpan FurthestReset = TimeSpan.FromDays(8);

    /// <summary>How many failures outside a card in a row show on the project's page and on
    /// "Waiting for you".</summary>
    public const int Shown = 6;

    /// <summary>The wait after <paramref name="failures"/> failures in a row: 5 minutes, doubling,
    /// at most 60.</summary>
    public static TimeSpan Wait(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        var minutes = First.TotalMinutes * Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromMinutes(Math.Min(minutes, Longest.TotalMinutes));
    }

    /// <summary>What the project's page and "Waiting for you" say of a project that cannot run its
    /// cards.</summary>
    public static string CannotRun(string project, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Dev flow cannot run {project}'s cards: {reason}");

    /// <summary>
    /// A run of the project failed outside its card, or was lost, inside the transaction that
    /// records it: one more in a row, with its reason, and the project waits until
    /// <paramref name="resetsAt"/>, when it is after now and no further than
    /// <see cref="FurthestReset"/>, or else for <see cref="Wait"/>.
    /// </summary>
    public static async Task Failed(NpgsqlTransaction transaction, Guid projectId, Guid userId, string reason, DateTimeOffset? resetsAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // The count comes from the row as the upsert left it, its lock held, so two results
        // recorded at once each count.
        var failures = await transaction.Connection!.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO project_waits (project_id, user_id, failures, reason, wait_until, since)
            VALUES (@projectId, @userId, 1, @reason, @now, @now)
            ON CONFLICT (project_id) DO UPDATE SET failures = project_waits.failures + 1, reason = EXCLUDED.reason
            RETURNING failures
            """,
            new { projectId, userId, reason, now },
            transaction,
            cancellationToken: cancellationToken));
        var untilReset = Believed(resetsAt, now);
        var until = untilReset ? resetsAt!.Value : now + Wait(failures);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE project_waits SET wait_until = @until, until_reset = @untilReset WHERE project_id = @projectId",
            new { projectId, until, untilReset },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Whether <paramref name="resetsAt"/>, a usage limit's reset as a run's answer named
    /// it, is believed at <paramref name="now"/>: after now and no further than
    /// <see cref="FurthestReset"/>.</summary>
    public static bool Believed([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } reset && reset > now && reset - now <= FurthestReset;

    /// <summary>A run of the project succeeded, inside the transaction that records it: the count
    /// ends, and so does the wait.</summary>
    public static Task Succeeded(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_waits WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The project's wait, or null when no failure outside a card is counted.</summary>
    public static async Task<ProjectWait?> Of(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT failures AS Failures, reason AS Reason, since AS Since, wait_until AS WaitUntil, until_reset AS UntilReset FROM project_waits WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is null ? null : new ProjectWait(row.Failures, row.Reason, Utc(row.Since), Utc(row.WaitUntil), row.UntilReset);
    }

    /// <summary>What the project's page says of a project that cannot run its cards,
    /// <see cref="CannotRun"/>, once <see cref="Shown"/> failures outside a card in a row are
    /// counted; null before then, and for a project the user does not have.</summary>
    public static async Task<string?> CannotRunOf(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Failing>(new CommandDefinition(
            $"SELECT p.name AS Name, {CannotRunReasonSql} AS Reason FROM projects p WHERE p.id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is { Reason: { } reason } ? CannotRun(row.Name, reason) : null;
    }

    /// <summary>The last reason of project <c>p</c>'s failures outside a card, once
    /// <see cref="Shown"/> in a row are counted, as SQL; null before then. What
    /// <see cref="CannotRun"/> says it with, for the workers list and each project's
    /// summary.</summary>
    internal static readonly string CannotRunReasonSql = string.Create(CultureInfo.InvariantCulture,
        $"(SELECT pw.reason FROM project_waits pw WHERE pw.project_id = p.id AND pw.failures >= {Shown})");

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed record Failing(string Name, string? Reason);

    private sealed record Row(int Failures, string Reason, DateTime Since, DateTime WaitUntil, bool UntilReset);
}
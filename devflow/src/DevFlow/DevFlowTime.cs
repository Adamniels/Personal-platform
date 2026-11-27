using System.Text.RegularExpressions;

using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow;

/// <summary>
/// Time zones, the one place Dev flow reads one, and the days and wall clock times counted in them.
/// Each user has one zone, <c>user_settings.time_zone</c>, Europe/Stockholm until they set another,
/// an IANA id read with .NET's own <see cref="TimeZoneInfo"/>; every day Dev flow counts, the tokens
/// per day on "Usage and history" among them, and every schedule is read in it, never in UTC or in
/// the server's own zone. See "Days" in devflow/docs/devflow-architecture.md.
/// </summary>
public static partial class DevFlowTime
{
    /// <summary>The zone of a user who has set none.</summary>
    public const string DefaultZoneId = "Europe/Stockholm";

    /// <summary>The zone of a user who has set none, and of one whose zone this machine does not
    /// know.</summary>
    public static TimeZoneInfo DefaultZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(DefaultZoneId);

    /// <summary>The longest id taken, as the table bounds it.</summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// The user's zone as stored, in SQL, for a project <c>p</c>: their row's, or
    /// <see cref="DefaultZoneId"/> without one. Read into a zone with <see cref="Zone"/>.
    /// </summary>
    public const string ZoneSql = $"coalesce((SELECT us.time_zone FROM user_settings us WHERE us.user_id = p.user_id), '{DefaultZoneId}')";

    /// <summary>
    /// The zone <paramref name="id"/> names when it is an IANA id: <c>UTC</c>, or an area and a
    /// place joined by <c>/</c>, of letters, digits, <c>_</c>, <c>+</c> and <c>-</c>, that
    /// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> finds; null for anything else. The shape is
    /// checked first, so a Windows id, which .NET would convert, and a path, which on Linux it would
    /// look for as a file, are never asked about.
    /// </summary>
    public static TimeZoneInfo? FindIana(string? id)
    {
        if (id is null || id.Length > MaxIdLength || !IanaShape().IsMatch(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The zone a stored id names, or <see cref="DefaultZone"/> when this machine does not
    /// know it.</summary>
    public static TimeZoneInfo Zone(string? stored) => FindIana(stored) ?? DefaultZone;

    /// <summary>The user's zone, read through the door as them.</summary>
    public static async Task<TimeZoneInfo> ZoneOf(UserDb database, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => ZoneOf(transaction, cancellationToken), cancellationToken);
    }

    /// <summary>The zone of the user <paramref name="transaction"/> runs as.</summary>
    public static async Task<TimeZoneInfo> ZoneOf(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return Zone(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT time_zone FROM user_settings",
            transaction: transaction,
            cancellationToken: cancellationToken)));
    }

    /// <summary>Sets the user's zone to <paramref name="id"/>, which <see cref="FindIana"/> must
    /// find; the caller checks it first.</summary>
    public static async Task SetZone(UserDb database, Guid userId, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (FindIana(id) is null)
        {
            throw new ArgumentException("not an IANA zone this machine knows", nameof(id));
        }

        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_settings (user_id, time_zone) VALUES (@userId, @id)
            ON CONFLICT (user_id) DO UPDATE SET time_zone = excluded.time_zone
            """,
            new { userId, id },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The day <paramref name="instant"/> falls on in <paramref name="zone"/>.</summary>
    public static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The instant it is <paramref name="time"/> on <paramref name="day"/> in <paramref name="zone"/>,
    /// as wall clock: where the zone's clocks skip it, the first minute after that exists, and where
    /// they pass it twice, the first time. In the zone's own offset then.
    /// </summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        // Of two offsets, the larger is the earlier instant.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>The first instant of <paramref name="day"/> in <paramref name="zone"/>: its midnight,
    /// by <see cref="At"/>. Neither change of clocks touches midnight in Stockholm, whose clocks
    /// change at 02:00 and 03:00, but in other zones they may.</summary>
    public static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone) => At(day, TimeOnly.MinValue, zone).ToUniversalTime();

    /// <summary>The instant <paramref name="day"/> ends, which is not in it: the next day's start. A
    /// day is 23 hours long when summer time starts, and 25 when it ends.</summary>
    public static DateTimeOffset EndOf(DateOnly day, TimeZoneInfo zone) => StartOf(day.AddDays(1), zone);

    [GeneratedRegex(@"^(UTC|[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaShape();
}
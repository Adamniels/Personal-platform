using Dapper;

using Microsoft.AspNetCore.Http.HttpResults;

using PlatformData;

using Review.Api;
using Review.Scheduling;

namespace Review.Display;

/// <summary>
/// <c>GET /review/api/display</c>, what review offers the core's front page: how many cards are
/// due now and before the day ends, when the next one comes back, and the decks with cards due
/// now. Data about review's own domain and nothing meant for display: the core decides what to
/// show and how. Pinned by contracts/display/review.json. See "What it serves" in
/// review/README.md, and "What a feature exposes for the front page" in
/// Core/docs/core-architecture.md.
/// </summary>
public static class DisplayEndpoints
{
    /// <summary>The most decks one answer holds.</summary>
    public const int DecksMax = 20;

    /// <summary>When a day starts, on the wall clock of the day's zone: Anki's rule, so a late
    /// session does not roll over at midnight.</summary>
    private static readonly TimeSpan DayStarts = TimeSpan.FromHours(4);

    /// <param name="dayZone">Where the day ends, <c>REVIEW_DAY_ZONE</c>.</param>
    public static IEndpointRouteBuilder MapDisplay(this IEndpointRouteBuilder routes, TimeZoneInfo dayZone)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(dayZone);
        routes.MapGet("/review/api/display", (HttpContext context, UserDb db, TimeProvider time, CancellationToken cancellationToken) =>
                Get(context, db, time, dayZone, cancellationToken))
            .UserRoute("getDisplay", "What is due, for the front page", $"due_now, the cards due at or before now; due_today, those due before the day ends at 04:00 in the zone REVIEW_DAY_ZONE names, those due now included; next_due_at, the earliest due after now, or null; and decks, every deck with a card due now, at most {DecksMax}, the most due first, then by name without letter case, each its name and due_now. The core calls it with the user's token in the Authorization header.");
        return routes;
    }

    private static async Task<Ok<DisplayData>> Get(
        HttpContext context, UserDb db, TimeProvider time, TimeZoneInfo dayZone, CancellationToken cancellationToken) =>
        TypedResults.Ok(await Read(db, context.UserId(), GradeEndpoints.Now(time), dayZone, cancellationToken));

    /// <summary>The display data of <paramref name="user"/>'s cards, as of
    /// <paramref name="now"/>.</summary>
    internal static async Task<DisplayData> Read(UserDb db, Guid user, DateTimeOffset now, TimeZoneInfo dayZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        // UTC, as Npgsql writes a timestamptz only from a zero offset.
        var parameters = new { now = now.ToUniversalTime(), dayEnd = DayEnd(now, dayZone).ToUniversalTime(), decksMax = DecksMax };
        return await db.InUserTransaction(user, async transaction =>
        {
            var counts = await transaction.Connection!.QuerySingleAsync<Counts>(new CommandDefinition(
                """
                SELECT count(*) FILTER (WHERE due <= @now)::int AS DueNow,
                       count(*) FILTER (WHERE due < @dayEnd)::int AS DueToday,
                       min(due) FILTER (WHERE due > @now) AS NextDueAt
                FROM cards
                """,
                parameters, transaction, cancellationToken: cancellationToken));
            // A second statement under READ COMMITTED, so a grade landing between the two can leave
            // the decks one off the counts for that answer, as the queue's total may trail its
            // page. A card on the front page tolerates it.
            var decks = await transaction.Connection!.QueryAsync<DueDeck>(new CommandDefinition(
                """
                SELECT d.name AS Name, count(*)::int AS DueNow
                FROM cards c JOIN decks d ON d.user_id = c.user_id AND d.id = c.deck_id
                WHERE c.due <= @now
                GROUP BY d.id, d.name
                ORDER BY count(*) DESC, lower(d.name), d.name, d.id
                LIMIT @decksMax
                """,
                parameters, transaction, cancellationToken: cancellationToken));
            return new DisplayData(counts.DueNow, counts.DueToday, counts.NextDueAt, [.. decks]);
        }, cancellationToken);
    }

    /// <summary>
    /// When the day that holds <paramref name="now"/> ends: the next 04:00 on the wall clock of
    /// <paramref name="zone"/>, the same morning before 04:00 and the next one from it. A day
    /// holding a change of clocks is 23 or 25 hours long.
    /// </summary>
    internal static DateTimeOffset DayEnd(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var day = local.TimeOfDay < DayStarts ? local.Date : local.Date.AddDays(1);
        return OnTheWallClock(day + DayStarts, zone);
    }

    /// <summary>
    /// The instant <paramref name="zone"/>'s wall clock shows <paramref name="local"/>. A time the
    /// clocks skip is read with the offset before the change, which is the instant they change;
    /// a time they show twice is the first. Neither happens at 04:00 in Europe/Stockholm, whose
    /// clocks change at 02:00 and 03:00.
    /// </summary>
    private static DateTimeOffset OnTheWallClock(DateTime local, TimeZoneInfo zone)
    {
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.IsInvalidTime(local) ? zone.GetUtcOffset(local.AddDays(-1))
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private sealed record Counts(int DueNow, int DueToday, DateTime? NextDueAt);
}

/// <summary>What review offers the front page: how many cards are due at or before now, how many
/// before the day ends, those due now included, the earliest due after now or null, and every
/// deck with a card due now, at most <see cref="DisplayEndpoints.DecksMax"/>, the most due first,
/// then by name without letter case.</summary>
public sealed record DisplayData(int DueNow, int DueToday, DateTime? NextDueAt, DueDeck[] Decks);

/// <summary>A deck with cards due now: its name, and how many.</summary>
public sealed record DueDeck(string Name, int DueNow);
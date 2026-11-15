using Dapper;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformData;

using Review.Api;
using Review.Cards;

namespace Review.Scheduling;

/// <summary>
/// <c>GET /review/api/queue</c>: the cards due now, oldest due first, each with its previews, the
/// time they were counted from, and when the next card not yet due comes back. See "When a card
/// comes back" in review/README.md.
/// </summary>
public static class QueueEndpoints
{
    /// <summary>The most cards one queue answers; <c>total</c> counts them all.</summary>
    public const int QueueMax = 200;

    public static IEndpointRouteBuilder MapQueue(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/review/api/queue", Get)
            .UserRoute("getQueue", "The cards due now", $"The cards due at or before now, oldest due first, at most {QueueMax}, each as a single read answers it plus when each grade would bring it back; total, how many are due in all; as_of, the now every preview is counted from; and next_due, the earliest due after it, or null. Narrowed to a deck by ?deck=<id>, to a topic by ?topic=<slug>, or both, total and next_due with them. 422 for a topic that is no slug.",
                StatusCodes.Status422UnprocessableEntity);
        return routes;
    }

    private static async Task<Results<Ok<DueCards>, ProblemHttpResult>> Get(
        [FromQuery] Guid? deck, [FromQuery] string? topic, HttpContext context, UserDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (topic is not null && !CardRules.IsSlug(topic))
        {
            var broken = new Broken();
            broken.Add("topic", "must be a topic's slug");
            return broken.Problem();
        }

        var now = GradeEndpoints.Now(time);
        var (rows, total, nextDue) = await db.InUserTransaction(context.UserId(), async transaction =>
        {
            var parameters = new { deck, topic, now };
            var rows = await transaction.Connection!.QueryAsync<CardEndpoints.Row>(new CommandDefinition(
                $"""
                {CardEndpoints.Select}
                WHERE c.due <= @now AND {CardEndpoints.Narrowed}
                ORDER BY c.due, c.id
                LIMIT {QueueMax}
                """,
                parameters, transaction, cancellationToken: cancellationToken));
            // A second statement under READ COMMITTED, so a grade landing between the two can leave
            // total one off the page for that answer. A count shown beside the queue tolerates it;
            // matching them exactly would mean REPEATABLE READ in the door for one number.
            var total = await transaction.Connection!.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT count(*) FROM cards c WHERE c.due <= @now AND {CardEndpoints.Narrowed}",
                parameters, transaction, cancellationToken: cancellationToken));
            // When the queue would next grow, so a page with nothing left to show says when to
            // come back; null when no card is scheduled after now.
            var nextDue = await transaction.Connection!.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                $"SELECT min(c.due) FROM cards c WHERE c.due > @now AND {CardEndpoints.Narrowed}",
                parameters, transaction, cancellationToken: cancellationToken));
            return (rows, total, nextDue);
        }, cancellationToken);

        return TypedResults.Ok(new DueCards(
            [.. rows.Select(row => CardWithPreviews.From(row.Card, Fsrs.Preview(row.Schedule, now)))],
            total,
            now.UtcDateTime,
            nextDue));
    }
}

/// <summary>The queue: the cards due now, oldest due first, at most
/// <see cref="QueueEndpoints.QueueMax"/>; how many are due in all; the time every preview was
/// counted from, so the page writes each as an interval without its own clock; and the earliest
/// due after that time, or null when no card is scheduled after it.</summary>
public sealed record DueCards(CardWithPreviews[] Cards, int Total, DateTime AsOf, DateTime? NextDue);
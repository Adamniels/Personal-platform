using System.Globalization;

using Dapper;

using Microsoft.AspNetCore.Http.HttpResults;

using Npgsql;

using PlatformData;

using Review.Api;
using Review.Cards;

namespace Review.Scheduling;

/// <summary>
/// <c>POST /review/api/cards/{id}/grades</c>: grading a card schedules when it comes back, by
/// <see cref="Fsrs"/>, and logs the grade. See "When a card comes back" in review/README.md.
/// </summary>
public static class GradeEndpoints
{
    public static IEndpointRouteBuilder MapGrades(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/review/api/cards/{id:guid}/grades", Grade)
            .UserRoute("gradeCard", "Grade a card", "Again 1, Hard 2, Good 3 or Easy 4, from the card's version: schedules when it comes back, logs the grade, and answers 201 with the card as it now is and its new previews. 404 for a card the user does not have, 409 for a version that is not the card's, 422 naming the field for a rule it breaks.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);
        return routes;
    }

    /// <summary>The time now, to the microsecond Postgres keeps, so what is stored and what is
    /// answered from it are the same instant.</summary>
    internal static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static async Task<Results<Created<CardWithPreviews>, ProblemHttpResult>> Grade(
        Guid id, GradeInput input, HttpContext context, UserDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        var broken = new Broken();
        if (input.Rating is not (>= 1 and <= 4))
        {
            broken.Add("rating", "must be 1 to 4: Again 1, Hard 2, Good 3 or Easy 4");
        }

        if (input.Version is null)
        {
            broken.Add("version", "is required: the version of the card the grade was given from");
        }

        if (broken.Any)
        {
            return broken.Problem();
        }

        var rating = (Rating)input.Rating!.Value;
        var version = input.Version!.Value;
        var user = context.UserId();
        var now = Now(time);
        var outcome = await db.InUserTransaction(user, async transaction =>
        {
            var row = await CardEndpoints.ReadRow(transaction, id, cancellationToken);
            if (row is null)
            {
                return Outcome.Missing;
            }

            // The read gives the schedule to grade from; it does not decide whether the grade
            // counts. Only the conditional update below does, for a stale version as for two grades
            // racing from the same one, so the tests that send a stale grade exercise it.
            var before = row.Schedule;
            var after = Fsrs.Grade(before, rating, now);
            // Only from the version named: if the card is at another version, nothing changes and
            // nothing is logged. Every change to a card moves its version, so when a row changes,
            // the state graded from is the state replaced.
            var changed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cards
                SET state = @state, step = @step, stability = @stability, difficulty = @difficulty, due = @due,
                    last_review = @now, version = version + 1
                WHERE id = @id AND version = @version
                """,
                new { id, version, state = after.Name, step = after.Step, stability = after.Stability, difficulty = after.Difficulty, due = after.Due, now },
                transaction, cancellationToken: cancellationToken));
            if (changed == 0)
            {
                return Outcome.Stale;
            }

            await Log(transaction, user, id, rating, now, before, after, cancellationToken);
            var card = (await CardEndpoints.ReadRow(transaction, id, cancellationToken))!;
            return new Outcome(CardWithPreviews.From(card.Card, Fsrs.Preview(card.Schedule, now)), Exists: true);
        }, cancellationToken);

        return outcome switch
        {
            { Card: { } card } => TypedResults.Created((string?)null, card),
            { Exists: true } => Problems.Conflict(string.Create(CultureInfo.InvariantCulture,
                $"the card has changed since version {version}: read it again and grade it from there")),
            _ => Problems.NotFound("the card"),
        };
    }

    private static Task<int> Log(
        NpgsqlTransaction transaction, Guid user, Guid card, Rating rating, DateTimeOffset now, CardState before, CardState after, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO grades (id, user_id, card_id, rating, reviewed_at, elapsed_days, state_before, stability_before,
                                difficulty_before, state_after, stability_after, difficulty_after, due_after)
            VALUES (@id, @user, @card, @rating, @now, @elapsed, @stateBefore, @stabilityBefore,
                    @difficultyBefore, @stateAfter, @stabilityAfter, @difficultyAfter, @dueAfter)
            """,
            new
            {
                id = Guid.CreateVersion7(now),
                user,
                card,
                rating = (short)rating,
                now,
                // The time since the last grade, with its fraction, which the scheduler reads in
                // whole days; 0 for the first.
                elapsed = before.LastReview is { } last ? Math.Max(0, (now - last).TotalDays) : 0,
                stateBefore = before.Name,
                stabilityBefore = before.Stability,
                difficultyBefore = before.Difficulty,
                stateAfter = after.Name,
                stabilityAfter = after.Stability,
                difficultyAfter = after.Difficulty,
                dueAfter = after.Due,
            },
            transaction, cancellationToken: cancellationToken));

    private sealed record Outcome(CardWithPreviews? Card, bool Exists)
    {
        public static readonly Outcome Missing = new(null, false);
        public static readonly Outcome Stale = new(null, true);
    }
}

/// <summary>A grade as a request gives it.</summary>
/// <param name="Rating">Again 1, Hard 2, Good 3 or Easy 4.</param>
/// <param name="Version">The card's version the grade was given from. Required: a stale one is a
/// 409, so a grade sent twice counts once.</param>
public sealed record GradeInput(int? Rating, int? Version);
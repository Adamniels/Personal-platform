using System.Globalization;
using System.Text.Json;

using Dapper;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Projects;
using DevFlow.Protocol;
using DevFlow.Readings;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Acting;

/// <summary>What <c>POST /devflow/api/cards/{id}/answers</c> takes.</summary>
/// <param name="ReadingId">The reading whose questions the page showed, which must still be the
/// card's newest.</param>
/// <param name="Answers">One answer for each of the reading's questions, in its order.</param>
public sealed record AnswersRequest(Guid? ReadingId, IReadOnlyList<GivenAnswer?>? Answers);

/// <summary>One answer as the page sends it: the recommendation accepted, or the owner's own
/// text, never both.</summary>
/// <param name="Accept">True to accept the reader's recommendation.</param>
/// <param name="Text">The owner's own answer, 1 to <see cref="AnswerEndpoints.MostAnswer"/>
/// characters once trimmed.</param>
public sealed record GivenAnswer(bool? Accept, string? Text);

/// <summary>
/// The owner's answers to a card's questions, from "Waiting for you": posted as him, with his linked
/// token and never as the app, in one comment on the card's issue, then kept with the reading that
/// asked them and the card queued, its build's brief carrying them. Under the <c>Api</c> policy and
/// the origin check. Everything that can be refused without the code host is refused before it is
/// asked, and nothing is held while it answers. See "Reading a card" under "Jobs" in
/// devflow/README.md.
/// </summary>
public static class AnswerEndpoints
{
    /// <summary>The most characters an answer of the owner's own may have once trimmed.</summary>
    public const int MostAnswer = 4000;

    public static IEndpointRouteBuilder MapAnswers(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/cards/{id:guid}/answers", Answer)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("answerCard")
            .WithSummary("Answer a card's questions, as the caller, and queue it")
            .WithDescription("Sends the caller's answers to the questions of the reading reading_id of a card of Dev flow's own that needs answers: answers holds one for each question, in the reading's order, each either accept true, the reader's recommendation, or text, the caller's own, 1 to 4,000 characters once trimmed, never both. They are posted as one comment on the card's issue, as the caller's linked GitHub account and never as the app, listing each question with its answer and (recommended) where it was accepted; the comment's id is kept, so it never reads the card again, the answers are kept with the reading, and the card is queued, its build's brief carrying them under \"Answers to its questions\". A comment of the caller's on the issue that is exactly the one to post, left by a send whose answer was lost, is kept instead of posting it again. In order: 404 for a card the caller does not have; 422 without reading_id or answers; 409 when the card does not need answers; 409 when reading_id is not its newest reading; 422 when answers does not hold exactly one valid answer for each question; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and kept. 409 when the card moved on while GitHub answered, the comment posted; 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, nothing kept, and sending the same answers again finds a comment it took. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Answer(
        Guid id,
        AnswersRequest request,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] UserTokens tokens,
        [FromServices] ProjectSettingsStore settings,
        [FromServices] ICodeHost host,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await Find(database, userId, id, cancellationToken) is not { } card)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that id.");
        }

        if (request.ReadingId is not { } readingId)
        {
            return ActionEndpoints.Invalid("reading_id", "is missing; it is the reading whose questions the page showed");
        }

        if (request.Answers is not { } given)
        {
            return ActionEndpoints.Invalid("answers", "is missing");
        }

        if (card.State != "needs_answers")
        {
            return ActionEndpoints.Conflict("This card does not wait for answers", "Its answers are sent once, while it waits for them. Read the page again.");
        }

        if (card.ReadingId != readingId)
        {
            return ActionEndpoints.Conflict("The card's questions have changed since this was shown", "It was read again. Read the page again and answer its questions.");
        }

        var questions = JsonSerializer.Deserialize<List<ReadingQuestion>>(card.Questions, Protocol.Reading.Json) ?? [];
        if (Answers(given, questions) is not { } answers)
        {
            return ActionEndpoints.Invalid("answers", Problem(given, questions.Count));
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return ActionEndpoints.TokenProblem(userToken, context, time);
        }

        var repository = new Repository(card.Installation, card.RepositoryId, card.Owner, card.Name);
        var body = AnswerText.Comment(card.Number, readingId, questions, answers);
        long? commentId;
        switch (await Posted(host, userId, repository, card.Number, body, await settings.Mine(userId, cancellationToken), cancellationToken))
        {
            case CodeHostResult<long>.Found { Value: var found }:
                commentId = found;
                break;
            case CodeHostResult<long>.Unavailable { RetryAt: var notNow }:
                return ActionEndpoints.Unavailable(context, time, notNow, "Nothing was sent. Try again in a moment.");
            default:
                switch (await host.Comment(userId, access, repository, card.Number, body, cancellationToken))
                {
                    case CommentResult.Posted { Id: var posted }:
                        commentId = posted;
                        break;
                    case CommentResult.Refused { Status: var status, Message: var message }:
                        return ActionEndpoints.Conflict("GitHub refused the comment", ActionEndpoints.HostSaid(status, message));
                    case CommentResult.Unavailable { RetryAt: var retryAt }:
                        return ActionEndpoints.Unavailable(context, time, retryAt, "It is not known whether your answers were posted; sending them again keeps the comment GitHub took, if it took one.");
                    default:
                        throw new InvalidOperationException("a comment result that is not one of the three");
                }

                break;
        }

        var kept = await database.InUserTransaction(userId, transaction =>
            ReadingStore.Answered(transaction, card.ProjectId, card.Number, readingId, answers, commentId, time.GetUtcNow(), cancellationToken), cancellationToken);
        return kept
            ? TypedResults.NoContent()
            : ActionEndpoints.Conflict("The card moved on while GitHub answered", "Your answers were posted on its issue, and not kept here. Read the page again.");
    }

    /// <summary>The answers to keep, one for each of <paramref name="questions"/>, or null when
    /// <paramref name="given"/> breaks a rule, <see cref="Problem"/>.</summary>
    private static List<OwnerAnswer>? Answers(IReadOnlyList<GivenAnswer?> given, List<ReadingQuestion> questions)
    {
        if (given.Count != questions.Count || questions.Count == 0)
        {
            return null;
        }

        var answers = new List<OwnerAnswer>(given.Count);
        for (var at = 0; at < given.Count; at++)
        {
            switch (given[at])
            {
                case { Accept: true, Text: null }:
                    answers.Add(new OwnerAnswer(questions[at].Recommendation, true));
                    break;
                case { Accept: not true, Text: { } text } when text.Trim() is { Length: > 0 and <= MostAnswer } trimmed:
                    answers.Add(new OwnerAnswer(trimmed, false));
                    break;
                default:
                    return null;
            }
        }

        return answers;
    }

    /// <summary>Why <paramref name="given"/> is refused, naming the first answer that breaks a
    /// rule.</summary>
    private static string Problem(IReadOnlyList<GivenAnswer?> given, int questions)
    {
        if (given.Count != questions)
        {
            return string.Create(CultureInfo.InvariantCulture, $"holds {given.Count} answers; the card has {questions} questions, and each needs one, in order");
        }

        for (var at = 0; at < given.Count; at++)
        {
            var number = at + 1;
            var answer = given[at];
            var text = answer?.Text?.Trim();
            if (answer is null || (answer.Accept is not true && answer.Text is null))
            {
                return string.Create(CultureInfo.InvariantCulture, $"answer {number} neither accepts the recommendation nor is your own text");
            }

            if (answer.Accept is true && answer.Text is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"answer {number} both accepts the recommendation and is your own text; it is one or the other");
            }

            if (text is { Length: 0 })
            {
                return string.Create(CultureInfo.InvariantCulture, $"answer {number} is empty; write your answer or accept the recommendation");
            }

            if (text is { Length: > MostAnswer })
            {
                return string.Create(CultureInfo.InvariantCulture, $"answer {number} is {text.Length:N0} characters; it may have at most {MostAnswer:N0}");
            }
        }

        return "breaks a rule";
    }

    /// <summary>The id of a comment of the owner's, <paramref name="login"/>, already on the issue
    /// that is exactly <paramref name="body"/>, left by a send whose answer was lost, so it is never
    /// posted twice: Found with it, NotFound when there is none or the owner has no login, and
    /// Unavailable when the issue's comments could not be read, when nothing is posted.</summary>
    private static async Task<CodeHostResult<long>> Posted(ICodeHost host, Guid userId, Repository repository, int number, string body, string? login, CancellationToken cancellationToken)
    {
        if (login is null)
        {
            return new CodeHostResult<long>.NotFound();
        }

        return await host.Comments(userId, repository, number, cancellationToken) switch
        {
            CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments } =>
                comments.FirstOrDefault(comment => string.Equals(comment.Author, login, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(comment.Body.TrimEnd(), body.TrimEnd(), StringComparison.Ordinal)) is { } posted
                    ? new CodeHostResult<long>.Found(posted.Id)
                    : new CodeHostResult<long>.NotFound(),
            CodeHostResult<IReadOnlyList<Comment>>.Unavailable { RetryAt: var retryAt } => new CodeHostResult<long>.Unavailable(retryAt),
            _ => new CodeHostResult<long>.NotFound(),
        };
    }

    /// <summary>The caller's card with the id, with its project's repository and its newest
    /// reading's questions, or null when they have none.</summary>
    private static async Task<AnswerTarget?> Find(UserDb database, Guid userId, Guid id, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<AnswerTarget>(new CommandDefinition(
            """
            SELECT p.id AS ProjectId, p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner,
                   p.name AS Name, c.number AS Number, c.state AS State, cr.run_id AS ReadingId, coalesce(cr.questions::text, '[]') AS Questions
            FROM cards c
            JOIN projects p ON p.id = c.project_id
            LEFT JOIN LATERAL (SELECT * FROM card_readings newest
                               WHERE newest.project_id = c.project_id AND newest.card_number = c.number AND newest.outcome IS NOT NULL
                               ORDER BY newest.read_at DESC, newest.run_id DESC
                               LIMIT 1) cr ON true
            WHERE c.id = @id
            """,
            new { id },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    private sealed record AnswerTarget(Guid ProjectId, long Installation, long RepositoryId, string Owner, string Name, int Number, string State, Guid? ReadingId, string Questions);
}
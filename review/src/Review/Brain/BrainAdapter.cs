using System.Globalization;

using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Bundle;

using Review.Brain.Generated;
using Review.Brain.Generated.Brain.Api.Events;

namespace Review.Brain;

/// <summary>
/// The one <see cref="IBrain"/>: <c>POST /brain/api/events</c> through the client Kiota generates
/// from <c>contracts/brain/openapi.json</c>, into <c>Brain/Generated/</c>, whose types never leave
/// this class. See "What Review tells the brain" in review/README.md.
///
/// Each call carries the user's token as <c>Authorization: Bearer</c>, the only header that does,
/// and the platform's own <c>Origin</c>, without which the brain refuses any request that changes
/// state, see "How a feature authenticates" in <c>High-level/docs/feature-contract.md</c>. Each call
/// waits <see cref="Timeout"/> and no more, on <paramref name="time"/>.
/// </summary>
/// <param name="http">Sends the request as it is: no retries, no redirects, see
/// <see cref="Client"/>. Kiota's own handlers are not used, so nothing is added or resent.</param>
/// <param name="brainUrl">The brain's own address, <c>REVIEW_BRAIN_URL</c>.</param>
/// <param name="publicOrigin">The platform's origin, <c>REVIEW_PUBLIC_ORIGIN</c>.</param>
public sealed class BrainAdapter(HttpClient http, Uri brainUrl, string publicOrigin, TimeProvider time) : IBrain
{
    /// <summary>How long one call waits for the brain.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly BrainClient _client = new(new DefaultRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
    {
        BaseUrl = brainUrl.ToString().TrimEnd('/'),
    });

    /// <summary>The client for the life of the service: its connections renewed so a moved brain
    /// is found again, redirects not followed, since one would carry the token elsewhere, and no
    /// timeout of its own, since each call has <see cref="Timeout"/>.</summary>
    public static HttpClient Client() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public async Task<BrainAnswer> RecordEvent(string token, AssessmentEvent assessment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(assessment);
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var raw = new NativeResponseHandler();
        try
        {
            await _client.Brain.Api.Events.PostAsync(Body(assessment), request =>
            {
                request.Headers.Add("Authorization", $"Bearer {token}");
                request.Headers.Add("Origin", publicOrigin);
                request.Options.Add(new ResponseHandlerOption { ResponseHandler = raw });
            }, call.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return BrainAnswer.Unavailable("timeout");
        }
        catch (HttpRequestException failed)
        {
            return BrainAnswer.Unavailable($"no connection: {failed.HttpRequestError}");
        }

        using var response = raw.Value as HttpResponseMessage
            ?? throw new InvalidOperationException("Kiota gave the response handler no response");
        return Answer((int)response.StatusCode);
    }

    /// <summary>What a status means. 200 and 201 are the two the brain records with, 200 for a
    /// key it already has. Any other 4xx but 401 and 429 would be refused again, so it is final.
    /// Everything else, 401 and 429 included, may pass.</summary>
    internal static BrainAnswer Answer(int status) => status switch
    {
        200 or 201 => BrainAnswer.Recorded,
        401 or 429 => BrainAnswer.Unavailable(Status(status)),
        >= 400 and < 500 => BrainAnswer.Refused(status),
        _ => BrainAnswer.Unavailable(Status(status)),
    };

    private static string Status(int status) => string.Create(CultureInfo.InvariantCulture, $"status {status}");

    /// <summary>The event in the generated client's types. Fields left null, <c>entity</c> and
    /// <c>item</c>, are not written.</summary>
    private static EventsPostRequestBody Body(AssessmentEvent assessment)
    {
        var payload = assessment.Payload;
        return new EventsPostRequestBody
        {
            Verb = assessment.Verb,
            Topic = new EventsPostRequestBody.EventsPostRequestBody_topic { String = assessment.Topic },
            Origin = assessment.Origin,
            Source = new EventsPostRequestBody.EventsPostRequestBody_source { String = assessment.Source },
            Run = new EventsPostRequestBody.EventsPostRequestBody_run { String = assessment.Run },
            OccurredAt = new EventsPostRequestBody.EventsPostRequestBody_occurred_at { DateTimeOffset = assessment.OccurredAt },
            OccurredPrecision = EventsPostRequestBody_occurred_precision.Instant,
            Evidence = assessment.Evidence,
            Payload = new EventsPostRequestBody_payload
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["card_id"] = payload.CardId,
                    ["deck_id"] = payload.DeckId,
                    ["rating"] = payload.Rating,
                    ["rating_name"] = payload.RatingName,
                    ["elapsed_days"] = payload.ElapsedDays,
                    ["state_before"] = payload.StateBefore,
                    ["stability_before"] = payload.StabilityBefore!,
                    ["stability_after"] = payload.StabilityAfter,
                    ["difficulty_after"] = payload.DifficultyAfter,
                    ["due_after"] = payload.DueAfter,
                },
            },
            PayloadVersion = assessment.PayloadVersion,
            IdempotencyKey = assessment.IdempotencyKey,
        };
    }
}
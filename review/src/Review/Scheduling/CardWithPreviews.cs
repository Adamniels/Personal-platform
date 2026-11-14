using Review.Cards;

namespace Review.Scheduling;

/// <summary>
/// A card as a single read answers it, <see cref="Card"/> field for field, with when each grade,
/// given now, would bring it back: each item of the queue, and the card after a grade. The page
/// shows the four times on the buttons. The fields are written out rather than inherited, so the
/// schema marks each one required, as it does a <see cref="Card"/>'s.
/// </summary>
/// <param name="Previews">When each grade would bring the card back, were it given now.</param>
public sealed record CardWithPreviews(
    Guid Id,
    CardDeck Deck,
    string Front,
    string Back,
    CardTopic[] Topics,
    CardSource? Source,
    string State,
    DateTime Due,
    DateTime? LastReview,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    PreviewTimes Previews)
{
    public static CardWithPreviews From(Card card, Previews previews)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(previews);
        return new(
            card.Id,
            card.Deck,
            card.Front,
            card.Back,
            card.Topics,
            card.Source,
            card.State,
            card.Due,
            card.LastReview,
            card.Version,
            card.CreatedAt,
            card.UpdatedAt,
            new PreviewTimes(previews.Again.UtcDateTime, previews.Hard.UtcDateTime, previews.Good.UtcDateTime, previews.Easy.UtcDateTime));
    }
}

/// <summary>
/// <see cref="Scheduling.Previews"/> on the wire: UTC times, written as every other time review
/// answers is.
/// </summary>
public sealed record PreviewTimes(DateTime Again, DateTime Hard, DateTime Good, DateTime Easy);
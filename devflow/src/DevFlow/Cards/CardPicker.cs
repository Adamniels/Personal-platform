using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using DevFlow.CodeHost;
using DevFlow.Projects;

namespace DevFlow.Cards;

/// <summary>Where picking puts a card: queued, held by the hold label, or ignored as not the
/// owner's.</summary>
public enum Verdict
{
    Queued,
    Held,
    Ignored,
}

/// <summary>What was picked, which later parts compare against before building and before
/// merging: the body's SHA-256, the title, and the id of the edit history's newest entry, null when
/// it was never edited.</summary>
public sealed record PickedRecord(byte[] BodySha256, string Title, string? NewestEdit);

/// <summary>A card as one read of its project found it.</summary>
/// <param name="Priority"><c>high</c>, <c>none</c> or <c>low</c>, from its priority label.</param>
/// <param name="Reason">Why it is held, ignored or waits, a sentence for the page; null when it
/// is queued with nothing to wait for.</param>
/// <param name="WaitsFor">What it is blocked by that is still open, as <c>#12</c>, or
/// <c>owner/name#12</c> in another repository.</param>
/// <param name="Record">What was picked; null when it is ignored, since an ignored card is not
/// picked.</param>
public sealed record PickedCard(
    int Number,
    string Title,
    Uri Url,
    DateTimeOffset CreatedAt,
    string Priority,
    Verdict Verdict,
    string? Reason,
    IReadOnlyList<string> WaitsFor,
    PickedRecord? Record);

/// <summary>
/// Reads a project's cards from the code host, as the app, in its read: the open issues carrying
/// the pick-up label, never a pull request among them; then, only for one the owner's linked login
/// wrote, its edits; then, only for one nobody else edited and that is not held, what it is
/// blocked by. The only account whose cards count is the owner's, fixed until part 5. See "Cards"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public static class CardPicker
{
    /// <summary>Why every card is ignored while the owner has not linked GitHub.</summary>
    public const string NotLinked = "Dev flow does not know your GitHub login: link GitHub on the Projects tab";

    private const string HighPriority = "priority: high";
    private const string LowPriority = "priority: low";

    /// <summary>
    /// The project's cards as the code host shows them now, by number. Not found when the code host
    /// does not show the repository; unavailable when any read does not answer, so half a picture
    /// is never stored. An issue that is gone between the list and a later read of it is left out,
    /// as it would be from the next list.
    /// </summary>
    /// <param name="ownerLogin">The owner's linked login, whatever the link's state; null without
    /// one, when no card is his.</param>
    public static async Task<CodeHostResult<IReadOnlyList<PickedCard>>> Pick(
        ICodeHost host, Guid userId, Repository repository, ProjectSettings settings, string? ownerLogin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<Issue> listed;
        switch (await host.LabelledIssues(userId, repository, settings.PickupLabel, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var found }:
                listed = found;
                break;
            case CodeHostResult<IReadOnlyList<Issue>>.NotFound:
                return new CodeHostResult<IReadOnlyList<PickedCard>>.NotFound();
            case CodeHostResult<IReadOnlyList<Issue>>.Unavailable { RetryAt: var retryAt }:
                return new CodeHostResult<IReadOnlyList<PickedCard>>.Unavailable(retryAt);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        var cards = new List<PickedCard>();
        foreach (var issue in listed.Where(issue => !issue.IsPullRequest).DistinctBy(issue => issue.Number).OrderBy(issue => issue.Number))
        {
            var card = new PickedCard(issue.Number, issue.Title, issue.Url, issue.CreatedAt, Priority(issue.Labels), Verdict.Ignored, null, [], null);
            if (ownerLogin is null)
            {
                cards.Add(card with { Reason = NotLinked });
                continue;
            }

            if (!IsOwner(issue.Author, ownerLogin))
            {
                cards.Add(card with { Reason = $"Written by {issue.Author}: only your own cards are picked" });
                continue;
            }

            IReadOnlyList<IssueEdit> edits;
            switch (await host.Edits(userId, repository, issue.Number, cancellationToken))
            {
                case CodeHostResult<IReadOnlyList<IssueEdit>>.Found { Value: var found }:
                    edits = found;
                    break;
                case CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound:
                    continue;
                case CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<IReadOnlyList<PickedCard>>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            if (edits.Where(edit => !IsOwner(edit.Editor, ownerLogin)).OrderBy(edit => edit.At).FirstOrDefault() is { } stranger)
            {
                cards.Add(card with { Reason = $"Edited by {stranger.Editor}: only cards nobody else has edited are picked" });
                continue;
            }

            var record = new PickedRecord(
                SHA256.HashData(Encoding.UTF8.GetBytes(issue.Body)),
                issue.Title,
                edits.OrderByDescending(edit => edit.At).Select(edit => edit.Id).FirstOrDefault());
            if (issue.Labels.Contains(settings.HoldLabel, StringComparer.OrdinalIgnoreCase))
            {
                cards.Add(card with { Verdict = Verdict.Held, Reason = $"Held by the label {settings.HoldLabel}", Record = record });
                continue;
            }

            IReadOnlyList<Dependency> blockers;
            switch (await host.BlockedBy(userId, repository, issue.Number, cancellationToken))
            {
                case CodeHostResult<IReadOnlyList<Dependency>>.Found { Value: var found }:
                    blockers = found;
                    break;
                case CodeHostResult<IReadOnlyList<Dependency>>.NotFound:
                    continue;
                case CodeHostResult<IReadOnlyList<Dependency>>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<IReadOnlyList<PickedCard>>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            var waitsFor = WaitsFor(repository, blockers);
            cards.Add(card with
            {
                Verdict = Verdict.Queued,
                Reason = waitsFor.Count == 0 ? null : $"Waits for {Listed(waitsFor)}",
                WaitsFor = waitsFor,
                Record = record,
            });
        }

        return new CodeHostResult<IReadOnlyList<PickedCard>>.Found(cards);
    }

    /// <summary>Logins are one account whatever their case, as GitHub's are.</summary>
    private static bool IsOwner(string login, string ownerLogin) => string.Equals(login, ownerLogin, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>high</c> with the high label, else <c>low</c> with the low one, else
    /// <c>none</c>; labels matched whatever their case.</summary>
    private static string Priority(IReadOnlyList<string> labels) =>
        labels.Contains(HighPriority, StringComparer.OrdinalIgnoreCase) ? "high"
        : labels.Contains(LowPriority, StringComparer.OrdinalIgnoreCase) ? "low"
        : "none";

    /// <summary>The open blockers, this repository's as <c>#12</c> by number first, then any in
    /// another repository as <c>owner/name#12</c>.</summary>
    private static IReadOnlyList<string> WaitsFor(Repository repository, IReadOnlyList<Dependency> blockers) =>
    [
        .. blockers
            .Where(blocker => blocker.Open)
            .Select(blocker => (Here: IsHere(repository, blocker), blocker))
            .OrderBy(pair => pair.Here ? 0 : 1)
            .ThenBy(pair => pair.blocker.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.blocker.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.blocker.Number)
            .Select(pair => pair.Here
                ? string.Create(CultureInfo.InvariantCulture, $"#{pair.blocker.Number}")
                : string.Create(CultureInfo.InvariantCulture, $"{pair.blocker.Owner}/{pair.blocker.Name}#{pair.blocker.Number}"))
            .Distinct(StringComparer.Ordinal),
    ];

    private static bool IsHere(Repository repository, Dependency blocker) =>
        string.Equals(blocker.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(blocker.Name, repository.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>#1</c>, <c>#1 and #2</c>, <c>#1, #2 and #3</c>.</summary>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
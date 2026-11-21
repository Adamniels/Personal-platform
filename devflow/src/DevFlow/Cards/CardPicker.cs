using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using DevFlow.CodeHost;
using DevFlow.Projects;

namespace DevFlow.Cards;

/// <summary>Where picking puts a card: queued, held by the hold label, or ignored as not a
/// trusted account's.</summary>
public enum Verdict
{
    Queued,
    Held,
    Ignored,
}

/// <summary>What was picked, which later parts compare against before building and before
/// merging: the body's SHA-256, the title, and the id of the edit history's newest entry, null when
/// it was never edited; and the body itself, which a job's brief quotes.</summary>
public sealed record PickedRecord(byte[] BodySha256, string Title, string? NewestEdit, string Body);

/// <summary>A card as one read of its project found it.</summary>
/// <param name="Priority"><c>high</c>, <c>none</c> or <c>low</c>, from its priority label, or its
/// feature's for a card of a feature.</param>
/// <param name="Reason">Why it is held, ignored or waits, a sentence for the page; null when it
/// is queued with nothing to wait for.</param>
/// <param name="WaitsFor">What it is blocked by that is still open, as <c>#12</c>, or
/// <c>owner/name#12</c> in another repository.</param>
/// <param name="Record">What was picked; null when it is ignored, since an ignored card is not
/// picked, and for a closed card of a feature, which is not read again.</param>
/// <param name="Feature">The number of the feature it is a card of, null for a card on its
/// own.</param>
/// <param name="Open">Whether its issue is open. Only a card of a feature may be closed, and it is
/// kept only when Dev flow built it, which <see cref="CardStore.Store"/> decides.</param>
public sealed record PickedCard(
    int Number,
    string Title,
    Uri Url,
    DateTimeOffset CreatedAt,
    string Priority,
    Verdict Verdict,
    string? Reason,
    IReadOnlyList<string> WaitsFor,
    PickedRecord? Record,
    int? Feature = null,
    bool Open = true);

/// <summary>A feature as one read of its project found it: its parent issue, checked as a card is,
/// and its cards in their sub-issue order.</summary>
/// <param name="Priority">The parent's, from its priority label.</param>
/// <param name="Reason">Why it is held or ignored, or which of its sub-issues are in another
/// repository; null when there is nothing to say.</param>
/// <param name="Record">What was picked of the parent; null when it is ignored.</param>
/// <param name="Cards">Its direct sub-issues in this repository, pull requests left out, in their
/// order, each with <see cref="PickedCard.Feature"/> its number.</param>
public sealed record PickedFeature(
    int Number,
    string Title,
    Uri Url,
    DateTimeOffset CreatedAt,
    string Priority,
    Verdict Verdict,
    string? Reason,
    PickedRecord? Record,
    IReadOnlyList<PickedCard> Cards)
{
    /// <summary>The parent as a card on its own, which it stays when it started as one before it
    /// had sub-issues.</summary>
    public PickedCard AsCard() => new(Number, Title, Url, CreatedAt, Priority, Verdict, Verdict == Verdict.Queued ? null : Reason, [], Record);
}

/// <summary>What one read of a project picked: its features, by number, and every other card,
/// each on its own, by number.</summary>
public sealed record PickedProject(IReadOnlyList<PickedFeature> Features, IReadOnlyList<PickedCard> Cards);

/// <summary>
/// Reads a project's cards and features from the code host, as the app, in its read: the open
/// issues carrying the pick-up label, never a pull request among them, and each one's sub-issues.
/// One with sub-issues is a feature, whose direct sub-issues in this repository are its cards;
/// one without is a card on its own. Each card, and a feature's parent, is checked by the same
/// rules: then, only for one a trusted account of the project wrote, its edits; then, only for one
/// nobody outside the list edited, its timeline's label events, judged by <see cref="LabelActors"/>;
/// then, only for a card that is still picked and not held, what it is blocked by. The accounts
/// whose cards count are the project's trusted accounts, see <see cref="TrustedAccounts"/>. See
/// "Cards" and "Features and their cards" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class CardPicker
{
    /// <summary>Why every card is ignored while the owner has not linked GitHub.</summary>
    public const string NotLinked = "Dev flow does not know your GitHub login: link GitHub on the Projects tab";

    /// <summary>Why a sub-issue of a sub-issue, or one in another repository, is not a card of
    /// the feature.</summary>
    public const string NotDirect = "Only a feature's direct sub-issues in this repository are its cards";

    private const string HighPriority = "priority: high";
    private const string LowPriority = "priority: low";

    /// <summary>Why each card of a feature that is not picked is ignored.</summary>
    public static string FeatureNotPicked(int feature) => string.Create(CultureInfo.InvariantCulture, $"Its feature, #{feature}, is not picked");

    /// <summary>Why a card written by a login that is not on the project's list is ignored.</summary>
    public static string NotTrusted(string login) => $"{login} is not a trusted account of this project";

    /// <summary>Why a card edited by a login that is not on the project's list is ignored.</summary>
    public static string EditedByUntrusted(string login) => $"Edited by {login}, who is not a trusted account of this project";

    /// <summary>Why a card whose pick-up label no trusted account put on is ignored, naming who
    /// put it on, when the timeline says.</summary>
    public static string PickupNotTrusted(string label, string? by) => by is null
        ? $"The pick-up label {label} was not added by a trusted account of this project"
        : $"The pick-up label {label} was added by {by}, who is not a trusted account of this project";

    /// <summary>Why a card whose hold label a trusted account put on is held though it is off,
    /// naming who took it off, when the timeline says.</summary>
    public static string HoldTakenOffUntrusted(string label, string? by) => by is null
        ? $"Held by the label {label}, which a trusted account put on and none took off"
        : $"Held by the label {label}, taken off by {by}, who is not a trusted account of this project";

    /// <summary>Why a sub-issue of an issue that started as a card on its own is not a card.</summary>
    public static string ParentIsACard(int parent) => string.Create(CultureInfo.InvariantCulture, $"Its parent, #{parent}, is being built as a card");

    /// <summary>
    /// The project's features and cards as the code host shows them now, by number. Not found when
    /// the code host does not show the repository; unavailable when any read does not answer, so
    /// half a picture is never stored. An issue that is gone between the list and a later read of
    /// it is left out, as it would be from the next list.
    /// </summary>
    /// <param name="trusted">The project's trusted accounts, with the owner's linked login
    /// whatever the link's state; without one, no card is picked.</param>
    public static async Task<CodeHostResult<PickedProject>> Pick(
        ICodeHost host, Guid userId, Repository repository, ProjectSettings settings, TrustedAccounts trusted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(trusted);
        var check = new Checks(host, userId, repository, settings, trusted, cancellationToken);

        IReadOnlyList<Issue> found;
        switch (await host.LabelledIssues(userId, repository, settings.PickupLabel, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var value }:
                found = value;
                break;
            case CodeHostResult<IReadOnlyList<Issue>>.NotFound:
                return new CodeHostResult<PickedProject>.NotFound();
            case CodeHostResult<IReadOnlyList<Issue>>.Unavailable { RetryAt: var retryAt }:
                return new CodeHostResult<PickedProject>.Unavailable(retryAt);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        // Every labelled issue's sub-issues, which tell a feature from a card; one gone since the
        // list is left out.
        var listed = new List<Issue>();
        var subIssues = new Dictionary<int, IReadOnlyList<SubIssue>>();
        foreach (var issue in found.Where(issue => !issue.IsPullRequest).DistinctBy(issue => issue.Number).OrderBy(issue => issue.Number))
        {
            switch (await host.SubIssues(userId, repository, issue.Number, cancellationToken))
            {
                case CodeHostResult<IReadOnlyList<SubIssue>>.Found { Value: var value }:
                    listed.Add(issue);
                    subIssues[issue.Number] = value;
                    break;
                case CodeHostResult<IReadOnlyList<SubIssue>>.NotFound:
                    break;
                case CodeHostResult<IReadOnlyList<SubIssue>>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedProject>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        // Which labelled issue each issue here is a sub-issue of. A labelled issue that is no other
        // labelled issue's sub-issue stands at the top: a feature with sub-issues, a card without.
        var parentOf = new Dictionary<int, int>();
        foreach (var issue in listed)
        {
            foreach (var sub in subIssues[issue.Number].Where(sub => IsHere(repository, sub)))
            {
                parentOf.TryAdd(sub.Number, issue.Number);
            }
        }

        var features = new List<PickedFeature>();
        var cards = new List<PickedCard>();
        var claimed = new HashSet<int>();
        foreach (var issue in listed.Where(issue => !parentOf.ContainsKey(issue.Number) && subIssues[issue.Number].Count > 0))
        {
            switch (await PickFeature(check, issue, subIssues[issue.Number]))
            {
                case CodeHostResult<PickedFeature>.Found { Value: var feature }:
                    features.Add(feature);
                    claimed.Add(feature.Number);
                    claimed.UnionWith(feature.Cards.Select(card => card.Number));
                    break;
                case CodeHostResult<PickedFeature>.NotFound:
                    break;
                case CodeHostResult<PickedFeature>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedProject>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        // Below a feature's cards: a labelled issue that is a sub-issue of a sub-issue, and the
        // open sub-issues here of any labelled issue that is not at the top. Each is ignored on its
        // own, never a card of the feature and never a second feature.
        var deeper = listed.Where(issue => parentOf.ContainsKey(issue.Number))
            .SelectMany(issue => subIssues[issue.Number])
            .Where(sub => IsHere(repository, sub) && sub.Open)
            .Select(sub => (sub.Number, sub.Title, sub.Url, sub.CreatedAt))
            .Concat(listed.Select(issue => (issue.Number, issue.Title, issue.Url, issue.CreatedAt)));
        foreach (var (number, title, url, createdAt) in deeper)
        {
            if (claimed.Contains(number) || !parentOf.ContainsKey(number))
            {
                continue;
            }

            claimed.Add(number);
            cards.Add(new PickedCard(number, title, url, createdAt, "none", Verdict.Ignored, NotDirect, [], null));
        }

        foreach (var issue in listed.Where(issue => !claimed.Contains(issue.Number)))
        {
            switch (await check.Card(Candidate.Of(issue), Priority(issue.Labels), feature: null, featureHeld: false))
            {
                case CodeHostResult<PickedCard>.Found { Value: var card }:
                    cards.Add(card);
                    break;
                case CodeHostResult<PickedCard>.NotFound:
                    break;
                case CodeHostResult<PickedCard>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedProject>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        return new CodeHostResult<PickedProject>.Found(new PickedProject(
            [.. features.OrderBy(feature => feature.Number)],
            [.. cards.OrderBy(card => card.Number)]));
    }

    /// <summary>
    /// A feature: its parent checked as a card is, by everything but what blocks it; then its
    /// direct sub-issues in this repository, pull requests left out, in their order. Each card is
    /// checked by the card rules, held with a held parent, and ignored with a parent that is not
    /// picked, which costs no more reads. A closed one is passed on unread, for the store to keep
    /// only when Dev flow built it. Not found when the parent is gone since the list.
    /// </summary>
    private static async Task<CodeHostResult<PickedFeature>> PickFeature(Checks check, Issue issue, IReadOnlyList<SubIssue> subIssues)
    {
        var priority = Priority(issue.Labels);
        PickedCard parent;
        switch (await check.Card(Candidate.Of(issue), priority, feature: null, featureHeld: false, readBlockers: false))
        {
            case CodeHostResult<PickedCard>.Found { Value: var value }:
                parent = value;
                break;
            case CodeHostResult<PickedCard>.NotFound:
                return new CodeHostResult<PickedFeature>.NotFound();
            case CodeHostResult<PickedCard>.Unavailable { RetryAt: var retryAt }:
                return new CodeHostResult<PickedFeature>.Unavailable(retryAt);
            default:
                throw new InvalidOperationException("a code host result that is not one of the three");
        }

        var cards = new List<PickedCard>();
        foreach (var sub in subIssues.Where(sub => IsHere(check.Repository, sub)))
        {
            var candidate = Candidate.Of(sub);
            if (parent.Verdict == Verdict.Ignored || !sub.Open)
            {
                cards.Add(new PickedCard(sub.Number, sub.Title, sub.Url, sub.CreatedAt, priority,
                    parent.Verdict == Verdict.Ignored ? Verdict.Ignored : Verdict.Queued,
                    parent.Verdict == Verdict.Ignored ? FeatureNotPicked(issue.Number) : null,
                    [], null, issue.Number, sub.Open));
                continue;
            }

            switch (await check.Card(candidate, priority, issue.Number, featureHeld: parent.Verdict == Verdict.Held))
            {
                case CodeHostResult<PickedCard>.Found { Value: var card }:
                    cards.Add(card);
                    break;
                case CodeHostResult<PickedCard>.NotFound:
                    break;
                case CodeHostResult<PickedCard>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedFeature>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }
        }

        // A sub-issue in another repository is not stored, its number being that repository's;
        // the feature names it instead, while nothing else is to be said of it.
        IReadOnlyList<string> elsewhere =
        [
            .. subIssues
                .Where(sub => !sub.IsPullRequest && !IsHere(check.Repository, sub))
                .Select(sub => string.Create(CultureInfo.InvariantCulture, $"{sub.Owner}/{sub.Name}#{sub.Number}")),
        ];
        var reason = parent.Verdict == Verdict.Queued && elsewhere.Count > 0
            ? $"{NotDirect}: {Listed(elsewhere)} {(elsewhere.Count == 1 ? "is not one" : "are not")}"
            : parent.Reason;
        return new CodeHostResult<PickedFeature>.Found(new PickedFeature(
            issue.Number, issue.Title, issue.Url, issue.CreatedAt, priority, parent.Verdict, reason, parent.Record, cards));
    }

    /// <summary>What the card rules read of an issue, a labelled one or a sub-issue.</summary>
    private sealed record Candidate(int Number, string Title, Uri Url, DateTimeOffset CreatedAt, string Author, IReadOnlyList<string> Labels, string Body)
    {
        public static Candidate Of(Issue issue) => new(issue.Number, issue.Title, issue.Url, issue.CreatedAt, issue.Author, issue.Labels, issue.Body);

        public static Candidate Of(SubIssue sub) => new(sub.Number, sub.Title, sub.Url, sub.CreatedAt, sub.Author, sub.Labels, sub.Body);
    }

    /// <summary>The card rules, as one read of one project applies them.</summary>
    private sealed record Checks(ICodeHost Host, Guid UserId, Repository Repository, ProjectSettings Settings, TrustedAccounts Trusted, CancellationToken CancellationToken)
    {
        /// <summary>
        /// The card rules, in order, the first that holds deciding: no link, an author who is not
        /// trusted, an editor who is not, for a labelled issue a pick-up label no trusted account
        /// put on, a held feature, its own hold label, on it or taken off only by someone
        /// untrusted, and then, when <paramref name="readBlockers"/>, what it is blocked by. A card
        /// costs only what decides it: the timeline is read only past the edits, and for a card of
        /// a feature only when no hold label on it or its feature decides first. Not found when the
        /// issue is gone since the list.
        /// </summary>
        public async Task<CodeHostResult<PickedCard>> Card(Candidate issue, string priority, int? feature, bool featureHeld, bool readBlockers = true)
        {
            var card = new PickedCard(issue.Number, issue.Title, issue.Url, issue.CreatedAt, priority, Verdict.Ignored, null, [], null, feature);
            if (Trusted.Mine is null)
            {
                return new CodeHostResult<PickedCard>.Found(card with { Reason = NotLinked });
            }

            if (!Trusted.Trusts(issue.Author))
            {
                return new CodeHostResult<PickedCard>.Found(card with { Reason = NotTrusted(issue.Author) });
            }

            IReadOnlyList<IssueEdit> edits;
            switch (await Host.Edits(UserId, Repository, issue.Number, CancellationToken))
            {
                case CodeHostResult<IReadOnlyList<IssueEdit>>.Found { Value: var found }:
                    edits = found;
                    break;
                case CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound:
                    return new CodeHostResult<PickedCard>.NotFound();
                case CodeHostResult<IReadOnlyList<IssueEdit>>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedCard>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            if (edits.Where(edit => !Trusted.Trusts(edit.Editor)).OrderBy(edit => edit.At).FirstOrDefault() is { } stranger)
            {
                return new CodeHostResult<PickedCard>.Found(card with { Reason = EditedByUntrusted(stranger.Editor) });
            }

            // A labelled issue, a card on its own or a feature's parent, is switched on only by a
            // trusted account's pick-up label; a card of a feature carries none of its own.
            CodeHostResult<IReadOnlyList<LabelEvent>>? timeline = null;
            if (feature is null)
            {
                timeline = await Host.LabelEvents(UserId, Repository, issue.Number, CancellationToken);
                if (timeline is not CodeHostResult<IReadOnlyList<LabelEvent>>.Found { Value: var events })
                {
                    return Failed(timeline);
                }

                if (LabelActors.Judge(Settings.PickupLabel, LabelKind.SwitchesOn, issue.Labels, events, Trusted) is { Counts: false, By: var by })
                {
                    return new CodeHostResult<PickedCard>.Found(card with { Reason = PickupNotTrusted(Settings.PickupLabel, by) });
                }
            }

            var record = new PickedRecord(
                SHA256.HashData(Encoding.UTF8.GetBytes(issue.Body)),
                issue.Title,
                edits.OrderByDescending(edit => edit.At).Select(edit => edit.Id).FirstOrDefault(),
                issue.Body);
            if (featureHeld)
            {
                return new CodeHostResult<PickedCard>.Found(card with { Verdict = Verdict.Held, Reason = $"The feature is held by the label {Settings.HoldLabel}", Record = record });
            }

            if (issue.Labels.Contains(Settings.HoldLabel, StringComparer.OrdinalIgnoreCase))
            {
                return new CodeHostResult<PickedCard>.Found(card with { Verdict = Verdict.Held, Reason = $"Held by the label {Settings.HoldLabel}", Record = record });
            }

            // Off the issue, the hold label still holds while only someone untrusted took it off
            // since a trusted account put it on, which only its timeline says.
            timeline ??= await Host.LabelEvents(UserId, Repository, issue.Number, CancellationToken);
            if (timeline is not CodeHostResult<IReadOnlyList<LabelEvent>>.Found { Value: var history })
            {
                return Failed(timeline);
            }

            if (LabelActors.Judge(Settings.HoldLabel, LabelKind.HoldsBack, issue.Labels, history, Trusted) is { Counts: true, By: var takenOffBy })
            {
                return new CodeHostResult<PickedCard>.Found(card with { Verdict = Verdict.Held, Reason = HoldTakenOffUntrusted(Settings.HoldLabel, takenOffBy), Record = record });
            }

            if (!readBlockers)
            {
                return new CodeHostResult<PickedCard>.Found(card with { Verdict = Verdict.Queued, Record = record });
            }

            IReadOnlyList<Dependency> blockers;
            switch (await Host.BlockedBy(UserId, Repository, issue.Number, CancellationToken))
            {
                case CodeHostResult<IReadOnlyList<Dependency>>.Found { Value: var found }:
                    blockers = found;
                    break;
                case CodeHostResult<IReadOnlyList<Dependency>>.NotFound:
                    return new CodeHostResult<PickedCard>.NotFound();
                case CodeHostResult<IReadOnlyList<Dependency>>.Unavailable { RetryAt: var retryAt }:
                    return new CodeHostResult<PickedCard>.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            var waitsFor = WaitsFor(Repository, blockers);
            return new CodeHostResult<PickedCard>.Found(card with
            {
                Verdict = Verdict.Queued,
                Reason = waitsFor.Count == 0 ? null : $"Waits for {Listed(waitsFor)}",
                WaitsFor = waitsFor,
                Record = record,
            });
        }
    }

    /// <summary>A read that did not answer, not found or unavailable, as the card's.</summary>
    private static CodeHostResult<PickedCard> Failed<T>(CodeHostResult<T> result) => result switch
    {
        CodeHostResult<T>.NotFound => new CodeHostResult<PickedCard>.NotFound(),
        CodeHostResult<T>.Unavailable { RetryAt: var retryAt } => new CodeHostResult<PickedCard>.Unavailable(retryAt),
        _ => throw new InvalidOperationException("a code host result that is not a failure"),
    };

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
            .Select(blocker => (Here: IsHere(repository, blocker.Owner, blocker.Name), blocker))
            .OrderBy(pair => pair.Here ? 0 : 1)
            .ThenBy(pair => pair.blocker.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.blocker.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.blocker.Number)
            .Select(pair => pair.Here
                ? string.Create(CultureInfo.InvariantCulture, $"#{pair.blocker.Number}")
                : string.Create(CultureInfo.InvariantCulture, $"{pair.blocker.Owner}/{pair.blocker.Name}#{pair.blocker.Number}"))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>A sub-issue that may be a card: an issue, not a pull request, in this
    /// repository.</summary>
    private static bool IsHere(Repository repository, SubIssue sub) => !sub.IsPullRequest && IsHere(repository, sub.Owner, sub.Name);

    private static bool IsHere(Repository repository, string owner, string name) =>
        string.Equals(owner, repository.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(name, repository.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>#1</c>, <c>#1 and #2</c>, <c>#1, #2 and #3</c>.</summary>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
using DevFlow.CodeHost;
using DevFlow.Projects;

namespace DevFlow.Cards;

/// <summary>What a label does, which decides whose hand it counts by.</summary>
public enum LabelKind
{
    /// <summary>It switches the automation on, as the pick-up label does: only a trusted account
    /// can, so it counts only when the newest change of it by one put it on.</summary>
    SwitchesOn,

    /// <summary>It holds the automation back, as the hold label does: anyone may, so it counts
    /// while it is on the issue whoever put it there, and only a trusted account can take it off.
    /// The review label is read this way too, from "Sometimes" on.</summary>
    HoldsBack,
}

/// <summary>Whether a label counts, and the login the reason names.</summary>
/// <param name="Counts">Whether the label is to be read as on.</param>
/// <param name="By">For a label that switches on and does not count, who put it on last; for one
/// that holds back and counts though it is off, who took it off last. Null otherwise, and when the
/// timeline does not say.</param>
public sealed record LabelCount(bool Counts, string? By);

/// <summary>
/// The one rule every label is read by, from who changed it: the newest change of it on the
/// issue's timeline by a trusted account decides, and the rule fails closed, so someone untrusted
/// can neither switch anything on nor take a hold off. Pure: the caller reads the timeline and the
/// labels. Labels and logins are one whatever their case, as GitHub's are; events at the same time
/// keep their order. See "How a card is picked" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class LabelActors
{
    /// <summary>Whether <paramref name="label"/>, read as <paramref name="kind"/> says, counts on an
    /// issue that carries <paramref name="labels"/> and whose timeline lists
    /// <paramref name="events"/>, for a project that trusts <paramref name="trusted"/>.</summary>
    public static LabelCount Judge(string label, LabelKind kind, IReadOnlyList<string> labels, IReadOnlyList<LabelEvent> events, TrustedAccounts trusted)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(trusted);
        var on = labels.Contains(label, StringComparer.OrdinalIgnoreCase);
        var changes = events.Where(change => string.Equals(change.Label, label, StringComparison.OrdinalIgnoreCase)).OrderBy(change => change.At).ToList();
        var trustedAddedIt = changes.LastOrDefault(change => trusted.Trusts(change.Actor)) is { Change: LabelChange.Added };
        return kind switch
        {
            LabelKind.SwitchesOn when !on => new(false, null),
            LabelKind.SwitchesOn when trustedAddedIt => new(true, null),
            LabelKind.SwitchesOn => new(false, changes.LastOrDefault(change => change.Change == LabelChange.Added)?.Actor),
            LabelKind.HoldsBack when on => new(true, null),
            LabelKind.HoldsBack when trustedAddedIt => new(true, changes.LastOrDefault(change => change.Change == LabelChange.Removed)?.Actor),
            LabelKind.HoldsBack => new(false, null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a label kind that is not one of the two"),
        };
    }
}
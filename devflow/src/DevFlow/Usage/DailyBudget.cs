using System.Globalization;

namespace DevFlow.Usage;

/// <summary>
/// A project's daily budget in tokens, off by default: what the setting takes. See "Settings" under
/// "Cards" in devflow/README.md.
/// </summary>
public static class DailyBudget
{
    /// <summary>A million tokens, the unit the settings page takes the budget in.</summary>
    public const long Million = 1_000_000;

    /// <summary>The fewest millions a budget may be.</summary>
    public const long LeastMillions = 1;

    /// <summary>The most millions a budget may be.</summary>
    public const long MostMillions = 10_000;

    /// <summary>Why a budget that is not null or a whole number of millions within the range is
    /// refused.</summary>
    public static readonly string Problem = string.Create(CultureInfo.InvariantCulture,
        $"must be null for off, or a whole number of millions of tokens from {LeastMillions * Million} to {MostMillions * Million}");

    /// <summary>Whether <paramref name="budget"/> is one the setting takes: null for off, or a whole
    /// number of millions from <see cref="LeastMillions"/> to <see cref="MostMillions"/>.</summary>
    public static bool Holds(long? budget) =>
        budget is not { } tokens || (tokens % Million == 0 && tokens / Million is >= LeastMillions and <= MostMillions);
}
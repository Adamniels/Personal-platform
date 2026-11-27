namespace DevFlow.Usage;

/// <summary>
/// The one definition of a token, everywhere Dev flow shows or counts one: <c>input_tokens +
/// cache_creation_input_tokens + output_tokens</c>, a figure Claude did not give counted as 0. Cache
/// reads are shown beside the tokens and are never in them. Here as C#, for what a page reads run by
/// run, and as SQL, for what a query sums, so the two can never say different things. See "Usage
/// and history" in devflow/README.md.
/// </summary>
public static class Tokens
{
    /// <summary>A run's tokens: its input, its cache writes and its output, each 0 when
    /// missing.</summary>
    public static long Of(long? input, long? cacheCreation, long? output) => (input ?? 0) + (cacheCreation ?? 0) + (output ?? 0);

    /// <summary>A run's tokens, or null when Claude gave none of the three figures, as a page shows
    /// one run: "no figures" is not "0 tokens".</summary>
    public static long? OrNone(long? input, long? cacheCreation, long? output) =>
        input is null && cacheCreation is null && output is null ? null : Of(input, cacheCreation, output);

    /// <summary><see cref="Of"/> as SQL over the run named <paramref name="run"/>, a table alias
    /// from the caller's own query.</summary>
    public static string Sql(string run) =>
        $"(coalesce({run}.input_tokens, 0) + coalesce({run}.cache_creation_input_tokens, 0) + coalesce({run}.output_tokens, 0))";

    /// <summary>The run's cache reads as SQL, 0 when missing, shown beside its tokens.</summary>
    public static string CacheReadsSql(string run) => $"coalesce({run}.cache_read_input_tokens, 0)";
}
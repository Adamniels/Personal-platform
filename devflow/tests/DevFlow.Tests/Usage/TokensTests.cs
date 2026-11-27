using DevFlow.Usage;

namespace DevFlow.Tests.Usage;

/// <summary>
/// The one definition of a token, <see cref="Tokens"/>, which every page and count of Dev flow
/// reads. Pure. See "Usage and history" in devflow/README.md.
/// </summary>
public sealed partial class UsageTests
{
    [Fact]
    public void TokensAreOneDefinition()
    {
        // Input, cache writes and output, each as Claude gave it.
        Assert.Equal(41 + 18_204 + 6_113, Tokens.Of(input: 41, cacheCreation: 18_204, output: 6_113));
        // A figure Claude did not give counts as 0, and none at all is 0.
        Assert.Equal(6_113, Tokens.Of(input: null, cacheCreation: null, output: 6_113));
        Assert.Equal(0, Tokens.Of(null, null, null));

        // The same sum as SQL over a run, with each missing figure 0; cache reads are never in it.
        Assert.Equal(
            "(coalesce(r.input_tokens, 0) + coalesce(r.cache_creation_input_tokens, 0) + coalesce(r.output_tokens, 0))",
            Tokens.Sql("r"));
        Assert.DoesNotContain("cache_read", Tokens.Sql("r"), StringComparison.Ordinal);
        Assert.Equal("coalesce(r.cache_read_input_tokens, 0)", Tokens.CacheReadsSql("r"));
    }
}
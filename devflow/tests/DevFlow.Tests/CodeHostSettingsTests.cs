using System.Security.Cryptography;

using DevFlow.Settings;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>
/// <c>DEVFLOW_CODE_HOST</c>: unset is the fake, <c>fake</c> and <c>github</c> are themselves, and
/// anything else stops startup naming it. Until the GitHub adapter is built, the GitHub mode
/// stops startup saying so. See "Settings" in devflow/README.md.
/// </summary>
public sealed class CodeHostSettingsTests
{
    private const string CodeHost = "DEVFLOW_CODE_HOST";

    /// <summary>A vault key, which the GitHub mode needs before it reaches the code host.</summary>
    private static readonly (string, string) Key = ("DEVFLOW_VAULT_KEY", $"k1:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");

    private static DevFlowSettings Load(params (string Name, string Value)[] set)
    {
        var values = new Dictionary<string, string>(Harness.MinimalEnvironment);
        foreach (var (name, value) in set)
        {
            values[name] = value;
        }

        return DevFlowSettings.Load(name => values.GetValueOrDefault(name));
    }

    [Fact]
    public void TheModeParsesOrRefusesStartup()
    {
        Assert.Equal(CodeHostMode.Fake, Load().CodeHost);
        Assert.Equal(CodeHostMode.Fake, Load((CodeHost, "fake")).CodeHost);
        Assert.Equal(CodeHostMode.GitHub, Load((CodeHost, "github"), Key).CodeHost);

        // Set and empty is refused, never the fake: the hosted copy passes it empty when its .env
        // leaves it out, and must not fall back to the fake that way.
        foreach (var mode in new[] { "", "GitHub", "Fake", "FAKE", "gitlab", " github", "fake ", "fake,github" })
        {
            var refused = Assert.Throws<StartupException>(() => Load((CodeHost, mode), Key));
            Assert.StartsWith($"{CodeHost}: ", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheGitHubModeStopsStartupUntilItsAdapterIsBuilt()
    {
        var refused = await Assert.ThrowsAsync<StartupException>(() => Harness.Start(Load((CodeHost, "github"), Key)));

        Assert.StartsWith($"{CodeHost}: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not built yet", refused.Message, StringComparison.Ordinal);
    }
}
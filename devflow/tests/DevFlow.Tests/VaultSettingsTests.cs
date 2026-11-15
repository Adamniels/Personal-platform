using System.Security.Cryptography;

using DevFlow.Settings;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>
/// The vault's keys, <c>DEVFLOW_VAULT_KEY</c> and <c>DEVFLOW_VAULT_OLD_KEYS</c>, and the one rule of
/// <c>DEVFLOW_CODE_HOST</c> they follow: a key parses or stops startup, naming the setting and never
/// showing it, and without one the fake mode makes one in memory while the GitHub mode refuses. See
/// "The vault" in devflow/README.md.
/// </summary>
public sealed class VaultSettingsTests
{
    private const string Key = "DEVFLOW_VAULT_KEY";
    private const string OldKeys = "DEVFLOW_VAULT_OLD_KEYS";
    private const string CodeHost = "DEVFLOW_CODE_HOST";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Key material, the base64 of 32 bytes, as <c>openssl rand -base64 32</c> prints it.</summary>
    private static readonly string Material = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string OtherMaterial = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The settings from the minimal environment with <paramref name="set"/> on top.</summary>
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
    public void AKeyParsesOrRefusesStartup()
    {
        var settings = Load((Key, $"k2:{Material}"), (OldKeys, $"k1:{OtherMaterial},0:{Convert.ToBase64String(new byte[32])}"));

        Assert.Equal("k2", settings.Vault.Current.Id);
        Assert.Equal(Convert.FromBase64String(Material), settings.Vault.Current.Bytes.ToArray());
        Assert.False(settings.Vault.Generated);
        Assert.Equal(["k1", "0"], settings.Vault.Old.Select(key => key.Id));
        Assert.Equal(Convert.FromBase64String(OtherMaterial), settings.Vault.Old[0].Bytes.ToArray());
        // The longest id there may be.
        Assert.Equal("abcdefgh12345678", Load((Key, $"abcdefgh12345678:{Material}")).Vault.Current.Id);

        // Empty is unset, since the hosted copy passes each with ${VAR:-}: no old keys.
        Assert.Empty(Load((Key, $"k2:{Material}"), (OldKeys, "")).Vault.Old);
        Assert.Empty(Load((Key, $"k2:{Material}")).Vault.Old);

        // What the settings print names each key's id and never its bytes.
        Assert.DoesNotContain(Material, settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(OtherMaterial, settings.ToString(), StringComparison.Ordinal);
        Assert.Contains("k2", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Material, settings.Vault.Current.ToString(), StringComparison.Ordinal);

        foreach (var (name, set) in Refused)
        {
            var refused = Assert.Throws<StartupException>(() => Load(set));
            Assert.StartsWith($"{name}: ", refused.Message, StringComparison.Ordinal);
            foreach (var (_, value) in set)
            {
                Assert.DoesNotContain(value, refused.Message, StringComparison.Ordinal);
            }

            NoKeyIn(refused.Message);
        }
    }

    // Each fails, and stops startup naming the setting that is wrong.
    private static readonly (string Name, (string, string)[] Set)[] Refused =
    [
        // A missing id, and an id that is empty, too long, or not lowercase letters and digits.
        (Key, [(Key, Material)]),
        (Key, [(Key, $":{Material}")]),
        (Key, [(Key, $"abcdefgh123456789:{Material}")]),
        (Key, [(Key, $"K1:{Material}")]),
        (Key, [(Key, $"k-1:{Material}")]),
        (Key, [(Key, $"k1 :{Material}")]),
        // A wrong length, or not exactly the padded base64 of 32 bytes.
        (Key, [(Key, $"k1:{Convert.ToBase64String(new byte[31])}")]),
        (Key, [(Key, $"k1:{Convert.ToBase64String(new byte[33])}")]),
        (Key, [(Key, $"k1:{Material.TrimEnd('=')}")]),
        (Key, [(Key, $"k1:{Material} ")]),
        (Key, [(Key, $"k1: {Material}")]),
        (Key, [(Key, $"k1:{Material.Replace('+', '-').Replace('/', '_')}x")]),
        (Key, [(Key, "k1:")]),
        (Key, [(Key, $"k1:{Material},k0:{OtherMaterial}")]),
        // The old keys: the same rules for each, two with one id, one repeating the current id,
        // and an empty entry.
        (OldKeys, [(Key, $"k2:{Material}"), (OldKeys, OtherMaterial)]),
        (OldKeys, [(Key, $"k2:{Material}"), (OldKeys, $"k1:{Convert.ToBase64String(new byte[16])}")]),
        (OldKeys, [(Key, $"k3:{Material}"), (OldKeys, $"k1:{OtherMaterial},k1:{Convert.ToBase64String(new byte[32])}")]),
        (OldKeys, [(Key, $"k2:{Material}"), (OldKeys, $"k2:{OtherMaterial}")]),
        (OldKeys, [(Key, $"k2:{Material}"), (OldKeys, $"k1:{OtherMaterial},")]),
        (OldKeys, [(Key, $"k2:{Material}"), (OldKeys, $"k1:{OtherMaterial}, k0:{Convert.ToBase64String(new byte[32])}")]),
        // Old keys with no current one to seal with.
        (Key, [(OldKeys, $"k1:{OtherMaterial}")]),
    ];

    [Fact]
    public async Task WithoutAKeyTheFakeModeMakesOneAndTheGitHubModeRefuses()
    {
        // Unset, the code host is the fake, and the vault makes a key of its own, a new one each time.
        var made = Load().Vault;
        Assert.True(made.Generated);
        Assert.Equal(32, made.Current.Bytes.Length);
        Assert.NotEqual(made.Current.Bytes.ToArray(), Load().Vault.Current.Bytes.ToArray());
        Assert.True(Load((CodeHost, "fake")).Vault.Generated);
        Assert.True(Load((Key, "")).Vault.Generated);

        // A key that is set is used in either mode.
        Assert.False(Load((Key, $"k1:{Material}")).Vault.Generated);
        Assert.False(Load((CodeHost, "github"), (Key, $"k1:{Material}")).Vault.Generated);

        // Without one, the GitHub mode stops startup, naming the key's setting.
        foreach (var set in new[] { new[] { (CodeHost, "github") }, [(CodeHost, "github"), (Key, "")] })
        {
            var refused = Assert.Throws<StartupException>(() => Load(set));
            Assert.StartsWith($"{Key}: ", refused.Message, StringComparison.Ordinal);
        }

        // The code host is fake or github and nothing else.
        foreach (var mode in new[] { "", "GitHub", "gitlab", " github" })
        {
            var refused = Assert.Throws<StartupException>(() => Load((CodeHost, mode)));
            Assert.StartsWith($"{CodeHost}: ", refused.Message, StringComparison.Ordinal);
        }

        // The service says so in one line at startup, without the key. Here it stops after it, at
        // the page's build, which the tests' folder does not hold.
        var app = Environment.GetEnvironmentVariable(Databases.DevFlow.AppVariable)
            ?? throw new InvalidOperationException($"{Databases.DevFlow.AppVariable} is not set, see tests/DevFlow.Tests/Harness/TestDatabase.cs");
        await using var stderr = new StringWriter { NewLine = "\n" };
        var code = await Commands.Run([], name => name == DevFlowSettings.AppDatabaseUrl ? app : null, TextWriter.Null, stderr, Ct);

        Assert.Equal(1, code);
        var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "devflow: DEVFLOW_VAULT_KEY is not set, so the vault seals with a key made for this process alone, and what it seals is lost when it stops",
            lines[0]);
        Assert.Contains("web/dist", lines[1], StringComparison.Ordinal);
    }

    /// <summary>Asserts that <paramref name="text"/> holds none of this class's key material.</summary>
    private static void NoKeyIn(string text)
    {
        foreach (var material in new[] { Material, OtherMaterial, Convert.ToBase64String(new byte[32]) })
        {
            // A third of it, so a key cut, trimmed or wrapped is found as well.
            Assert.DoesNotContain(material[..16], text, StringComparison.Ordinal);
        }
    }
}
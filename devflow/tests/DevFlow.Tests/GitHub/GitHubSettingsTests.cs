using System.Security.Cryptography;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;
using DevFlow.Settings;

using Microsoft.Extensions.DependencyInjection;

using PlatformData;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The GitHub mode's four settings: <c>DEVFLOW_GITHUB_CLIENT_ID</c>,
/// <c>DEVFLOW_GITHUB_CLIENT_SECRET</c>, <c>DEVFLOW_GITHUB_PRIVATE_KEY_FILE</c> and
/// <c>DEVFLOW_GITHUB_APP_SLUG</c>. Each is required with <c>DEVFLOW_CODE_HOST=github</c>, refused
/// naming the setting and never its value, and ignored in the fake mode. See "Settings" in
/// devflow/README.md.
/// </summary>
public sealed class GitHubSettingsTests
{
    private const string ClientId = "DEVFLOW_GITHUB_CLIENT_ID";
    private const string ClientSecret = "DEVFLOW_GITHUB_CLIENT_SECRET";
    private const string KeyFile = "DEVFLOW_GITHUB_PRIVATE_KEY_FILE";
    private const string AppSlug = "DEVFLOW_GITHUB_APP_SLUG";

    private static readonly string[] Names = [ClientId, ClientSecret, KeyFile, AppSlug];

    /// <summary>The GitHub mode's environment with <paramref name="set"/> on top.</summary>
    private static DevFlowSettings Load(params (string Name, string? Value)[] set) =>
        TestApp.Load([.. TestApp.Environment.Select(pair => (pair.Name, (string?)pair.Value)), .. set]);

    [Fact]
    public void GitHubModeNeedsEverySettingOrRefusesStartup()
    {
        var settings = Load().GitHub!;
        Assert.Equal(TestApp.ClientId, settings.ClientId);
        Assert.Equal(TestApp.ClientSecret, settings.ClientSecret);
        Assert.Equal(TestApp.AppSlug, settings.AppSlug);
        Assert.Equal(TestApp.Key.ExportRSAPublicKey(), settings.PrivateKey.ExportRSAPublicKey());

        // What the settings print names neither the secret nor the key.
        foreach (var printed in new[] { settings.ToString(), Load().ToString() })
        {
            Assert.DoesNotContain(TestApp.ClientSecret, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", printed, StringComparison.Ordinal);
            Assert.Contains(TestApp.AppSlug, printed, StringComparison.Ordinal);
        }

        // Each of the four missing in turn, unset or empty, since the hosted copy passes each with
        // ${VAR:-}.
        foreach (var name in Names)
        {
            foreach (var value in new string?[] { null, "" })
            {
                Refused(name, (name, value));
            }
        }

        // A key file that does not exist, and a folder.
        var missing = Path.Combine(Path.GetTempPath(), $"devflow-no-such-key-{Guid.NewGuid():N}.pem");
        Refused(KeyFile, (KeyFile, missing));
        Refused(KeyFile, (KeyFile, Path.GetTempPath()));

        // One that is not an RSA private key in PEM: text, a key's public half, a key of another
        // kind, a PEM that does not decode, a key too short, and one encrypted with a password.
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var small = RSA.Create(1024);
        foreach (var pem in new[]
        {
            "",
            "not a key at all",
            TestApp.Key.ExportRSAPublicKeyPem(),
            TestApp.Key.ExportSubjectPublicKeyInfoPem(),
            ec.ExportECPrivateKeyPem(),
            ec.ExportPkcs8PrivateKeyPem(),
            "-----BEGIN RSA PRIVATE KEY-----\nbm90IGEga2V5\n-----END RSA PRIVATE KEY-----\n",
            small.ExportRSAPrivateKeyPem(),
            TestApp.Key.ExportEncryptedPkcs8PrivateKeyPem("pass", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)),
        })
        {
            var refused = Refused(KeyFile, (KeyFile, TestApp.WriteKey(pem)));
            // The first line of the key's body, or what there is of it.
            var body = pem.Split('\n') is [_, var line, ..] ? line : pem;
            if (body.Length > 3)
            {
                Assert.DoesNotContain(body[..Math.Min(20, body.Length)], refused, StringComparison.Ordinal);
            }
        }

        // Both forms GitHub and openssl write a private key in are taken.
        Assert.NotNull(Load((KeyFile, TestApp.WriteKey(TestApp.Key.ExportRSAPrivateKeyPem()))).GitHub);
        Assert.NotNull(Load((KeyFile, TestApp.WriteKey(TestApp.Key.ExportPkcs8PrivateKeyPem()))).GitHub);

        // A client id, a secret and a slug that are not what GitHub gives.
        foreach (var (name, value) in new[]
        {
            (ClientId, "Iv23 li"),
            (ClientId, "Iv23li/x"),
            (ClientSecret, "abc def"),
            (ClientSecret, "abc\n"),
            (AppSlug, "Platform Devflow"),
            (AppSlug, "-devflow"),
            (AppSlug, "devflow/"),
        })
        {
            Refused(name, (name, value));
        }
    }

    [Fact]
    public async Task ARefusalIsOneLineNamingTheSetting()
    {
        var values = new Dictionary<string, string>(Harness.MinimalEnvironment);
        foreach (var (name, value) in TestApp.Environment)
        {
            values[name] = value;
        }

        var missing = Path.Combine(Path.GetTempPath(), $"devflow-no-such-key-{Guid.NewGuid():N}.pem");
        values[KeyFile] = missing;
        await using var stderr = new StringWriter { NewLine = "\n" };

        var code = await Commands.Run([], name => values.GetValueOrDefault(name), TextWriter.Null, stderr, GitHubHost.Ct);

        Assert.Equal(1, code);
        var line = Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith($"devflow: {KeyFile}: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, line, StringComparison.Ordinal);
    }

    [Fact]
    public void FakeModeIgnoresThem()
    {
        // Unset, empty or wrong, the fake mode reads none of them.
        Assert.Null(TestApp.Load().GitHub);
        Assert.Null(TestApp.Load(("DEVFLOW_CODE_HOST", "fake")).GitHub);
        Assert.Null(TestApp.Load(
            ("DEVFLOW_CODE_HOST", "fake"),
            (ClientId, "not an id"),
            (ClientSecret, ""),
            (KeyFile, "/no/such/file.pem"),
            (AppSlug, "Not A Slug")).GitHub);
        // An empty file, as the hosted check mounts, is never read either.
        Assert.Null(TestApp.Load(("DEVFLOW_CODE_HOST", "fake"), (KeyFile, TestApp.WriteKey(""))).GitHub);
    }

    [Fact]
    public async Task GitHubModeBuildsTheGitHubCodeHost()
    {
        await using var harness = await Harness.Start(Load());

        // Every part of Dev flow gets the scrub of the project's values, GitHub's adapter behind it.
        Assert.IsType<ScrubbingCodeHost>(harness.Services.GetRequiredService<ICodeHost>());
        Assert.IsType<GitHubCodeHost>(harness.Services.GetRequiredKeyedService<ICodeHost>(ScrubbingCodeHost.Unscrubbed));
        // The fake's addresses do not exist.
        Assert.Equal(404, (await harness.Get("/devflow/api/fake/world", Contract.Token)).Response.StatusCode);
    }

    /// <summary>Asserts loading with <paramref name="set"/> stops startup naming
    /// <paramref name="name"/> and none of the values, and returns the message.</summary>
    private static string Refused(string name, params (string Name, string? Value)[] set)
    {
        var refused = Assert.Throws<StartupException>(() => Load(set));
        Assert.StartsWith($"{name}: ", refused.Message, StringComparison.Ordinal);
        // The four settings' values, as set here or by the environment; the mode's own word is
        // named in a message on purpose.
        foreach (var (setting, value) in set.Concat(TestApp.Environment.Select(pair => (pair.Name, (string?)pair.Value))))
        {
            if (Names.Contains(setting) && !string.IsNullOrEmpty(value) && value.Length > 3)
            {
                Assert.DoesNotContain(value, refused.Message, StringComparison.Ordinal);
            }
        }

        Assert.Null(refused.InnerException);
        return refused.Message;
    }
}
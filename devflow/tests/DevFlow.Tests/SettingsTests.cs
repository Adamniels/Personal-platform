using DevFlow.Settings;

namespace DevFlow.Tests;

/// <summary>
/// <see cref="DevFlowSettings"/>: unset is the default, and a set value that does not parse stops
/// the service at startup, naming the setting. See "Settings" in devflow/README.md.
/// </summary>
public sealed class SettingsTests
{
    private static DevFlowSettings Load(params (string Name, string Value)[] set)
    {
        var values = set.ToDictionary(pair => pair.Name, pair => pair.Value);
        return DevFlowSettings.Load(name => values.GetValueOrDefault(name));
    }

    [Fact]
    public void EachSettingParsesOrRefusesStartup()
    {
        var defaults = Load();
        Assert.Equal("127.0.0.1", defaults.ListenHost);
        Assert.Equal(8084, defaults.ListenPort);
        Assert.Equal(new Uri("http://127.0.0.1:8080"), defaults.CoreUrl);
        Assert.Equal("https://platform.test", defaults.PublicOrigin);
        Assert.False(defaults.ExposeOpenApi);

        foreach (var (name, value) in Malformed)
        {
            var refused = Assert.Throws<StartupException>(() => Load((name, value)));
            Assert.StartsWith($"{name}: ", refused.Message, StringComparison.Ordinal);
        }
    }

    // Each value is set, and none parses.
    private static readonly (string Name, string Value)[] Malformed =
    [
        ("DEVFLOW_LISTEN_ADDR", ""),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1"),
        ("DEVFLOW_LISTEN_ADDR", ":8084"),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1:"),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1:0"),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1:65536"),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1:80a"),
        ("DEVFLOW_LISTEN_ADDR", "127.0.0.1:+8084"),
        ("DEVFLOW_LISTEN_ADDR", "http://127.0.0.1:8084"),
        ("DEVFLOW_LISTEN_ADDR", " 127.0.0.1:8084"),
        ("DEVFLOW_LISTEN_ADDR", "host name:8084"),
        ("DEVFLOW_CORE_URL", ""),
        ("DEVFLOW_CORE_URL", "127.0.0.1:8080"),
        ("DEVFLOW_CORE_URL", "ftp://127.0.0.1:8080"),
        ("DEVFLOW_CORE_URL", "http://127.0.0.1:8080/"),
        ("DEVFLOW_CORE_URL", "http://127.0.0.1:8080/auth"),
        ("DEVFLOW_CORE_URL", "http://127.0.0.1:8080?x=1"),
        ("DEVFLOW_CORE_URL", "http://user@127.0.0.1:8080"),
        ("DEVFLOW_CORE_URL", "http://127.0.0.1:"),
        ("DEVFLOW_CORE_URL", " http://127.0.0.1:8080"),
        ("DEVFLOW_PUBLIC_ORIGIN", ""),
        ("DEVFLOW_PUBLIC_ORIGIN", "https://platform.test/"),
        ("DEVFLOW_PUBLIC_ORIGIN", "https://Platform.test"),
        ("DEVFLOW_PUBLIC_ORIGIN", "https://platform.test:443"),
        ("DEVFLOW_PUBLIC_ORIGIN", "http://platform.test:80"),
        ("DEVFLOW_PUBLIC_ORIGIN", "platform.test"),
        ("DEVFLOW_PUBLIC_ORIGIN", "https://platform.test/path"),
        ("DEVFLOW_PUBLIC_ORIGIN", "https://user@platform.test"),
        ("DEVFLOW_EXPOSE_OPENAPI", ""),
        ("DEVFLOW_EXPOSE_OPENAPI", "true"),
        ("DEVFLOW_EXPOSE_OPENAPI", "yes"),
        ("DEVFLOW_EXPOSE_OPENAPI", " 1"),
    ];

    [Theory]
    [InlineData("0.0.0.0:8084", "0.0.0.0", 8084)]
    [InlineData("localhost:9000", "localhost", 9000)]
    [InlineData("[::1]:8084", "[::1]", 8084)]
    public void TheListenAddressIsAHostAndAPort(string value, string host, int port)
    {
        var settings = Load(("DEVFLOW_LISTEN_ADDR", value));

        Assert.Equal(host, settings.ListenHost);
        Assert.Equal(port, settings.ListenPort);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://core.internal")]
    [InlineData("http://localhost:9999")]
    public void TheCoreUrlIsASchemeAHostAndAPort(string value)
    {
        Assert.Equal(new Uri(value), Load(("DEVFLOW_CORE_URL", value)).CoreUrl);
    }

    [Theory]
    [InlineData("https://platform.test")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://platform.test:8443")]
    public void ThePublicOriginIsAsABrowserSendsIt(string value)
    {
        Assert.Equal(value, Load(("DEVFLOW_PUBLIC_ORIGIN", value)).PublicOrigin);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void TheSchemaIsExposedOnlyWhenAskedFor(string value, bool exposed)
    {
        Assert.Equal(exposed, Load(("DEVFLOW_EXPOSE_OPENAPI", value)).ExposeOpenApi);
    }
}
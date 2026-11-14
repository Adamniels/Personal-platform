using Review.Settings;

namespace Review.Tests;

/// <summary>
/// <see cref="ReviewSettings"/>: unset is the default, and a set value that does not parse stops
/// the service at startup, naming the setting. See "Settings" in review/README.md.
/// </summary>
public sealed class SettingsTests
{
    private static ReviewSettings Load(params (string Name, string Value)[] set)
    {
        var values = set.ToDictionary(pair => pair.Name, pair => pair.Value);
        return ReviewSettings.Load(name => values.GetValueOrDefault(name));
    }

    [Fact]
    public void EachSettingParsesOrRefusesStartup()
    {
        var defaults = Load();
        Assert.Equal("127.0.0.1", defaults.ListenHost);
        Assert.Equal(8083, defaults.ListenPort);
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
        ("REVIEW_LISTEN_ADDR", ""),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1"),
        ("REVIEW_LISTEN_ADDR", ":8083"),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1:"),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1:0"),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1:65536"),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1:80a"),
        ("REVIEW_LISTEN_ADDR", "127.0.0.1:+8083"),
        ("REVIEW_LISTEN_ADDR", "http://127.0.0.1:8083"),
        ("REVIEW_LISTEN_ADDR", " 127.0.0.1:8083"),
        ("REVIEW_LISTEN_ADDR", "host name:8083"),
        ("REVIEW_CORE_URL", ""),
        ("REVIEW_CORE_URL", "127.0.0.1:8080"),
        ("REVIEW_CORE_URL", "ftp://127.0.0.1:8080"),
        ("REVIEW_CORE_URL", "http://127.0.0.1:8080/"),
        ("REVIEW_CORE_URL", "http://127.0.0.1:8080/auth"),
        ("REVIEW_CORE_URL", "http://127.0.0.1:8080?x=1"),
        ("REVIEW_CORE_URL", "http://user@127.0.0.1:8080"),
        ("REVIEW_CORE_URL", "http://127.0.0.1:"),
        ("REVIEW_CORE_URL", " http://127.0.0.1:8080"),
        ("REVIEW_PUBLIC_ORIGIN", ""),
        ("REVIEW_PUBLIC_ORIGIN", "https://platform.test/"),
        ("REVIEW_PUBLIC_ORIGIN", "https://Platform.test"),
        ("REVIEW_PUBLIC_ORIGIN", "https://platform.test:443"),
        ("REVIEW_PUBLIC_ORIGIN", "http://platform.test:80"),
        ("REVIEW_PUBLIC_ORIGIN", "platform.test"),
        ("REVIEW_PUBLIC_ORIGIN", "https://platform.test/path"),
        ("REVIEW_PUBLIC_ORIGIN", "https://user@platform.test"),
        ("REVIEW_EXPOSE_OPENAPI", ""),
        ("REVIEW_EXPOSE_OPENAPI", "true"),
        ("REVIEW_EXPOSE_OPENAPI", "yes"),
        ("REVIEW_EXPOSE_OPENAPI", " 1"),
    ];

    [Theory]
    [InlineData("0.0.0.0:8083", "0.0.0.0", 8083)]
    [InlineData("localhost:9000", "localhost", 9000)]
    [InlineData("[::1]:8083", "[::1]", 8083)]
    public void TheListenAddressIsAHostAndAPort(string value, string host, int port)
    {
        var settings = Load(("REVIEW_LISTEN_ADDR", value));

        Assert.Equal(host, settings.ListenHost);
        Assert.Equal(port, settings.ListenPort);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://core.internal")]
    [InlineData("http://localhost:9999")]
    public void TheCoreUrlIsASchemeAHostAndAPort(string value)
    {
        Assert.Equal(new Uri(value), Load(("REVIEW_CORE_URL", value)).CoreUrl);
    }

    [Theory]
    [InlineData("https://platform.test")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://platform.test:8443")]
    public void ThePublicOriginIsAsABrowserSendsIt(string value)
    {
        Assert.Equal(value, Load(("REVIEW_PUBLIC_ORIGIN", value)).PublicOrigin);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void TheSchemaIsExposedOnlyWhenAskedFor(string value, bool exposed)
    {
        Assert.Equal(exposed, Load(("REVIEW_EXPOSE_OPENAPI", value)).ExposeOpenApi);
    }
}
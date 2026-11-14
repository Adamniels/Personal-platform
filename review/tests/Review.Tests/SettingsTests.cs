using Review.Settings;

namespace Review.Tests;

/// <summary>
/// <see cref="ReviewSettings"/>: unset is the default, and a set value that does not parse stops
/// the service at startup, naming the setting. See "Settings" in review/README.md.
/// </summary>
public sealed class SettingsTests
{
    /// <summary>The settings from the minimal environment with <paramref name="set"/> on top.</summary>
    private static ReviewSettings Load(params (string Name, string Value)[] set)
    {
        var values = new Dictionary<string, string>(Harness.MinimalEnvironment);
        foreach (var (name, value) in set)
        {
            values[name] = value;
        }

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
        Assert.Equal("Europe/Stockholm", defaults.DayZone.Id);
        Assert.Equal(new Uri("http://127.0.0.1:8082"), defaults.BrainUrl);

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
        ("REVIEW_BRAIN_URL", ""),
        ("REVIEW_BRAIN_URL", "127.0.0.1:8082"),
        ("REVIEW_BRAIN_URL", "http://127.0.0.1:8082/"),
        ("REVIEW_BRAIN_URL", "http://127.0.0.1:8082/brain"),
        ("REVIEW_BRAIN_URL", "http://user@127.0.0.1:8082"),
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
        ("REVIEW_APP_DATABASE_URL", ""),
        ("REVIEW_DAY_ZONE", ""),
        ("REVIEW_DAY_ZONE", "Mars/Olympus_Mons"),
        ("REVIEW_DAY_ZONE", " Europe/Stockholm"),
        // A Windows name, which .NET would map to an IANA zone: only an IANA name is accepted.
        ("REVIEW_DAY_ZONE", "W. Europe Standard Time"),
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
    [InlineData("http://127.0.0.1:8082")]
    [InlineData("http://localhost:9999")]
    public void TheBrainUrlIsASchemeAHostAndAPort(string value)
    {
        Assert.Equal(new Uri(value), Load(("REVIEW_BRAIN_URL", value)).BrainUrl);
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

    [Theory]
    [InlineData("Europe/Stockholm")]
    [InlineData("America/New_York")]
    [InlineData("UTC")]
    public void TheDayZoneIsAnIanaName(string value)
    {
        Assert.Equal(value, Load(("REVIEW_DAY_ZONE", value)).DayZone.Id);
    }

    [Fact]
    public void AnUnknownDayZoneIsNamedWhenStartupStops()
    {
        var refused = Assert.Throws<StartupException>(() => Load(("REVIEW_DAY_ZONE", "Mars/Olympus_Mons")));

        Assert.Contains("Mars/Olympus_Mons", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppDatabaseIsRequired()
    {
        var refused = Assert.Throws<StartupException>(() => ReviewSettings.Load(_ => null));

        Assert.StartsWith("REVIEW_APP_DATABASE_URL: ", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432/review", "127.0.0.1", 5432, "review_app", "secret-a1b2", "review")]
    [InlineData("postgresql://review_app:secret-a1b2@db.internal/review", "db.internal", 5432, "review_app", "secret-a1b2", "review")]
    [InlineData("postgres://review_app:p%40ss%3Aw%2Fd@[::1]:6543/review", "::1", 6543, "review_app", "p@ss:w/d", "review")]
    public void TheAppDatabaseIsAPostgresUrl(string value, string host, int port, string user, string password, string database)
    {
        var settings = Load(("REVIEW_APP_DATABASE_URL", value)).AppDatabase;

        Assert.Equal((host, port, user, password, database), (settings.Host, settings.Port, settings.Username, settings.Password, settings.Database));
    }

    [Theory]
    [InlineData("review_app:secret-a1b2@127.0.0.1:5432/review")]
    [InlineData("http://review_app:secret-a1b2@127.0.0.1:5432/review")]
    [InlineData("POSTGRES://review_app:secret-a1b2@127.0.0.1:5432/review")]
    [InlineData("postgres://review_app@127.0.0.1:5432/review")]
    [InlineData("postgres://:secret-a1b2@127.0.0.1:5432/review")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432/")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432/review/more")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432/review?sslmode=disable")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:5432/review#here")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:0/review")]
    [InlineData("postgres://review_app:secret-a1b2@127.0.0.1:99999/review")]
    [InlineData(" postgres://review_app:secret-a1b2@127.0.0.1:5432/review")]
    [InlineData("postgres://review_app:secret a1b2@127.0.0.1:5432/review")]
    public void AMalformedAppDatabaseStopsStartupWithoutShowingIt(string value)
    {
        var refused = Assert.Throws<StartupException>(() => Load(("REVIEW_APP_DATABASE_URL", value)));

        Assert.StartsWith("REVIEW_APP_DATABASE_URL: ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("review_app", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsNeverShowThePassword()
    {
        var settings = Load(("REVIEW_APP_DATABASE_URL", "postgres://review_app:secret-a1b2@127.0.0.1:5432/review"));

        Assert.DoesNotContain("secret-a1b2", settings.ToString(), StringComparison.Ordinal);
        Assert.Contains("review_app@127.0.0.1:5432/review", settings.ToString(), StringComparison.Ordinal);
    }
}
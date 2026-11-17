using DevFlow.Settings;

using PlatformData;

namespace DevFlow.Tests;

/// <summary>
/// <see cref="DevFlowSettings"/>: unset is the default, and a set value that does not parse stops
/// the service at startup, naming the setting. See "Settings" in devflow/README.md.
/// </summary>
public sealed class SettingsTests
{
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
    public void EachSettingParsesOrRefusesStartup()
    {
        var defaults = Load();
        Assert.Equal("127.0.0.1", defaults.ListenHost);
        Assert.Equal(8084, defaults.ListenPort);
        Assert.Equal(new Uri("http://127.0.0.1:8080"), defaults.CoreUrl);
        Assert.Equal("https://platform.test", defaults.PublicOrigin);
        Assert.False(defaults.ExposeOpenApi);
        Assert.Equal(("devflow_app", "127.0.0.1", 5432, "devflow"), (defaults.AppDatabase.Username, defaults.AppDatabase.Host, defaults.AppDatabase.Port, defaults.AppDatabase.Database));

        // The loop looks every minute unless told otherwise; the tests' environment turns it off.
        Assert.Null(defaults.PollInterval);
        var unsetPoll = new Dictionary<string, string>(Harness.MinimalEnvironment);
        unsetPoll.Remove("DEVFLOW_POLL_SECONDS");
        Assert.Equal(TimeSpan.FromSeconds(60), DevFlowSettings.Load(name => unsetPoll.GetValueOrDefault(name)).PollInterval);
        // Empty is unset, as the hosted copy passes it when its .env leaves it out.
        Assert.Equal(TimeSpan.FromSeconds(60), Load(("DEVFLOW_POLL_SECONDS", "")).PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), Load(("DEVFLOW_POLL_SECONDS", "10")).PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(300), Load(("DEVFLOW_POLL_SECONDS", "300")).PollInterval);
        Assert.Equal(TimeSpan.FromDays(1), Load(("DEVFLOW_POLL_SECONDS", "86400")).PollInterval);
        Assert.Null(Load(("DEVFLOW_POLL_SECONDS", "0")).PollInterval);

        // The worker's base image and address: the dev stack's unless set.
        Assert.Equal("platform-hosted/devflow-worker:dev", defaults.WorkerImage);
        Assert.Equal(new Uri("http://host.docker.internal:8084"), defaults.WorkerUrl);
        var worker = Load(("DEVFLOW_WORKER_IMAGE", "platform-hosted/devflow-worker:2026.11.17"), ("DEVFLOW_WORKER_URL", "http://host.docker.internal:9080"));
        Assert.Equal("platform-hosted/devflow-worker:2026.11.17", worker.WorkerImage);
        Assert.Equal(new Uri("http://host.docker.internal:9080"), worker.WorkerUrl);
        Assert.Equal("registry.example:5000/a_b/worker@sha256:0123abcd", Load(("DEVFLOW_WORKER_IMAGE", "registry.example:5000/a_b/worker@sha256:0123abcd")).WorkerImage);

        // The app's database is required, with no default to fall back on.
        var unset = Assert.Throws<StartupException>(() => DevFlowSettings.Load(_ => null));
        Assert.StartsWith("DEVFLOW_APP_DATABASE_URL: ", unset.Message, StringComparison.Ordinal);

        // The owner's is read by migrate alone, never by the settings serving loads.
        var read = new List<string>();
        DevFlowSettings.Load(name => { read.Add(name); return Harness.MinimalEnvironment.GetValueOrDefault(name); });
        Assert.Contains("DEVFLOW_APP_DATABASE_URL", read);
        Assert.DoesNotContain("DEVFLOW_OWNER_DATABASE_URL", read);

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
        ("DEVFLOW_POLL_SECONDS", "9"),
        ("DEVFLOW_POLL_SECONDS", "1"),
        ("DEVFLOW_POLL_SECONDS", "-60"),
        ("DEVFLOW_POLL_SECONDS", "+60"),
        ("DEVFLOW_POLL_SECONDS", " 60"),
        ("DEVFLOW_POLL_SECONDS", "60s"),
        ("DEVFLOW_POLL_SECONDS", "1.5"),
        ("DEVFLOW_POLL_SECONDS", "86401"),
        ("DEVFLOW_POLL_SECONDS", "99999999999"),
        ("DEVFLOW_POLL_SECONDS", "٦٠"),
        // Shown unquoted in a shell command, so nothing a shell would read.
        ("DEVFLOW_WORKER_IMAGE", ""),
        ("DEVFLOW_WORKER_IMAGE", "Platform/worker"),
        ("DEVFLOW_WORKER_IMAGE", "-worker"),
        ("DEVFLOW_WORKER_IMAGE", "worker; rm -rf ~"),
        ("DEVFLOW_WORKER_IMAGE", "worker $(id)"),
        ("DEVFLOW_WORKER_IMAGE", "worker'x"),
        ("DEVFLOW_WORKER_IMAGE", new string('a', 256)),
        ("DEVFLOW_WORKER_URL", ""),
        ("DEVFLOW_WORKER_URL", "host.docker.internal:9080"),
        ("DEVFLOW_WORKER_URL", "http://host.docker.internal:9080/"),
        ("DEVFLOW_WORKER_URL", "http://[::1]:9080"),
        ("DEVFLOW_WORKER_URL", "http://host.docker.internal:9080;id"),
        ("DEVFLOW_WORKER_URL", "ftp://host.docker.internal"),
        ("DEVFLOW_APP_DATABASE_URL", ""),
        ("DEVFLOW_APP_DATABASE_URL", "devflow_app:secret-a1b2@127.0.0.1:5432/devflow"),
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

    [Theory]
    [InlineData("postgres://devflow_app:secret-a1b2@127.0.0.1:5432/devflow", "127.0.0.1", 5432, "devflow_app", "secret-a1b2", "devflow")]
    [InlineData("postgresql://devflow_app:secret-a1b2@db.internal/devflow", "db.internal", 5432, "devflow_app", "secret-a1b2", "devflow")]
    [InlineData("postgres://devflow_app:p%40ss%3Aw%2Fd@[::1]:6543/devflow", "::1", 6543, "devflow_app", "p@ss:w/d", "devflow")]
    public void TheAppDatabaseIsAPostgresUrl(string value, string host, int port, string user, string password, string database)
    {
        var settings = Load(("DEVFLOW_APP_DATABASE_URL", value)).AppDatabase;

        Assert.Equal((host, port, user, password, database), (settings.Host, settings.Port, settings.Username, settings.Password, settings.Database));
    }

    [Theory]
    [InlineData("devflow_app:secret-a1b2@127.0.0.1:5432/devflow")]
    [InlineData("http://devflow_app:secret-a1b2@127.0.0.1:5432/devflow")]
    [InlineData("postgres://devflow_app@127.0.0.1:5432/devflow")]
    [InlineData("postgres://devflow_app:secret-a1b2@127.0.0.1:5432")]
    [InlineData("postgres://devflow_app:secret-a1b2@127.0.0.1:5432/devflow?sslmode=disable")]
    [InlineData("postgres://devflow_app:secret-a1b2@127.0.0.1:99999/devflow")]
    [InlineData(" postgres://devflow_app:secret-a1b2@127.0.0.1:5432/devflow")]
    public void AMalformedAppDatabaseStopsStartupWithoutShowingIt(string value)
    {
        var refused = Assert.Throws<StartupException>(() => Load(("DEVFLOW_APP_DATABASE_URL", value)));

        Assert.StartsWith("DEVFLOW_APP_DATABASE_URL: ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("devflow_app", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsNeverShowThePassword()
    {
        var settings = Load(("DEVFLOW_APP_DATABASE_URL", "postgres://devflow_app:secret-a1b2@127.0.0.1:5432/devflow"));

        Assert.DoesNotContain("secret-a1b2", settings.ToString(), StringComparison.Ordinal);
        Assert.Contains("devflow_app@127.0.0.1:5432/devflow", settings.ToString(), StringComparison.Ordinal);
    }
}
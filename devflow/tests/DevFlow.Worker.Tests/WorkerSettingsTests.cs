using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>The worker's settings, from the environment: Dev flow's address and the project's key
/// required, the image's versions optional.</summary>
public sealed class WorkerSettingsTests
{
    private const string Url = "http://host.docker.internal:9080";

    [Theory]
    [InlineData("DEVFLOW_URL", null)]
    [InlineData("DEVFLOW_URL", "")]
    [InlineData("DEVFLOW_URL", "  ")]
    [InlineData("DEVFLOW_PROJECT_KEY", null)]
    [InlineData("DEVFLOW_PROJECT_KEY", "")]
    public async Task MissingSettingsAreNamed(string missing, string? value)
    {
        var environment = Environment(
            ("DEVFLOW_URL", missing == "DEVFLOW_URL" ? value : Url),
            ("DEVFLOW_PROJECT_KEY", missing == "DEVFLOW_PROJECT_KEY" ? value : Key));
        var devFlow = new ScriptedDevFlow();

        var code = await WorkerProgram.Run(environment, Writer(out var output), Writer(out var error), Ct, devFlow, new WatchedClock());

        Assert.Equal(2, code);
        Assert.Contains(missing, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(devFlow.Calls);
        Assert.DoesNotContain(Key, output.ToString() + error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host.docker.internal:9080")]
    [InlineData("ftp://host.docker.internal")]
    [InlineData("http://host.docker.internal:9080/devflow")]
    [InlineData("http://host.docker.internal:9080/?a=b")]
    public void AnAddressThatIsNotOnlyAnOriginIsRefused(string url)
    {
        var settings = WorkerSettings.Load(Environment(("DEVFLOW_URL", url), ("DEVFLOW_PROJECT_KEY", Key)), out var problem);

        Assert.Null(settings);
        Assert.Contains("DEVFLOW_URL", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionsAreTheImagesOrUnset()
    {
        var given = WorkerSettings.Load(Environment(
            ("DEVFLOW_URL", Url + "/"),
            ("DEVFLOW_PROJECT_KEY", Key),
            ("DEVFLOW_RECIPE_VERSION", "3"),
            ("DEVFLOW_BASE_IMAGE", "platform-hosted/devflow-worker:2026.11.17")), out var problem)!;
        Assert.Null(problem);
        Assert.Equal(new Uri(Url), given.DevFlowUrl);
        Assert.Equal(Key, given.ProjectKey);
        Assert.Equal("3", given.RecipeVersion);
        Assert.Equal("platform-hosted/devflow-worker:2026.11.17", given.BaseImage);

        // Dev flow requires both in hello, so a worker started outside its image reports them as
        // unset rather than empty, and is told apart from one that runs the project's recipe.
        var unset = WorkerSettings.Load(Environment(("DEVFLOW_URL", Url), ("DEVFLOW_PROJECT_KEY", Key), ("DEVFLOW_BASE_IMAGE", " ")), out _)!;
        Assert.Equal("unset", unset.RecipeVersion);
        Assert.Equal("unset", unset.BaseImage);
    }

    [Fact]
    public void TheKeyIsNeverPrinted()
    {
        var settings = WorkerSettings.Load(Environment(("DEVFLOW_URL", Url), ("DEVFLOW_PROJECT_KEY", Key)), out _)!;

        Assert.DoesNotContain(Key, settings.ToString(), StringComparison.Ordinal);
    }
}
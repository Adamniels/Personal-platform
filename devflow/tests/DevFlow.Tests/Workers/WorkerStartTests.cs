using DevFlow.Recipes;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The command a setup step shows: it starts a worker from the base image itself, with no recipe
/// and no build, for a project that has no valid <c>.devflow/</c> yet. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public sealed class WorkerStartTests
{
    private const string Image = "platform-hosted/devflow-worker:2026.11.17";
    private const string WorkerUrl = "http://host.docker.internal:9080";

    [Fact]
    public void TheSetupCommandStartsTheBaseImage()
    {
        var command = new WorkerStart(Image, new Uri(WorkerUrl), "https://platform.example").SetupCommand("Fodr");

        // devflow-<project>-setup, replacing the last, from DEVFLOW_WORKER_IMAGE itself with the
        // project's key file, 2 GB of memory and the base image it reports, so it is not held back
        // for another; no recipe is fetched and nothing is built.
        Assert.Equal(
            $"{{ docker rm -f devflow-fodr-setup 2>/dev/null; docker run -d --name devflow-fodr-setup --memory 2g --env-file ~/.devflow/fodr.env -e DEVFLOW_URL={WorkerUrl} -e DEVFLOW_BASE_IMAGE={Image} {Image}; }}",
            command);
        Assert.DoesNotContain("docker build", command, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", command, StringComparison.Ordinal);
        Assert.DoesNotContain("DEVFLOW_RECIPE_VERSION", command, StringComparison.Ordinal);
        Assert.DoesNotContain("dfw_", command, StringComparison.Ordinal);
    }
}
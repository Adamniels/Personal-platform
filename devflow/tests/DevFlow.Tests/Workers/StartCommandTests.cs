using System.Diagnostics;
using System.Text.Json.Nodes;

using DevFlow.Recipes;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The two commands on a project's page that start a worker: saving the key, shown once with the
/// key in it, and building and starting the worker, shown always and never with the key. See
/// "Starting a worker" in devflow/README.md.
/// </summary>
public sealed class StartCommandTests
{
    private const string Image = "platform-hosted/devflow-worker:2026.11.17";
    private const string WorkerUrl = "http://host.docker.internal:9080";
    private const string Origin = "https://platform.example";
    private const string Key = "dfw_AAAAbbbbCCCCddddEEEEffffGGGGhhhhIIIIjjjjKKK";

    private static CancellationToken Ct => Workers.Ct;

    [Fact]
    public void TheCommandsNameTheImageTheAddressAndTheKeyFile()
    {
        var start = new WorkerStart(Image, new Uri(WorkerUrl), Origin);

        // The first saves the key, readable by the owner alone, in ~/.devflow/<project>.env, without
        // changing the shell's own umask.
        Assert.Equal(
            "(umask 077 && mkdir -p ~/.devflow && printf 'DEVFLOW_PROJECT_KEY=%s\\n' '" + Key + "' > ~/.devflow/fodr.env)",
            start.SaveCommand("Fodr", Key));

        var command = start.StartCommand("Fodr");

        // The recipe is fetched from the platform's own address with the key read from the file,
        // built FROM the base image with the version from the header, and run with the worker's
        // address, the key file and 2 GB of memory, replacing the last worker of the project.
        Assert.Contains("-H \"Authorization: Bearer $(sed -n 's/^DEVFLOW_PROJECT_KEY=//p' ~/.devflow/fodr.env)\"", command, StringComparison.Ordinal);
        Assert.Contains($"{Origin}/devflow/api/worker/recipe", command, StringComparison.Ordinal);
        Assert.Contains($"--build-arg DEVFLOW_BASE={Image} ", command, StringComparison.Ordinal);
        Assert.Contains("--build-arg DEVFLOW_RECIPE_VERSION=\"$(", command, StringComparison.Ordinal);
        Assert.Contains("-t devflow-fodr - < /tmp/devflow-fodr.Dockerfile", command, StringComparison.Ordinal);
        Assert.Contains("docker rm -f devflow-fodr-1 2>/dev/null;", command, StringComparison.Ordinal);
        Assert.Contains($"docker run -d --name devflow-fodr-1 --restart unless-stopped --memory 2g --env-file ~/.devflow/fodr.env -e DEVFLOW_URL={WorkerUrl} devflow-fodr", command, StringComparison.Ordinal);
        Assert.Equal(1, Count(command, $"DEVFLOW_URL={WorkerUrl}"));

        // Never the key, nor anything shaped like one.
        Assert.DoesNotContain("dfw_", command, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, command, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectsNameIsMadeSafeForDockerAndTheShell()
    {
        Assert.Equal("fodr", WorkerStart.ProjectName("Fodr"));
        Assert.Equal("personal-platform", WorkerStart.ProjectName("Personal-platform"));
        Assert.Equal("my-repo-x", WorkerStart.ProjectName("My..Repo_x"));
        Assert.Equal("a-b", WorkerStart.ProjectName("-.a__b.-"));
        Assert.Equal("project", WorkerStart.ProjectName("..."));
        Assert.Equal(40, WorkerStart.ProjectName(new string('a', 100)).Length);
    }

    [Fact]
    public async Task TheVersionIsReadFromTheHeadersCurlWrote()
    {
        var folder = Directory.CreateTempSubdirectory("devflow-headers-");
        try
        {
            // HTTP/2 writes header names in lowercase, and every line ends in CRLF.
            var headers = Path.Combine(folder.FullName, "headers");
            await File.WriteAllTextAsync(headers, "HTTP/2 200\r\ncontent-type: text/plain; charset=utf-8\r\nx-devflow-recipe-version: 0123456789abcdef0123456789abcdef01234567\r\n\r\n", Ct);
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"printf '%s' \"$({WorkerStart.VersionFrom(headers)})\"");
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);

            Assert.Equal("0123456789abcdef0123456789abcdef01234567", output);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ThePageIsGivenTheCommands()
    {
        await using var routes = await Reading.Routes.Start();
        var project = await routes.Project(Workers.User, name: "Fodr");

        // Before a key, the start command already, for the project by its name.
        var list = await Workers.List(routes, project);
        var command = (string)list["start_command"]!;
        Assert.Contains("~/.devflow/fodr.env", command, StringComparison.Ordinal);
        Assert.Contains($"DEVFLOW_URL={WorkerStart.DefaultWorkerUrl}", command, StringComparison.Ordinal);
        Assert.Contains($"DEVFLOW_BASE={WorkerStart.DefaultImage} ", command, StringComparison.Ordinal);

        // Making the key answers the command that saves it, with it, this once.
        var made = await Workers.MakeKey(routes, project);
        Assert.Equal(StatusCodes.Status200OK, made.Response.StatusCode);
        var answer = await Workers.Json(made);
        var key = (string)answer["key"]!;
        Assert.Contains($"'{key}' > ~/.devflow/fodr.env", (string)answer["save_command"]!, StringComparison.Ordinal);
        Assert.DoesNotContain(key, (await Workers.List(routes, project)).ToJsonString(), StringComparison.Ordinal);
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}

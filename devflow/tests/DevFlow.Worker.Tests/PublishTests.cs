using System.Diagnostics;

using DevFlow.Tests;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>
/// The worker publishes as one self-contained file for each Linux it runs on, so a project's image
/// needs no .NET. Each publish runs in a folder of its own, apart from the build the tests run
/// from, against the committed lock files.
/// </summary>
public sealed class PublishTests
{
    /// <summary>The ELF machine field of each runtime, at byte 18.</summary>
    [Theory]
    [InlineData("linux-arm64", 0xB7)]
    [InlineData("linux-x64", 0x3E)]
    public async Task TheWorkerPublishesAsOneFile(string runtime, int machine)
    {
        var work = Directory.CreateTempSubdirectory("devflow-worker-publish-");
        try
        {
            var output = Path.Combine(work.FullName, "out");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Repository.Path("devflow"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "publish", "src/DevFlow.Worker/DevFlow.Worker.csproj",
                "-c", "Release",
                "-r", runtime,
                "-o", output,
                // Its own build and restore folders, so it never touches the tests' own build.
                "--artifacts-path", Path.Combine(work.FullName, "artifacts"),
                "-p:RestoreLockedMode=true",
                "--nologo",
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Ct);
            var stderr = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);

            Assert.True(process.ExitCode == 0, $"dotnet publish failed:\n{await stdout}\n{await stderr}");
            // One file, the program, with its runtime inside.
            var file = Assert.Single(Directory.GetFiles(output));
            Assert.Equal("DevFlow.Worker", Path.GetFileName(file));
            var header = new byte[20];
            await using (var stream = File.OpenRead(file))
            {
                await stream.ReadExactlyAsync(header, Ct);
            }

            Assert.Equal("\u007fELF"u8.ToArray(), header[..4]);
            Assert.Equal(machine, BitConverter.ToUInt16(header, 18));
            Assert.True(new FileInfo(file).Length > 20_000_000, "a self-contained file carries the runtime");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Review.Tests.Brain;

/// <summary>
/// The brain's client in <c>src/Review/Brain/Generated/</c> is what the pinned Kiota generates
/// from <c>contracts/brain/openapi.json</c>, so a change to the brain's schema fails here until
/// the client is regenerated and its diff read. See "What Review tells the brain" in
/// review/README.md, and "brain/" in contracts/README.md.
/// </summary>
public sealed class BrainClientTests
{
    /// <summary>The command in review/README.md, run from review/, but for where it writes.</summary>
    private static readonly string[] Generate =
    [
        "kiota", "generate",
        "--openapi", "../contracts/brain/openapi.json",
        "--language", "CSharp",
        "--include-path", "/brain/api/events#POST",
        "--class-name", "BrainClient",
        "--namespace-name", "Review.Brain.Generated",
        "--exclude-backward-compatible",
        "--clean-output",
    ];

    private static readonly string Committed = Repository.Path("review", "src", "Review", "Brain", "Generated");

    /// <summary>
    /// Every file Kiota writes, by its path under the output folder, with its text. Kiota's log is
    /// left out, as git ignores it. So is the lock file's <c>descriptionLocation</c>, the schema's
    /// path relative to the output folder, which is all that differs when it writes elsewhere.
    /// </summary>
    private static SortedDictionary<string, string> Files(string folder)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (name == ".kiota.log")
            {
                continue;
            }

            var text = File.ReadAllText(path);
            if (name == "kiota-lock.json")
            {
                var lockFile = JsonNode.Parse(text)!.AsObject();
                lockFile.Remove("descriptionLocation");
                text = lockFile.ToJsonString();
            }

            files[name] = text;
        }

        return files;
    }

    [Fact]
    public async Task TheGeneratedClientIsCurrent()
    {
        var output = Directory.CreateTempSubdirectory("review-brain-client-").FullName;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Repository.Path("review"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in Generate.Concat(["--output", output]))
            {
                start.ArgumentList.Add(argument);
            }

            using var kiota = Process.Start(start)!;
            var stdout = kiota.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = kiota.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await kiota.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(kiota.ExitCode == 0, $"kiota failed: {await stdout}{await stderr}");

            var generated = Files(output);
            var committed = Files(Committed);
            Assert.Equal(generated.Keys, committed.Keys);
            foreach (var (name, text) in generated)
            {
                Assert.True(text == committed[name], $"{name} differs from what Kiota generates: regenerate it, see review/README.md");
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
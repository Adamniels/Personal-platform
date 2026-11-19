using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Worker.Agents;

using Microsoft.Extensions.DependencyInjection;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// What the developer agent has to look at its own pages with, as Settled in the slice "The
/// developer checks its own pages": a folder for its pictures, <c>self</c>, beside its working copy
/// in the job's folder, made as <c>agent</c> by the worker for each build and fix job and removed
/// with the job; nothing in it is committed or pushed. That the commands the brief gives start a
/// project and write both pictures in the image is TheAgentCanStartTheProjectAndTakePictures, in
/// devflow/tests/image/base-image.test.sh. See "Screenshots" in devflow/README.md.
/// </summary>
public sealed class AgentEnvironmentTests
{
    [Fact]
    public async Task PicturesStayOutOfTheCommit()
    {
        await using var bench = await Bench.Start();
        var folders = new List<string>();
        await bench.RunJob(Looking(bench, folders, "greeting.txt", "Add the greeting"));
        var built = await bench.RemoteHead();
        Assert.NotNull(built);
        await bench.Jobs.Routes.App.Services.GetRequiredService<PullRequestOpener>().OpenPending(bench.Jobs.Project, Jobs.User, Ct);
        bench.Jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await bench.Review(new ScriptedAgent((_, _) => Task.CompletedTask, () => AgentResult.Success(Changes(), [], "", "oauth_token"))).Run(await bench.NextJob(), Ct);
        bench.Jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));

        var fix = await bench.NextJob();
        Assert.Equal(Job.Fix, fix.GetProperty("kind").GetString());
        await bench.Build(Looking(bench, folders, "greeting.test.txt", "Test the greeting")).Run(fix, Ct);

        // Both a build and a fix had the folder, beside the copy and made as the agent.
        Assert.Equal(2, folders.Count);
        Assert.Equal(2, bench.AsAgent.Started.Count(started => started.Spec.FileName == "mkdir" && folders.Contains(started.Spec.Arguments[^1])));

        // The pictures are in no commit the worker pushed, and the folder went with the job.
        var head = await bench.RemoteHead();
        Assert.NotEqual(built, head);
        var files = await Bench.Git(bench.Remote, "ls-tree", "-r", "--name-only", head!);
        Assert.Contains("greeting.txt", files, StringComparison.Ordinal);
        Assert.Contains("greeting.test.txt", files, StringComparison.Ordinal);
        Assert.DoesNotContain(".png", files, StringComparison.Ordinal);
        Assert.DoesNotContain("start.log", files, StringComparison.Ordinal);
        var everyCommit = await Bench.Git(bench.Remote, "log", "--name-only", "--format=", Bench.Branch);
        Assert.DoesNotContain(".png", everyCommit, StringComparison.Ordinal);
        Assert.All(folders, folder => Assert.False(Directory.Exists(folder), folder));
        Assert.Empty(Directory.GetFileSystemEntries(bench.Folders.Work));
    }

    /// <summary>An agent that finds its folder for pictures beside its copy, as the brief names it,
    /// writes both pictures and the project's log there, as the brief's commands would, and commits
    /// <paramref name="file"/> in its copy; each folder it found is added to
    /// <paramref name="folders"/>.</summary>
    private static ScriptedAgent Looking(Bench bench, List<string> folders, string file, string message) => new(async (run, _) =>
    {
        var job = Path.GetDirectoryName(run.WorkingDirectory)!;
        var self = Path.Combine(job, AgentFolders.Self);
        Assert.True(Directory.Exists(self), $"{self} is there when the agent runs");
        Assert.Equal(Path.Combine(bench.Folders.Work, Path.GetFileName(job)), job);
        Assert.False(self.StartsWith(run.WorkingDirectory, StringComparison.Ordinal), "the folder is outside the copy");
        folders.Add(self);
        await File.WriteAllBytesAsync(Path.Combine(self, "home-desktop.png"), Png, Ct);
        await File.WriteAllBytesAsync(Path.Combine(self, "home-phone.png"), Png, Ct);
        await File.WriteAllTextAsync(Path.Combine(self, "start.log"), "listening\n", Ct);
        await ScriptedAgent.Commit(run, file, "Hello\n", message);
    });

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A verdict in its form, changes with one blocking finding, so a fix follows.</summary>
    private static JsonObject Changes() => new()
    {
        ["verdict"] = "changes",
        ["summary"] = "The greeting has no test.",
        ["findings"] = new JsonArray(new JsonObject { ["blocking"] = true, ["file"] = "greeting.txt", ["line"] = 1, ["what"] = "No test reads it", ["why"] = "The card asks for one" }),
        ["for_owner"] = new JsonArray(),
    };
}
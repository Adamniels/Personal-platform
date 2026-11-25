using System.Text.Json.Nodes;

namespace DevFlow.Worker.Agents;

/// <summary>
/// An agent that answers a prepared report and changes files as told, so the worker's own tests
/// run a whole job without spending usage. Each change is a path relative to the working copy,
/// written with its text, or removed when the text is null; a path that leads outside the working
/// copy is refused before anything is changed. It writes a few lines to the run's log as it goes,
/// as Claude Code's stream would: a line of text, a tool line for each change, and a last line.
/// </summary>
internal sealed class FakeAgent(JsonObject report, IReadOnlyDictionary<string, string?> changes) : IAgent
{
    public async Task<AgentResult> Run(AgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var root = Path.GetFullPath(run.WorkingDirectory);
        var targets = changes.Select(change => (Path: Inside(root, change.Key), Relative: change.Key, Text: change.Value)).ToList();

        run.Log?.Add(RunLineKind.Text, $"The fake agent makes {targets.Count} change{(targets.Count == 1 ? "" : "s")}.");
        foreach (var (path, relative, text) in targets)
        {
            if (text is null)
            {
                run.Log?.Add(RunLineKind.Tool, $"Bash: rm {relative}");
                File.Delete(path);
                continue;
            }

            run.Log?.Add(RunLineKind.Tool, $"Write: {relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken);
        }

        run.Log?.Add(RunLineKind.Text, "Done.");

        return AgentResult.Success((JsonObject)report.DeepClone(), [], "", authMethod: null);
    }

    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"the change {relative} is not inside the working copy", nameof(relative));
        }

        return full;
    }
}
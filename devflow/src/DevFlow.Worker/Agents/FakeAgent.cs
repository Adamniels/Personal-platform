using System.Text.Json.Nodes;

namespace DevFlow.Worker.Agents;

/// <summary>
/// An agent that answers a prepared report and changes files as told, so the worker's own tests
/// run a whole job without spending usage. Each change is a path relative to the working copy,
/// written with its text, or removed when the text is null; a path that leads outside the working
/// copy is refused before anything is changed.
/// </summary>
internal sealed class FakeAgent(JsonObject report, IReadOnlyDictionary<string, string?> changes) : IAgent
{
    public async Task<AgentResult> Run(AgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var root = Path.GetFullPath(run.WorkingDirectory);
        var targets = changes.Select(change => (Path: Inside(root, change.Key), Text: change.Value)).ToList();

        foreach (var (path, text) in targets)
        {
            if (text is null)
            {
                File.Delete(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken);
        }

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
using System.Text;

using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Build;

/// <summary>
/// The push key as git is given it: an <c>Authorization</c> header for the clone address alone,
/// handed to one root git command in its environment, through <c>GIT_CONFIG_COUNT</c>, and so never
/// in an argument, which <c>ps</c> shows every user, nor in a file or a remote's address. Prints as
/// redacted.
/// </summary>
internal sealed class PushCredential(string key, Uri cloneAddress)
{
    /// <summary>The user name GitHub takes with an installation's key.</summary>
    public const string User = "x-access-token";

    /// <summary>What stands in for the key wherever git wrote it.</summary>
    public const string Redacted = "[redacted]";

    private readonly string _header = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{key}"));

    /// <summary>The variables that give git the header, scoped to the clone address.</summary>
    public IEnumerable<KeyValuePair<string, string>> Environment()
    {
        yield return new("GIT_CONFIG_COUNT", "1");
        yield return new("GIT_CONFIG_KEY_0", $"http.{cloneAddress.OriginalString}.extraHeader");
        yield return new("GIT_CONFIG_VALUE_0", $"Authorization: Basic {_header}");
    }

    /// <summary><paramref name="text"/> with the key, and the header carrying it, replaced.</summary>
    public string Scrub(string text) =>
        text.Replace(key, Redacted, StringComparison.Ordinal).Replace(_header, Redacted, StringComparison.Ordinal);

    public override string ToString() => "PushCredential(redacted)";
}

/// <summary>
/// Every git command a build job runs, as one of two users. Root's run in the worker's own
/// repository, or for the first clone in the job's folder of secrets, never anywhere the agent
/// controls, with <c>core.hooksPath=/dev/null</c> and no system or global config. The agent's run
/// as the agent, with hooks, <c>core.fsmonitor</c> and signing off, so nothing the agent left in
/// its copy runs or blocks the worker's commit. Arguments are lists, never through a shell. See
/// "Workers" in devflow/docs/devflow-architecture.md for why.
/// </summary>
internal sealed class WorkerGit(IProcessStarter root, IProcessStarter asAgent, AgentUser user)
{
    /// <summary>The longest one git command may take; a fetch or a push of a large repository is
    /// the slowest.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    /// <summary>What every root git command is given before its own arguments.</summary>
    public static readonly string[] RootOptions = ["-c", "core.hooksPath=/dev/null"];

    /// <summary>What every git command run as the agent is given before its own arguments.</summary>
    public static readonly string[] AgentOptions = ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false"];

    /// <summary>A root git command in the worker's own repository, or, with
    /// <paramref name="inSecret"/>, in the job's folder of secrets, for the clone that makes it;
    /// with the push key in its environment when <paramref name="credential"/> is given.</summary>
    public Task<ProcessOutcome> AsRoot(JobPaths paths, IReadOnlyList<string> arguments, CancellationToken cancellationToken, PushCredential? credential = null, bool inSecret = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = paths.Secret,
            ["PATH"] = user.Path,
            ["LANG"] = user.Lang,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        };
        foreach (var (name, value) in credential?.Environment() ?? [])
        {
            environment[name] = value;
        }

        return root.Run(new ProcessSpec("git", [.. RootOptions, .. arguments], inSecret ? paths.Secret : paths.Repository, environment), Limit, cancellationToken);
    }

    /// <summary>A git command as the agent, in <paramref name="workingDirectory"/>.</summary>
    public Task<ProcessOutcome> AsAgent(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        asAgent.Run(
            new ProcessSpec("git", [.. AgentOptions, .. arguments], workingDirectory, user.Environment(("GIT_TERMINAL_PROMPT", "0"), ("GIT_CONFIG_NOSYSTEM", "1"))),
            Limit,
            cancellationToken);

}
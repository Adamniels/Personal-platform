namespace DevFlow.Worker.Build;

/// <summary>
/// Where a job's files go: its secrets under <see cref="Secrets"/>, root's alone, and its working
/// copies under <see cref="Work"/>, the agent's. In the base image these are <c>/run/devflow</c>,
/// mode 700, owned by root, and <c>/work</c>, owned by <c>agent</c>; tests give folders of their
/// own. See "The worker" in devflow/README.md.
/// </summary>
internal sealed record JobFolders(string Secrets, string Work)
{
    public static JobFolders Default { get; } = new("/run/devflow", "/work");

    /// <summary>The paths of one job, named by its id.</summary>
    public JobPaths For(Guid jobId)
    {
        var id = jobId.ToString("D");
        var secret = Path.Combine(Secrets, id);
        var work = Path.Combine(Work, id);
        return new JobPaths(secret, Path.Combine(secret, "repo"), work, Path.Combine(work, "copy"), Path.Combine(work, "check"));
    }
}

/// <summary>The paths of one job.</summary>
/// <param name="Secret">The job's folder of secrets, mode 700, root's.</param>
/// <param name="Repository">The worker's own repository, a bare one, in <see cref="Secret"/>: it
/// alone talks to the code host, and it alone holds the push key, for one command at a time.</param>
/// <param name="Work">The job's folder under the agent's, made by the agent.</param>
/// <param name="Copy">The agent's working copy.</param>
/// <param name="Check">The clean copy of the fetched head commit the check runs in.</param>
internal sealed record JobPaths(string Secret, string Repository, string Work, string Copy, string Check);

/// <summary>
/// Who the agent's processes run as, and the environment each is given, built from nothing: the
/// agent's home, <c>PATH</c> and <c>LANG</c>. <see cref="Name"/> null runs them as the worker's own
/// user, as the tests do, where no second user exists.
/// </summary>
internal sealed record AgentUser(string? Name, string Home, string Path, string Lang)
{
    public static AgentUser Default { get; } = new("agent", "/home/agent", "/usr/local/bin:/usr/bin:/bin", "C.UTF-8");

    /// <summary>The environment of an agent's process: its home, <c>PATH</c> and <c>LANG</c>, and
    /// <paramref name="extra"/>; nothing of the worker's.</summary>
    public Dictionary<string, string> Environment(params (string Name, string Value)[] extra)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["PATH"] = Path,
            ["LANG"] = Lang,
        };
        foreach (var (name, value) in extra)
        {
            environment[name] = value;
        }

        return environment;
    }
}
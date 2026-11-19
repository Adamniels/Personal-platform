namespace DevFlow.Protocol;

/// <summary>
/// Where in a worker's image an agent's job folders are, as both sides name them: Dev flow in the
/// developer's brief, and the worker when it makes them. Each job has its folder under
/// <see cref="Work"/>, owned by <c>agent</c>, holding its working copy and, for a build or a fix,
/// <see cref="Self"/> beside it: the folder for the agent's own pictures, made by the worker and
/// removed with the job, outside the copy so nothing in it is committed or pushed. See
/// "Screenshots" in devflow/README.md.
/// </summary>
public static class AgentFolders
{
    /// <summary>The agent's folder in the base image, where each job's folder is made.</summary>
    public const string Work = "/work";

    /// <summary>The name of the folder for the agent's own pictures, in the job's folder.</summary>
    public const string Self = "self";

    /// <summary>The folder for the agent's own pictures of the job <paramref name="jobId"/>, as the
    /// brief names it: <c>/work/&lt;job&gt;/self</c>.</summary>
    public static string SelfOf(Guid jobId) => $"{Work}/{jobId:D}/{Self}";
}
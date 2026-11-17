namespace DevFlow.Worker;

/// <summary>
/// The worker's settings, from the environment. <c>DEVFLOW_URL</c>, Dev flow's address, and
/// <c>DEVFLOW_PROJECT_KEY</c>, the project's key, are required. <c>DEVFLOW_RECIPE_VERSION</c> and
/// <c>DEVFLOW_BASE_IMAGE</c> come from the worker's image and are sent in <c>hello</c>; unset, each
/// is <see cref="Unset"/>. See "The worker" in devflow/README.md.
/// </summary>
internal sealed class WorkerSettings
{
    public const string UrlVariable = "DEVFLOW_URL";
    public const string KeyVariable = "DEVFLOW_PROJECT_KEY";
    public const string RecipeVersionVariable = "DEVFLOW_RECIPE_VERSION";
    public const string BaseImageVariable = "DEVFLOW_BASE_IMAGE";

    /// <summary>
    /// What the worker reports for a version its image did not set. Dev flow requires both in
    /// <c>hello</c>, each 1 to 255 characters, so an empty one would be refused; this says plainly
    /// that the worker runs outside a project's image, and never matches a version Dev flow
    /// expects.
    /// </summary>
    public const string Unset = "unset";

    private WorkerSettings(Uri devFlowUrl, string projectKey, string recipeVersion, string baseImage)
    {
        DevFlowUrl = devFlowUrl;
        ProjectKey = projectKey;
        RecipeVersion = recipeVersion;
        BaseImage = baseImage;
    }

    /// <summary>Dev flow's origin, such as <c>http://host.docker.internal:9080</c>.</summary>
    public Uri DevFlowUrl { get; }

    /// <summary>The project's key, sent as <c>Authorization: Bearer</c> and nowhere else.</summary>
    public string ProjectKey { get; }

    public string RecipeVersion { get; }

    public string BaseImage { get; }

    /// <summary>
    /// The settings, or null with <paramref name="problem"/> naming the setting that is missing or
    /// wrong. A value of only spaces is missing. Neither the key nor the address is ever repeated
    /// in the problem.
    /// </summary>
    public static WorkerSettings? Load(Func<string, string?> environment, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Read(string name) => environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        if (Read(UrlVariable) is not { } url)
        {
            problem = $"{UrlVariable} is not set. It is Dev flow's address, such as http://host.docker.internal:9080.";
            return null;
        }

        // An origin and nothing more: the worker's calls carry their own paths, so a path, a query
        // or a fragment here would be dropped silently.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0
            || address.UserInfo.Length > 0)
        {
            problem = $"{UrlVariable} must be an http or https address with no path, such as http://host.docker.internal:9080.";
            return null;
        }

        if (Read(KeyVariable) is not { } key)
        {
            problem = $"{KeyVariable} is not set. It is the project's worker key, made on the project's page in Dev flow.";
            return null;
        }

        problem = null;
        return new WorkerSettings(address, key, Read(RecipeVersionVariable) ?? Unset, Read(BaseImageVariable) ?? Unset);
    }

    /// <summary>Everything but the key.</summary>
    public override string ToString() =>
        $"WorkerSettings {{ {UrlVariable} = {DevFlowUrl}, {KeyVariable} = (set), {RecipeVersionVariable} = {RecipeVersion}, {BaseImageVariable} = {BaseImage} }}";
}
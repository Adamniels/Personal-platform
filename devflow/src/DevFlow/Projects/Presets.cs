namespace DevFlow.Projects;

/// <summary>
/// The presets, shortcuts the settings panel offers that fill in the settings they cover, and are
/// never stored: a project's preset is the one its settings match, or <see cref="Custom"/>. Each
/// covers questions, <c>waits_for_owner</c> and the three conditions of <c>sometimes</c>, which on
/// <c>never</c> and <c>always</c> are not read and so do not count. See "Settings" in
/// devflow/README.md, and why they are not stored "Projects" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class Presets
{
    /// <summary>Questions off, <c>never</c>: every approved card merges by itself.</summary>
    public const string FullyAutomatic = "fully_automatic";

    /// <summary>Questions off, <c>sometimes</c> with all three conditions ticked.</summary>
    public const string Guided = "guided";

    /// <summary>Questions on, <c>always</c>: a new project's.</summary>
    public const string OnlyIMerge = "only_i_merge";

    /// <summary>Settings no preset matches.</summary>
    public const string Custom = "custom";

    /// <summary>The preset <paramref name="settings"/> match, or <see cref="Custom"/>.</summary>
    public static string Of(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (settings.Questions, settings.WaitsForOwner) switch
        {
            (false, "never") => FullyAutomatic,
            (false, "sometimes") when settings is { WaitWhenMarked: true, WaitWhenPaths: true, WaitWhenDecision: true } => Guided,
            (true, "always") => OnlyIMerge,
            _ => Custom,
        };
    }
}
namespace DevFlow.Projects;

/// <summary>
/// What a project runs on, its setting <c>runs_on</c>: the owner's subscription, through the Claude
/// login, or the API key he gave on the Projects tab. Each job that runs an agent carries the one
/// credential this names, as <see cref="Protocol.JobCredential"/>, and each run records which. Outside
/// every preset. See "Settings" under "Cards" and "The API key" in devflow/README.md.
/// </summary>
public static class RunsOn
{
    /// <summary>The subscription, the default: the Claude login's token, waiting at its usage
    /// limit.</summary>
    public const string Subscription = "subscription";

    /// <summary>The API key only, billed to the owner's Anthropic account.</summary>
    public const string ApiKey = "api_key";

    /// <summary>The values the setting takes.</summary>
    public static readonly IReadOnlyList<string> Values = [Subscription, ApiKey];

    /// <summary>Why a project on the API key is refused while none is given.</summary>
    public const string NoApiKey = "Give an API key on the Projects tab first";

    /// <summary>Whether a project on <paramref name="runsOn"/> is held without a working Claude
    /// login: only one on the subscription is.</summary>
    public static bool NeedsClaudeLogin(string runsOn) => runsOn == Subscription;

    /// <summary>Whether a project on <paramref name="runsOn"/> uses the API key, so the key cannot
    /// be removed under it: any but the subscription alone.</summary>
    public static bool NeedsApiKey(string runsOn) => runsOn != Subscription;
}
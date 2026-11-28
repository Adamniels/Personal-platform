namespace DevFlow.Projects;

/// <summary>
/// What a project runs on, its setting <c>runs_on</c>: the owner's subscription, through the Claude
/// login; the subscription, then the API key while the user's subscription is at its usage limit;
/// or the API key he gave on the Projects tab alone. Each job that runs an agent carries the one
/// credential <see cref="Carried"/> names, as <see cref="Protocol.JobCredential"/>, and each run
/// records which. Outside every preset. See "Settings" under "Cards" and "The API key" in
/// devflow/README.md.
/// </summary>
public static class RunsOn
{
    /// <summary>The subscription, the default: the Claude login's token, waiting at its usage
    /// limit.</summary>
    public const string Subscription = "subscription";

    /// <summary>The subscription, then the API key while the user's subscription is limited, by
    /// <see cref="Stops.SubscriptionLimits"/>; the first job after the limit resets carries the
    /// subscription again.</summary>
    public const string SubscriptionThenApiKey = "subscription_then_api_key";

    /// <summary>The API key only, billed to the owner's Anthropic account.</summary>
    public const string ApiKey = "api_key";

    /// <summary>The values the setting takes, in the order the settings page offers them.</summary>
    public static readonly IReadOnlyList<string> Values = [Subscription, SubscriptionThenApiKey, ApiKey];

    /// <summary>Why a project on the API key is refused while none is given.</summary>
    public const string NoApiKey = "Give an API key on the Projects tab first";

    /// <summary>The credential a job of a project on <paramref name="runsOn"/> carries, one of
    /// <see cref="Protocol.JobCredential"/>'s kinds, while the user's subscription is
    /// <paramref name="limited"/> or not: the key on <see cref="ApiKey"/>, and on
    /// <see cref="SubscriptionThenApiKey"/> while it is limited; the subscription otherwise.</summary>
    public static string Carried(string runsOn, bool limited) => runsOn switch
    {
        ApiKey => Protocol.JobCredential.ApiKey,
        SubscriptionThenApiKey when limited => Protocol.JobCredential.ApiKey,
        _ => Protocol.JobCredential.Subscription,
    };

    /// <summary>Whether a project on <paramref name="runsOn"/> is held without a working Claude
    /// login while the user's subscription is <paramref name="limited"/> or not: one whose jobs
    /// carry the subscription, by <see cref="Carried"/>.</summary>
    public static bool NeedsClaudeLogin(string runsOn, bool limited) => Carried(runsOn, limited) == Protocol.JobCredential.Subscription;

    /// <summary>Whether a project on <paramref name="runsOn"/> uses the API key, so the key cannot
    /// be removed under it: any but the subscription alone.</summary>
    public static bool NeedsApiKey(string runsOn) => runsOn != Subscription;
}
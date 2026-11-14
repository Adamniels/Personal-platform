namespace PlatformAuth;

/// <summary>
/// The two requirements a route takes, as authorization policies. A missing token, an expired
/// one, one that fails verification, and a core that cannot be reached with no key cached are the
/// same to both: the service never decides between refreshing and logging in, the core does, see
/// "The refresh endpoint is the one place that decides between refreshing and logging in" in
/// <c>Core/docs/core-architecture.md</c>.
/// </summary>
public static class PlatformAuthPolicies
{
    /// <summary>
    /// For a page a browser loads: without a valid token, a <c>303</c> to the core's refresh
    /// endpoint with the way back, which refreshes or sends the browser to log in, and back here
    /// either way.
    /// </summary>
    public const string Page = "PlatformAuth.Page";

    /// <summary>
    /// For a route a script calls: without a valid token, <c>401</c> with an empty body, the same
    /// whatever the reason, and the frontend refreshes and retries.
    /// </summary>
    public const string Api = "PlatformAuth.Api";
}
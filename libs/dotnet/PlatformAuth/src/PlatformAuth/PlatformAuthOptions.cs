namespace PlatformAuth;

/// <summary>What a service tells <see cref="PlatformAuthServiceCollectionExtensions.AddPlatformAuth"/>.</summary>
public sealed class PlatformAuthOptions
{
    /// <summary>
    /// The core's own address, where its key set is fetched from, never the proxy's. Required.
    /// </summary>
    public Uri? CoreUrl { get; set; }
}
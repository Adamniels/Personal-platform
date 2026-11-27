using Microsoft.AspNetCore.Http.Features;

using PlatformAuth;

namespace DevFlow.Api;

/// <summary>Who a request under <see cref="PlatformAuthPolicies.Api"/> is made by, as review's
/// <c>Caller</c> reads it.</summary>
internal static class Caller
{
    /// <summary>The verified token's <c>sub</c>, the user id.</summary>
    public static Guid UserId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId;
    }

    /// <summary>When the verified token runs out, its <c>exp</c>: a stream it let in ends then, so
    /// nothing is sent on a token past its time, and the page opens the stream again with the
    /// session as it is then.</summary>
    public static DateTimeOffset TokenExpiresAt(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return DateTimeOffset.FromUnixTimeSeconds(context.Features.GetRequiredFeature<IPlatformToken>().Claims.ExpiresAt);
    }
}
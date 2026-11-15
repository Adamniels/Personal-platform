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
}
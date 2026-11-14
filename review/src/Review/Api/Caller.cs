using Microsoft.AspNetCore.Http.Features;

using PlatformAuth;

namespace Review.Api;

/// <summary>Who a request under <see cref="PlatformAuthPolicies.Api"/> is made by.</summary>
internal static class Caller
{
    /// <summary>The verified token's <c>sub</c>, the user id every query runs as, through the
    /// door.</summary>
    public static Guid UserId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId;
    }
}
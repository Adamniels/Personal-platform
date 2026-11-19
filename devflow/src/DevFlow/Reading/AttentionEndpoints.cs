using System.Text.Json.Serialization;

using DevFlow.Api;
using DevFlow.ClaudeLogin;

using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Reading;

/// <summary>
/// <c>GET /devflow/api/attention</c>, Dev flow's answer to the nav bar: whether something here needs
/// the caller, which draws the dot on Dev flow's link. Read from the stored snapshot and the Claude
/// login alone, as
/// <c>GET /devflow/api/waiting</c> is, so it answers quickly and never waits on the code host. It
/// carries no count: the nav shows a dot, never a number. See "The attention endpoint" in
/// High-level/docs/feature-contract.md, and "What it serves" in devflow/README.md.
/// </summary>
public static class AttentionEndpoints
{
    public static IEndpointRouteBuilder MapAttention(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/attention", async (HttpContext context, [FromServices] WaitingStore store, [FromServices] ClaudeLogins logins) =>
                TypedResults.Ok(Attention.From(
                    await store.For(context.UserId(), context.RequestAborted),
                    await logins.Status(context.UserId(), context.RequestAborted))))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getAttention")
            .WithSummary("Whether something needs the caller, for the nav bar")
            .WithDescription("Read from what Dev flow stored and never from GitHub. attention is true when the caller has a pull request, waiting or stopped, that is not sent back, a stopped project, a card of Dev flow's whose pull request waits, a GitHub link that has expired, or a Claude login due for renewal within a month or past it, and false otherwise. The nav bar asks it with the session's cookie. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}

/// <summary>What Dev flow tells the nav bar.</summary>
/// <param name="Value">Whether something here needs the caller.</param>
public sealed record Attention([property: JsonPropertyName("attention")] bool Value)
{
    /// <summary>Whether anything is the owner's to act on: what a waiting list's <c>count</c>
    /// counts, the items not sent back, Dev flow's own items, cards and decisions to look at and a
    /// needed relink, or a Claude login that is due.</summary>
    public static Attention From(WaitingList list, ClaudeLoginStatus login)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(login);
        return new(list.NeedsRelink || list.Items.Any(item => !item.SentBack) || list.OwnItems.Count > 0 || list.Cards.Count > 0 || list.Decisions.Count > 0 || login.NeedsYou);
    }
}
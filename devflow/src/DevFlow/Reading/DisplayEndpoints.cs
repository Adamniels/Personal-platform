using DevFlow.Api;
using DevFlow.ClaudeLogin;

using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Reading;

/// <summary>
/// <c>GET /devflow/api/display</c>, what Dev flow offers the core's front page: how many pull
/// requests wait for the caller and how many stopped, how many projects they have, and whether the
/// picture is stale or their GitHub link needs renewing. Read from the stored snapshot alone, as
/// <c>GET /devflow/api/waiting</c> is, so it answers well inside the core's deadline. Data about
/// Dev flow's own domain and nothing meant for display: the core decides what to show and how.
/// Pinned by contracts/display/devflow.json. See "What it serves" in devflow/README.md, and "What
/// a feature exposes for the front page" in Core/docs/core-architecture.md.
/// </summary>
public static class DisplayEndpoints
{
    public static IEndpointRouteBuilder MapDisplay(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/display", async (HttpContext context, [FromServices] WaitingStore store, [FromServices] ClaudeLogins logins) =>
                TypedResults.Ok(DisplayData.From(
                    await store.For(context.UserId(), context.RequestAborted),
                    await logins.Status(context.UserId(), context.RequestAborted))))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getDisplay")
            .WithSummary("What waits, for the front page")
            .WithDescription("Read from what Dev flow stored and never from GitHub. waiting, the pull requests that wait for the caller and were not sent back, the runner's and Dev flow's own cards', with Dev flow's own features ready for review or waiting for the caller; stopped, those the runner stopped on, not sent back, and the caller's projects stopped because something landed on their default branch that Dev flow did not put there; projects, how many the caller has connected; stale, whether any project is stale or failing, as the waiting list says them; needs_relink, whether the caller's GitHub link has expired; and claude_login, where their Claude login stands, none, ok, due_soon or expired, as GET /devflow/api/claude-login says it. The core calls it with the user's token in the Authorization header. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}

/// <summary>What Dev flow offers the front page.</summary>
/// <param name="Waiting">Items of kind waiting that are not sent back, Dev flow's own cards whose
/// pull requests wait, the decisions to look at, and Dev flow's own features ready for review or
/// waiting for the caller.</param>
/// <param name="Stopped">Items of kind stopped that are not sent back, and stopped projects.</param>
/// <param name="Projects">How many projects the caller has connected.</param>
/// <param name="Stale">Whether any project is stale or failing, by <see cref="WaitingStore"/>'s
/// rule.</param>
/// <param name="NeedsRelink">Whether GitHub stopped taking the caller's link.</param>
/// <param name="ClaudeLogin">Where the caller's Claude login stands.</param>
public sealed record DisplayData(int Waiting, int Stopped, int Projects, bool Stale, bool NeedsRelink, ClaudeLoginState ClaudeLogin)
{
    /// <summary>The display data of a waiting list and the Claude login. A sent back item is the
    /// runner's turn, so it is counted in neither; a needed relink is said apart rather than
    /// counted, unlike the list's <c>count</c>, and so is a login that is due.</summary>
    public static DisplayData From(WaitingList list, ClaudeLoginStatus login)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(login);
        return new(
            list.Items.Count(item => item.Kind == WaitingKind.Waiting && !item.SentBack) + list.Cards.Count + list.Decisions.Count + list.Features.Count,
            list.Items.Count(item => item.Kind == WaitingKind.Stopped && !item.SentBack) + list.OwnItems.Count(item => item.Kind == OwnItemKind.ProjectStopped),
            list.Projects.Count,
            list.Projects.Any(project => project.Stale || project.Failing),
            list.NeedsRelink,
            login.State);
    }
}
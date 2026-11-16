using System.Net;

using DevFlow.Api;
using DevFlow.Link;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.CodeHost;

/// <summary>
/// The fake code host's pages, mapped only in the fake mode: its install page,
/// <c>/devflow/fake-github/install</c>, which installs nothing and says where a test stages
/// installations instead, and its sign in page, <c>/devflow/fake-github/authorize</c>, under the
/// <c>Page</c> policy, so it knows whose world's user to show, as GitHub's page
/// knows who is signed in to GitHub. A plain page that names itself "Fake GitHub", shows the
/// login of the world's user, and has Authorize and Deny, a form posted back to it, which sends the
/// browser to the redirect address with a code or with <c>error=access_denied</c>. It only ever
/// sends the browser to Dev flow's own redirect address, as GitHub only sends it to a registered
/// one. See "The fake" in devflow/README.md.
/// </summary>
public static class FakeSignInEndpoints
{
    public static IEndpointRouteBuilder MapFakeSignIn(this IEndpointRouteBuilder routes, Uri redirect)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(redirect);

        // Where "Install it on GitHub" leads in the fake mode: a page of its own, so the link goes
        // somewhere harmless rather than to a real GitHub App.
        routes.MapGet(FakeCodeHost.InstallPath, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Content(InstallPage, "text/html; charset=utf-8");
            })
            .RequireAuthorization(PlatformAuthPolicies.Page)
            .ExcludeFromDescription();

        routes.MapGet(FakeCodeHost.AuthorizePath, Results<ContentHttpResult, ProblemHttpResult> (
                HttpContext context,
                [FromServices] FakeCodeHost host,
                [FromQuery(Name = "client_id")] string? clientId,
                [FromQuery(Name = "redirect_uri")] string? redirectUri,
                [FromQuery] string? state,
                [FromQuery(Name = "code_challenge")] string? challenge,
                [FromQuery(Name = "code_challenge_method")] string? method) =>
            {
                var wrong = clientId != FakeCodeHost.ClientId ? "client_id"
                    : redirectUri != redirect.AbsoluteUri ? "redirect_uri"
                    : string.IsNullOrEmpty(state) ? "state"
                    : string.IsNullOrEmpty(challenge) ? "code_challenge"
                    : method != Pkce.Method ? "code_challenge_method"
                    : null;
                if (wrong is not null)
                {
                    return AskedWrongly(wrong);
                }

                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Content(Page(host.SignedInUser(context.UserId()).Login, redirect, state!, challenge!), "text/html; charset=utf-8");
            })
            .RequireAuthorization(PlatformAuthPolicies.Page)
            .ExcludeFromDescription();

        // A form posted from the page above, so the origin check covers it like every change.
        routes.MapPost(FakeCodeHost.AuthorizePath, async Task<Results<StatusCodeHttpResult, ProblemHttpResult>> (HttpContext context, [FromServices] FakeCodeHost host) =>
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                string? Field(string name) => form.TryGetValue(name, out var value) && value.Count == 1 ? value[0] : null;
                var state = Field("state");
                var challenge = Field("code_challenge");
                var decision = Field("decision");
                var wrong = Field("redirect_uri") != redirect.AbsoluteUri ? "redirect_uri"
                    : string.IsNullOrEmpty(state) ? "state"
                    : string.IsNullOrEmpty(challenge) ? "code_challenge"
                    : decision is not ("authorize" or "deny") ? "decision"
                    : null;
                if (wrong is not null)
                {
                    return AskedWrongly(wrong);
                }

                var back = decision == "authorize"
                    ? QueryString.Create(new KeyValuePair<string, string?>[] { new("code", host.Grant(context.UserId(), challenge!, redirect)), new("state", state) })
                    : QueryString.Create(new KeyValuePair<string, string?>[] { new("error", "access_denied"), new("state", state) });
                return LinkEndpoints.SeeOther(context, redirect.AbsoluteUri + back.ToUriComponent());
            })
            .RequireAuthorization(PlatformAuthPolicies.Page)
            .ExcludeFromDescription();

        return routes;
    }

    private static ProblemHttpResult AskedWrongly(string field) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The fake GitHub was asked wrongly", detail: $"{field}: is missing or is not what Dev flow sends");

    /// <summary>The install page, which holds no value from the request.</summary>
    private const string InstallPage = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Fake GitHub</title>
        <style>
        body { font: 16px/1.5 system-ui, sans-serif; max-width: 28rem; margin: 3rem auto; padding: 0 1rem; }
        </style>
        </head>
        <body>
        <h1>Fake GitHub</h1>
        <p>This stands in for GitHub in tests. Nothing here is real.</p>
        <p>There is no app to install here. A test stages the installations it needs in its world, through <code>/devflow/api/fake/world</code>.</p>
        <p><a href="/devflow/projects">Back to Projects</a></p>
        </body>
        </html>
        """;

    /// <summary>The page, every value in it encoded, and nothing loaded from anywhere.</summary>
    private static string Page(string login, Uri redirect, string state, string challenge)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Fake GitHub</title>
            <style>
            body { font: 16px/1.5 system-ui, sans-serif; max-width: 28rem; margin: 3rem auto; padding: 0 1rem; }
            form { display: flex; gap: 0.75rem; margin-top: 1.5rem; }
            button { font: inherit; padding: 0.5rem 1.25rem; border-radius: 999px; border: 1px solid #888; cursor: pointer; }
            button[value=authorize] { background: #1f6f3f; border-color: #1f6f3f; color: #fff; }
            </style>
            </head>
            <body>
            <h1>Fake GitHub</h1>
            <p>This stands in for GitHub in tests. Nothing here is real.</p>
            <p>Signed in as <strong>@{{E(login)}}</strong>.</p>
            <p>Authorize Dev flow to act as you?</p>
            <form method="post" action="{{E(FakeCodeHost.AuthorizePath)}}">
            <input type="hidden" name="redirect_uri" value="{{E(redirect.AbsoluteUri)}}">
            <input type="hidden" name="state" value="{{E(state)}}">
            <input type="hidden" name="code_challenge" value="{{E(challenge)}}">
            <button type="submit" name="decision" value="authorize">Authorize</button>
            <button type="submit" name="decision" value="deny">Deny</button>
            </form>
            </body>
            </html>
            """;
    }
}
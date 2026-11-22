using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Setup;

/// <summary>
/// A project's setup over HTTP, every route under the <c>Api</c> policy and the origin check: its
/// steps and its newest check, asking for another check of a version whose check failed, sending a
/// failed check back with his comment as a new setup run, a check's pictures, the owner's merge of
/// his setup's pull request, and his comment that runs a setup stopped before it had a pull request
/// again. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var setup = routes.MapGroup("/devflow/api/projects/{id:guid}/setup");

        setup.MapGet("", async Task<Results<Ok<ProjectSetup>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database, [FromServices] Recipes.WorkerStart start) =>
                await SetupStore.Of(database, context.UserId(), id, start, context.RequestAborted) is { } answer
                    ? TypedResults.Ok(answer)
                    : NoSuchProject())
            .Route("getProjectSetup", "A project's setup",
                "state, setting_up until a check of any version of its .devflow/ has passed and its settings are accepted, then registered, and a project setting up starts no card's work, no reading or build; steps, in their order, each with whether it is done: setup, both files of .devflow/ on its default branch and valid, which a setup run writes when they are not, recipe, a version of .devflow/ read from its default branch, settings, the caller accepted the proposed settings or saved the settings once, without which it hands out no reading or build, worker, a worker built from that version said hello, and check, a check of that version passed; check, the newest check of that version, or of any before it is known, with its version, its commit, whether it passed, its exit code, the last 200 lines it printed, the names of its pictures of the project's address, why there are none, when, and whether another was asked for, or null before one ran; checking, whether a check of that version is running; problem, why .devflow/ is not valid as last read, or null; setup_command, the command that starts the project's setup worker from the base image, never holding the key; and setup, its newest setup run or null: its number, branch, state, queued, checking, reviewing, fixing, waiting_for_owner, stopped or done, reason, pull request, whether a run of it is under way, the commit it waits at, its newest report in its form, with what it proposes, the values its tests need and what it could not settle, and the review of that commit; and proposal, the preset, why and the paths its newest setup run that succeeded proposed, which the settings step offers to accept, or null. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        setup.MapPost("/merge", MergeSetup)
            .Route("mergeProjectSetup", "Merge a project's setup as yourself",
                "Merges the pull request of the project's setup run as the caller, with their linked GitHub token, naming head_commit, the commit the page showed: only while it waits for them, its review approved, and its project is not stopped. A setup's pull request is never merged by Dev flow, whatever the project's settings. 204, and the setup is followed and the project read again. 404 for a project the caller does not have; 422 without head_commit; 409 when no setup waits, the project is stopped, the commit moved since it was shown, GitHub is not linked, or GitHub would not merge it; 503 when GitHub did not answer.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable);

        setup.MapPost("/check-again", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                await SetupStore.Again(database, context.UserId(), id, Now(time), context.RequestAborted) switch
                {
                    CheckAgain.Asked => TypedResults.NoContent(),
                    CheckAgain.NothingToCheck => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to check again", detail: "The newest check did not fail, or another was already asked for."),
                    _ => NoSuchProject(),
                })
            .Route("checkProjectAgain", "Check a project's setup again",
                "Asks for another check of the version whose newest check, the one getProjectSetup answers, failed: the next worker on that version that asks is handed it. 204. 409 when that check did not fail or another was already asked for; 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        setup.MapPost("/run-again", RunAgain)
            .Route("runProjectSetupAgain", "Run a stopped setup again with a comment",
                "Takes comment, trimmed, as the owner's comment on a setup run that stopped before it had a pull request, which has nothing on GitHub to comment on: the setup runs again with it, a fix of its branch, or a first run when nothing was pushed, with comment in its brief to be answered, as his comment on a setup's pull request runs one again. In order: 404 for a project the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the project's newest setup has not stopped, has a pull request, which his comment on GitHub runs again, or is running, or the project no longer needs a setup. 204 once taken.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        setup.MapPost("/send-back", SendBack)
            .Route("sendProjectSetupBack", "Send a failed check back with a comment",
                "Takes comment, trimmed, as the owner's comment on the failed check of the project's newest version of .devflow/: a new setup run starts from the default branch on the next free devflow/setup-<k>, its brief holding the two files as they were at the commit checked, the check's output and comment, and its pull request is the setup's, reviewed, fixed and waiting for the caller. In order: 404 for a project the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 4,000 characters; 409 when the newest check of the project's version did not fail, another check of it runs, or a setup is under way; 503 when GitHub did not answer for the files. 204 once taken.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable);

        setup.MapGet("/checks/{check:guid}/pictures/{name}", async Task<Results<FileContentHttpResult, ProblemHttpResult>> (Guid id, Guid check, string name, HttpContext context, [FromServices] UserDb database) =>
                {
                    Reading.ScreenshotEndpoints.Guard(context);
                    if (await SetupStore.Picture(database, context.UserId(), id, check, name, context.RequestAborted) is not { } png)
                    {
                        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such picture", detail: "The project has no check with a picture by that name.");
                    }

                    // A check's pictures never change, so the browser keeps one an hour.
                    context.Response.Headers.CacheControl = "private, max-age=3600";
                    return TypedResults.File(png, "image/png");
                })
            .Route("getSetupPicture", "One of a check's pictures of the project's address",
                "The PNG its worker sent, as Dev flow stored it, checked when it came to be a PNG of at most 5 MB. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for a project the caller does not have, or a check or name it does not hold.",
                StatusCodes.Status404NotFound)
            .Produces<Stream>(StatusCodes.Status200OK, "image/png");

        return routes;
    }

    /// <summary>
    /// The owner's merge of his project's setup's pull request, as a card's merge from the page is:
    /// only while it waits for him, its project not stopped, at the commit the page showed, as him
    /// with his linked token and never as the app. Merged, the setup is followed at once and the
    /// project read again, so its step is done as soon as the default branch has the files.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> MergeSetup(
        Guid id,
        SetupMergeRequest request,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] Recipes.WorkerStart start,
        [FromServices] Workers.WorkerStore workers,
        [FromServices] Watching.WatchStore watch,
        [FromServices] Link.UserTokens tokens,
        [FromServices] CodeHost.ICodeHost host,
        [FromServices] SetupLoop loop,
        [FromServices] Reading.ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await SetupStore.Of(database, userId, id, start, cancellationToken) is not { } project
            || await workers.RepositoryOf(new Workers.WorkerCaller(id, userId), cancellationToken) is not { } repository)
        {
            return NoSuchProject();
        }

        if (string.IsNullOrEmpty(request.HeadCommit))
        {
            return Acting.ActionEndpoints.Invalid("head_commit", "is missing; it is the commit the page showed");
        }

        if (project.Setup is not { State: "waiting_for_owner", PullRequestNumber: { } pull, HeadCommit: { } head })
        {
            return Acting.ActionEndpoints.Conflict("The setup is not waiting for you", "Only a setup whose pull request waits for you is merged from its step.");
        }

        if (await watch.Hold(userId, id, cancellationToken) == Watching.ProjectHold.Stopped)
        {
            return Acting.ActionEndpoints.Conflict("This project is stopped", "Something landed on its default branch that Dev flow did not put there. Look at it and resume the project first.");
        }

        if (!string.Equals(request.HeadCommit, head, StringComparison.Ordinal))
        {
            return Acting.ActionEndpoints.Conflict("It changed since you saw it", "Look at it again before you merge.");
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not Link.UserToken.Usable { Token: var access })
        {
            return Acting.ActionEndpoints.TokenProblem(userToken, context, time);
        }

        var result = await host.Merge(userId, access, repository, pull, head, cancellationToken);
        if (result is CodeHost.MergeResult.Merged or CodeHost.MergeResult.HeadMoved or CodeHost.MergeResult.Unavailable)
        {
            // It may have merged, so the setup is followed and the project read again.
            await loop.TendPending(id, userId, cancellationToken);
            try
            {
                await refresher.Request(id, userId).WaitAsync(Reading.WaitingEndpoints.RefreshWait, time, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }

        return result switch
        {
            CodeHost.MergeResult.Merged => TypedResults.NoContent(),
            CodeHost.MergeResult.HeadMoved => Acting.ActionEndpoints.Conflict("It changed since you saw it", "It was read again. Look at it before you merge."),
            CodeHost.MergeResult.NotMergeable { Message: var message } => Acting.ActionEndpoints.Conflict("GitHub would not merge it", message is null ? "GitHub did not say why." : $"GitHub said: {message}"),
            CodeHost.MergeResult.Refused { Status: var status, Message: var message } => Acting.ActionEndpoints.Conflict("GitHub refused the merge", Acting.ActionEndpoints.HostSaid(status, message)),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: "It is not known whether it merged; the project was read again."),
        };
    }

    /// <summary>
    /// The owner's comment on a setup that stopped before it had a pull request, from its step: with
    /// nothing on GitHub to comment on, his comment is taken here as one on its pull request would
    /// be, and the setup runs again with it. A stopped setup with a pull request is run again by his
    /// comment there, as a stopped card's is by his comment on its issue.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RunAgain(
        Guid id,
        SetupRunAgainRequest request,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] Recipes.WorkerStart start,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await SetupStore.Of(database, userId, id, start, cancellationToken) is not { } project)
        {
            return NoSuchProject();
        }

        if (Acting.ActionEndpoints.Comment(request.Comment, "write what the agent should do differently") is { } invalid)
        {
            return invalid;
        }

        return await SetupRunStore.RunAgain(database, userId, id, request.Comment!.Trim(), Now(time), cancellationToken)
            ? TypedResults.NoContent()
            : Acting.ActionEndpoints.Conflict(
                "This setup is not run again from here",
                project.Setup is { State: "stopped", PullRequestNumber: not null }
                    ? "It has a pull request; comment on it on GitHub to run it again."
                    : "Only a setup that stopped before it had a pull request is run again from its step.");
    }

    /// <summary>
    /// The owner's comment on a failed check of his project's newest version of <c>.devflow/</c>,
    /// which starts a new setup run with it, the check's output and the files as they were checked,
    /// read as the app at the commit checked before anything is stored. Only on the newest check of
    /// the version, failed, with no other check of it running and no setup under way. His comment
    /// on GitHub is not read for this: the check has no issue or pull request to comment on.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SendBack(
        Guid id,
        SetupSendBackRequest request,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] Recipes.WorkerStart start,
        [FromServices] Workers.WorkerStore workers,
        [FromServices] CodeHost.ICodeHost host,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await SetupStore.Of(database, userId, id, start, cancellationToken) is not { } project
            || await workers.RepositoryOf(new Workers.WorkerCaller(id, userId), cancellationToken) is not { } repository)
        {
            return NoSuchProject();
        }

        var comment = request.Comment?.Trim();
        if (comment is null)
        {
            return Acting.ActionEndpoints.Invalid("comment", "is missing");
        }

        if (comment.Length == 0)
        {
            return Acting.ActionEndpoints.Invalid("comment", "is empty; write what the setup should do differently");
        }

        if (comment.Length > SetupRunStore.MostSentBackComment)
        {
            return Acting.ActionEndpoints.Invalid("comment", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"is {comment.Length:N0} characters; it may have at most {SetupRunStore.MostSentBackComment:N0}"));
        }

        if (!project.CanSendBack || project.Check is not { Passed: false } check)
        {
            return NotSentBack();
        }

        if (await Checked(host, userId, repository, Recipes.RecipeEndpoints.RecipePath, check.Commit, cancellationToken) is not { } recipe
            || await Checked(host, userId, repository, Jobs.Dispatcher.ProjectFilePath, check.Commit, cancellationToken) is not { } projectFile)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: "The files that were checked could not be read. Try again.");
        }

        return await SetupRunStore.SendBack(database, userId, id, check.Id, comment, recipe.Text, projectFile.Text, Now(time), cancellationToken)
            ? TypedResults.NoContent()
            : NotSentBack();
    }

    /// <summary>The longest a file sent back is kept, in characters, as its column takes it.</summary>
    private const int MostSentBackFile = 65536;

    /// <summary>A file of <c>.devflow/</c> at the commit checked, read as the app, as text with no
    /// NUL, which Postgres's text cannot hold, cut to <see cref="MostSentBackFile"/>; its text null
    /// inside when it is not there; null when the code host did not answer.</summary>
    private static async Task<SentBackFile?> Checked(CodeHost.ICodeHost host, Guid userId, CodeHost.Repository repository, string path, string commit, CancellationToken cancellationToken)
    {
        switch (await host.File(userId, repository, path, commit, cancellationToken))
        {
            case CodeHost.CodeHostResult<byte[]>.Found { Value: var bytes }:
                var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\0", "", StringComparison.Ordinal);
                return new SentBackFile(text.Length > MostSentBackFile ? text[..MostSentBackFile] : text);
            case CodeHost.CodeHostResult<byte[]>.NotFound:
                return new SentBackFile(null);
            default:
                return null;
        }
    }

    private static ProblemHttpResult NotSentBack() =>
        Acting.ActionEndpoints.Conflict("This check is not sent back", "Only the newest check of the project's setup that failed is sent back, while no other check of it runs and no setup is under way.");

    private sealed record SentBackFile(string? Text);

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>A route under the <c>Api</c> policy, named in the schema with the problems it may
    /// answer, as the projects' routes are.</summary>
    private static RouteHandlerBuilder Route(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            route.ProducesProblem(status);
        }

        return route;
    }
}
/// <summary>What the page sends to merge a project's setup: the commit it showed.</summary>
public sealed record SetupMergeRequest(string? HeadCommit);

/// <summary>What the step sends to send a failed check back.</summary>
/// <param name="Comment">What the owner wrote, taken trimmed and quoted in the setup's brief.</param>
public sealed record SetupSendBackRequest(string? Comment);

/// <summary>What the step sends to run a stopped setup again.</summary>
/// <param name="Comment">What the owner wrote, taken trimmed and quoted in the setup's brief.</param>
public sealed record SetupRunAgainRequest(string? Comment);
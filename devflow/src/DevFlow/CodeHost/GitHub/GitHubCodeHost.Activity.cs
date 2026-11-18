using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// What watching a project's default branch reads, as the app, with the read only token every
/// read has: the activity of the default branch, as a conditional request, so a minute in which
/// nothing landed costs nothing against the rate limit. The default branch itself is read by
/// <see cref="DefaultBranch"/>, which handing out a job reads too. See "Watching the default
/// branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/activity?ref=&lt;branch&gt;&amp;per_page=100</c>, which GitHub
    /// lists newest first by default and pages by a cursor in the <c>Link</c> header. Read page by
    /// page until the change <paramref name="since"/> is reached, or for the first page only when
    /// it is null. A change on another ref than the branch, a word for its type the adapter does
    /// not know, or a commit id that is not 40 or 64 lowercase hex digits, is an answer that does
    /// not parse, since its commit is shown and made a link: the whole read is unavailable.
    /// </summary>
    public async Task<CodeHostResult<IReadOnlyList<BranchActivity>>> Activity(Guid userId, Repository repository, string branch, long? since, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        var listed = await List<IReadOnlyList<ActivityAnswer>, ActivityAnswer>(
            $"{Repo(repository)}/activity?ref={Escape(branch)}&per_page=100",
            page => page,
            AsApp(repository, cancellationToken),
            read => since is not { } id || read.Any(activity => activity.Id == id));
        return Map(listed, activities => (IReadOnlyList<BranchActivity>)[.. activities
            .TakeWhile(activity => activity.Id != since)
            .Select(activity => ToActivity(activity, branch))]);
    }

    private static BranchActivity ToActivity(ActivityAnswer activity, string branch)
    {
        if (activity.Ref != $"refs/heads/{branch}" && activity.Ref != branch)
        {
            throw new JsonException("a change on another ref than the branch asked for");
        }

        if (!CommitId().IsMatch(activity.Before) || !CommitId().IsMatch(activity.After))
        {
            throw new JsonException("a commit id that is not one");
        }

        return new BranchActivity(
            activity.Id,
            activity.Before,
            activity.After,
            activity.Timestamp,
            activity.ActivityType switch
            {
                "push" => ActivityKind.Push,
                "force_push" => ActivityKind.ForcePush,
                "branch_creation" => ActivityKind.BranchCreation,
                "branch_deletion" => ActivityKind.BranchDeletion,
                "pr_merge" => ActivityKind.PullRequestMerge,
                "merge_queue_merge" => ActivityKind.MergeQueueMerge,
                _ => throw Unknown("a change's type"),
            },
            activity.Actor?.Login);
    }

    /// <summary>A change to a branch; GitHub's schema lets the actor be null.</summary>
    private sealed record ActivityAnswer(long Id, string Before, string After, string Ref, DateTimeOffset Timestamp, string ActivityType, UserAnswer? Actor);
}
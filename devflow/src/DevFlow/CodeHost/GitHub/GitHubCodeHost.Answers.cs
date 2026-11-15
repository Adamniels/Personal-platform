using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// GitHub's answers, as records private to the adapter holding only the fields it reads, and how
/// each becomes the interface's records. A field GitHub always sends must be there, null only
/// where GitHub's schema allows it, and a word the adapter does not know, such as a new state, is
/// an answer that does not parse, which is unavailable rather than a guess. Fields it does not
/// read are ignored, since GitHub adds them.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>The login GitHub shows for an account that has been deleted.</summary>
    private const string Ghost = "ghost";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The body as <typeparamref name="T"/>, or null when it does not parse.</summary>
    private static T? Parse<T>(byte[] body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequest ToPullRequest(PullAnswer pull) => new(
        pull.Number,
        pull.Title,
        Login(pull.User),
        [.. pull.Labels.Select(label => label.Name)],
        pull.Head.Sha,
        pull.Head.Ref,
        pull.Base.Ref,
        pull.Draft,
        pull.Body ?? "",
        pull.HtmlUrl,
        pull.UpdatedAt,
        // GitHub says merged as closed with the time it was merged.
        pull.State switch
        {
            "open" => PullRequestState.Open,
            "closed" when pull.MergedAt is not null => PullRequestState.Merged,
            "closed" => PullRequestState.Closed,
            _ => throw Unknown("a pull request's state"),
        });

    private static string Login(UserAnswer? user) => user?.Login ?? Ghost;

    private static StatusState ToStatusState(string state) => state switch
    {
        "pending" => StatusState.Pending,
        "success" => StatusState.Success,
        "failure" => StatusState.Failure,
        "error" => StatusState.Error,
        _ => throw Unknown("a status's state"),
    };

    /// <summary>Waiting, requested and pending are GitHub's other words for a run that has not
    /// started, which the interface calls queued.</summary>
    private static CheckStatus ToCheckStatus(string status) => status switch
    {
        "queued" or "waiting" or "requested" or "pending" => CheckStatus.Queued,
        "in_progress" => CheckStatus.InProgress,
        "completed" => CheckStatus.Completed,
        _ => throw Unknown("a check run's status"),
    };

    private static CheckConclusion ToConclusion(string conclusion) => conclusion switch
    {
        "success" => CheckConclusion.Success,
        "failure" => CheckConclusion.Failure,
        "neutral" => CheckConclusion.Neutral,
        "cancelled" => CheckConclusion.Cancelled,
        "skipped" => CheckConclusion.Skipped,
        "timed_out" => CheckConclusion.TimedOut,
        "action_required" => CheckConclusion.ActionRequired,
        "stale" => CheckConclusion.Stale,
        _ => throw Unknown("a check run's conclusion"),
    };

    /// <summary>A word the adapter does not know, which makes the answer one that does not parse.
    /// The word itself is not repeated, since it is GitHub's data.</summary>
    private static JsonException Unknown(string what) => new($"{what} is not one the adapter knows");

    private sealed record UserAnswer(string Login);

    private sealed record LabelAnswer(string Name);

    private sealed record BranchAnswer(string Ref, string Sha);

    private sealed record PullAnswer(
        int Number,
        string Title,
        UserAnswer? User,
        IReadOnlyList<LabelAnswer> Labels,
        BranchAnswer Head,
        BranchAnswer Base,
        bool Draft,
        string? Body,
        Uri HtmlUrl,
        DateTimeOffset UpdatedAt,
        string State,
        DateTimeOffset? MergedAt);

    private sealed record CommentAnswer(long Id, UserAnswer? User, string? Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record StatusAnswer(string Context, string State, string? Description);

    private sealed record AppAnswer(string Slug);

    private sealed record CheckRunAnswer(string Name, string Status, string? Conclusion, AppAnswer App);

    private sealed record CheckRunsAnswer(IReadOnlyList<CheckRunAnswer> CheckRuns);
}
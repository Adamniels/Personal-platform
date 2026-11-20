namespace DevFlow.Features;

/// <summary>What a feature says while its whole review is due, under way or done.</summary>
public static class FeatureReview
{
    /// <summary>A feature whose head no review covers, waiting for a worker.</summary>
    public const string ToReview = "To be reviewed as a whole";

    /// <summary>A feature whose review is under way.</summary>
    public const string BeingReviewed = "The reviewer reads the whole feature";

    /// <summary>A feature whose review is done, until the minute reads whether it is ready.</summary>
    public const string Reviewed = "Reviewed; Dev flow checks whether it is ready";

    /// <summary>Why a feature waits for the owner when its review failed in the job:
    /// <paramref name="reason"/> is its run's.</summary>
    public static string Failed(string reason) => $"Dev flow could not review the whole feature: {reason}";

    /// <summary>A feature whose review asked for changes, until "Fixing a feature" lands.</summary>
    public const string ChangesAsked = "The review of the whole feature asked for changes";
}
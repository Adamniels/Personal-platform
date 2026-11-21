using System.Globalization;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Features;

/// <summary>
/// Tells a feature's cards how the feature ended, as the app. Not built yet.
/// </summary>
public sealed partial class FeatureOutcomes(UserDb database, ICodeHost host, TimeProvider time, ILogger<FeatureOutcomes> log)
{
    /// <summary>What the app says on a card still open when its feature merged.</summary>
    public static string MergedComment(int feature, int pullRequest, string defaultBranch) =>
        string.Create(CultureInfo.InvariantCulture, $"Its feature, #{feature}, was merged into `{defaultBranch}` in #{pullRequest}, so this card is closed.");

    /// <summary>What the app says on each card it opens again when its feature's pull request was
    /// closed without merging.</summary>
    public static string ClosedComment(int pullRequest) =>
        string.Create(CultureInfo.InvariantCulture, $"The feature's pull request #{pullRequest} was closed without merging, so this card is open again.");

    /// <summary>Not built yet.</summary>
    public Task TellPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        _ = (database, host, time, log, projectId, userId, cancellationToken);
        return Task.CompletedTask;
    }
}
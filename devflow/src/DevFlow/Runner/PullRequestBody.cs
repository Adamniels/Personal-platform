namespace DevFlow.Runner;

/// <summary>A decision read from the body, and whether it is marked for the owner.</summary>
internal sealed record BodyDecision(OwnerDecision Text, bool IsOwners);

/// <summary>Stub, built in the next task.</summary>
internal static class PullRequestBody
{
    internal static string Summary(string? body) => "";

    internal static int? Slice(string? body) => null;

    internal static IReadOnlyList<BodyDecision> Decisions(string? body) => [];

    internal static IReadOnlyList<string> ReviewerView(string? verdictBody) => [];
}
using DevFlow.CodeHost;

namespace DevFlow.Runner;

/// <summary>Stub, built in a later task.</summary>
internal static class RunnerReason
{
    internal const string NotSaid = "The runner did not say why.";

    internal static IReadOnlyList<string> Mentions => [];

    internal static string Gate(string line) => line;

    internal static string From(int pullRequest, IEnumerable<Comment?>? sliceComments, IEnumerable<Comment?>? pullRequestComments, string bot) => NotSaid;
}
using System.Text.Json;

using DevFlow.Protocol;
using DevFlow.Worker.Build;

namespace DevFlow.Worker.Tests.Build;

/// <summary>
/// The parts of a build job a whole run does not reach: the branch names refused before anything
/// runs, and the tail of an output. See "The worker"
/// in devflow/README.md.
/// </summary>
public sealed class BuildPartsTests
{
    private static readonly Guid JobId = Guid.Parse("0190a8f0-0000-7000-8000-0000000000b1");

    [Fact]
    public void TheDefaultBranchIsRefused()
    {
        Assert.Contains("default branch", BuildJob.Refusal(Job("main")), StringComparison.Ordinal);
        Assert.Contains("default branch", BuildJob.Refusal(Job("devflow/1-x", baseBranch: "devflow/1-x")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("--delete")]
    [InlineData("devflow/../main")]
    [InlineData("devflow//1")]
    [InlineData("devflow/1/")]
    [InlineData("devflow/1.")]
    [InlineData("devflow/1.lock")]
    [InlineData("devflow/@{u}")]
    [InlineData("devflow/1 2")]
    [InlineData("devflow/1\n2")]
    [InlineData("+devflow/1")]
    [InlineData("devflow/1:refs/heads/main")]
    [InlineData("")]
    public void ABranchGitCouldReadAsSomethingElseIsRefused(string branch)
    {
        Assert.NotNull(BuildJob.Refusal(Job(branch)));
        Assert.NotNull(BuildJob.Refusal(Job("devflow/1-add-the-greeting", baseBranch: branch)));
    }

    [Fact]
    public void ACardsBranchIsTaken() =>
        Assert.Null(BuildJob.Refusal(Job("devflow/12-show-the-empty_state.v2")));

    [Theory]
    [InlineData("", 2, "")]
    [InlineData("one\n", 2, "one\n")]
    [InlineData("one\ntwo\nthree\n", 2, "two\nthree\n")]
    [InlineData("one\ntwo\nthree", 2, "two\nthree")]
    [InlineData("one\ntwo\n", 2, "one\ntwo\n")]
    [InlineData("\n\n\n", 2, "\n\n")]
    public void TheLastLinesAreKept(string text, int lines, string expected) =>
        Assert.Equal(expected, BuildJob.Last(text, lines));

    private static Job Job(string branch, string baseBranch = "main") => new(
        JobId,
        DevFlow.Protocol.Job.Build,
        new JobCard(1, "Add the greeting", "Say hello."),
        new JobRepository("Adamniels", "Personal-platform", new Uri("https://github.com/Adamniels/Personal-platform.git"), "main"),
        branch,
        baseBranch,
        "The brief",
        JsonDocument.Parse("{}").RootElement,
        "true",
        "sk-ant-oat01-a-token",
        "ghs_a_push_key",
        new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        new JobCommitter("devflow[bot]", "1+devflow[bot]@users.noreply.github.com"),
        JobLimits.Default);
}
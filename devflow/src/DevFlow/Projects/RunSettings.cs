using System.Globalization;

using Dapper;

using DevFlow.Protocol;

using Npgsql;

namespace DevFlow.Projects;

/// <summary>
/// A project's models and limits, as handing out work and judging a verdict read them, fresh each
/// time, so a change takes effect at the next decision; a run under way keeps what its job carried.
/// The values are Claude Code's aliases, which follow the newest model of each family. See
/// "Settings" and "Jobs" in devflow/README.md.
/// </summary>
/// <param name="ModelReading">The reading job's model.</param>
/// <param name="ModelBuilding">The model of a build, a fix, a conflict and a feature's fix.</param>
/// <param name="ModelReviewing">The model of a card's review and a feature's.</param>
/// <param name="Minutes">The longest a run may take.</param>
/// <param name="Turns">The most turns a run may take.</param>
/// <param name="FixRoundsCard">The fix rounds a card's reviews may start.</param>
/// <param name="FixRoundsFeature">The fix rounds a whole feature's reviews may start.</param>
public sealed record RunSettings(string ModelReading, string ModelBuilding, string ModelReviewing, int Minutes, int Turns, int FixRoundsCard, int FixRoundsFeature)
{
    /// <summary>The models a setting takes, Claude Code's aliases.</summary>
    public static readonly IReadOnlyList<string> Models = ["opus", "sonnet", "haiku"];

    /// <summary>What every job is given to spend, in US dollars, until part 8's budget makes it a
    /// setting.</summary>
    public const decimal BudgetUsd = 25m;

    /// <summary>What <see cref="Minutes"/> takes.</summary>
    public static readonly Range MinutesRange = new(10, 240);

    /// <summary>What <see cref="Turns"/> takes.</summary>
    public static readonly Range TurnsRange = new(20, 1000);

    /// <summary>What <see cref="FixRoundsCard"/> and <see cref="FixRoundsFeature"/> take.</summary>
    public static readonly Range RoundsRange = new(0, 10);

    /// <summary>A new project's, as the table's defaults make them.</summary>
    public static RunSettings Defaults { get; } = new("opus", "opus", "opus", 90, 200, 3, 3);

    /// <summary>The model a job of <paramref name="kind"/> runs on.</summary>
    public string ModelFor(string kind) => kind switch
    {
        Job.Build or Job.Fix or Job.Conflict or Job.FeatureFix => ModelBuilding,
        Job.Review or Job.FeatureReview => ModelReviewing,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No job of this kind runs a model"),
    };

    /// <summary>The limits a job carries: the project's minutes and turns, and
    /// <see cref="BudgetUsd"/>.</summary>
    public JobLimits Limits => new(Turns, BudgetUsd, Minutes);

    /// <summary>The project's models and limits inside <paramref name="transaction"/>, which runs as
    /// the project's user; null when the user has no such project.</summary>
    public static async Task<RunSettings?> Read(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.Connection!.QuerySingleOrDefaultAsync<RunSettings>(new CommandDefinition(
            """
            SELECT model_reading AS ModelReading, model_building AS ModelBuilding, model_reviewing AS ModelReviewing,
                   run_minutes AS Minutes, run_turns AS Turns, fix_rounds_card AS FixRoundsCard, fix_rounds_feature AS FixRoundsFeature
            FROM project_settings WHERE project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>A whole number setting's range, both ends included.</summary>
    public sealed record Range(int Least, int Most)
    {
        public bool Holds(int value) => value >= Least && value <= Most;

        /// <summary>Why a value outside it is refused.</summary>
        public string Problem => string.Create(CultureInfo.InvariantCulture, $"must be from {Least} to {Most}");
    }
}
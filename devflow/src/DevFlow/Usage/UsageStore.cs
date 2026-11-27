using Dapper;

using PlatformData;

namespace DevFlow.Usage;

/// <summary>What a set of runs used in the period.</summary>
/// <param name="Runs">How many started in it.</param>
/// <param name="AgentMs">The sum of their times: each as its worker measured it, or from its start to
/// its end when it measured none, or to now while it runs.</param>
/// <param name="Tokens">Their tokens by the one definition, <see cref="Usage.Tokens"/>.</param>
/// <param name="CacheReads">Their cache reads, beside the tokens and never in them.</param>
public sealed record UsageFigures(int Runs, long AgentMs, long Tokens, long CacheReads)
{
    /// <summary>Nothing used.</summary>
    public static readonly UsageFigures None = new(0, 0, 0, 0);

    /// <summary>Both together.</summary>
    public UsageFigures Plus(UsageFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Runs + other.Runs, AgentMs + other.AgentMs, Tokens + other.Tokens, CacheReads + other.CacheReads);
    }
}

/// <summary>A card's row: what its runs used in the period, and its whole.</summary>
/// <param name="Number">The issue's number.</param>
/// <param name="Title">Its title as last read, null for a card Dev flow no longer holds, whose label was
/// taken off, which has no page.</param>
/// <param name="ElapsedMs">From its first run's start to its last run's end, or now while one runs,
/// over all its runs and not only the period's.</param>
/// <param name="FixRounds">The fix rounds its reviews have started.</param>
/// <param name="SentBack">How many times the owner sent it back.</param>
public sealed record UsageCard(
    int Number,
    string? Title,
    int Runs,
    long AgentMs,
    long ElapsedMs,
    long Tokens,
    long CacheReads,
    int FixRounds,
    int SentBack);

/// <summary>A feature's row: its own runs and its cards' together, and its cards.</summary>
/// <param name="Number">Its parent issue's number.</param>
/// <param name="Title">Its title as last read.</param>
/// <param name="ElapsedMs">From the first start to the last end of all its runs and its cards', or now
/// while one runs.</param>
/// <param name="FixRounds">The fix rounds of the whole feature its reviews have started.</param>
/// <param name="SentBack">How many times the owner sent its cards back, summed over all of them.</param>
/// <param name="Own">What its own runs used, its readings, reviews, fixes and conflicts, "The feature's
/// own runs"; null when none started in the period.</param>
/// <param name="Cards">Its cards with a run in the period, by tokens, most first.</param>
public sealed record UsageFeature(
    int Number,
    string Title,
    int Runs,
    long AgentMs,
    long ElapsedMs,
    long Tokens,
    long CacheReads,
    int FixRounds,
    int SentBack,
    UsageFigures? Own,
    IReadOnlyList<UsageCard> Cards);

/// <summary>A project's table: everything it used, and by whom.</summary>
/// <param name="Id">The project.</param>
/// <param name="Name">Its repository's name.</param>
/// <param name="SettingUp">What its own runs used, its checks and its setup's runs, "Setting up"; null
/// when none started in the period.</param>
/// <param name="Features">Its features with a run in the period, of their own or of a card, by tokens,
/// most first.</param>
/// <param name="Cards">Its cards on their own with a run in the period, by tokens, most first.</param>
/// <param name="Days">Its tokens per day, the last <see cref="UsageStore.DayCount"/> days ending today
/// in <see cref="DevFlowTime.Zone"/>, oldest first, whatever the period.</param>
public sealed record UsageProject(
    Guid Id,
    string Name,
    int Runs,
    long AgentMs,
    long Tokens,
    long CacheReads,
    UsageFigures? SettingUp,
    IReadOnlyList<UsageFeature> Features,
    IReadOnlyList<UsageCard> Cards,
    IReadOnlyList<UsageDay> Days);

/// <summary>A day of a project's last 30.</summary>
/// <param name="Date">The day, in <see cref="DevFlowTime.Zone"/>.</param>
/// <param name="Tokens">The tokens, by the one definition, of every run of the project started on it,
/// 0 with none.</param>
public sealed record UsageDay(DateOnly Date, long Tokens);

/// <summary>What <c>GET /devflow/api/usage</c> answers.</summary>
/// <param name="Period">The period counted, <c>7d</c>, <c>30d</c> or <c>all</c>.</param>
/// <param name="Projects">Each of the caller's projects with a run in it, by tokens, most first.</param>
public sealed record UsageView(string Period, IReadOnlyList<UsageProject> Projects);

/// <summary>
/// "Usage and history": what the user's runs used, by project, feature and card, read through the
/// door as the user, so another user's run never counts. A run counts where its own columns put it:
/// to its card, to its feature, or with neither, a check or a setup's, to its project as "Setting
/// up"; a card counts to the feature it is a card of now. See "Usage and history" in
/// devflow/README.md.
/// </summary>
public static class UsageStore
{
    /// <summary>The periods, by the name the address carries: rolling, so many times 24 hours back from
    /// now, or all time.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan?> Periods = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal)
    {
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
        ["all"] = null,
    };

    /// <summary>The period without one in the address.</summary>
    public const string DefaultPeriod = "30d";

    /// <summary>How many days, ending today, each project's tokens per day covers.</summary>
    public const int DayCount = 30;

    /// <summary>What <paramref name="userId"/>'s runs started in <paramref name="period"/>, one of
    /// <see cref="Periods"/>, before <paramref name="now"/> used.</summary>
    public static async Task<UsageView> For(UserDb database, Guid userId, string period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var since = Periods[period] is { } span ? now - span : (DateTimeOffset?)null;
        var today = DevFlowTime.DayOf(now);
        var days = Enumerable.Range(0, DayCount).Select(back => today.AddDays(back - (DayCount - 1))).ToArray();
        var (groups, cards, features, projects, perDay) = await database.InUserTransaction(userId, async transaction =>
        {
            // Every card, feature and project's own runs, each a group: what it used in the period,
            // null figures when none started in it, and its whole span.
            var groups = (await transaction.Connection!.QueryAsync<GroupRow>(new CommandDefinition(
                $"""
                SELECT r.project_id AS ProjectId, r.card_number AS CardNumber, r.feature_number AS FeatureNumber,
                       (count(*) FILTER (WHERE {InPeriod}))::int AS Runs,
                       coalesce(sum({AgentMs}) FILTER (WHERE {InPeriod}), 0)::bigint AS AgentMs,
                       coalesce(sum({Tokens.Sql("r")}) FILTER (WHERE {InPeriod}), 0)::bigint AS Tokens,
                       coalesce(sum({Tokens.CacheReadsSql("r")}) FILTER (WHERE {InPeriod}), 0)::bigint AS CacheReads,
                       min(r.started_at) AS FirstStarted, max(coalesce(r.finished_at, @now::timestamptz)) AS LastEnded
                FROM runs r
                GROUP BY r.project_id, r.card_number, r.feature_number
                """,
                new { since, now },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            var cards = (await transaction.Connection!.QueryAsync<CardRow>(new CommandDefinition(
                """
                SELECT c.project_id AS ProjectId, c.number AS Number, c.title AS Title, f.number AS FeatureNumber,
                       c.fix_rounds AS FixRounds, c.sent_backs AS SentBacks
                FROM cards c LEFT JOIN features f ON f.project_id = c.project_id AND f.id = c.feature_id
                """,
                transaction: transaction,
                cancellationToken: cancellationToken))).ToDictionary(card => (card.ProjectId, card.Number));
            var features = (await transaction.Connection!.QueryAsync<FeatureRow>(new CommandDefinition(
                "SELECT project_id AS ProjectId, number AS Number, title AS Title, fix_round AS FixRound FROM features",
                transaction: transaction,
                cancellationToken: cancellationToken))).ToDictionary(feature => (feature.ProjectId, feature.Number));
            var projects = (await transaction.Connection!.QueryAsync<ProjectRow>(new CommandDefinition(
                "SELECT id AS Id, name AS Name FROM projects",
                transaction: transaction,
                cancellationToken: cancellationToken))).ToDictionary(project => project.Id);
            // Each project's tokens on each of the days, whose bounds DevFlowTime gives, so Postgres
            // never reads a zone of its own; a day is the 1-based place of its bounds.
            var perDay = (await transaction.Connection!.QueryAsync<DayRow>(new CommandDefinition(
                $"""
                SELECT r.project_id AS ProjectId, d.place::int AS Place, sum({Tokens.Sql("r")})::bigint AS Tokens
                FROM unnest(@starts::timestamptz[], @ends::timestamptz[]) WITH ORDINALITY AS d(starts, ends, place)
                JOIN runs r ON r.started_at >= d.starts AND r.started_at < d.ends
                GROUP BY r.project_id, d.place
                """,
                new { starts = days.Select(DevFlowTime.StartOf).ToArray(), ends = days.Select(DevFlowTime.EndOf).ToArray() },
                transaction,
                cancellationToken: cancellationToken))).ToDictionary(day => (day.ProjectId, day.Place), day => day.Tokens);
            return (groups, cards, features, projects, perDay);
        }, cancellationToken);

        var shown = new List<UsageProject>();
        foreach (var ofProject in groups.GroupBy(group => group.ProjectId))
        {
            if (!projects.TryGetValue(ofProject.Key, out var project) || ofProject.All(group => group.Runs == 0))
            {
                continue;
            }

            var setup = ofProject.Where(group => group is { CardNumber: null, FeatureNumber: null }).ToList();
            var settingUp = setup.Sum(group => group.Runs) == 0 ? null : setup.Select(Figures).Aggregate(UsageFigures.None, (sum, next) => sum.Plus(next));

            // Each card's group, with the feature it is a card of now, null on its own.
            var cardGroups = ofProject
                .Where(group => group.CardNumber is not null)
                .Select(group => (Group: group, Card: cards.GetValueOrDefault((project.Id, group.CardNumber!.Value))))
                .ToList();
            var ownGroups = ofProject.Where(group => group.FeatureNumber is not null).ToDictionary(group => group.FeatureNumber!.Value);

            var featureNumbers = ownGroups.Keys
                .Concat(cardGroups.Where(each => each.Card?.FeatureNumber is not null).Select(each => each.Card!.FeatureNumber!.Value))
                .Distinct();
            var shownFeatures = new List<UsageFeature>();
            foreach (var number in featureNumbers)
            {
                var ofFeature = cardGroups.Where(each => each.Card?.FeatureNumber == number).ToList();
                var own = ownGroups.GetValueOrDefault(number);
                var inPeriod = ofFeature.Where(each => each.Group.Runs > 0).Select(each => Card(each.Group, each.Card)).OrderBy(Order).ToList();
                if (inPeriod.Count == 0 && own is not { Runs: > 0 })
                {
                    continue;
                }

                var spans = ofFeature.Select(each => each.Group).Append(own).OfType<GroupRow>().ToList();
                var figures = ofFeature.Select(each => Figures(each.Group)).Append(own is null ? UsageFigures.None : Figures(own)).Aggregate(UsageFigures.None, (sum, next) => sum.Plus(next));
                var feature = features.GetValueOrDefault((project.Id, number));
                shownFeatures.Add(new UsageFeature(
                    number,
                    feature?.Title ?? $"#{number}",
                    figures.Runs,
                    figures.AgentMs,
                    Elapsed(spans.Min(span => span.FirstStarted), spans.Max(span => span.LastEnded)),
                    figures.Tokens,
                    figures.CacheReads,
                    feature?.FixRound ?? 0,
                    cards.Values.Where(card => card.ProjectId == project.Id && card.FeatureNumber == number).Sum(card => card.SentBacks),
                    own is { Runs: > 0 } ? Figures(own) : null,
                    inPeriod));
            }

            var alone = cardGroups
                .Where(each => each.Card?.FeatureNumber is null && each.Group.Runs > 0)
                .Select(each => Card(each.Group, each.Card))
                .OrderBy(Order)
                .ToList();
            var total = ofProject.Select(Figures).Aggregate(UsageFigures.None, (sum, next) => sum.Plus(next));
            shown.Add(new UsageProject(
                project.Id,
                project.Name,
                total.Runs,
                total.AgentMs,
                total.Tokens,
                total.CacheReads,
                settingUp,
                [.. shownFeatures.OrderByDescending(feature => feature.Tokens).ThenBy(feature => feature.Number)],
                alone,
                [.. days.Select((day, index) => new UsageDay(day, perDay.GetValueOrDefault((project.Id, index + 1))))]));
        }

        return new UsageView(period, [.. shown.OrderByDescending(project => project.Tokens).ThenBy(project => project.Name, StringComparer.Ordinal)]);
    }

    /// <summary>Whether a run <c>r</c> started in the period from <c>@since</c>, every run when it is
    /// null.</summary>
    private const string InPeriod = "(@since::timestamptz IS NULL OR r.started_at >= @since::timestamptz)";

    /// <summary>A run <c>r</c>'s time in milliseconds: as its worker measured it, or from its start to
    /// its end, or to <c>@now</c> while it runs.</summary>
    private const string AgentMs = "coalesce(r.duration_ms, (extract(epoch FROM coalesce(r.finished_at, @now::timestamptz) - r.started_at) * 1000)::bigint)";

    private static UsageFigures Figures(GroupRow group) => new(group.Runs, group.AgentMs, group.Tokens, group.CacheReads);

    private static UsageCard Card(GroupRow group, CardRow? card) => new(
        group.CardNumber!.Value,
        card?.Title,
        group.Runs,
        group.AgentMs,
        Elapsed(group.FirstStarted, group.LastEnded),
        group.Tokens,
        group.CacheReads,
        card?.FixRounds ?? 0,
        card?.SentBacks ?? 0);

    /// <summary>Most tokens first, then by number.</summary>
    private static (long, int) Order(UsageCard card) => (-card.Tokens, card.Number);

    private static long Elapsed(DateTime from, DateTime to) => Math.Max(0, (long)(to - from).TotalMilliseconds);

    private sealed record GroupRow(Guid ProjectId, int? CardNumber, int? FeatureNumber, int Runs, long AgentMs, long Tokens, long CacheReads, DateTime FirstStarted, DateTime LastEnded);

    private sealed record CardRow(Guid ProjectId, int Number, string Title, int? FeatureNumber, int FixRounds, int SentBacks);

    private sealed record FeatureRow(Guid ProjectId, int Number, string Title, int FixRound);

    private sealed record ProjectRow(Guid Id, string Name);

    private sealed record DayRow(Guid ProjectId, int Place, long Tokens);
}
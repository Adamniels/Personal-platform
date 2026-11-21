using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Decisions;

/// <summary>A decision whose issue is still to be opened, as <see cref="DecisionIssues"/> opens it.</summary>
public sealed record UnopenedDecision(
    Guid Id,
    int CardNumber,
    string CardUrl,
    int PullRequestNumber,
    string PullRequestUrl,
    int Position,
    string Chosen,
    string Alternative,
    string Why,
    string? ReviewerView,
    string Label);

/// <summary>
/// The decisions the agent marked for the owner on a card Dev flow merged, in
/// <c>decision_issues</c>, through the door as the project's user: recorded with the merge, their
/// issues once opened, and closed when a read no longer finds them open. Written in SQL alone, over
/// the runs' reports as stored, so the merge that records them names no type of the agent's. See
/// "The merge check" in devflow/README.md.
/// </summary>
public static class DecisionStore
{
    /// <summary>
    /// The decisions marked <c>for_owner</c>, with something chosen, in the reports of the builds
    /// and fixes that succeeded of the card numbered <paramref name="card"/> in the project
    /// <paramref name="project"/>, both SQL expressions of the caller's: a <c>FROM</c> and
    /// <c>WHERE</c> over <c>runs r</c> and each decision <c>d</c>, with its place in its report as
    /// <c>d.ordinality</c>. The one rule for what is a decision for the owner, which recording them
    /// and the merge check of "sometimes" both read, so the two cannot drift apart.
    /// </summary>
    public static string MarkedFrom(string project, string card) =>
        $"""
        FROM runs r
        CROSS JOIN LATERAL jsonb_array_elements(
            CASE WHEN jsonb_typeof(r.report->'decisions') = 'array' THEN r.report->'decisions' ELSE '[]'::jsonb END
        ) WITH ORDINALITY d
        WHERE r.project_id = {project} AND r.card_number = {card} AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
          AND jsonb_typeof(d.value) = 'object' AND d.value->'for_owner' = 'true'::jsonb
          AND btrim(coalesce(d.value->>'chosen', '')) <> ''
        """;

    /// <summary>
    /// A query of the decisions for the owner of the card numbered <paramref name="card"/> in the
    /// project <paramref name="project"/>, both SQL expressions of the caller's, as
    /// <see cref="MarkedFrom"/> finds them: once each by what was chosen, whatever its case and
    /// surrounding space, with the newest run's words, <c>chosen</c>, <c>alternative</c> and
    /// <c>why</c>, and where it first appeared, <c>first_at</c>, <c>first_run</c> and
    /// <c>first_place</c>, to order them by. Recording them with a merge and listing a waiting
    /// card's read the same, so the decisions a card waits for are the ones it shows.
    /// </summary>
    public static string ForOwnerSql(string project, string card) =>
        $"""
        WITH marked AS (
            SELECT btrim(d.value->>'chosen') AS chosen, btrim(coalesce(d.value->>'alternative', '')) AS alternative,
                   btrim(coalesce(d.value->>'why', '')) AS why, r.started_at, r.id AS run_id, d.ordinality
            {MarkedFrom(project, card)}
        ), newest AS (
            SELECT DISTINCT ON (lower(chosen)) chosen, alternative, why FROM marked
            ORDER BY lower(chosen), started_at DESC, run_id DESC, ordinality
        ), first AS (
            SELECT DISTINCT ON (lower(chosen)) lower(chosen) AS name, started_at, run_id, ordinality FROM marked
            ORDER BY lower(chosen), started_at, run_id, ordinality
        )
        SELECT n.chosen, n.alternative, n.why, f.started_at AS first_at, f.run_id AS first_run, f.ordinality AS first_place
        FROM newest n JOIN first f ON f.name = lower(n.chosen)
        """;

    /// <summary>
    /// Records, inside the merge's <paramref name="transaction"/>, each decision marked
    /// <c>for_owner</c> in the reports of the card's builds and fixes that succeeded, once each by
    /// what was chosen, whatever its case and surrounding space, the newest run's words winning and
    /// placed where it first appeared; each with the reviewer's view on it from the review that
    /// approved <paramref name="commit"/>, matched by the decision's name, and the project's attention
    /// label. Every run's, not only the newest's, since a fix's report need not repeat what the
    /// build decided, and a decision for the owner is never dropped. A card merged again records
    /// nothing twice.
    /// </summary>
    public static async Task Record(NpgsqlTransaction transaction, Guid projectId, Guid userId, int card, int pullRequest, string commit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var decisions = await transaction.Connection!.QueryAsync<RecordedRow>(new CommandDefinition(
            $"""
            WITH views AS (
                SELECT lower(btrim(v.value->>'decision')) AS name, btrim(v.value->>'view') AS view
                FROM (SELECT for_owner FROM reviews
                      WHERE project_id = @projectId AND card_number = @card AND reviewed_commit = @commit AND verdict = 'approve'
                      ORDER BY reviewed_at DESC, run_id DESC LIMIT 1) approved
                CROSS JOIN LATERAL jsonb_array_elements(approved.for_owner) v
                WHERE jsonb_typeof(v.value) = 'object'
            )
            SELECT o.chosen AS Chosen, o.alternative AS Alternative, o.why AS Why,
                   (SELECT nullif(view, '') FROM views WHERE views.name = lower(o.chosen) LIMIT 1) AS ReviewerView
            FROM ({ForOwnerSql("@projectId", "@card")}) o
            ORDER BY o.first_at, o.first_run, o.first_place
            """,
            new { projectId, card, commit },
            transaction,
            cancellationToken: cancellationToken));
        var position = 0;
        foreach (var decision in decisions)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO decision_issues (id, project_id, user_id, card_number, card_title, card_url, pull_request_number,
                                             pull_request_url, position, chosen, alternative, why, reviewer_view, label, recorded_at)
                SELECT @id, c.project_id, c.user_id, c.number, coalesce(c.picked_title, c.title), c.web_url, @pullRequest,
                       c.pull_request_url, @position, @chosen, @alternative, @why, @reviewerView, s.attention_label, @now
                FROM cards c JOIN project_settings s ON s.project_id = c.project_id
                WHERE c.project_id = @projectId AND c.number = @card AND c.pull_request_url IS NOT NULL
                ON CONFLICT (project_id, pull_request_number, position) DO NOTHING
                """,
                new
                {
                    id = Guid.CreateVersion7(now),
                    projectId,
                    card,
                    pullRequest,
                    position = position++,
                    chosen = decision.Chosen,
                    alternative = decision.Alternative,
                    why = decision.Why,
                    reviewerView = decision.ReviewerView,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>The project's decisions whose issue is still to be opened, oldest first.</summary>
    public static async Task<IReadOnlyList<UnopenedDecision>> Unopened(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<UnopenedDecision>(new CommandDefinition(
            """
            SELECT id AS Id, card_number AS CardNumber, card_url AS CardUrl, pull_request_number AS PullRequestNumber,
                   pull_request_url AS PullRequestUrl, position AS Position, chosen AS Chosen, alternative AS Alternative,
                   why AS Why, reviewer_view AS ReviewerView, label AS Label
            FROM decision_issues WHERE project_id = @projectId AND issue_number IS NULL
            ORDER BY recorded_at, pull_request_number, position
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>The decision's issue, once, while none is recorded for it.</summary>
    public static Task<int> Opened(UserDb database, Guid userId, Guid id, int number, Uri url, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(url);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE decision_issues SET issue_number = @number, issue_url = @url, opened_at = @now WHERE id = @id AND issue_number IS NULL",
            new { id, number, url = url.ToString(), now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The labels the project's open decision issues were opened with, which a read
    /// lists by to learn which are still open; none when none is open, so a read costs nothing
    /// more.</summary>
    public static async Task<IReadOnlyList<string>> OpenLabels(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT label FROM decision_issues WHERE project_id = @projectId AND issue_number IS NOT NULL AND closed_at IS NULL ORDER BY label",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>Inside a read's <paramref name="transaction"/>: every open decision issue opened
    /// with <paramref name="label"/> whose number is not among <paramref name="open"/>, the issues
    /// the code host lists open with that label, is closed.</summary>
    public static Task<int> Close(NpgsqlTransaction transaction, Guid projectId, string label, IReadOnlyCollection<int> open, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(open);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE decision_issues SET closed_at = @now
            WHERE project_id = @projectId AND label = @label AND issue_number IS NOT NULL AND closed_at IS NULL
              AND NOT (issue_number = ANY (@open))
            """,
            new { projectId, label, open = open.ToArray(), now },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record RecordedRow(string Chosen, string Alternative, string Why, string? ReviewerView);
}
using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Cards;

/// <summary>
/// A project's cards in the database, through the door as the project's user: what a read found
/// merged into them, and the order Queued cards are handed out in. See "Cards" in
/// devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public static class CardStore
{
    /// <summary>The states of a card that has not started, which picking sets.</summary>
    private static readonly string[] NotStarted = ["queued", "held", "ignored"];

    /// <summary>The states of a card whose run is under way, which finishes before its card is
    /// removed.</summary>
    private static readonly string[] Running = ["building", "checking", "reviewing", "fixing", "waiting_for_owner"];

    /// <summary>
    /// Merges what a read found into the project's features and cards, inside the read's
    /// transaction. First, two rules that hang on what is stored, read here under the read's
    /// transaction so a card taken meanwhile is seen as started:
    /// <list type="bullet">
    /// <item>A feature whose parent has started as a card on its own stays that card, and its open
    /// sub-issues are ignored on their own, <see cref="CardPicker.ParentIsACard"/>.</item>
    /// <item>A closed card of a feature is kept, where it is, only when it has started, since Dev
    /// flow built it; any other is left out, as if not found.</item>
    /// </list>
    /// Then each feature is merged as a card is, see <see cref="StoreFeatures"/>, and each card:
    /// <list type="bullet">
    /// <item>A card not found any more is deleted, unless its run is under way, when it is marked
    /// unlabelled and finishes first.</item>
    /// <item>A new card is inserted where picking put it.</item>
    /// <item>A card that has not started is put where picking put it now.</item>
    /// <item>A card that has started stays where it is, unless picking ignores it now, when it
    /// is ignored with the reason; its record is replaced when the owner changed it.</item>
    /// </list>
    /// Every card found takes the feature and the position picking gave it, 1 first among the
    /// feature's cards kept. An ignored card keeps the record it had, null when it was never
    /// picked, so a run on it is still checked against what was picked. <c>changed_at</c> moves only
    /// when the state, the reason or what it waits for does. A feature <c>closed</c> without merging
    /// whose cards were told, not found any more, gives up the cards it kept. Last, a feature not
    /// found any more is deleted once no card refers to it, and marked unlabelled until then, so a
    /// closed feature labelled again later is a new one.
    /// </summary>
    public static async Task Store(NpgsqlTransaction transaction, Guid projectId, Guid userId, PickedProject found, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(found);
        var stored = (await transaction.Connection!.QueryAsync<StoredCard>(new CommandDefinition(
            """
            SELECT number AS Number, state AS State, reason AS Reason, waits_for AS WaitsFor, unlabelled AS Unlabelled,
                   pull_request_number AS PullRequestNumber, feature_id AS FeatureId
            FROM cards WHERE project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(card => card.Number);
        bool Started(int number) => stored.GetValueOrDefault(number) is { } card && !NotStarted.Contains(card.State, StringComparer.Ordinal);

        var features = new List<PickedFeature>();
        var onTheirOwn = new List<PickedCard>(found.Cards);
        foreach (var feature in found.Features)
        {
            if (Started(feature.Number) && stored[feature.Number].FeatureId is null)
            {
                onTheirOwn.Add(feature.AsCard());
                onTheirOwn.AddRange(feature.Cards.Where(card => card.Open).Select(card => card with
                {
                    Priority = "none",
                    Verdict = Verdict.Ignored,
                    Reason = CardPicker.ParentIsACard(feature.Number),
                    WaitsFor = [],
                    Record = null,
                    Feature = null,
                }));
            }
            else
            {
                features.Add(feature);
            }
        }

        var featureIds = await StoreFeatures(transaction, projectId, userId, features, now, cancellationToken);
        var placed = new List<(PickedCard Card, Guid? Feature, int? Position)>();
        foreach (var feature in features)
        {
            var position = 0;
            foreach (var card in feature.Cards.Where(card => card.Open || Started(card.Number)))
            {
                placed.Add((card, featureIds[feature.Number], ++position));
            }
        }

        placed.AddRange(onTheirOwn.Select(card => (card, (Guid?)null, (int?)null)));
        placed = [.. placed.DistinctBy(entry => entry.Card.Number)];

        // A feature whose pull request was closed without merging, its cards told, that is no
        // longer found gives its cards up, so labelled again later it starts afresh: what was built
        // for it never reached the default branch.
        var givenUp = (await transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT id FROM features
            WHERE project_id = @projectId AND state = 'closed' AND outcome_told_at IS NOT NULL AND NOT (number = ANY (@kept))
            """,
            new { projectId, kept = features.Select(feature => feature.Number).ToArray() },
            transaction,
            cancellationToken: cancellationToken))).ToHashSet();

        var numbers = placed.Select(entry => entry.Card.Number).ToHashSet();
        foreach (var gone in stored.Values.Where(card => !numbers.Contains(card.Number)))
        {
            // A card Dev flow carried to a merged pull request is kept: the pull request's Closes
            // line closed its issue as it merged, which takes it off the list, and the card is the
            // story of what was built; unless its feature gave it up.
            var command = Running.Contains(gone.State, StringComparer.Ordinal)
                ? (gone.Unlabelled ? null : "UPDATE cards SET unlabelled = true, changed_at = @now WHERE project_id = @projectId AND number = @number")
                : gone.State == "done" && gone.PullRequestNumber is not null && !(gone.FeatureId is { } feature && givenUp.Contains(feature)) ? null
                : "DELETE FROM cards WHERE project_id = @projectId AND number = @number";
            if (command is not null)
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(command, new { projectId, number = gone.Number, now }, transaction, cancellationToken: cancellationToken));
            }
        }

        foreach (var (card, feature, position) in placed)
        {
            if (!card.Open)
            {
                // A closed card kept is one Dev flow built: it is stored, and stays where its run
                // put it, with the record it had; only where it is in its feature moves.
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE cards SET title = @title, web_url = @webUrl, feature_id = @feature, position = @position, unlabelled = false, read_at = @now
                    WHERE project_id = @projectId AND number = @number
                    """,
                    new { projectId, number = card.Number, title = card.Title, webUrl = card.Url.ToString(), feature, position, now },
                    transaction,
                    cancellationToken: cancellationToken));
                continue;
            }

            var before = stored.GetValueOrDefault(card.Number);
            var started = before is not null && !NotStarted.Contains(before.State, StringComparer.Ordinal);
            var (state, reason, waitsFor) = started && card.Verdict != Verdict.Ignored
                ? (before!.State, before.Reason, before.WaitsFor)
                : (State(card.Verdict), card.Reason, card.WaitsFor.ToArray());
            var changed = before is null
                || before.State != state
                || before.Reason != reason
                || !before.WaitsFor.SequenceEqual(waitsFor, StringComparer.Ordinal)
                || before.Unlabelled;
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, reason, waits_for, priority, issue_created_at,
                                   picked_body_sha256, picked_title, picked_edit, picked_body, unlabelled, feature_id, position, changed_at, read_at)
                VALUES (@id, @projectId, @userId, @number, @title, @webUrl, @state, @reason, @waitsFor, @priority, @createdAt,
                        @bodySha, @pickedTitle, @pickedEdit, @pickedBody, false, @feature, @position, @now, @now)
                ON CONFLICT (project_id, number) DO UPDATE
                SET title = EXCLUDED.title, web_url = EXCLUDED.web_url, state = EXCLUDED.state, reason = EXCLUDED.reason,
                    waits_for = EXCLUDED.waits_for, priority = EXCLUDED.priority, issue_created_at = EXCLUDED.issue_created_at,
                    picked_body_sha256 = CASE WHEN @picked THEN EXCLUDED.picked_body_sha256 ELSE cards.picked_body_sha256 END,
                    picked_title = CASE WHEN @picked THEN EXCLUDED.picked_title ELSE cards.picked_title END,
                    picked_edit = CASE WHEN @picked THEN EXCLUDED.picked_edit ELSE cards.picked_edit END,
                    picked_body = CASE WHEN @picked THEN EXCLUDED.picked_body ELSE cards.picked_body END,
                    unlabelled = false,
                    feature_id = EXCLUDED.feature_id, position = EXCLUDED.position,
                    changed_at = CASE WHEN @changed THEN EXCLUDED.changed_at ELSE cards.changed_at END,
                    read_at = EXCLUDED.read_at
                """,
                new
                {
                    id = Guid.CreateVersion7(now),
                    projectId,
                    userId,
                    number = card.Number,
                    title = card.Title,
                    webUrl = card.Url.ToString(),
                    state,
                    reason,
                    waitsFor,
                    priority = card.Priority,
                    createdAt = card.CreatedAt,
                    picked = card.Record is not null,
                    bodySha = card.Record?.BodySha256,
                    pickedTitle = card.Record?.Title,
                    pickedEdit = card.Record?.NewestEdit,
                    pickedBody = card.Record?.Body,
                    feature,
                    position,
                    changed,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        // A feature no longer found goes once nothing refers to it; while a card of it is still
        // under way, or kept, it is marked unlabelled, as that card is.
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM features f
            WHERE f.project_id = @projectId AND NOT (f.number = ANY (@kept))
              AND NOT EXISTS (SELECT 1 FROM cards c WHERE c.project_id = f.project_id AND c.feature_id = f.id)
            """,
            new { projectId, kept = features.Select(feature => feature.Number).ToArray() },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET unlabelled = true, changed_at = @now
            WHERE project_id = @projectId AND NOT (number = ANY (@kept)) AND NOT unlabelled
            """,
            new { projectId, kept = features.Select(feature => feature.Number).ToArray(), now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Merges the features found into the project's, as <see cref="Store"/> merges cards: a new
    /// one is inserted where picking put it, one that has not started is put there now, and one
    /// that has started, which only a later slice does, stays where it is unless it is now
    /// ignored; its record is replaced when it was picked. Each found is labelled again. Answers
    /// each feature's id by its number.
    /// </summary>
    private static async Task<Dictionary<int, Guid>> StoreFeatures(NpgsqlTransaction transaction, Guid projectId, Guid userId, IReadOnlyList<PickedFeature> found, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = (await transaction.Connection!.QueryAsync<StoredFeature>(new CommandDefinition(
            "SELECT number AS Number, state AS State, reason AS Reason, unlabelled AS Unlabelled FROM features WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(feature => feature.Number);
        var ids = new Dictionary<int, Guid>();
        foreach (var feature in found)
        {
            var before = stored.GetValueOrDefault(feature.Number);
            var started = before is not null && !NotStarted.Contains(before.State, StringComparer.Ordinal);
            var (state, reason) = started && feature.Verdict != Verdict.Ignored
                ? (before!.State, before.Reason)
                : (State(feature.Verdict), feature.Reason);
            var changed = before is null || before.State != state || before.Reason != reason || before.Unlabelled;
            ids[feature.Number] = await transaction.Connection!.QuerySingleAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO features (id, project_id, user_id, number, title, web_url, state, reason, priority, issue_created_at,
                                      picked_body_sha256, picked_title, picked_edit, picked_body, unlabelled, changed_at, read_at)
                VALUES (@id, @projectId, @userId, @number, @title, @webUrl, @state, @reason, @priority, @createdAt,
                        @bodySha, @pickedTitle, @pickedEdit, @pickedBody, false, @now, @now)
                ON CONFLICT (project_id, number) DO UPDATE
                SET title = EXCLUDED.title, web_url = EXCLUDED.web_url, state = EXCLUDED.state, reason = EXCLUDED.reason,
                    priority = EXCLUDED.priority, issue_created_at = EXCLUDED.issue_created_at,
                    picked_body_sha256 = CASE WHEN @picked THEN EXCLUDED.picked_body_sha256 ELSE features.picked_body_sha256 END,
                    picked_title = CASE WHEN @picked THEN EXCLUDED.picked_title ELSE features.picked_title END,
                    picked_edit = CASE WHEN @picked THEN EXCLUDED.picked_edit ELSE features.picked_edit END,
                    picked_body = CASE WHEN @picked THEN EXCLUDED.picked_body ELSE features.picked_body END,
                    unlabelled = false,
                    changed_at = CASE WHEN @changed THEN EXCLUDED.changed_at ELSE features.changed_at END,
                    read_at = EXCLUDED.read_at
                RETURNING id
                """,
                new
                {
                    id = Guid.CreateVersion7(now),
                    projectId,
                    userId,
                    number = feature.Number,
                    title = feature.Title,
                    webUrl = feature.Url.ToString(),
                    state,
                    reason,
                    priority = feature.Priority,
                    createdAt = feature.CreatedAt,
                    picked = feature.Record is not null,
                    bodySha = feature.Record?.BodySha256,
                    pickedTitle = feature.Record?.Title,
                    pickedEdit = feature.Record?.NewestEdit,
                    pickedBody = feature.Record?.Body,
                    changed,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        return ids;
    }

    /// <summary>
    /// The number of the card that holds a card of a feature back, as SQL over <c>cards</c>: the
    /// nearest before it in its feature that is not <c>done</c>, <c>stopped</c> or <c>held</c>, so
    /// whatever else it is, queued, ignored, under way or waiting for the owner, it comes first.
    /// Null for a card with none before it in such a state, and for a card on its own. A stopped
    /// or held card holds back only itself, and what is blocked by it on GitHub, which picking
    /// records in <c>waits_for</c>. See "A feature's cards, one at a time" in
    /// devflow/docs/devflow-architecture.md.
    /// </summary>
    public const string HeldBackBy = """
        (SELECT e.number FROM cards e
         WHERE e.project_id = cards.project_id AND e.feature_id = cards.feature_id AND e.position < cards.position
           AND e.state NOT IN ('done', 'stopped', 'held')
         ORDER BY e.position DESC LIMIT 1)
        """;

    /// <summary>
    /// A card's reason as the page shows it, as SQL over <c>cards</c>: its own, and for a queued
    /// card of a feature with none, "Waits for #&lt;n&gt;, earlier in the feature" while
    /// <see cref="HeldBackBy"/> names one. Computed when it is read rather than stored, since the
    /// card before it moves on without a read of the project.
    /// </summary>
    public const string ShownReason = $"""
        CASE WHEN cards.state = 'queued' AND cards.reason IS NULL AND cards.feature_id IS NOT NULL AND {HeldBackBy} IS NOT NULL
             THEN 'Waits for #' || {HeldBackBy} || ', earlier in the feature'
             ELSE cards.reason END
        """;

    /// <summary>The order queued cards free to start are handed out in, as SQL over <c>cards</c>:
    /// <c>priority: high</c>, then no priority, then <c>priority: low</c>, each oldest first by when
    /// the issue was written. Handing out a job takes the first, see
    /// <see cref="Jobs.JobStore.Claim"/>.</summary>
    public const string QueueOrder = "CASE priority WHEN 'high' THEN 0 WHEN 'none' THEN 1 ELSE 2 END, issue_created_at, number";

    /// <summary>The numbers of the project's cards on their own that are queued and free to start,
    /// in the order they are handed out, <see cref="QueueOrder"/>. A feature's cards are left out:
    /// which of them is next is the feature's own rule, <see cref="HeldBackBy"/>, which
    /// <see cref="Jobs.JobStore"/> applies when it hands one out.</summary>
    public static async Task<IReadOnlyList<int>> Queue(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<int>(new CommandDefinition(
            $"""
            SELECT number FROM cards
            WHERE project_id = @projectId AND state = 'queued' AND cardinality(waits_for) = 0 AND feature_id IS NULL
            ORDER BY {QueueOrder}
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>The project's features as last read, in the order their cards would be taken,
    /// <see cref="QueueOrder"/>, each with its cards by position; and its cards on their own,
    /// newest change first; each card's reason as <see cref="ShownReason"/> gives it. Null when the
    /// user has no such project.</summary>
    public static async Task<CardList?> List(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<CardList?>(userId, async transaction =>
        {
            if (await transaction.Connection!.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM projects WHERE id = @projectId", new { projectId }, transaction, cancellationToken: cancellationToken)) is null)
            {
                return null;
            }

            var rows = (await transaction.Connection!.QueryAsync<ListedCard>(new CommandDefinition(
                $"""
                SELECT number AS Number, title AS Title, web_url AS WebUrl, state AS State, {ShownReason} AS Reason, waits_for AS WaitsFor,
                       priority AS Priority, changed_at AS ChangedAt, feature_id AS FeatureId
                FROM cards WHERE project_id = @projectId
                ORDER BY position NULLS LAST, changed_at DESC, number DESC
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            var features = await transaction.Connection!.QueryAsync<ListedFeature>(new CommandDefinition(
                $"""
                SELECT id AS Id, number AS Number, title AS Title, web_url AS WebUrl, state AS State, reason AS Reason,
                       priority AS Priority, changed_at AS ChangedAt
                FROM features WHERE project_id = @projectId
                ORDER BY {QueueOrder}
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            return new CardList(
                [.. features.Select(feature => new FeatureView(
                    feature.Number, feature.Title, feature.WebUrl, feature.State, feature.Reason, feature.Priority, Utc(feature.ChangedAt),
                    [.. rows.Where(row => row.FeatureId == feature.Id).Select(View)]))],
                [.. rows.Where(row => row.FeatureId is null).Select(View)]);
        }, cancellationToken);
    }

    private static CardView View(ListedCard row) =>
        new(row.Number, row.Title, row.WebUrl, row.State, row.Reason, row.WaitsFor, row.Priority, Utc(row.ChangedAt));

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private static string State(Verdict verdict) => verdict switch
    {
        Verdict.Queued => "queued",
        Verdict.Held => "held",
        Verdict.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not a verdict"),
    };

    /// <summary>Why a card whose pull request was closed without merging is stopped.</summary>
    public const string PullRequestClosed = "The pull request was closed";

    /// <summary>The project's cards with a pull request that is followed, each with its number:
    /// those that wait for the owner, and those under review or being fixed, read as the project's
    /// user.</summary>
    public static async Task<IReadOnlyList<(int Card, int PullRequest)>> FollowedPulls(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<(int, int)>(new CommandDefinition(
            """
            SELECT number, pull_request_number FROM cards
            WHERE project_id = @projectId AND state IN ('waiting_for_owner', 'reviewing', 'fixing') AND pull_request_number IS NOT NULL
            ORDER BY number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows];
    }

    /// <summary>
    /// Moves a card that waits for the owner, or is under review or being fixed, on as its pull
    /// request went,
    /// inside the read's transaction: merged, by anyone, is <c>done</c>; closed without merging is
    /// <c>stopped</c>, <see cref="PullRequestClosed"/>, or removed when its label was taken off while
    /// it ran, as a run that ends removes such a card. Only while it still waits, or is under
    /// review or being fixed, with that pull request; a review or fix still running then ends
    /// without moving it.
    /// </summary>
    public static async Task Follow(NpgsqlTransaction transaction, Guid projectId, FollowedPull pull, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(pull);
        const string Where = "project_id = @projectId AND number = @card AND state IN ('waiting_for_owner', 'reviewing', 'fixing') AND pull_request_number = @pullRequest";
        var parameters = new { projectId, card = pull.Card, pullRequest = pull.PullRequest, reason = PullRequestClosed, now };
        if (pull.Merged)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                $"UPDATE cards SET state = 'done', reason = NULL, changed_at = @now WHERE {Where}",
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            return;
        }

        if (await transaction.Connection!.ExecuteAsync(new CommandDefinition($"DELETE FROM cards WHERE {Where} AND unlabelled", parameters, transaction, cancellationToken: cancellationToken)) > 0)
        {
            return;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"UPDATE cards SET state = 'stopped', reason = @reason, changed_at = @now, stopped_at = @now, stop_marked_at = NULL WHERE {Where}",
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>A card as listed, set by property, as <see cref="StoredCard"/> is.</summary>
    private sealed class ListedCard
    {
        public int Number { get; init; }

        public string Title { get; init; } = "";

        public string WebUrl { get; init; } = "";

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public string Priority { get; init; } = "";

        public DateTime ChangedAt { get; init; }

        public Guid? FeatureId { get; init; }
    }

    /// <summary>A feature as listed.</summary>
    private sealed record ListedFeature(Guid Id, int Number, string Title, string WebUrl, string State, string? Reason, string Priority, DateTime ChangedAt);

    /// <summary>A card as stored, set by property: Dapper reads <c>text[]</c> as an array no
    /// constructor parameter matches.</summary>
    private sealed class StoredCard
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public bool Unlabelled { get; init; }

        public int? PullRequestNumber { get; init; }

        public Guid? FeatureId { get; init; }
    }

    /// <summary>A feature as stored, what merging it needs.</summary>
    private sealed record StoredFeature(int Number, string State, string? Reason, bool Unlabelled);
}

/// <summary>Where a card's pull request went, as a read of the project found it: merged, or closed
/// without merging.</summary>
/// <param name="Card">The card's issue number.</param>
/// <param name="PullRequest">Its pull request's number.</param>
/// <param name="Merged">Whether it merged, rather than closed.</param>
public sealed record FollowedPull(int Card, int PullRequest, bool Merged);
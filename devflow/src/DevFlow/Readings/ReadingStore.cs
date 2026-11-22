using System.Globalization;
using System.Text.Json;

using Dapper;

using DevFlow.Briefs;
using DevFlow.Cards;
using DevFlow.Projects;
using DevFlow.Protocol;

using Npgsql;

using PlatformData;

using ReadingForm = DevFlow.Protocol.Reading;

namespace DevFlow.Readings;

/// <summary>Cards taken for a reading, with the run made for it.</summary>
/// <param name="RunId">The reading's run id, the job's.</param>
/// <param name="Card">The job's card: the feature's parent as picked, or the card on its own.</param>
/// <param name="Feature">The feature the cards belong to, with what its done cards left; null for a
/// card on its own.</param>
/// <param name="Cards">The cards to read, in the feature's order, each as picked.</param>
/// <param name="Branch">The branch whose code is read: the feature's where it is made, otherwise the
/// default branch.</param>
/// <param name="Base">The default branch.</param>
/// <param name="Again">For a refused card read again, alone, what its reading is given beside it;
/// null for a first reading.</param>
public sealed record ClaimedRead(Guid RunId, BriefCard Card, BriefFeature? Feature, IReadOnlyList<BriefCard> Cards, string Branch, string Base, ReaderAgain? Again = null);

/// <summary>A card's reading, as its page shows it: what the reader answered, the questions with the
/// answer each is built on, here the recommendation, and what is missing. Every text is the reader's,
/// shown as text.</summary>
/// <param name="Outcome"><c>ready</c>, <c>questions</c> or <c>too_thin</c>.</param>
/// <param name="Questions">Each question, with the recommendation, why, and the answer the card is
/// built on.</param>
/// <param name="Missing">What the card lacks, when it is too thin.</param>
/// <param name="ReadAt">When the reading came back.</param>
/// <param name="Commented">Whether the comment for the reading is on the card yet: Dev flow's listing
/// the questions and the answers chosen, the owner's answers posted as him, or the refusal's listing
/// what is missing; false for a ready card.</param>
/// <param name="Answered">Whether the owner answered its questions, rather than the card going on
/// the recommendations; false for any outcome but <c>questions</c>.</param>
public sealed record CardReadingView(string Outcome, IReadOnlyList<CardQuestion> Questions, IReadOnlyList<string> Missing, DateTimeOffset ReadAt, bool Commented, bool Answered);

/// <summary>A question a reading asked of a card, and the answer it is built on.</summary>
/// <param name="Question">The question.</param>
/// <param name="Recommendation">The reader's recommendation.</param>
/// <param name="Why">Why it recommends so.</param>
/// <param name="Answer">The answer the card is built on: the owner's, once he answered, and the
/// recommendation otherwise.</param>
/// <param name="Recommended">Whether the answer is the recommendation, accepted by the owner or
/// chosen without him.</param>
public sealed record CardQuestion(string Question, string Recommendation, string Why, string Answer, bool Recommended);

/// <summary>A card's questions whose comment Dev flow has yet to post, with the reading that asked
/// them.</summary>
public sealed record PendingQuestions(Guid RunId, int Number, IReadOnlyList<CardQuestion> Questions);

/// <summary>A refused card whose refusal waits to be marked on its issue.</summary>
/// <param name="RunId">The reading that refused it, which its comment names.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Missing">What that reading found missing.</param>
/// <param name="Commented">Whether that reading's comment is posted.</param>
/// <param name="Labelled">Whether the app's attention label is on the issue.</param>
public sealed record PendingRefusal(Guid RunId, int Number, IReadOnlyList<string> Missing, bool Commented, bool Labelled);

/// <summary>
/// Readings in the database, through the door as the project's user: which cards are read, taking a
/// feature's cards or a card on its own for a reading, what its result does to each card, a reading
/// put back, and what the briefs and a card's page read of a reading. A card's reading is the newest
/// row of <c>card_readings</c> with an outcome; the note of the code a feature touches is its
/// newest reading's report's <c>touches</c>, as a card's handover note is its run's report's
/// <c>handover</c>. See "Jobs" in devflow/README.md and "Reading a card" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class ReadingStore
{
    /// <summary>What a card says while it is read.</summary>
    public const string BeingRead = "An agent reads it before anything is built";

    /// <summary>What a card says while it waits for the owner's answers to its reading's
    /// questions, <c>needs_answers</c>.</summary>
    public const string NeedsAnswers = "Waits for your answers to its questions";

    /// <summary>Whether a card has been read, as SQL over <c>cards</c>: a reading of it came back in
    /// its form.</summary>
    public const string IsRead = """
        EXISTS (SELECT 1 FROM card_readings cr
                WHERE cr.project_id = cards.project_id AND cr.card_number = cards.number AND cr.outcome IS NOT NULL)
        """;

    /// <summary>Whether a card may be built by its reading, as SQL over <c>cards</c>: its newest
    /// reading that came back in its form found it ready or with questions. A card read as too thin
    /// is never built, however it came to be queued again; reading it again after it changes is
    /// "Reading again".</summary>
    public const string ReadToBuild = """
        (SELECT cr.outcome FROM card_readings cr
         WHERE cr.project_id = cards.project_id AND cr.card_number = cards.number AND cr.outcome IS NOT NULL
         ORDER BY cr.read_at DESC, cr.run_id DESC
         LIMIT 1) IN ('ready', 'questions')
        """;

    /// <summary>Whether a card is to be read again, as SQL over <c>cards</c>: its newest reading that
    /// came back in its form found it too thin. Only a refused card a trusted account edited or
    /// commented on since is queued with such a reading, <see cref="Refusals"/>, and it is read
    /// again, alone, rather than built.</summary>
    public const string ReadAgain = """
        coalesce((SELECT cr.outcome FROM card_readings cr
                  WHERE cr.project_id = cards.project_id AND cr.card_number = cards.number AND cr.outcome IS NOT NULL
                  ORDER BY cr.read_at DESC, cr.run_id DESC
                  LIMIT 1) = 'too_thin', false)
        """;

    /// <summary>What makes a card due a reading, as SQL over <c>cards</c>: free to start, as
    /// <see cref="Jobs.JobStore.FreeToStart"/> says, and not read, or to be read again,
    /// <see cref="ReadAgain"/>; with <c>@projectId</c> and <c>@watchedSince</c>.</summary>
    public const string Due = $"{Jobs.JobStore.FreeToStart} AND (NOT {IsRead} OR {ReadAgain})";

    /// <summary>Why a card read as too thin is refused: what it lacks, each on its own.</summary>
    public static string TooThin(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return Cut("Too thin to build; it lacks: " + string.Join("; ", missing.Select(line => line.Trim())));
    }

    /// <summary>
    /// Takes the project's first card due a reading, by <see cref="CardStore.QueueOrder"/>, inside
    /// <paramref name="transaction"/>, which runs as the project's user: <see cref="Due"/>; locks it with <c>FOR UPDATE SKIP LOCKED</c>. A
    /// card on its own is read alone. A card of a feature is read with every queued card of its
    /// feature that is not read, in the feature's order, while no run of
    /// the feature itself is under way, its row locked as well, so one reading of a feature runs at a
    /// time; the code read is the feature's branch where it is made, and otherwise
    /// <paramref name="defaultBranch"/>. Records the reading's run and a row of
    /// <c>card_readings</c> for each card read, with its body's SHA-256 and title as handed, and moves
    /// each to <c>reading</c>. A held card is left out, since it would not be handed out: it is read
    /// once released, when its turn comes, so no reading's outcome waits on a card it cannot move.
    /// A card to be read again, <see cref="ReadAgain"/>, is read alone, of a feature or not, as a run
    /// of the card, with what the reading that refused it found missing, the note of the code its
    /// work touches, <see cref="NoteOfCard"/>, and the comments that asked for it.
    /// Null when there is none.
    /// </summary>
    public static async Task<ClaimedRead?> Claim(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);
        var watchedSince = Watching.WatchStore.WatchedSince(now);
        var first = await transaction.Connection!.QuerySingleOrDefaultAsync<FirstCard>(new CommandDefinition(
            $"""
            SELECT number AS Number, feature_id AS FeatureId, {ReadAgain} AS Again FROM cards
            WHERE {Due}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            new { projectId, watchedSince },
            transaction,
            cancellationToken: cancellationToken));
        if (first is null)
        {
            return null;
        }

        FeatureRow? feature = null;
        if (first.FeatureId is { } featureId)
        {
            feature = await transaction.Connection!.QuerySingleOrDefaultAsync<FeatureRow>(new CommandDefinition(
                """
                SELECT f.number AS Number, coalesce(f.picked_title, f.title) AS Title, coalesce(f.picked_body, '') AS Body,
                       CASE WHEN f.branch_base_commit IS NOT NULL THEN f.branch END AS Branch
                FROM features f
                WHERE f.id = @featureId
                  AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = f.project_id AND r.feature_number = f.number AND r.finished_at IS NULL)
                FOR UPDATE SKIP LOCKED
                """,
                new { featureId },
                transaction,
                cancellationToken: cancellationToken));
            if (feature is null)
            {
                return null;
            }
        }

        var cards = (await transaction.Connection!.QueryAsync<ToRead>(new CommandDefinition(
            feature is null || first.Again
                ? "SELECT number AS Number, picked_title AS Title, picked_body AS Body, picked_body_sha256 AS BodySha256 FROM cards WHERE project_id = @projectId AND number = @number"
                : $"""
                  SELECT number AS Number, picked_title AS Title, picked_body AS Body, picked_body_sha256 AS BodySha256
                  FROM cards
                  WHERE project_id = @projectId AND feature_id = @featureId AND state = 'queued' AND picked_body IS NOT NULL
                    AND NOT {IsRead}
                    AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
                  ORDER BY position
                  FOR UPDATE SKIP LOCKED
                  """,
            new { projectId, number = first.Number, featureId = first.FeatureId },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        var runId = Guid.CreateVersion7(now);
        var branch = feature?.Branch ?? defaultBranch;
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, feature_number, worker_id, kind, branch, base, started_at, run_minutes)
            VALUES (@runId, @projectId, @userId, @card, @feature, @workerId, 'read', @branch, @baseBranch, @now, @minutes)
            """,
            new { runId, projectId, userId, card = feature is null || first.Again ? first.Number : (int?)null, feature = first.Again ? null : feature?.Number, workerId, branch, baseBranch = defaultBranch, now, minutes = run.Minutes },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var card in cards)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO card_readings (run_id, project_id, user_id, card_number, body_sha256, title)
                VALUES (@runId, @projectId, @userId, @number, @sha, @title)
                """,
                new { runId, projectId, userId, number = card.Number, sha = card.BodySha256, title = card.Title },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = 'reading', reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = ANY(@numbers) AND state = 'queued'",
            new { projectId, numbers = cards.Select(card => card.Number).ToArray(), reason = BeingRead, now },
            transaction,
            cancellationToken: cancellationToken));

        var read = cards.Select(card => new BriefCard(card.Number, card.Title, card.Body)).ToList();
        var again = first.Again ? await Again(transaction, projectId, first.Number, cancellationToken) : null;
        if (feature is null)
        {
            return new ClaimedRead(runId, read[0], null, read, branch, defaultBranch, again);
        }

        var parent = new BriefCard(feature.Number, feature.Title, feature.Body);
        var done = await Jobs.JobStore.FeatureOf(transaction, projectId, first.Number, cancellationToken) ?? new BriefFeature(feature.Number, feature.Title, feature.Body, []);
        return new ClaimedRead(runId, again is null ? parent : read[0], done, read, branch, defaultBranch, again);
    }

    /// <summary>What a card read again is given beside it: what its newest reading found missing,
    /// the note of the code its work touches, and the comments kept that asked for it.</summary>
    private static async Task<ReaderAgain> Again(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        var missing = (await Of(transaction, projectId, number, cancellationToken))?.Missing ?? [];
        var comments = await transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT read_again_comments::text FROM cards WHERE project_id = @projectId AND number = @number",
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        return new ReaderAgain(missing, await NoteOfCard(transaction, projectId, number, cancellationToken), Refusals.Kept(comments));
    }

    /// <summary>The numbers of the cards a reading run reads, read inside
    /// <paramref name="transaction"/>, as its user.</summary>
    public static async Task<IReadOnlyList<int>> CardsOf(NpgsqlTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return [.. await transaction.Connection!.QueryAsync<int>(new CommandDefinition(
            "SELECT card_number FROM card_readings WHERE run_id = @runId ORDER BY card_number",
            new { runId },
            transaction,
            cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// What a reading in its form does, inside the transaction that records its result: each card's
    /// answer is kept on its row, and each card still <c>reading</c> moves on: a ready card to
    /// <c>queued</c>; one with questions to <c>needs_answers</c>, <see cref="NeedsAnswers"/>, while
    /// the project's setting <c>questions</c> is on, as it is read now, and otherwise to
    /// <c>queued</c>, on the recommendations; and one too thin to <c>refused</c>, with what is
    /// missing as its reason, <see cref="TooThin"/>. A card whose label was taken off while it
    /// was read is removed instead, as any card whose run ends is. The count of failures in the card
    /// starts afresh for each, and the comments that asked for a card to be read again are let go.
    /// </summary>
    public static async Task Read(NpgsqlTransaction transaction, Guid projectId, Guid runId, ReadingForm reading, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(reading);
        var asks = await Asks(transaction, projectId, cancellationToken);
        foreach (var card in reading.Cards)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE card_readings SET outcome = @outcome, questions = @questions::jsonb, missing = @missing::jsonb, read_at = @now
                WHERE run_id = @runId AND card_number = @number
                """,
                new
                {
                    runId,
                    number = card.Number,
                    outcome = Word(card.Outcome),
                    questions = JsonSerializer.Serialize(card.Questions, ReadingForm.Json),
                    missing = JsonSerializer.Serialize(card.Missing, ReadingForm.Json),
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
            var (state, reason) = card.Outcome switch
            {
                ReadingOutcome.TooThin => ("refused", TooThin(card.Missing)),
                ReadingOutcome.Questions when asks => ("needs_answers", NeedsAnswers),
                _ => ("queued", (string?)null),
            };
            await Move(transaction, projectId, card.Number, state, reason, now, cancellationToken);
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET in_card_failures = 0, failed_at_commit = NULL, read_again_comments = NULL WHERE project_id = @projectId AND number = @number",
                new { projectId, number = card.Number },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>
    /// Holds for the owner's answers, inside the read's transaction, after the cards are stored,
    /// every queued card of the project whose newest reading found questions nobody has answered yet,
    /// neither the owner nor Dev flow's comment on the recommendations, while the setting
    /// <c>questions</c> is on: it moves to <c>needs_answers</c>, <see cref="NeedsAnswers"/>. So a card
    /// picked again, after its label was taken off and put back, still waits for his answers rather
    /// than going on the recommendations. Only a card with no run under way.
    /// </summary>
    public static Task<int> HoldForAnswers(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = 'needs_answers', reason = @needsAnswers, changed_at = @now
            WHERE project_id = @projectId AND state = 'queued' AND picked_body IS NOT NULL
              AND (SELECT s.questions FROM project_settings s WHERE s.project_id = cards.project_id)
              AND EXISTS (SELECT 1 FROM (SELECT cr.outcome, cr.comment_id FROM card_readings cr
                                         WHERE cr.project_id = cards.project_id AND cr.card_number = cards.number AND cr.outcome IS NOT NULL
                                         ORDER BY cr.read_at DESC, cr.run_id DESC
                                         LIMIT 1) newest
                          WHERE newest.outcome = 'questions' AND newest.comment_id IS NULL)
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
            """,
            new { projectId, needsAnswers = NeedsAnswers, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Whether the project's questions wait for the owner, its setting <c>questions</c> as
    /// it is now, read inside <paramref name="transaction"/>; false for a project the user does not
    /// have.</summary>
    private static async Task<bool> Asks(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        await transaction.Connection!.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT questions FROM project_settings WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)) ?? false;

    /// <summary>
    /// A reading that ended without one in its form, inside the transaction that records it, or one
    /// found lost: its rows stay without an outcome, the record of the cards it was handed, so none of
    /// them counts as read; and each card it read that is still <c>reading</c> moves to the state and
    /// reason <paramref name="ending"/> gives for it; a card whose label was taken off meanwhile is
    /// removed.
    /// </summary>
    public static async Task PutBack(NpgsqlTransaction transaction, Guid projectId, Guid runId, Func<int, Task<(string State, string? Reason)>> ending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(ending);
        var cards = await CardsOf(transaction, runId, cancellationToken);
        foreach (var number in cards)
        {
            var (state, reason) = await ending(number);
            await Move(transaction, projectId, number, state, reason, now, cancellationToken);
        }
    }

    /// <summary>Moves a card that is <c>reading</c> to <paramref name="state"/>, or removes it when
    /// its label was taken off while it was read; any other card stays as it is.</summary>
    private static async Task Move(NpgsqlTransaction transaction, Guid projectId, int number, string state, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var removed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM cards WHERE project_id = @projectId AND number = @number AND unlabelled AND state = 'reading'",
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        if (removed > 0)
        {
            return;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = @state, reason = @reason, changed_at = @now,
                stopped_at = CASE WHEN @state = 'stopped' THEN @now ELSE stopped_at END,
                stop_marked_at = CASE WHEN @state = 'stopped' THEN NULL ELSE stop_marked_at END
            WHERE project_id = @projectId AND number = @number AND state = 'reading'
            """,
            new { projectId, number, state, reason, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The note of the code a card's work touches, as its briefs quote it, read inside
    /// <paramref name="transaction"/>, as the card's user: for a card of a feature, the newest reading
    /// of the feature that came back in its form, and for a card on its own, its own newest; null
    /// before one did, or when its note is empty.
    /// </summary>
    public static async Task<string?> NoteOfCard(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var note = await transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT r.report->>'touches' FROM cards c
            JOIN runs r ON r.project_id = c.project_id AND r.kind = 'read' AND r.result = 'succeeded'
                       AND (r.card_number = c.number AND c.feature_id IS NULL
                            OR r.feature_number = (SELECT f.number FROM features f WHERE f.id = c.feature_id))
            WHERE c.project_id = @projectId AND c.number = @number
            ORDER BY r.finished_at DESC, r.id DESC
            LIMIT 1
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        return Kept(note);
    }

    /// <summary>The note of the code a feature touches, its newest reading's, read inside
    /// <paramref name="transaction"/>, as its user; null before one came back in its form, or when
    /// its note is empty.</summary>
    public static async Task<string?> NoteOfFeature(NpgsqlTransaction transaction, Guid projectId, int feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var note = await transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT r.report->>'touches' FROM runs r
            WHERE r.project_id = @projectId AND r.feature_number = @feature AND r.kind = 'read' AND r.result = 'succeeded'
            ORDER BY r.finished_at DESC, r.id DESC
            LIMIT 1
            """,
            new { projectId, feature },
            transaction,
            cancellationToken: cancellationToken));
        return Kept(note);
    }

    /// <summary>The questions of a card's reading, each with the answer it is built on, read inside
    /// <paramref name="transaction"/>, as its user; empty when its reading has none.</summary>
    public static async Task<IReadOnlyList<CardQuestion>> Answers(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken) =>
        (await Of(transaction, projectId, number, cancellationToken))?.Questions ?? [];

    /// <summary>A card's reading, its newest with an outcome, read inside
    /// <paramref name="transaction"/>, as its user; null before one came back.</summary>
    public static async Task<CardReadingView?> Of(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<ReadingRow>(new CommandDefinition(
            """
            SELECT outcome AS Outcome, questions::text AS Questions, missing::text AS Missing, read_at AS ReadAt, comment_id AS CommentId
            FROM card_readings
            WHERE project_id = @projectId AND card_number = @number AND outcome IS NOT NULL
            ORDER BY read_at DESC, run_id DESC
            LIMIT 1
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new CardReadingView(
                row.Outcome,
                Questions(row.Questions),
                JsonSerializer.Deserialize<List<string>>(row.Missing) ?? [],
                new DateTimeOffset(DateTime.SpecifyKind(row.ReadAt, DateTimeKind.Utc)),
                row.CommentId is not null,
                false);
    }

    /// <summary>The project's cards whose newest reading has questions Dev flow has yet to post,
    /// read as the project's user, cards still kept, not ignored and not waiting for the owner's
    /// answers, in number order.</summary>
    public static async Task<IReadOnlyList<PendingQuestions>> Pending(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<PendingRow>(new CommandDefinition(
            """
            SELECT cr.run_id AS RunId, cr.card_number AS Number, cr.questions::text AS Questions
            FROM card_readings cr
            JOIN cards c ON c.project_id = cr.project_id AND c.number = cr.card_number AND c.state NOT IN ('ignored', 'needs_answers')
            WHERE cr.project_id = @projectId AND cr.outcome = 'questions' AND cr.comment_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM card_readings newer
                              WHERE newer.project_id = cr.project_id AND newer.card_number = cr.card_number
                                AND newer.outcome IS NOT NULL AND newer.read_at > cr.read_at)
            ORDER BY cr.card_number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new PendingQuestions(row.RunId, row.Number, Questions(row.Questions)))];
    }

    /// <summary>The project's refused cards whose newest reading, too thin, waits to be marked on the
    /// issue: the attention label not on it, or the refusal's comment not posted. Read as the
    /// project's user, in number order.</summary>
    public static async Task<IReadOnlyList<PendingRefusal>> PendingRefusals(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<RefusalRow>(new CommandDefinition(
            """
            SELECT cr.run_id AS RunId, c.number AS Number, cr.missing::text AS Missing, cr.comment_id IS NOT NULL AS Commented,
                   c.attention_labelled AS Labelled
            FROM cards c
            JOIN LATERAL (SELECT * FROM card_readings newest
                          WHERE newest.project_id = c.project_id AND newest.card_number = c.number AND newest.outcome IS NOT NULL
                          ORDER BY newest.read_at DESC, newest.run_id DESC
                          LIMIT 1) cr ON true
            WHERE c.project_id = @projectId AND c.state = 'refused' AND cr.outcome = 'too_thin'
              AND (cr.comment_id IS NULL OR NOT c.attention_labelled)
            ORDER BY c.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new PendingRefusal(row.RunId, row.Number, JsonSerializer.Deserialize<List<string>>(row.Missing) ?? [], row.Commented, row.Labelled))];
    }

    /// <summary>Records Dev flow's comment on the card, by its id, for the reading it is for: the one
    /// that asked its questions, or the one that refused it. As the project's user.</summary>
    public static async Task Commented(UserDb database, Guid userId, Guid projectId, Guid runId, int number, long commentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE card_readings SET comment_id = @commentId WHERE project_id = @projectId AND run_id = @runId AND card_number = @number AND comment_id IS NULL",
            new { projectId, runId, number, commentId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>Questions as stored, each with the answer the card is built on: the
    /// recommendation, until the owner answers.</summary>
    private static List<CardQuestion> Questions(string stored) =>
        [.. (JsonSerializer.Deserialize<List<ReadingQuestion>>(stored, ReadingForm.Json) ?? [])
            .Select(question => new CardQuestion(question.Question, question.Recommendation, question.Why, question.Recommendation, true))];

    private static string? Kept(string? note) => note is { } text && text.Trim().Length > 0 ? text : null;

    private static string Word(ReadingOutcome outcome) => outcome switch
    {
        ReadingOutcome.Ready => "ready",
        ReadingOutcome.Questions => "questions",
        ReadingOutcome.TooThin => "too_thin",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not an outcome"),
    };

    /// <summary>A reason cut to the length a card's line keeps, one fewer where the cut would split a
    /// surrogate pair.</summary>
    private static string Cut(string reason) =>
        reason.Length <= Jobs.RunStore.MostReason ? reason
        : char.IsHighSurrogate(reason[Jobs.RunStore.MostReason - 1]) ? reason[..(Jobs.RunStore.MostReason - 1)]
        : reason[..Jobs.RunStore.MostReason];

    private sealed record FirstCard(int Number, Guid? FeatureId, bool Again);

    private sealed record FeatureRow(int Number, string Title, string Body, string? Branch);

    private sealed record ToRead(int Number, string Title, string Body, byte[] BodySha256);

    private sealed record ReadingRow(string Outcome, string Questions, string Missing, DateTime ReadAt, long? CommentId);

    private sealed record PendingRow(Guid RunId, int Number, string Questions);

    private sealed record RefusalRow(Guid RunId, int Number, string Missing, bool Commented, bool Labelled);
}
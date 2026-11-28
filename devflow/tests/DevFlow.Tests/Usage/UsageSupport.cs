using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Usage;

/// <summary>
/// A world of runs for the usage tests, staged past row level security with the figures each test
/// names, so a test reads what it counts: projects with a worker each, features, cards of a feature
/// or on their own, and runs of every kind, each with its start, end, time and usage.
/// </summary>
internal sealed class UsageWorld : IAsyncDisposable
{
    public const string Path = "/devflow/api/usage";

    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly Dictionary<Guid, Guid> _workers = [];

    private UsageWorld(Routes routes) => Routes = routes;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Guid User => Routes.User;

    public Routes Routes { get; }

    /// <summary>The time on the app's clock, which a test moves.</summary>
    public DateTimeOffset Now => Routes.Time.GetUtcNow();

    public static async Task<UsageWorld> Start() => new(await Routes.Start());

    /// <summary>A project of <paramref name="user"/>'s, named <paramref name="name"/>, with a worker
    /// of its own that every run of it is handed to.</summary>
    public async Task<Guid> Project(string name, Guid? user = null, long repository = Reading.Reading.RepositoryId)
    {
        var owner = user ?? User;
        var project = await Reading.Reading.InsertProject(Routes.Door, owner, repository, name);
        var worker = Guid.CreateVersion7();
        await Execute(
            """
            INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
            VALUES (@project, @worker, @owner, 'usage-worker', '1', 'base', now(), now())
            """,
            new { project, worker, owner });
        _workers[project] = worker;
        return project;
    }

    /// <summary>A feature of the project, numbered <paramref name="number"/>, titled "Feature
    /// <paramref name="number"/>", with <paramref name="fixRounds"/> of its own used.</summary>
    public async Task<Guid> Feature(Guid project, int number, int fixRounds = 0)
    {
        var id = Guid.CreateVersion7();
        await Execute(
            """
            INSERT INTO features (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at,
                                  picked_body_sha256, picked_title, picked_body, fix_round)
            SELECT @id, @project, user_id, @number, 'Feature ' || @number, 'https://github.com/Adamniels/Personal-platform/issues/' || @number,
                   'building', 'none', now(), now(), now(), sha256('body'::bytea), 'Feature ' || @number, 'body', @fixRounds
            FROM projects WHERE id = @project
            """,
            new { id, project, number, fixRounds });
        return id;
    }

    /// <summary>A card of the project, numbered <paramref name="number"/>, titled "Card
    /// <paramref name="number"/>", on its own or at <paramref name="position"/> in
    /// <paramref name="feature"/>, with <paramref name="fixRounds"/> used and sent back
    /// <paramref name="sentBacks"/> times.</summary>
    public Task Card(Guid project, int number, Guid? feature = null, int? position = null, int fixRounds = 0, int sentBacks = 0) => Execute(
        """
        INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at, feature_id, position,
                           picked_body_sha256, picked_title, picked_body, fix_rounds, sent_backs, sent_back_from, sent_back_from_url)
        SELECT gen_random_uuid(), @project, user_id, @number, 'Card ' || @number, 'https://github.com/Adamniels/Personal-platform/issues/' || @number,
               'queued', 'none', now(), now(), now(), @feature, @position, sha256('body'::bytea), 'Card ' || @number, 'body', @fixRounds, @sentBacks,
               CASE WHEN @sentBacks > 0 THEN 90 END, CASE WHEN @sentBacks > 0 THEN 'https://github.com/Adamniels/Personal-platform/pull/90' END
        FROM projects WHERE id = @project
        """,
        new { project, number, feature, position, fixRounds, sentBacks });

    /// <summary>
    /// A run of the project of <paramref name="kind"/>, of <paramref name="card"/>, of
    /// <paramref name="feature"/> or, for a check or a setup's run, of neither; started
    /// <paramref name="startedAgo"/> before now and ended <paramref name="endedAgo"/> before now,
    /// or under way when that is null; with the time its worker measured, and Claude's figures, each
    /// null when not given.
    /// </summary>
    public Task Run(
        Guid project,
        string kind,
        TimeSpan startedAgo,
        TimeSpan? endedAgo,
        int? card = null,
        int? feature = null,
        long? durationMs = null,
        long? input = null,
        long? cacheCreation = null,
        long? cacheRead = null,
        long? output = null,
        string credential = "subscription",
        decimal? cost = null) =>
        RunAt(project, kind, Now - startedAgo, endedAgo is { } ago ? Now - ago : null, card, feature, durationMs, input, cacheCreation, cacheRead, output, credential, cost);

    /// <summary>As <see cref="Run"/>, started at <paramref name="started"/> and ended at
    /// <paramref name="ended"/>, whatever the app's clock says, for a test that reads the store at a
    /// time of its own.</summary>
    public async Task RunAt(
        Guid project,
        string kind,
        DateTimeOffset started,
        DateTimeOffset? ended,
        int? card = null,
        int? feature = null,
        long? durationMs = null,
        long? input = null,
        long? cacheCreation = null,
        long? cacheRead = null,
        long? output = null,
        string credential = "subscription",
        decimal? cost = null)
    {
        int? setup = null;
        if (kind is "setup" or "setup_review")
        {
            setup = 1;
            await Execute(
                """
                INSERT INTO setups (project_id, user_id, number, branch, state, created_at, changed_at)
                SELECT @project, user_id, 1, 'devflow/setup', 'queued', now(), now() FROM projects WHERE id = @project
                ON CONFLICT DO NOTHING
                """,
                new { project });
        }

        await Execute(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, feature_number, worker_id, kind, branch, base, started_at, finished_at, result,
                              run_minutes, duration_ms, input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens,
                              head_commit, from_commit, default_commit, recipe_version, setup_number, credential, cost_usd)
            SELECT gen_random_uuid(), @project, user_id, @card, @feature, @worker, @kind, 'devflow/usage', 'main', @started, @ended,
                   CASE WHEN @ended::timestamptz IS NULL THEN NULL ELSE 'succeeded' END,
                   90, @durationMs, @input, @cacheCreation, @cacheRead, @output,
                   CASE WHEN @kind IN ('review', 'feature_review', 'setup_review') THEN @commit END,
                   CASE WHEN @kind IN ('fix', 'feature_fix', 'check', 'conflict') THEN @commit END,
                   CASE WHEN @kind = 'conflict' THEN @commit END,
                   CASE WHEN @kind = 'check' THEN '1' END,
                   @setup, @credential, @cost
            FROM projects WHERE id = @project
            """,
            new
            {
                project,
                card,
                feature,
                worker = _workers[project],
                kind,
                started,
                ended,
                durationMs,
                input,
                cacheCreation,
                cacheRead,
                output,
                commit = Commit,
                setup,
                credential,
                cost,
            });
    }

    /// <summary>What <c>GET /devflow/api/usage</c> answers with <paramref name="query"/>, which must
    /// be 200.</summary>
    public async Task<JsonObject> Usage(string query = "")
    {
        var response = await Routes.App.Get(Path + query, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    private async Task Execute(string sql, object parameters)
    {
        await using var admin = await Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: Ct));
    }

    public ValueTask DisposeAsync() => Routes.DisposeAsync();
}
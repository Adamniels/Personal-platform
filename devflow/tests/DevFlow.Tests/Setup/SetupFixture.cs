using Dapper;

using PlatformData.Testing;

namespace DevFlow.Tests.Setup;

/// <summary>
/// A project's setup taken as already checked, for the tests of everything after setting up: each
/// setup version a worker of the project reports, with no check that passed, gets one, as a check
/// job that passed would leave it. No run is made for it, so a test's runs are its own, as with
/// <see cref="Readings.ReadingFixture"/>. See "Setting up a project" in devflow/README.md.
/// </summary>
internal static class SetupFixture
{
    /// <summary>A commit id the fixture's checks name.</summary>
    public static readonly string Commit = new('0', 40);

    /// <summary>Every version a worker of <paramref name="project"/> reports with no check that
    /// passed, and the one the fake worker reports, the project's as last read or <c>fake</c>,
    /// checked and passed, past row level security.</summary>
    public static async Task AllChecked(TestDatabase database, Guid project, CancellationToken cancellationToken)
    {
        await using var admin = await database.OpenAdmin(cancellationToken);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO recipe_checks (run_id, project_id, user_id, version, commit, passed, exit_code, output, checked_at)
            SELECT gen_random_uuid(), p.id, p.user_id, v.version, @commit, true, 0, 'The fixture''s check passed', now()
            FROM projects p
            JOIN (SELECT project_id, recipe_version AS version FROM workers
                  UNION SELECT id, coalesce(recipe_version, 'fake') FROM projects) v ON v.project_id = p.id
            WHERE p.id = @project
              AND NOT EXISTS (SELECT 1 FROM recipe_checks c WHERE c.project_id = p.id AND c.version = v.version AND c.passed)
            """,
            new { project, commit = Commit },
            cancellationToken: cancellationToken));
    }

    /// <summary>The project's settings taken as accepted on its settings step, as the owner saving
    /// them would leave them, past row level security; once, as the step is.</summary>
    public static async Task Accepted(TestDatabase database, Guid project, CancellationToken cancellationToken)
    {
        await using var admin = await database.OpenAdmin(cancellationToken);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET accepted_at = now() WHERE project_id = @project AND accepted_at IS NULL",
            new { project },
            cancellationToken: cancellationToken));
    }
}
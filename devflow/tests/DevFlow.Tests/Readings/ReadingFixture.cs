using Dapper;

using PlatformData.Testing;

namespace DevFlow.Tests.Readings;

/// <summary>
/// Cards taken as already read, for the tests of everything after reading: each card of the project
/// that was picked and has no reading gets one, ready, with no questions and nothing missing, as a
/// reading job that found it so would leave it. No run is made for it, so a test's runs are its own.
/// See "Jobs" in devflow/README.md.
/// </summary>
internal static class ReadingFixture
{
    /// <summary>Every picked card of <paramref name="project"/> with no reading, read as ready, past
    /// row level security.</summary>
    public static async Task AllReady(TestDatabase database, Guid project, CancellationToken cancellationToken)
    {
        await using var admin = await database.OpenAdmin(cancellationToken);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO card_readings (run_id, project_id, user_id, card_number, body_sha256, title, outcome, questions, missing, read_at)
            SELECT gen_random_uuid(), c.project_id, c.user_id, c.number, c.picked_body_sha256, c.picked_title, 'ready', '[]', '[]', c.read_at
            FROM cards c
            WHERE c.project_id = @project AND c.picked_body_sha256 IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM card_readings cr
                              WHERE cr.project_id = c.project_id AND cr.card_number = c.number AND cr.outcome IS NOT NULL)
            """,
            new { project },
            cancellationToken: cancellationToken));
    }
}
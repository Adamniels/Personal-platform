using PlatformData.Testing;

using Review.Settings;

namespace Review.Tests;

/// <summary>
/// Review's databases for the tests that need one, from <c>PlatformData.Testing</c>: a test takes
/// its own with <c>Databases.Review.Empty</c>, with no migration applied, or
/// <c>Databases.Review.Migrated</c>, a copy of a template every migration in review/migrations/ has
/// run on, owned by review_owner and dropped when the test ends. A test that needs no database does
/// not take one. See "Running its tests" in libs/dotnet/PlatformData/README.md.
///
/// Two connection strings, both from the environment, both required: unset is a failure, never a
/// skip, so a run that lost one cannot pass by testing nothing.
/// <list type="bullet">
/// <item><c>REVIEW_TEST_ADMIN_DATABASE_URL</c>, the superuser, for making and dropping databases
/// and for acting as review_owner, whose password tests do not hold. Only tests hold it.</item>
/// <item><c>REVIEW_APP_DATABASE_URL</c>, review_app's own, the one review serves with, pointed at
/// each test's database, so a test checks what the running service is.</item>
/// </list>
/// From review/:
/// <code>
/// REVIEW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
/// REVIEW_APP_DATABASE_URL="postgres://review_app:$(grep '^REVIEW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/review" \
/// dotnet test
/// </code>
/// </summary>
internal static class Databases
{
    /// <summary>The run's one Postgres for review, which holds its template.</summary>
    public static TestPostgres Review { get; } = new(ReviewSettings.Service, ReviewSettings.AppDatabaseUrl, typeof(Commands).Assembly);
}

/// <summary>
/// Drops the run's template once every test has run. An assembly fixture, created for every run,
/// that does nothing until then, so a run of tests that need no database needs no database.
/// </summary>
internal sealed class Templates : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await Databases.Review.DropTemplate();
}
using DevFlow.Settings;

using PlatformData.Testing;

namespace DevFlow.Tests;

/// <summary>
/// Dev flow's databases for the tests that need one, from <c>PlatformData.Testing</c>: a test takes
/// its own with <c>Databases.DevFlow.Empty</c>, with no migration applied, or
/// <c>Databases.DevFlow.Migrated</c>, a copy of a template every migration in devflow/migrations/
/// has run on, owned by devflow_owner and dropped when the test ends. A test that needs no database
/// does not take one. See "Running its tests" in libs/dotnet/PlatformData/README.md.
///
/// Two connection strings, both from the environment, both required: unset is a failure, never a
/// skip, so a run that lost one cannot pass by testing nothing.
/// <list type="bullet">
/// <item><c>DEVFLOW_TEST_ADMIN_DATABASE_URL</c>, the superuser, for making and dropping databases
/// and for acting as devflow_owner, whose password tests do not hold. Only tests hold it.</item>
/// <item><c>DEVFLOW_APP_DATABASE_URL</c>, devflow_app's own, the one Dev flow serves with, pointed
/// at each test's database, so a test checks what the running service is.</item>
/// </list>
/// From devflow/:
/// <code>
/// DEVFLOW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
/// DEVFLOW_APP_DATABASE_URL="postgres://devflow_app:$(grep '^DEVFLOW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/devflow" \
/// dotnet test
/// </code>
/// </summary>
internal static class Databases
{
    /// <summary>The run's one Postgres for Dev flow, which holds its template.</summary>
    public static TestPostgres DevFlow { get; } = new(DevFlowSettings.Service, DevFlowSettings.AppDatabaseUrl, typeof(Commands).Assembly);
}

/// <summary>
/// Drops the run's template once every test has run. An assembly fixture, created for every run,
/// that does nothing until then, so a run of tests that need no database needs no database.
/// </summary>
internal sealed class Templates : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await Databases.DevFlow.DropTemplate();
}
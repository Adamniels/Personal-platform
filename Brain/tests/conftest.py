"""The database fixtures, built from `tests/harness.py`, which says how the
test databases are made and which connection strings they need."""

from collections.abc import AsyncIterator, Generator, Iterator

import pytest
from psycopg_pool import AsyncConnectionPool

from brain import migrate
from tests.harness import (
    MIGRATIONS,
    app_url,
    create_database,
    drop_database,
    owner_connection,
    sweep,
    unique_name,
)

_FAILED = pytest.StashKey[bool]()


@pytest.hookimpl(wrapper=True)
def pytest_runtest_makereport(
    item: pytest.Item, call: pytest.CallInfo[None]
) -> Generator[None, pytest.TestReport, pytest.TestReport]:
    # Kept on the item, so a fixture can tell at teardown whether its test
    # passed, and keep the database of one that did not. The call's report
    # is made before the fixtures are torn down.
    report = yield
    if report.failed:
        item.stash[_FAILED] = True
    return report


def _keep_or_drop(request: pytest.FixtureRequest, name: str) -> None:
    if request.node.stash.get(_FAILED, False):
        print(f"kept {name} for a look, a later run sweeps it")
    else:
        drop_database(name)


@pytest.fixture(scope="session")
def template() -> Iterator[str]:
    """Migrated once per session, from the files in Brain/migrations/."""
    sweep()
    name = unique_name("tpl")
    create_database(name)
    with owner_connection(name) as conn:
        migrate.run(conn, MIGRATIONS)
    # A template cannot be copied while anyone is connected to it, and the
    # connection above is closed by now.
    yield name
    drop_database(name)


@pytest.fixture
def database(template: str, request: pytest.FixtureRequest) -> Iterator[str]:
    """This test's own copy of the migrated template."""
    name = unique_name("test")
    create_database(name, template=template)
    yield name
    _keep_or_drop(request, name)


@pytest.fixture
def empty_database(request: pytest.FixtureRequest) -> Iterator[str]:
    """A database of its own, set up as brain.sh sets up `brain` and with no
    migration applied, for testing `brain migrate` itself."""
    sweep()
    name = unique_name("empty")
    create_database(name)
    yield name
    _keep_or_drop(request, name)


@pytest.fixture
async def pool(database: str) -> AsyncIterator[AsyncConnectionPool]:
    """A pool of one connection as brain_app, so a test knows the next
    checkout is the same connection the last one returned."""
    pool = AsyncConnectionPool(app_url(database), min_size=1, max_size=1, open=False)
    await pool.open(wait=True)
    yield pool
    await pool.close()

"""The one door to the brain's database. See "The database" in Brain/README.md.

Every query the brain makes runs inside `transaction(pool, user_id)`, which
takes a connection from the pool, opens a transaction and sets
`brain.user_id` for that transaction alone. Nothing else in the package may
take a connection from the pool; `tests/test_isolation.py` checks it.

The setting is what every table's row level security policy reads, through
`CURRENT_USER_SQL`. It is transaction scoped, `set_config(..., true)`, the
`SET LOCAL` "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md` requires: a plain `SET` would
survive on the pooled connection into whatever borrows it next.
"""

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from uuid import UUID

from psycopg import AsyncConnection
from psycopg.rows import TupleRow
from psycopg_pool import AsyncConnectionPool

# The brain's pool: connections as brain_app, opened in the app's lifespan.
type Pool = AsyncConnectionPool[AsyncConnection[TupleRow]]

# The one setting name. Every policy reads it, and only `transaction` sets it.
USER_SETTING = "brain.user_id"

# The user a policy compares `user_id` with, the expression every scoped
# table's policy uses: `USING (user_id = <this>)`. With no user set it is
# NULL, so the comparison is never true and the query returns nothing. The
# setting is missing on a connection that never had it, hence the `true`, and
# reverts to '' after a transaction that set it, hence the `nullif`; either
# way a bare cast would raise instead of returning nothing.
CURRENT_USER_SQL = f"nullif(current_setting('{USER_SETTING}', true), '')::uuid"


# How long startup waits for the pool's first connections before the brain
# refuses to start.
OPEN_TIMEOUT_SECONDS = 10.0


async def open_pool(conninfo: str) -> Pool:
    """The pool the app holds for its lifetime, as brain_app. It waits for its
    first connections, so a database that cannot be reached, or a wrong
    password, stops the brain at startup rather than at its first query."""
    pool: Pool = AsyncConnectionPool(conninfo, open=False)
    await pool.open(wait=True, timeout=OPEN_TIMEOUT_SECONDS)
    return pool


@asynccontextmanager
async def transaction(
    pool: Pool, user_id: UUID
) -> AsyncIterator[AsyncConnection[TupleRow]]:
    """A connection in a transaction on behalf of `user_id`, committed when
    the block ends and rolled back if it raises. The connection is lent for
    the block only: keeping it past the block is keeping it outside the door."""
    if not isinstance(user_id, UUID):
        # Only the core mints a user id, and it reaches the brain as a UUID
        # from the verified token. A string here is a bug upstream.
        raise TypeError(f"user_id must be a UUID, not {type(user_id).__name__}")
    async with pool.connection() as conn, conn.transaction():
        await conn.execute(
            "SELECT set_config(%s, %s, true)", (USER_SETTING, str(user_id))
        )
        yield conn

"""The memory store: items, their subjects, relations and evidence, written
through the API as profile entries and rules, on a database of the test's
own, as brain_app. The tables and which kind goes in which store are in
`Brain/docs/data-model.md`; why a hand made entry needs no confirming is
"Standing rules are created deliberately" in `Brain/docs/trust-model.md`."""

import uuid

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")


# What the app role may change in the memory tables: an item's status and
# why it is inactive, nothing else. Content is never changed in place; an
# edit is a new version, see "Versions" in Brain/docs/data-model.md.
REFUSED = [
    "UPDATE items SET content = 'rewritten'",
    "UPDATE items SET kind = 'rule'",
    "UPDATE items SET store = 'knowledge'",
    "UPDATE items SET lineage_id = id",
    "UPDATE items SET created_by_event = created_by_event",
    "UPDATE item_subjects SET topic_id = NULL",
    "UPDATE item_relations SET ended_at = now()",
    "UPDATE item_evidence SET kind = 'contradicts'",
    "DELETE FROM items",
    "DELETE FROM item_subjects",
    "DELETE FROM item_relations",
    "DELETE FROM item_evidence",
]


@pytest.mark.parametrize("statement", REFUSED)
async def test_the_service_can_change_only_status_on_an_item(
    statement: str, pool: AsyncConnectionPool
) -> None:
    with pytest.raises(psycopg.errors.InsufficientPrivilege, match="permission denied"):
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(statement)


async def test_the_service_may_change_an_items_status(
    pool: AsyncConnectionPool,
) -> None:
    # The control for the test above: allowed, on no rows.
    async with db.transaction(pool, ALICE) as conn:
        await conn.execute(
            "UPDATE items SET status = 'inactive', inactive_reason = 'retired'"
        )

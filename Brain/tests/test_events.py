"""Events, what happened, recorded through the API and read back as the
timeline, on a database of the test's own, as brain_app. The shape is "The
shape" in `Brain/docs/event-model.md`, the table and the verb list are in
`Brain/docs/data-model.md`."""

import re
import uuid
from pathlib import Path

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db
from tests.harness import admin_connection

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")

DATA_MODEL = Path(__file__).resolve().parents[1] / "docs" / "data-model.md"


UPDATES = [
    "UPDATE events SET evidence = 'rewritten'",
    "UPDATE events SET payload = '{}'",
    "UPDATE events SET recorded_at = now()",
    "DELETE FROM events",
    "TRUNCATE events",
    "INSERT INTO verbs VALUES ('glanced', 'engagement', 1, 1)",
    "UPDATE verbs SET strength = 1",
    "DELETE FROM verbs",
]


@pytest.mark.parametrize("statement", UPDATES)
async def test_the_service_cannot_change_or_remove_an_event(
    statement: str, pool: AsyncConnectionPool, database: str
) -> None:
    # On a stored row, so a refusal is the role's and not an empty table's.
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            " VALUES (%s, %s, 'opened', NULL, NULL, NULL, 'news', NULL, NULL,"
            " now(), 'instant', now(), 'Opened it.', '{}', 1, 'k')",
            (uuid.uuid7(), ALICE),
        )

    with pytest.raises(psycopg.errors.InsufficientPrivilege, match="permission denied"):
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(statement)


async def test_the_service_reads_events_and_verbs_and_adds_events(
    pool: AsyncConnectionPool,
) -> None:
    # The control for the test above.
    async with db.transaction(pool, ALICE) as conn:
        await conn.execute("SELECT * FROM verbs")
        await conn.execute("SELECT * FROM events")
        await conn.execute(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            f" VALUES (%s, {db.CURRENT_USER_SQL}, 'opened', NULL, NULL, NULL,"
            " 'news', NULL, NULL, now(), 'instant', now(), 'Opened it.', '{}',"
            " 1, 'k')",
            (uuid.uuid7(),),
        )


def documented_verbs() -> set[tuple[str, str, int, int]]:
    """The rows of the table under "The verb list" in the data model doc."""
    text = DATA_MODEL.read_text()
    section = text.split("## The verb list", 1)[1].split("\n## ", 1)[0]
    found = set()
    for line in section.splitlines():
        match = re.match(
            r"^\|\s*`(\w+)`\s*\|\s*(\w+)\s*\|\s*(\d)\s*\|\s*(-?\d)\s*\|$", line
        )
        if match:
            verb, kind, strength, polarity = match.groups()
            found.add((verb, kind, int(strength), int(polarity)))
    return found


def test_every_verb_declares_strength_and_polarity(database: str) -> None:
    with admin_connection(database) as conn:
        seeded = {
            (str(verb), str(kind), int(str(strength)), int(str(polarity)))
            for verb, kind, strength, polarity in conn.execute(
                "SELECT verb, kind, strength, polarity FROM verbs"
            )
        }

    documented = documented_verbs()
    assert len(documented) == 16
    assert seeded == documented

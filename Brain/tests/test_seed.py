"""The evaluation's seed, `eval/person.json`: that it loads through the
functions the API uses, and that it holds what the slice settled it must, so
a later edit cannot quietly drop what a case group needs."""

from collections import Counter
from datetime import timedelta
from pathlib import Path
from typing import Any

import pytest
from psycopg_pool import AsyncConnectionPool

from brain import now_and_history, seed
from tests.harness import admin_connection


@pytest.fixture
async def mira(database: str, pool: AsyncConnectionPool) -> seed.Loaded:
    return await seed.load(pool, seed.read(seed.PERSON))


def counted(
    database: str, query: str, user: str, **params: object
) -> list[tuple[Any, ...]]:
    """Rows of `query`, run by the superuser, which row level security does
    not hold back, so `%(user)s` names the user."""
    with admin_connection(database) as conn:
        return conn.execute(query, {"user": seed.account_id(user), **params}).fetchall()


async def test_the_seed_holds_what_the_slice_lists(
    database: str, mira: seed.Loaded
) -> None:
    def rows(query: str, user: str = seed.ASKED_AS) -> list[tuple[Any, ...]]:
        return counted(database, query, user)

    categories = Counter(
        category
        for (category,) in rows(
            "SELECT category FROM items WHERE user_id = %(user)s"
            " AND kind = 'profile' AND status = 'active'"
        )
    )
    assert sum(categories.values()) >= 8
    assert set(categories) == {
        "identity",
        "goals",
        "values",
        "interests",
        "preferences",
        "working_style",
    }
    [(rules,)] = rows(
        "SELECT count(*) FROM items WHERE user_id = %(user)s AND kind = 'rule'"
        " AND status = 'active'"
    )
    assert rules >= 5

    [(topics, aliases, provisional)] = rows(
        "SELECT count(*), count(alias_of), count(*) FILTER (WHERE state = 'provisional')"
        " FROM topics WHERE user_id = %(user)s"
    )
    assert topics >= 14 and aliases == 2 and provisional == 2
    # A broader_than chain three levels deep: a topic over one over another.
    [(chains,)] = rows(
        "SELECT count(*) FROM topic_relations a JOIN topic_relations b"
        " ON b.from_topic = a.to_topic AND b.kind = 'broader_than'"
        " WHERE a.user_id = %(user)s AND a.kind = 'broader_than'"
    )
    assert chains >= 1
    [(related,)] = rows(
        "SELECT count(*) FROM topic_relations WHERE user_id = %(user)s"
        " AND kind = 'related_to'"
    )
    assert related >= 1
    [(set_on,)] = rows(
        "SELECT count(DISTINCT coalesce(t.alias_of, t.id)) FROM items i"
        " JOIN item_subjects s ON s.item_id = i.id JOIN topics t ON t.id = s.topic_id"
        " WHERE i.user_id = %(user)s AND i.status = 'active'"
        " AND i.kind IN ('topic_level', 'topic_focus')"
    )
    assert set_on >= 10

    stale_before = seed.ASKED_AT - now_and_history.STALE_AFTER
    [(states, stale)] = counted(
        database,
        "SELECT count(*), count(*) FILTER (WHERE last < %(stale_before)s) FROM ("
        " SELECT max(e.occurred_at) AS last FROM items i"
        " JOIN item_evidence ev ON ev.item_id = i.id AND ev.kind = 'supports'"
        " JOIN events e ON e.id = ev.event_id"
        " WHERE i.user_id = %(user)s AND i.kind = 'state' AND i.status = 'active'"
        " GROUP BY i.id) s",
        seed.ASKED_AS,
        stale_before=stale_before,
    )
    assert states >= 6 and stale == 2

    [(history, misconceptions, in_2021)] = rows(
        "SELECT count(*), count(*) FILTER (WHERE details ? 'misconception'),"
        " count(*) FILTER (WHERE occurred_precision = 'year'"
        "  AND extract(year FROM occurred_at AT TIME ZONE 'UTC') = 2021)"
        " FROM items WHERE user_id = %(user)s AND kind = 'history'"
    )
    assert history >= 10 and misconceptions == 3 and in_2021 >= 1

    [(decisions, replaced)] = rows(
        "SELECT count(*), count(*) FILTER (WHERE inactive_reason = 'superseded')"
        " FROM items WHERE user_id = %(user)s AND kind = 'decision'"
    )
    assert decisions >= 8 and replaced == 2

    [(retired,)] = rows(
        "SELECT count(*) FROM items WHERE user_id = %(user)s"
        " AND inactive_reason = 'retired'"
    )
    assert retired >= 3
    [(edited_twice,)] = rows(
        "SELECT count(*) FROM (SELECT lineage_id FROM items WHERE user_id = %(user)s"
        " GROUP BY lineage_id HAVING count(*) >= 3) l"
    )
    assert edited_twice >= 2

    # Every item and topic the seed names was made, for its own user.
    assert set(mira.items) == seed.read(seed.PERSON).item_keys
    [(others,)] = rows(
        "SELECT count(*) FROM items WHERE user_id = %(user)s", user="jonas"
    )
    assert others >= 3


async def test_the_three_forms_of_conflict_are_in_the_seed(
    database: str, mira: seed.Loaded
) -> None:
    # Which pairs are marked is read here; how each is judged is what the
    # conflicts cases ask.
    marked = counted(
        database,
        "SELECT r.from_item, r.to_item FROM item_relations r"
        " WHERE r.user_id = %(user)s AND r.kind = 'contradicts' AND r.ended_at IS NULL",
        seed.ASKED_AS,
    )
    assert len(marked) >= 3


def test_every_entry_was_made_before_the_seed_is_asked() -> None:
    entries = seed.read(seed.PERSON).entries
    times = [entry.at for entry in entries]
    assert times == sorted(times)
    assert times[-1] < seed.ASKED_AT
    assert seed.ASKED_AT - times[0] < timedelta(days=3 * 365)


@pytest.mark.parametrize(
    ("text", "reason"),
    [
        pytest.param('[{"key": "x"}]', "x", id="an entry that does nothing"),
        pytest.param(
            '[{"retire": "nothing", "at": "2026-01-01T00:00:00Z"}]',
            "nothing",
            id="a key never made",
        ),
        pytest.param(
            '[{"key": "t", "at": "2026-01-01T00:00:00Z",'
            ' "topic": {"name": "A", "slug": "a"}},'
            ' {"key": "t", "at": "2026-01-02T00:00:00Z",'
            ' "topic": {"name": "B", "slug": "b"}}]',
            "twice",
            id="a key made twice",
        ),
        pytest.param(
            '[{"key": "t", "at": "2026-01-02T00:00:00Z",'
            ' "topic": {"name": "A", "slug": "a"}},'
            ' {"key": "u", "at": "2026-01-01T00:00:00Z",'
            ' "topic": {"name": "B", "slug": "b"}}]',
            "before",
            id="out of order",
        ),
        pytest.param(
            '[{"key": "t", "at": "2026-07-01T00:00:00Z",'
            ' "topic": {"name": "A", "slug": "a"}}]',
            "asked",
            id="after the time it is asked at",
        ),
        pytest.param(
            '[{"key": "t", "at": "2026-01-01T00:00:00Z",'
            ' "topic": {"name": "A", "slug": "a"}},'
            ' {"retire": "t", "at": "2026-01-02T00:00:00Z"}]',
            "not an item",
            id="a topic where an item is meant",
        ),
    ],
)
def test_a_seed_that_is_malformed_fails_loudly(
    tmp_path: Path, text: str, reason: str
) -> None:
    file = tmp_path / "person.json"
    file.write_text(text)
    with pytest.raises(seed.SeedError) as raised:
        seed.read(file)
    assert reason in str(raised.value)

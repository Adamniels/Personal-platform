"""The building blocks in `brain.retrieval`, called directly on a database of
the test's own, as brain_app, at a time the test passes in: what the `ask`
endpoints cannot show, since they ask at the request's time. The answers
themselves are tested through the API in `tests/test_ask.py`."""

import asyncio
import uuid
from collections.abc import Awaitable, Callable
from datetime import UTC, datetime, timedelta
from typing import Any

from psycopg_pool import AsyncConnectionPool

from brain import db, retrieval
from tests.conftest import Caller
from tests.harness import app_url
from tests.test_items import ALICE, BOB, write
from tests.test_knowledge import level, topic
from tests.test_now_and_history import history, iso, state

type Read[T] = Callable[[retrieval.Connection], Awaitable[T]]


def read_as[T](database: str, user: uuid.UUID, block: Read[T]) -> T:
    """`block` run on a connection through the door, as `user`."""

    async def run() -> T:
        pool = AsyncConnectionPool(
            app_url(database), min_size=1, max_size=1, open=False
        )
        await pool.open(wait=True)
        try:
            async with db.transaction(pool, user) as conn:
                return await block(conn)
        finally:
            await pool.close()

    return asyncio.run(run())


def now_at(database: str, moment: datetime, mode: retrieval.Mode) -> list[Any]:
    return read_as(
        database, ALICE, lambda conn: retrieval.now(conn, now=moment, mode=mode)
    )


def test_a_state_is_stale_only_past_sixty_days_before_the_time_asked(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    said = datetime(2026, 1, 1, 9, 0, tzinfo=UTC)
    write(alice, state(occurred_at=iso(said)))

    def stale(moment: datetime, mode: retrieval.Mode = "now") -> bool:
        [answered] = now_at(database, moment, mode)
        flag: bool = answered.stale
        return flag

    sixty = said + timedelta(days=60)
    assert stale(said) is False
    assert stale(sixty) is False
    assert stale(sixty + timedelta(seconds=1)) is True
    # About history, age is ignored however old it is.
    assert stale(said + timedelta(days=3650), "history") is False


def test_a_record_is_never_stale_whenever_it_is_asked(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    course = write(alice, history(occurred_at="2015-01-01T00:00:00Z", topics=["rust"]))
    rust = uuid.UUID(course["topics"][0]["id"])
    later = datetime(2040, 1, 1, tzinfo=UTC)

    found = read_as(
        database,
        ALICE,
        lambda conn: retrieval.history(
            conn, retrieval.Subject("topic", rust), now=later, mode="now"
        ),
    )

    [answered] = found
    assert str(answered.item.id) == course["id"]
    assert answered.stale is False


def test_knowledge_leaves_out_an_id_the_user_has_no_topic_by(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    rust = uuid.UUID(topic(alice, "Rust", "rust")["id"])
    write(alice, level("rust", "solid"))
    bobs = uuid.UUID(topic(calling(BOB), "Rust", "rust")["id"])
    nothing = uuid.uuid7()

    found = read_as(
        database,
        ALICE,
        lambda conn: retrieval.knowledge(
            conn, [rust, bobs, nothing], now=datetime.now(UTC), mode="now"
        ),
    )

    assert list(found) == [rust]
    assert found[rust].topic.slug == "rust"
    solid = found[rust].level
    assert solid is not None and solid.item.value == "solid"

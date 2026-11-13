"""The evaluation's seed: a fictional person's memory, `eval/person.json`,
and the loader that writes it into a brain. See "Evaluating" in
Brain/README.md.

The person is made up, Mira Lindqvist, so the repository holds nothing of
the owner's. The seed is frozen: every case is asked at `ASKED_AT`, and every
entry says the time it was made, before that, so no answer depends on the
day it is run.

The file is the evaluation's own format, a list of entries in the order
they were made, each a JSON object with the time it was made, `at`, the user
it is for, `user`, Mira unless it says, and one action:

- `topic`, `item` or `event`, a body exactly as `POST /brain/api/topics`,
  `POST /brain/api/items` or `POST /brain/api/events` takes it;
- `edit`, an item's key, with `change`, a body as
  `POST /brain/api/items/{id}/edit` takes it;
- `same_as`, `broader_than` or `related_to`, a pair of topic keys: the first
  the same as the second, broader than it, or related to it;
- `retire` or `confirm`, an item's key; `replace`, a decision's key, with
  `by`, the newer decision's; `contradicts`, a pair of claims' keys.

An entry that makes a topic or an item, a `topic`, `item` or `edit`, has a
`key`, which later entries and the cases name it by; ids are made as it
loads, so nothing refers to one. An edit's key is the new version's, and so
is the key of a level or a focus set on a topic that has one.

The loader writes each entry through the same functions the API's routes
call, in a transaction of its own, as a request would be, passing the
entry's time as the time it was made, so the seed gets real events and
evidence. It does not depend on the export format."""

import json
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Annotated, Any, Literal
from uuid import NAMESPACE_URL, UUID, uuid5

from pydantic import (
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Discriminator,
    StringConstraints,
    Tag,
    TypeAdapter,
    ValidationError,
)

from brain import db, event_routes, events, item_routes, items, subjects, topics
from brain.errors import Conflict, NotFound, Refused

PERSON = Path(__file__).resolve().parents[2] / "eval" / "person.json"

# The time every case is asked at, and after which nothing in the seed was
# made.
ASKED_AT = datetime(2026, 6, 1, 12, 0, tzinfo=UTC)

# The person the cases ask as. Any other user in the seed is someone whose
# memory must never reach an answer to her.
ASKED_AS = "mira"

Key = Annotated[str, StringConstraints(pattern=r"^[a-z0-9][a-z0-9-]{0,79}$")]
User = Annotated[str, StringConstraints(pattern=r"^[a-z]{1,40}$")]


def account_id(user: str) -> UUID:
    """The account id of the seed's `user`: fixed, so a run's ids for users
    are the same every time, and made up, so it is nobody's."""
    return uuid5(NAMESPACE_URL, f"https://platform.test/brain/eval/{user}")


class _Entry(BaseModel):
    model_config = ConfigDict(extra="forbid")

    at: AwareDatetime
    user: User = ASKED_AS


class _Made(_Entry):
    key: Key


class TopicEntry(_Made):
    topic: subjects.NewTopic


class ItemEntry(_Made):
    item: item_routes.NewEntry


class EditEntry(_Made):
    edit: Key
    change: item_routes.EntryEdit


class EventEntry(_Entry):
    event: event_routes.NewEvent


class SameAsEntry(_Entry):
    same_as: tuple[Key, Key]


class BroaderEntry(_Entry):
    broader_than: tuple[Key, Key]


class RelatedEntry(_Entry):
    related_to: tuple[Key, Key]


class RetireEntry(_Entry):
    retire: Key


class ConfirmEntry(_Entry):
    confirm: Key


class ReplaceEntry(_Entry):
    replace: Key
    by: Key


class ContradictsEntry(_Entry):
    contradicts: tuple[Key, Key]


# Each action, in the order an entry is told apart by: the first one it has.
ACTIONS = (
    "topic",
    "item",
    "edit",
    "event",
    "same_as",
    "broader_than",
    "related_to",
    "retire",
    "confirm",
    "replace",
    "contradicts",
)


def _action(body: Any) -> str | None:
    if isinstance(body, BaseModel):
        return next(a for a in ACTIONS if a in type(body).model_fields)
    if isinstance(body, dict):
        return next((a for a in ACTIONS if a in body), None)
    return None


type Entry = Annotated[
    Annotated[TopicEntry, Tag("topic")]
    | Annotated[ItemEntry, Tag("item")]
    | Annotated[EditEntry, Tag("edit")]
    | Annotated[EventEntry, Tag("event")]
    | Annotated[SameAsEntry, Tag("same_as")]
    | Annotated[BroaderEntry, Tag("broader_than")]
    | Annotated[RelatedEntry, Tag("related_to")]
    | Annotated[RetireEntry, Tag("retire")]
    | Annotated[ConfirmEntry, Tag("confirm")]
    | Annotated[ReplaceEntry, Tag("replace")]
    | Annotated[ContradictsEntry, Tag("contradicts")],
    Discriminator(_action),
]

_ENTRY: TypeAdapter[Entry] = TypeAdapter(Entry)


class SeedError(Exception):
    """The seed is not well formed, or the brain refused one of its entries:
    the message names the entry."""


@dataclass(frozen=True)
class Seed:
    entries: tuple[Entry, ...]
    # The keys of every item and every topic the entries make, every user's.
    item_keys: frozenset[str]
    topic_keys: frozenset[str]


def _references(entry: Entry) -> list[tuple[str, Literal["topic", "item"]]]:
    """The keys `entry` names, each with what it must be."""
    match entry:
        case EditEntry():
            return [(entry.edit, "item")]
        case (
            SameAsEntry(same_as=pair)
            | BroaderEntry(broader_than=pair)
            | RelatedEntry(related_to=pair)
        ):
            return [(key, "topic") for key in pair]
        case RetireEntry():
            return [(entry.retire, "item")]
        case ConfirmEntry():
            return [(entry.confirm, "item")]
        case ReplaceEntry():
            return [(entry.replace, "item"), (entry.by, "item")]
        case ContradictsEntry():
            return [(key, "item") for key in entry.contradicts]
    return []


def read(path: Path) -> Seed:
    """The seed in `path`, checked without a database: every entry well
    formed, each key made once and named only after it is made, as what it
    is and for the same user, and the entries in the order they were made,
    none after `ASKED_AT`. Anything else is a `SeedError` naming the
    entry."""
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as err:
        raise SeedError(f"{path.name}: {err}") from err
    if not isinstance(raw, list):
        raise SeedError(f"{path.name}: the seed is a list of entries")
    entries: list[Entry] = []
    made: dict[str, tuple[Literal["topic", "item"], str]] = {}
    last: datetime | None = None
    for number, body in enumerate(raw, start=1):
        named = body.get("key") if isinstance(body, dict) else None
        where = f"{path.name}, entry {number}" + (f" ({named})" if named else "")
        try:
            entry = _ENTRY.validate_python(body)
        except ValidationError as err:
            raise SeedError(f"{where}: {err}") from err
        if last is not None and entry.at < last:
            raise SeedError(
                f"{where}: made at {entry.at}, before the entry ahead of it"
            )
        if entry.at > ASKED_AT:
            raise SeedError(
                f"{where}: made at {entry.at}, after the seed is asked at {ASKED_AT}"
            )
        last = entry.at
        for key, what in _references(entry):
            if key not in made:
                raise SeedError(f"{where}: {key} is not made before it is named")
            if made[key][0] != what:
                raise SeedError(f"{where}: {key} is a {made[key][0]}, not an {what}")
            if made[key][1] != entry.user:
                raise SeedError(f"{where}: {key} is another user's")
        if isinstance(entry, _Made):
            if entry.key in made:
                raise SeedError(f"{where}: the key {entry.key} is made twice")
            made[entry.key] = (
                "topic" if isinstance(entry, TopicEntry) else "item",
                entry.user,
            )
        entries.append(entry)
    return Seed(
        entries=tuple(entries),
        item_keys=frozenset(k for k, (what, _) in made.items() if what == "item"),
        topic_keys=frozenset(k for k, (what, _) in made.items() if what == "topic"),
    )


@dataclass(frozen=True)
class Loaded:
    """The ids the seed's keys were given as it loaded, every user's."""

    items: dict[str, UUID]
    topics: dict[str, UUID]


async def load(pool: db.Pool, seed: Seed) -> Loaded:
    """Writes every entry of `seed` into the brain behind `pool`, in order,
    each in a transaction of its own for its user, at its own time. An entry
    the brain refuses is a `SeedError` naming it, and what loaded before it
    stays: the database is the evaluation's own, made for one run."""
    made_items: dict[str, UUID] = {}
    made_topics: dict[str, UUID] = {}
    for number, entry in enumerate(seed.entries, start=1):
        try:
            async with db.transaction(pool, account_id(entry.user)) as conn:
                await _apply(conn, entry, made_items, made_topics)
        except (Refused, Conflict, NotFound, ValueError) as err:
            key = f" ({entry.key})" if isinstance(entry, _Made) else ""
            raise SeedError(
                f"entry {number}{key}: the brain refused it: {err or type(err).__name__}"
            ) from err
    return Loaded(items=made_items, topics=made_topics)


async def _apply(
    conn: items.Connection,
    entry: Entry,
    made_items: dict[str, UUID],
    made_topics: dict[str, UUID],
) -> None:
    now = entry.at
    match entry:
        case TopicEntry():
            topic = await subjects.create(conn, entry.topic, now=now)
            made_topics[entry.key] = topic.id
        case ItemEntry():
            made_items[entry.key] = await item_routes.write_entry(
                conn, entry.item.root, now=now
            )
        case EditEntry():
            made_items[entry.key] = await items.edit(
                conn,
                made_items[entry.edit],
                item_routes.correction(entry.change.root),
                now=now,
            )
        case EventEntry():
            await events.record(conn, event_routes.recorded(entry.event), now=now)
        case SameAsEntry():
            one, other = _ids(made_topics, entry.same_as)
            await topics.same_as(conn, one, other)
        case BroaderEntry():
            broader, under = _ids(made_topics, entry.broader_than)
            await topics.add_broader(conn, under, broader, now=now)
        case RelatedEntry():
            one, other = _ids(made_topics, entry.related_to)
            await topics.add_related(conn, one, other, now=now)
        case RetireEntry():
            await items.retire(conn, made_items[entry.retire], now=now)
        case ConfirmEntry():
            await items.confirm(conn, made_items[entry.confirm], now=now)
        case ReplaceEntry():
            await items.replace_decision(
                conn, made_items[entry.replace], made_items[entry.by], now=now
            )
        case ContradictsEntry():
            one, other = _ids(made_items, entry.contradicts)
            await items.mark_contradicting(conn, one, other, now=now)


def _ids(made: dict[str, UUID], pair: Sequence[str]) -> tuple[UUID, UUID]:
    one, other = pair
    return made[one], made[other]

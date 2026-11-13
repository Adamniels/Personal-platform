"""The API for the memory store, under `/brain/api/items`: entries the owner
makes by hand, as profile entries, rules, states, a level and a focus on each
topic, history entries, misconceptions among them, and decisions, and
correcting, confirming and replacing them; `/brain/api/knowledge`, every
topic with its level and focus; and `/brain/api/now` and
`/brain/api/history`, what is going on now and what has happened. Every route
needs a valid token and acts for the account it names, through
`brain.db.transaction`. The rules are in `brain.items`; this module reads the
request and shapes the answer. The routes are listed in "What it serves" in Brain/README.md."""

from datetime import UTC, datetime
from typing import Annotated, Any, Literal
from uuid import UUID

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import (
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Discriminator,
    Field,
    RootModel,
    StringConstraints,
    Tag,
    field_validator,
)

from brain import db, items, knowledge, now_and_history
from brain.edge import (
    NOT_FOUND,
    REFUSED,
    UNAUTHORIZED,
    authenticated,
    body_as,
    documented,
    path_id,
    query_as,
)
from brain.errors import NotFound
from brain.event_routes import EventEntity, EventTopic, Precision
from brain.subjects import Name, Slug

router = APIRouter(prefix="/brain/api")

# The longest entry, after its surrounding whitespace is dropped.
CONTENT_MAX = 4000
# The most topics, and the most named things, one entry may name.
SUBJECTS_MAX = 20

# What the owner typed: surrounding whitespace dropped and nothing else, then
# 1 to 4000 characters.
Content = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=CONTENT_MAX),
]


def _no_nul(text: str) -> str:
    # Postgres text cannot hold one.
    if "\x00" in text:
        raise ValueError("text may not hold a NUL character")
    return text


class ProfileEntry(BaseModel):
    """Who the owner is: an entry in one of the profile's six categories. It
    may name the topics and named things it is about. A field it does not
    know is refused, so a misspelt one is not silently lost."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["profile"]
    category: items.Category
    content: Content
    # Topics by slug, each one the user has; named things by name, each
    # created when the user has none by it.
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] = []
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] = []
    # When it became true, with a zone; without one, when it was entered.
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str) -> str:
        return _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str]) -> list[str]:
        return [_no_nul(name) for name in names]


class RuleEntry(BaseModel):
    """How the platform should work with the owner. A rule is global by
    construction, so it names no topic or named thing, and has no category."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["rule"]
    content: Content
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str) -> str:
        return _no_nul(content)


class StateEntry(BaseModel):
    """What is going on now: a claim in the semantic store that goes stale
    when nothing supports it for long enough. It may name the topics and
    named things it is about."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["state"]
    content: Content
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] = []
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] = []
    # When it became true, with a zone; without one, when it was entered.
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str) -> str:
        return _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str]) -> list[str]:
        return [_no_nul(name) for name in names]


class LevelEntry(BaseModel):
    """How well the owner knows one topic, in the knowledge store, where
    evidence may later move it: shallow, knows the vocabulary; working, can
    use it with help; solid, can use it unaided and explain it; deep, knows
    the internals and could teach it. Set where the topic has a level
    already, it is an edit of that one. The content is generated."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["topic_level"]
    # A slug the user has, read through its alias.
    topic: Slug
    value: items.Level


class FocusEntry(BaseModel):
    """What the owner is doing about one topic, in profile, his alone:
    learning, steady or not_pursuing. Set where the topic has a focus
    already, it is an edit of that one. The content is generated."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["topic_focus"]
    topic: Slug
    value: items.Focus


# Each half of a misconception, so the content built from both and the
# topic's name stays within CONTENT_MAX.
MISCONCEPTION_HALF_MAX = 1800

Half = Annotated[
    str,
    StringConstraints(
        strip_whitespace=True, min_length=1, max_length=MISCONCEPTION_HALF_MAX
    ),
]


class Misconception(BaseModel):
    """What the owner believed and what is right, each 1 to 1800 characters
    once its surrounding whitespace is dropped."""

    model_config = ConfigDict(extra="forbid")

    misconception: Literal[True]
    believed: Half
    correct: Half

    @field_validator("believed", "correct")
    @classmethod
    def storable(cls, text: str) -> str:
        return _no_nul(text)


class MisconceptionEntry(BaseModel):
    """A corrected misconception, on one topic: a history entry and not a
    kind of its own, told apart from any other by its details. The content is
    built from its two halves."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["history"]
    topic: Slug
    details: Misconception
    # When it happened, with a zone; without one, when it was entered.
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"


class HistoryEntry(BaseModel):
    """Something that happened: a record in reported history, with when it
    happened, kept apart from when it was entered, and how precisely that is
    known. It may name the topics and named things it is about."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["history"]
    content: Content
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] = []
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] = []
    # When it happened, with a zone, and how precisely: both required.
    occurred_at: AwareDatetime
    occurred_precision: Precision

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str) -> str:
        return _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str]) -> list[str]:
        return [_no_nul(name) for name in names]


# The most alternatives one decision may say it rejected.
REJECTED_MAX = 20


class DecisionDetails(BaseModel):
    """Why a decision was made, and what it was made over."""

    model_config = ConfigDict(extra="forbid")

    reasoning: Content
    rejected: Annotated[list[Content], Field(max_length=REJECTED_MAX)] = []

    @field_validator("reasoning")
    @classmethod
    def reasoning_storable(cls, reasoning: str) -> str:
        return _no_nul(reasoning)

    @field_validator("rejected")
    @classmethod
    def rejected_storable(cls, rejected: list[str]) -> list[str]:
        return [_no_nul(each) for each in rejected]


class DecisionEntry(BaseModel):
    """A decision: a decided event, the permanent record that it was made,
    and an item in the semantic store, the claim that it holds, which a
    newer decision can replace. It may name the topics and named things it
    is about."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["decision"]
    content: Content
    details: DecisionDetails
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] = []
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] = []
    # When it was made, with a zone; without one, when it was entered.
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str) -> str:
        return _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str]) -> list[str]:
        return [_no_nul(name) for name in names]


def _entry_form(body: Any) -> str | None:
    """Which form of entry `body` is: its kind, except that a history entry
    carrying details is a misconception, the one form whose details are its
    own. None for a body without a kind, which is refused."""
    if isinstance(body, MisconceptionEntry):
        return "misconception"
    if isinstance(body, BaseModel):
        kind = getattr(body, "kind", None)
        return kind if isinstance(kind, str) else None
    if not isinstance(body, dict):
        return None
    kind = body.get("kind")
    if kind == "history" and "details" in body:
        return "misconception"
    return kind if isinstance(kind, str) else None


class NewEntry(
    RootModel[
        Annotated[
            Annotated[ProfileEntry, Tag("profile")]
            | Annotated[RuleEntry, Tag("rule")]
            | Annotated[StateEntry, Tag("state")]
            | Annotated[LevelEntry, Tag("topic_level")]
            | Annotated[FocusEntry, Tag("topic_focus")]
            | Annotated[HistoryEntry, Tag("history")]
            | Annotated[MisconceptionEntry, Tag("misconception")]
            | Annotated[DecisionEntry, Tag("decision")],
            Discriminator(_entry_form),
        ]
    ]
):
    """An entry, told apart by its kind, and a history entry by whether it is
    a misconception."""


class ProfileEdit(BaseModel):
    """A correction of a profile entry: each field left out keeps what the
    entry has. topics and entities, when given, replace that kind of subject
    whole, so an empty list removes them."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["profile"]
    category: items.Category | None = None
    content: Content | None = None
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] | None = None
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] | None = None

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str | None) -> str | None:
        return None if content is None else _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str] | None) -> list[str] | None:
        return None if names is None else [_no_nul(name) for name in names]


class RuleEdit(BaseModel):
    """A correction of a rule, which is global, so only its content."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["rule"]
    content: Content | None = None

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str | None) -> str | None:
        return None if content is None else _no_nul(content)


class StateEdit(BaseModel):
    """A correction of what is going on now: each field left out keeps what
    the entry has, and topics and entities, when given, replace that kind of
    subject whole, as for a profile entry. The new version is said now, so
    it is current again."""

    model_config = ConfigDict(extra="forbid")

    kind: Literal["state"]
    content: Content | None = None
    topics: Annotated[list[Slug], Field(max_length=SUBJECTS_MAX)] | None = None
    entities: Annotated[list[Name], Field(max_length=SUBJECTS_MAX)] | None = None

    @field_validator("content")
    @classmethod
    def content_storable(cls, content: str | None) -> str | None:
        return None if content is None else _no_nul(content)

    @field_validator("entities")
    @classmethod
    def names_storable(cls, names: list[str] | None) -> list[str] | None:
        return None if names is None else [_no_nul(name) for name in names]


class EntryEdit(
    RootModel[
        Annotated[ProfileEdit | RuleEdit | StateEdit, Field(discriminator="kind")]
    ]
):
    """A correction, told apart by the kind of the item it corrects."""


# What a route changing an item may answer when the item is not in the state
# the change needs.
NOT_CURRENT: dict[int | str, dict[str, Any]] = {
    409: {
        "description": (
            "The item is not in the state this needs: a superseded version, or"
            " not active or retired as the change asks."
        )
    }
}


class Item(BaseModel):
    id: UUID
    # Shared by every version of one entry; the first version's id.
    lineage_id: UUID
    kind: Literal[
        "profile", "rule", "topic_level", "topic_focus", "state", "history", "decision"
    ]
    # Decided by the kind; it is what decides who may change the item.
    store: Literal["profile", "procedural", "knowledge", "semantic", "reported_history"]
    nature: Literal["claim", "record"]
    provenance: Literal["asserted", "derived"]
    content: str
    value: str | None
    category: str | None
    details: dict[str, Any]
    occurred_at: datetime | None
    occurred_precision: Precision | None
    status: Literal["active", "inactive"]
    inactive_reason: Literal["superseded", "retired"] | None
    created_at: datetime
    # The event that made it.
    created_by_event: UUID
    # What it is about, each topic read through its alias. Both empty for a
    # global item.
    topics: list[EventTopic]
    entities: list[EventEntity]

    @classmethod
    def fields_of(cls, item: items.Item) -> dict[str, Any]:
        return {
            "id": item.id,
            "lineage_id": item.lineage_id,
            "kind": item.kind,
            "store": item.store,
            "nature": item.nature,
            "provenance": item.provenance,
            "content": item.content,
            "value": item.value,
            "category": item.category,
            "details": item.details,
            "occurred_at": item.occurred_at,
            "occurred_precision": item.occurred_precision,
            "status": item.status,
            "inactive_reason": item.inactive_reason,
            "created_at": item.created_at,
            "created_by_event": item.created_by_event,
            "topics": [
                EventTopic(id=topic.id, slug=topic.slug, name=topic.name)
                for topic in item.topics
            ],
            "entities": [
                EventEntity(id=entity.id, name=entity.name) for entity in item.entities
            ],
        }

    @classmethod
    def of(cls, item: items.Item) -> Item:
        return cls(**cls.fields_of(item))


class EvidenceEvent(BaseModel):
    """The event behind a piece of evidence, as much of it as says why."""

    id: UUID
    verb: str
    origin: str
    source: str | None
    evidence: str
    occurred_at: datetime
    occurred_precision: Precision
    recorded_at: datetime


class Evidence(BaseModel):
    id: UUID
    kind: Literal["supports", "contradicts"]
    created_at: datetime
    event: EvidenceEvent


class ItemRelation(BaseModel):
    id: UUID
    from_item: UUID
    to_item: UUID
    kind: str
    created_at: datetime
    # Null while it holds.
    ended_at: datetime | None
    created_by_event: UUID

    @classmethod
    def of(cls, relation: items.Relation) -> ItemRelation:
        return cls(
            id=relation.id,
            from_item=relation.from_item,
            to_item=relation.to_item,
            kind=relation.kind,
            created_at=relation.created_at,
            ended_at=relation.ended_at,
            created_by_event=relation.created_by_event,
        )


class ItemDetail(Item):
    """One item with the events behind it, oldest first, and every relation
    it is on either end of."""

    evidence: list[Evidence]
    relations: list[ItemRelation]

    @classmethod
    def of_detail(cls, detail: items.Detail) -> ItemDetail:
        return cls(
            **Item.fields_of(detail.item),
            evidence=[
                Evidence(
                    id=each.id,
                    kind=each.kind,
                    created_at=each.created_at,
                    event=EvidenceEvent(
                        id=each.event.id,
                        verb=each.event.verb,
                        origin=each.event.origin,
                        source=each.event.source,
                        evidence=each.event.evidence,
                        occurred_at=each.event.occurred_at,
                        occurred_precision=each.event.occurred_precision,
                        recorded_at=each.event.recorded_at,
                    ),
                )
                for each in detail.evidence
            ],
            relations=[ItemRelation.of(relation) for relation in detail.relations],
        )


@router.post(
    "/items",
    status_code=201,
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(body=NewEntry),
)
@authenticated
async def write_item(request: Request, claims: Claims) -> Response:
    """Writes an entry made by hand, 201 with the item, its evidence and its
    relations. It applies at once: an asserted event, origin brain and source
    manual-entry, is recorded with it and is its supporting evidence. kind is
    profile, with a category, or rule, which is global, or state, what is
    going on now, with optional subjects like a profile entry; or topic_level or
    topic_focus, each on one topic with a value, which where the topic,
    read through its alias, has an active entry of that kind already is an
    edit of it, 201 with the new version. 422 for an unknown kind, category
    or value, content that is blank or over 4000 characters, a rule naming a
    subject, a topic the user does not have, or a level or focus the topic
    has already; or history, something that happened, with occurred_at and
    occurred_precision required and optional subjects, or, with details, a
    corrected misconception on one topic, its details misconception true with
    what was believed and what is right, and its content built from the two.
    Or decision, with details holding its reasoning and what it rejected,
    recorded with a decided event in place of an asserted one, and its
    occurred_at when it was made, the request's time unless given. A named
    thing the user does not have is created."""
    entry = (await body_as(request, NewEntry)).root
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        item_id = await write_entry(conn, entry, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(
        ItemDetail.of_detail(detail).model_dump(mode="json"), status_code=201
    )


type Entry = (
    ProfileEntry
    | RuleEntry
    | StateEntry
    | LevelEntry
    | FocusEntry
    | HistoryEntry
    | MisconceptionEntry
    | DecisionEntry
)


async def write_entry(conn: items.Connection, entry: Entry, *, now: datetime) -> UUID:
    """Writes `entry` at `now` and returns the item's id, a level's or a
    focus's new version where the topic had one: what the route does inside
    its transaction, and what the evaluation's seed is loaded through."""
    if isinstance(entry, LevelEntry | FocusEntry):
        return await items.set_on_topic(
            conn, entry.kind, entry.topic, entry.value, now=now
        )
    if isinstance(entry, MisconceptionEntry):
        return await items.write_misconception(
            conn,
            entry.topic,
            entry.details.believed,
            entry.details.correct,
            occurred_at=entry.occurred_at,
            occurred_precision=entry.occurred_precision,
            now=now,
        )
    named = (
        entry
        if isinstance(entry, ProfileEntry | StateEntry | HistoryEntry | DecisionEntry)
        else None
    )
    new = items.NewItem(
        kind=entry.kind,
        content=entry.content,
        category=entry.category if isinstance(entry, ProfileEntry) else None,
        topics=tuple(named.topics) if named is not None else (),
        entities=tuple(named.entities) if named is not None else (),
        occurred_at=entry.occurred_at,
        occurred_precision=entry.occurred_precision,
        details=entry.details.model_dump() if isinstance(entry, DecisionEntry) else {},
    )
    return await items.write(conn, new, now=now)


class ItemQuery(BaseModel):
    # Each filter narrows the list; all of them together, every one given.
    kind: (
        Literal[
            "profile",
            "rule",
            "topic_level",
            "topic_focus",
            "state",
            "history",
            "decision",
        ]
        | None
    ) = None
    status: Literal["active", "inactive"] | None = None
    # A topic by slug, read through aliases.
    topic: Slug | None = None
    # A named thing by name, without letter case.
    entity: Name | None = None


@router.get(
    "/items",
    response_model=list[Item],
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(query=ItemQuery),
)
@authenticated
async def list_items(request: Request, claims: Claims) -> Response:
    """The user's items, newest first, each with its subjects, narrowed by
    kind, status, topic and entity when they are given. A topic matches
    through aliases, and only itself, not the topics under it. 422 for an
    unknown kind or status, or a malformed slug."""
    query = query_as(request, ItemQuery)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await items.listing(
            conn,
            kind=query.kind,
            status=query.status,
            topic=query.topic,
            entity=query.entity,
        )
    return JSONResponse([Item.of(item).model_dump(mode="json") for item in found])


@router.get(
    "/items/{id}",
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def read_item(request: Request, claims: Claims) -> Response:
    """One item with its subjects, the events behind it and its relations.
    404 for an id the user does not have."""
    item_id = path_id(request)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        detail = await items.get(conn, item_id)
    if detail is None:
        raise NotFound
    return JSONResponse(ItemDetail.of_detail(detail).model_dump(mode="json"))


@router.post(
    "/items/{id}/edit",
    status_code=201,
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED, **NOT_CURRENT},
    openapi_extra=documented(body=EntryEdit, path=("id",)),
)
@authenticated
async def edit_item(request: Request, claims: Claims) -> Response:
    """Corrects an item, 201 with the new version. Nothing is changed in
    place: the new version shares the old one's lineage_id, is active and
    asserted, and holds the fields given and the old version's everything
    else; the old one becomes inactive, superseded, and a supersedes relation
    links the new to it. An edited event against the old version and an
    asserted event behind the new one are recorded with it, in one
    transaction. kind is the item's own: profile, rule or state. 404 for an
    id the user does not have. 422 for another kind, a field the kind does
    not take, a topic the user does not have, or an edit that changes
    nothing. 409 for a version that is not the newest, or an item that is
    retired."""
    item_id = path_id(request)
    change = (await body_as(request, EntryEdit)).root
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        new_id = await items.edit(conn, item_id, correction(change), now=now)
        detail = await items.get(conn, new_id)
    assert detail is not None
    return JSONResponse(
        ItemDetail.of_detail(detail).model_dump(mode="json"), status_code=201
    )


def correction(change: ProfileEdit | RuleEdit | StateEdit) -> items.Edit:
    """The correction `change` asks for, as `brain.items.edit` takes it: what
    the route edits with, and what the evaluation's seed is loaded through."""
    subjects = change if isinstance(change, ProfileEdit | StateEdit) else None
    return items.Edit(
        kind=change.kind,
        content=change.content,
        category=change.category if isinstance(change, ProfileEdit) else None,
        topics=tuple(subjects.topics)
        if subjects is not None and subjects.topics is not None
        else None,
        entities=tuple(subjects.entities)
        if subjects is not None and subjects.entities is not None
        else None,
    )


@router.post(
    "/items/{id}/retire",
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND, **NOT_CURRENT},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def retire_item(request: Request, claims: Claims) -> Response:
    """Puts an item away, 200 with it, now inactive with reason retired. It
    stops applying, stays readable, and can be restored. A retired event
    against it is recorded with it, in one transaction. Takes no body. 404
    for an id the user does not have. 409 for an item already retired, or a
    superseded version, whose newest version is the one to retire."""
    item_id = path_id(request)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        await items.retire(conn, item_id, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(ItemDetail.of_detail(detail).model_dump(mode="json"))


@router.post(
    "/items/{id}/restore",
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND, **NOT_CURRENT},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def restore_item(request: Request, claims: Claims) -> Response:
    """Brings a retired item back, 200 with it, active again. A restored
    event against it is recorded with it, in one transaction. Takes no body.
    404 for an id the user does not have. 409 for an item that is not
    retired: an active one, or a superseded version, which stays history."""
    item_id = path_id(request)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        await items.restore(conn, item_id, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(ItemDetail.of_detail(detail).model_dump(mode="json"))


@router.post(
    "/items/{id}/confirm",
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED, **NOT_CURRENT},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def confirm_item(request: Request, claims: Claims) -> Response:
    """Says a claim still holds, 200 with it. A confirmed event against it,
    linked as supporting evidence, is recorded in one transaction, and moves
    when it was last supported, so a stale state is stale no longer. Takes
    no body. 404 for an id the user does not have. 422 for a record, which
    asserts nothing about now. 409 for an item that is not active."""
    item_id = path_id(request)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        await items.confirm(conn, item_id, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(ItemDetail.of_detail(detail).model_dump(mode="json"))


class Replacement(BaseModel):
    """The newer decision, by id."""

    model_config = ConfigDict(extra="forbid")

    replaced_by: UUID


@router.post(
    "/items/{id}/replace",
    response_model=ItemDetail,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED, **NOT_CURRENT},
    openapi_extra=documented(body=Replacement, path=("id",)),
)
@authenticated
async def replace_item(request: Request, claims: Claims) -> Response:
    """Replaces a decision with a newer one, 200 with the old one, now
    inactive with reason superseded and a supersedes relation from the new
    one. An edited event against the old one, which makes the relation, is
    recorded with it, in one transaction; its decided event stays, and both
    stay readable. 404 for an id the user does not have. 422 for a
    replaced_by they do not have, either not being a decision, or two
    versions of one entry, a decision itself included. 409 when either is
    not active."""
    item_id = path_id(request)
    replacement = await body_as(request, Replacement)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        await items.replace_decision(conn, item_id, replacement.replaced_by, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(ItemDetail.of_detail(detail).model_dump(mode="json"))


class Contradiction(BaseModel):
    """The other item, by id."""

    model_config = ConfigDict(extra="forbid")

    contradicts: UUID


@router.post(
    "/items/{id}/contradicts",
    status_code=201,
    response_model=ItemRelation,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED, **NOT_CURRENT},
    openapi_extra=documented(body=Contradiction, path=("id",)),
)
@authenticated
async def mark_contradicting(request: Request, claims: Claims) -> Response:
    """Marks two claims as contradicting each other, 201 with the relation,
    a contradicts from this item to the other. A contradicted event about
    this item, which makes the relation, is recorded with it, in one
    transaction; it is evidence on neither. From then on an answer holding
    both lists them as a conflict, both kept. 404 for an id the user does
    not have. 422 for an other item they do not have, the item itself, or
    either being a record. 409 when either is not active, or the two are
    already marked, either way round."""
    item_id = path_id(request)
    body = await body_as(request, Contradiction)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        relation = await items.mark_contradicting(
            conn, item_id, body.contradicts, now=now
        )
    return JSONResponse(
        ItemRelation.of(relation).model_dump(mode="json"), status_code=201
    )


@router.post(
    "/item-relations/{id}/end",
    response_model=ItemRelation,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def end_contradiction(request: Request, claims: Claims) -> Response:
    """Takes back a contradiction, 200 with the relation, ended. The row
    stays, with the time it ended, and one already ended is answered as it
    is. Takes no body. 404 for an id the user does not have. 422 for a
    relation of any other kind, such as supersedes, which is an entry's
    history and is not taken back."""
    relation_id = path_id(request)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        relation = await items.end_contradiction(conn, relation_id, now=now)
    return JSONResponse(ItemRelation.of(relation).model_dump(mode="json"))


@router.get(
    "/items/{id}/versions",
    response_model=list[Item],
    responses={**UNAUTHORIZED, **NOT_FOUND},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def list_versions(request: Request, claims: Claims) -> Response:
    """Every version of the entry the item is one of, oldest first, each
    with its subjects, whichever version is asked for. An entry never edited
    has one. 404 for an id the user does not have."""
    item_id = path_id(request)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await items.versions(conn, item_id)
    if found is None:
        raise NotFound
    return JSONResponse([Item.of(item).model_dump(mode="json") for item in found])


class TopicSetting(BaseModel):
    """A topic's active level or focus."""

    # The entry's id, its newest version's.
    id: UUID
    value: str
    # When it was last set.
    set_at: datetime


class KnowledgeRow(BaseModel):
    """One topic, its aliases folded into it."""

    id: UUID
    slug: str
    name: str
    state: Literal["canonical", "provisional"]
    # Null when the topic has none.
    level: TopicSetting | None
    focus: TopicSetting | None
    # Its active history entries, misconceptions among them.
    history_count: int


class KnowledgeQuery(BaseModel):
    # Each filter narrows the list; all of them together, every one given.
    level: items.Level | None = None
    focus: items.Focus | None = None
    state: Literal["canonical", "provisional"] | None = None


def _setting(setting: knowledge.Setting | None) -> TopicSetting | None:
    if setting is None:
        return None
    return TopicSetting(id=setting.id, value=setting.value, set_at=setting.set_at)


@router.get(
    "/knowledge",
    response_model=list[KnowledgeRow],
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(query=KnowledgeQuery),
)
@authenticated
async def list_knowledge(request: Request, claims: Claims) -> Response:
    """What the user knows: one row per topic that is not an alias, by slug,
    each alias folded into its target, with its active level and focus, each
    with its id and when it was last set, or null, and its count of active
    history entries. A topic with neither is listed too. Narrowed by level,
    focus and state when they are given. 422 for a value not on their
    lists."""
    query = query_as(request, KnowledgeQuery)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        rows = await knowledge.view(
            conn, level=query.level, focus=query.focus, state=query.state
        )
    return JSONResponse(
        [
            KnowledgeRow(
                id=row.id,
                slug=row.slug,
                name=row.name,
                state=row.state,
                level=_setting(row.level),
                focus=_setting(row.focus),
                history_count=row.history_count,
            ).model_dump(mode="json")
            for row in rows
        ]
    )


class NowRow(Item):
    """A state as the now view reads it."""

    # The newest occurred_at among the events supporting it, computed when
    # read and never stored.
    last_supported_at: datetime
    # Supported last more than 60 days before the request: worth re checking.
    stale: bool


@router.get(
    "/now",
    response_model=list[NowRow],
    responses={**UNAUTHORIZED},
)
@authenticated
async def list_now(request: Request, claims: Claims) -> Response:
    """What is going on now: the user's active state entries, newest support
    first, each with its subjects, last_supported_at, when the newest event
    supporting it happened, and stale, true when that is more than 60 days
    before the request. Confirming one moves it to the front."""
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        entries = await now_and_history.now(conn, now=now)
    return JSONResponse(
        [
            NowRow(
                **Item.fields_of(entry.item),
                last_supported_at=entry.last_supported_at,
                stale=entry.stale,
            ).model_dump(mode="json")
            for entry in entries
        ]
    )


class HistoryRow(Item):
    """A history entry or a decision as the history view reads it."""

    # For a decision another one replaced, the one that did; else null.
    replaced_by: UUID | None


class HistoryQuery(BaseModel):
    # Each filter narrows the list; both together, each one given.
    # A topic by slug, read through aliases.
    topic: Slug | None = None
    # A named thing by name, without letter case.
    entity: Name | None = None


@router.get(
    "/history",
    response_model=list[HistoryRow],
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(query=HistoryQuery),
)
@authenticated
async def list_history(request: Request, claims: Claims) -> Response:
    """What has happened: the user's history entries and decisions by
    occurred_at, oldest first, each with its subjects. Active ones, and each
    decision another one replaced, inactive, with replaced_by naming the one
    that did; retired entries and older versions are left out. Narrowed by
    topic and entity as the item list is. 422 for a malformed slug."""
    query = query_as(request, HistoryQuery)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        entries = await now_and_history.history(
            conn, topic=query.topic, entity=query.entity
        )
    return JSONResponse(
        [
            HistoryRow(
                **Item.fields_of(entry.item), replaced_by=entry.replaced_by
            ).model_dump(mode="json")
            for entry in entries
        ]
    )

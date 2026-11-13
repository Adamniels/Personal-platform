"""The API for the memory store, under `/brain/api/items`: entries the owner
makes by hand, as profile entries and rules so far. Every route needs a valid
token and acts for the account it names, through `brain.db.transaction`. The
rules are in `brain.items`; this module reads the request and shapes the
answer. The routes are listed in "What it serves" in Brain/README.md."""

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
    Field,
    RootModel,
    StringConstraints,
    field_validator,
)

from brain import db, items
from brain.edge import (
    REFUSED,
    UNAUTHORIZED,
    authenticated,
    body_as,
    documented,
)
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


class NewEntry(
    RootModel[Annotated[ProfileEntry | RuleEntry, Field(discriminator="kind")]]
):
    """An entry, told apart by its kind."""


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
            relations=[
                ItemRelation(
                    id=relation.id,
                    from_item=relation.from_item,
                    to_item=relation.to_item,
                    kind=relation.kind,
                    created_at=relation.created_at,
                    ended_at=relation.ended_at,
                    created_by_event=relation.created_by_event,
                )
                for relation in detail.relations
            ],
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
    profile, with a category, or rule, which is global. 422 for an unknown
    kind or category, content that is blank or over 4000 characters, a rule
    naming a subject, or a topic the user does not have. A named thing the
    user does not have is created."""
    entry = (await body_as(request, NewEntry)).root
    now = datetime.now(UTC)
    new = items.NewItem(
        kind=entry.kind,
        content=entry.content,
        category=entry.category if isinstance(entry, ProfileEntry) else None,
        topics=tuple(entry.topics) if isinstance(entry, ProfileEntry) else (),
        entities=tuple(entry.entities) if isinstance(entry, ProfileEntry) else (),
        occurred_at=entry.occurred_at,
        occurred_precision=entry.occurred_precision,
    )
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        item_id = await items.write(conn, new, now=now)
        detail = await items.get(conn, item_id)
    assert detail is not None
    return JSONResponse(
        ItemDetail.of_detail(detail).model_dump(mode="json"), status_code=201
    )

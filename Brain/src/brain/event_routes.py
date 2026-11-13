"""The API for events, under `/brain/api/events`: the endpoint a feature
sends what happened to, "record event" in "The two contracts" in
High-level/docs/feature-contract.md. Every route needs a valid token and acts
for the account it names, through `brain.db.transaction`. The rules are in
`brain.events`; this module reads the request and shapes the answer. The
routes are listed in "What it serves" in Brain/README.md."""

import json
from datetime import UTC, datetime
from typing import Annotated, Any, Literal, Self
from uuid import UUID

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import (
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)

from brain import db, events, topics
from brain.edge import (
    REFUSED,
    UNAUTHORIZED,
    authenticated,
    body_as,
    documented,
    query_as,
)
from brain.subjects import Name, Slug

router = APIRouter(prefix="/brain/api")

# The largest payload, as compact JSON in UTF-8.
PAYLOAD_MAX_BYTES = 16 * 1024
# The largest evidence: a few sentences that stand on their own, not a copy
# of the thing itself, which stays the feature's.
EVIDENCE_MAX = 4000

# A feature's name: lowercase letters, digits and hyphens, starting with a
# letter or digit, as a topic's slug is.
Origin = Annotated[
    str, StringConstraints(pattern=topics.SLUG_PATTERN, max_length=topics.SLUG_MAX)
]
# Free text a feature chooses, such as a run's id: surrounding space
# dropped, then 1 to 200 characters.
Text = Annotated[
    str, StringConstraints(strip_whitespace=True, min_length=1, max_length=200)
]
Evidence = Annotated[
    str,
    StringConstraints(strip_whitespace=True, min_length=1, max_length=EVIDENCE_MAX),
]
Key = Annotated[str, StringConstraints(min_length=1, max_length=200)]
# Postgres' integer, so a larger one is refused here rather than by it.
Version = Annotated[int, Field(strict=True, ge=1, le=2**31 - 1)]
Precision = Literal["instant", "day", "month", "year"]


class NewEvent(BaseModel):
    """What happened, as a feature sends it. A field it does not know is
    refused, so a misspelt one is not silently lost."""

    model_config = ConfigDict(extra="forbid")

    verb: Annotated[str, StringConstraints(min_length=1, max_length=80)]
    # What it is about, at most one: a topic by its slug, or a named thing by
    # its name. Either is created when the user does not have it yet.
    topic: Slug | None = None
    entity: Name | None = None
    # A memory item, for what the owner did to it.
    item: UUID | None = None
    # The feature sending it, never brain.
    origin: Origin
    # How it arose, such as manual-entry.
    source: Text | None = None
    # Groups the events from one pass of the feature.
    run: Text | None = None
    # When it happened, with a zone; without one, when it was recorded.
    occurred_at: AwareDatetime | None = None
    occurred_precision: Precision = "instant"
    # Readable text that stands on its own.
    evidence: Evidence
    # A JSON object, opaque to the brain, at most 16 KiB. Typed Any, since the
    # body is parsed from JSON, so every value in it already is JSON.
    payload: dict[str, Any]
    payload_version: Version
    # Unique per origin: sent again, it is the event already recorded.
    idempotency_key: Key

    @field_validator("origin")
    @classmethod
    def not_the_brain(cls, origin: str) -> str:
        if origin == events.BRAIN_ORIGIN:
            raise ValueError("brain is the brain's own origin, send the feature's")
        return origin

    @field_validator("verb", "entity", "source", "run", "evidence", "idempotency_key")
    @classmethod
    def no_nul(cls, text: str | None) -> str | None:
        # Postgres text cannot hold one.
        if text is not None and "\x00" in text:
            raise ValueError("text may not hold a NUL character")
        return text

    @field_validator("payload")
    @classmethod
    def small_and_storable(cls, payload: dict[str, Any]) -> dict[str, Any]:
        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        if len(encoded.encode()) > PAYLOAD_MAX_BYTES:
            raise ValueError(f"the payload is over {PAYLOAD_MAX_BYTES} bytes as JSON")
        # Postgres jsonb cannot hold one, in a key or a value.
        if "\\u0000" in encoded:
            raise ValueError("the payload may not hold a NUL character")
        return payload

    @model_validator(mode="after")
    def one_subject(self) -> Self:
        if self.topic is not None and self.entity is not None:
            raise ValueError("an event is about a topic or a named thing, not both")
        return self


class EventTopic(BaseModel):
    id: UUID
    slug: str
    name: str


class EventEntity(BaseModel):
    id: UUID
    name: str


class Event(BaseModel):
    id: UUID
    verb: str
    # The topic as the event named it, an alias included.
    subject_topic: UUID | None
    # The topic it is read on: subject_topic, or its target if it is an alias.
    topic: EventTopic | None
    entity: EventEntity | None
    subject_item: UUID | None
    origin: str
    source: str | None
    run: str | None
    occurred_at: datetime
    occurred_precision: Precision
    # The brain's time when it was recorded.
    recorded_at: datetime
    evidence: str
    payload: dict[str, Any]
    payload_version: int
    idempotency_key: str

    @classmethod
    def of(cls, event: events.Event) -> Event:
        topic, entity = event.topic, event.entity
        return cls(
            id=event.id,
            verb=event.verb,
            subject_topic=event.subject_topic,
            topic=EventTopic(id=topic.id, slug=topic.slug, name=topic.name)
            if topic is not None
            else None,
            entity=EventEntity(id=entity.id, name=entity.name)
            if entity is not None
            else None,
            subject_item=event.subject_item,
            origin=event.origin,
            source=event.source,
            run=event.run,
            occurred_at=event.occurred_at,
            occurred_precision=event.occurred_precision,
            recorded_at=event.recorded_at,
            evidence=event.evidence,
            payload=event.payload,
            payload_version=event.payload_version,
            idempotency_key=event.idempotency_key,
        )


ALREADY_RECORDED: dict[int | str, dict[str, Any]] = {
    200: {
        "description": (
            "The user already has an event from this origin with this key:"
            " that event, unchanged."
        ),
        "model": Event,
    }
}


@router.post(
    "/events",
    status_code=201,
    response_model=Event,
    responses={**UNAUTHORIZED, **ALREADY_RECORDED, **REFUSED},
    openapi_extra=documented(body=NewEvent),
)
@authenticated
async def record_event(request: Request, claims: Claims) -> Response:
    """Records what happened, 201. The same origin and idempotency key again
    is 200, with the event already recorded. 422 for an unknown verb, an
    origin of brain, evidence that is missing or blank, a topic and a named
    thing together, a malformed slug, or a payload that is not a JSON object
    of at most 16 KiB. An unknown topic is created provisional, an unknown
    named thing is created too."""
    body = await body_as(request, NewEvent)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        event, created = await events.record(conn, recorded(body), now=now)
    return JSONResponse(
        Event.of(event).model_dump(mode="json"), status_code=201 if created else 200
    )


def recorded(body: NewEvent) -> events.NewEvent:
    """The event `body` sends, as `brain.events.record` takes it: what the
    route records, and what the evaluation's seed is loaded through."""
    return events.NewEvent(
        verb=body.verb,
        topic=body.topic,
        entity=body.entity,
        item=body.item,
        origin=body.origin,
        source=body.source,
        run=body.run,
        occurred_at=body.occurred_at,
        occurred_precision=body.occurred_precision,
        evidence=body.evidence,
        payload=body.payload,
        payload_version=body.payload_version,
        idempotency_key=body.idempotency_key,
    )


# The timeline's page size, by default and at most.
PAGE = 50
PAGE_MAX = 200


class TimelineQuery(BaseModel):
    # Only events recorded before this time, with a zone: the last one's
    # recorded_at, for the next page.
    before: AwareDatetime | None = None
    limit: Annotated[int, Field(ge=1, le=PAGE_MAX)] = PAGE


@router.get(
    "/events",
    response_model=list[Event],
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(query=TimelineQuery),
)
@authenticated
async def list_events(request: Request, claims: Claims) -> Response:
    """The timeline: the user's events, newest recorded first, 50 at a time
    unless limit says otherwise, at most 200. The next page is the one before
    the last event's recorded_at."""
    query = query_as(request, TimelineQuery)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await events.timeline(conn, before=query.before, limit=query.limit)
    return JSONResponse([Event.of(event).model_dump(mode="json") for event in found])

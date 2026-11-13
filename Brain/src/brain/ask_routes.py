"""The questions the brain answers, under `/brain/api/ask/`: who is this
person, what do they know about these topics, what is the history of this
subject, and what is going on now. Every route needs a valid token and
answers for the account it names, through `brain.db.transaction`; no field
of a question names a user. Each answer is built from the building blocks in
`brain.retrieval`, which this module calls and shapes, and every item in it
is one `AnswerItem`, with the reasons it is believed. The routes are listed
in "What it serves" in Brain/README.md.

Each is a POST because a question has a body, though none changes anything;
the origin check applies to it as to any POST. Asking never creates a topic
or a named thing, and an unknown one is answered with nothing known."""

from collections.abc import Iterable
from datetime import UTC, datetime
from typing import Annotated, Any, Literal, Self
from uuid import UUID

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from brain import db, entities, events, retrieval, topics
from brain.edge import REFUSED, UNAUTHORIZED, authenticated, body_as, documented
from brain.event_routes import EventEntity, EventTopic, Precision
from brain.items import Category
from brain.subjects import Name, Slug

router = APIRouter(prefix="/brain/api/ask")

# The most topics one question may ask about.
TOPICS_MAX = 50

Mode = Literal["now", "history"]


class Question(BaseModel):
    """What every question carries: whether it asks about now or about
    history. A field it does not know is refused, so nothing can name a
    user, or be misspelt and silently lost."""

    model_config = ConfigDict(extra="forbid")

    # now: active items, newest support first, a state flagged stale once
    # it has gone 60 days unsupported. history: age ignored, nothing stale,
    # ordered by when each thing happened, oldest first.
    mode: Mode = "now"


class ProfileQuestion(Question):
    """Who is this person."""


class TopicsQuestion(Question):
    """What do they know about these topics: 1 to 50 slugs, answered in the
    order asked, one entry each."""

    topics: Annotated[list[Slug], Field(min_length=1, max_length=TOPICS_MAX)]


class HistoryQuestion(Question):
    """What is the history of this subject: one topic, by slug, or one named
    thing, by name in any letter case, and not both."""

    topic: Slug | None = None
    entity: Name | None = None

    @field_validator("entity")
    @classmethod
    def name_storable(cls, name: str | None) -> str | None:
        # Postgres text cannot hold one, so no name the user has does.
        if name is not None and "\x00" in name:
            raise ValueError("text may not hold a NUL character")
        return name

    @model_validator(mode="after")
    def one_subject(self) -> Self:
        if (self.topic is None) == (self.entity is None):
            raise ValueError("a history is asked about one topic or one named thing")
        return self


class NowQuestion(Question):
    """What is going on now."""


class Subjects(BaseModel):
    """What an item is about: its topics, each read through its alias, and
    its named things. Both empty for a global item."""

    topics: list[EventTopic]
    entities: list[EventEntity]


class VerbCounts(BaseModel):
    """Events counted per verb kind, every kind present."""

    exposure: int
    engagement: int
    judgment: int
    assertion: int
    decision: int
    activity: int


class Support(BaseModel):
    """How many events support the item and how many contradict it. These
    are the facts a relevance or confidence score would be computed from;
    there is no score until the evaluation cases can test one."""

    supports: VerbCounts
    contradicts: VerbCounts


class Reason(BaseModel):
    """One event behind the item."""

    verb: str
    # Whether the event is for the item or against it.
    kind: Literal["supports", "contradicts"]
    # The event's evidence, what it says.
    text: str
    origin: str
    occurred_at: datetime
    occurred_precision: Precision
    recorded_at: datetime


class AnswerItem(BaseModel):
    """An item as every answer gives it, with why it is believed. Nothing
    here is stored: support, last_supported_at and stale are computed from
    the evidence when the question is asked."""

    id: UUID
    kind: Literal[
        "profile", "rule", "topic_level", "topic_focus", "state", "history", "decision"
    ]
    store: Literal["profile", "procedural", "knowledge", "semantic", "reported_history"]
    nature: Literal["claim", "record"]
    provenance: Literal["asserted", "derived"]
    # Active, or, only in a history asked about history, a decision another
    # one replaced, inactive and superseded.
    status: Literal["active", "inactive"]
    inactive_reason: Literal["superseded", "retired"] | None
    content: str
    value: str | None
    category: str | None
    details: dict[str, Any]
    occurred_at: datetime | None
    occurred_precision: Precision | None
    subjects: Subjects
    # The newest occurred_at among the events supporting it.
    last_supported_at: datetime
    # A state last supported more than 60 days before the question, asked
    # about now; always false asked about history.
    stale: bool
    support: Support
    # Every event behind it, oldest recorded first.
    evidence: list[Reason]

    @classmethod
    def of(cls, answered: retrieval.AnswerItem) -> AnswerItem:
        item = answered.item
        return cls(
            id=item.id,
            kind=item.kind,
            store=item.store,
            nature=item.nature,
            provenance=item.provenance,
            status=item.status,
            inactive_reason=item.inactive_reason,
            content=item.content,
            value=item.value,
            category=item.category,
            details=item.details,
            occurred_at=item.occurred_at,
            occurred_precision=item.occurred_precision,
            subjects=Subjects(
                topics=[
                    EventTopic(id=topic.id, slug=topic.slug, name=topic.name)
                    for topic in item.topics
                ],
                entities=[
                    EventEntity(id=entity.id, name=entity.name)
                    for entity in item.entities
                ],
            ),
            last_supported_at=answered.last_supported_at,
            stale=answered.stale,
            support=Support(
                supports=VerbCounts(**answered.support.supports),
                contradicts=VerbCounts(**answered.support.contradicts),
            ),
            evidence=[
                Reason(
                    verb=reason.verb,
                    kind=reason.kind,
                    text=reason.text,
                    origin=reason.origin,
                    occurred_at=reason.occurred_at,
                    occurred_precision=reason.occurred_precision,
                    recorded_at=reason.recorded_at,
                )
                for reason in answered.evidence
            ],
        )


def _all(found: Iterable[retrieval.AnswerItem]) -> list[AnswerItem]:
    return [AnswerItem.of(answered) for answered in found]


def _one(answered: retrieval.AnswerItem | None) -> AnswerItem | None:
    return AnswerItem.of(answered) if answered is not None else None


class ProfileGroup(BaseModel):
    category: Category
    items: list[AnswerItem]


class ProfileAnswer(BaseModel):
    mode: Mode
    # Every category, in the profile's order, an empty one included.
    categories: list[ProfileGroup]
    rules: list[AnswerItem]


@router.post(
    "/profile",
    response_model=ProfileAnswer,
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(body=ProfileQuestion),
)
@authenticated
async def ask_profile(request: Request, claims: Claims) -> Response:
    """Who is this person: their active profile entries, grouped by
    category, every one of the six in order, an empty one included, and then
    their rules, each group ordered by mode. A topic's focus is in the
    topics answer instead. 422 for an unknown mode or field."""
    question = await body_as(request, ProfileQuestion)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await retrieval.profile(conn, now=now, mode=question.mode)
    answer = ProfileAnswer(
        mode=question.mode,
        categories=[
            ProfileGroup(category=category, items=_all(grouped))
            for category, grouped in found.categories
        ],
        rules=_all(found.rules),
    )
    return JSONResponse(answer.model_dump(mode="json"))


def _topic(topic: events.TopicRef) -> EventTopic:
    return EventTopic(id=topic.id, slug=topic.slug, name=topic.name)


class NarrowerKnowledge(BaseModel):
    """What is known about one topic under the one asked."""

    topic: EventTopic
    # The slugs that led to it: the topic asked first, then each on the way,
    # down to the one it is directly under.
    via: list[str]
    level: AnswerItem | None
    focus: AnswerItem | None
    misconceptions: list[AnswerItem]
    history: list[AnswerItem]


class TopicAnswer(BaseModel):
    """What is known about one asked topic."""

    # The slug as it was asked, an alias included.
    asked: str
    # The topic that answered, the alias's target; null for a slug the user
    # has no topic by.
    topic: EventTopic | None
    # Whether the user has a topic by the slug asked.
    known: bool
    level: AnswerItem | None
    focus: AnswerItem | None
    misconceptions: list[AnswerItem]
    # Its other history entries.
    history: list[AnswerItem]
    # Every topic under it, to any depth, nearest first, each once.
    narrower: list[NarrowerKnowledge]
    # The slugs of the topics related to it, without what is known of them.
    related: list[str]


class TopicsAnswer(BaseModel):
    mode: Mode
    # One per slug asked, in the order asked.
    topics: list[TopicAnswer]


@router.post(
    "/topics",
    response_model=TopicsAnswer,
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(body=TopicsQuestion),
)
@authenticated
async def ask_topics(request: Request, claims: Claims) -> Response:
    """What does this person know about these topics: one entry per slug
    asked, in the order asked, each naming the slug asked and the topic that
    answered, an alias's target, with its active level and focus, or null,
    its corrected misconceptions and its other history entries, items about
    its aliases included, each ordered by mode. A slug the user has no topic
    by answers known false, with nothing in it, and is never created. 422
    for no slug, more than 50, a malformed one, or an unknown mode or
    field."""
    question = await body_as(request, TopicsQuestion)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        named = {
            slug: await topics.by_slug(conn, slug)
            for slug in dict.fromkeys(question.topics)
        }
        under: dict[UUID, list[retrieval.Narrower]] = {}
        beside: dict[UUID, list[events.TopicRef]] = {}
        for topic in named.values():
            if topic is not None:
                under[topic.id] = await retrieval.narrower(conn, topic.id)
                beside[topic.id] = await retrieval.related(conn, topic.id)
        wanted = [topic.id for topic in named.values() if topic is not None]
        wanted += [n.topic.id for found in under.values() for n in found]
        known_about = await retrieval.knowledge(
            conn, list(dict.fromkeys(wanted)), now=now, mode=question.mode
        )
    entries = []
    for slug in question.topics:
        topic = named[slug]
        known = known_about.get(topic.id) if topic is not None else None
        if topic is None or known is None:
            entries.append(
                TopicAnswer(
                    asked=slug,
                    topic=None,
                    known=False,
                    level=None,
                    focus=None,
                    misconceptions=[],
                    history=[],
                    narrower=[],
                    related=[],
                )
            )
            continue
        narrower = []
        for below in under[topic.id]:
            about = known_about[below.topic.id]
            narrower.append(
                NarrowerKnowledge(
                    topic=_topic(below.topic),
                    via=list(below.via),
                    level=_one(about.level),
                    focus=_one(about.focus),
                    misconceptions=_all(about.misconceptions),
                    history=_all(about.history),
                )
            )
        entries.append(
            TopicAnswer(
                asked=slug,
                topic=_topic(known.topic),
                known=True,
                level=_one(known.level),
                focus=_one(known.focus),
                misconceptions=_all(known.misconceptions),
                history=_all(known.history),
                narrower=narrower,
                related=[t.slug for t in beside[topic.id]],
            )
        )
    answer = TopicsAnswer(mode=question.mode, topics=entries)
    return JSONResponse(answer.model_dump(mode="json"))


class NarrowerHistory(BaseModel):
    """The records and decisions about one topic under the one asked."""

    topic: EventTopic
    # The slugs that led to it: the topic asked first, then each on the way,
    # down to the one it is directly under.
    via: list[str]
    items: list[AnswerItem]


class HistoryAnswer(BaseModel):
    mode: Mode
    # The slug or the name as it was asked.
    asked: str
    # Whether the user has a topic or a named thing by it.
    known: bool
    # The subject that answered, the topic, an alias's target, or the named
    # thing; both null when it is not known.
    topic: EventTopic | None
    entity: EventEntity | None
    items: list[AnswerItem]
    # For a topic, every topic under it, to any depth, nearest first, each
    # once with its own records and decisions; none for a named thing.
    narrower: list[NarrowerHistory]
    # For a topic, the slugs of the topics related to it, without their
    # items; none for a named thing.
    related: list[str]


@router.post(
    "/history",
    response_model=HistoryAnswer,
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(body=HistoryQuestion),
)
@authenticated
async def ask_history(request: Request, claims: Claims) -> Response:
    """What is the history of this subject: the records and decisions about
    one topic, read through aliases, or one named thing, ordered by mode.
    Asked about history, the decisions another one replaced are there too,
    inactive and superseded. A subject the user does not have answers known
    false, with no items, and is never created. 422 for neither or both of
    topic and entity, a malformed slug, or an unknown mode or field."""
    question = await body_as(request, HistoryQuestion)
    now = datetime.now(UTC)
    answered_topic: EventTopic | None = None
    answered_entity: EventEntity | None = None
    subject: retrieval.Subject | None = None
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        if question.topic is not None:
            named = await topics.by_slug(conn, question.topic)
            if named is not None:
                target = await topics.get(conn, named.resolves_to)
                assert target is not None
                answered_topic = EventTopic(
                    id=target.id, slug=target.slug, name=target.name
                )
                subject = retrieval.Subject("topic", target.id)
        else:
            assert question.entity is not None
            thing = await entities.by_name(conn, question.entity)
            if thing is not None:
                answered_entity = EventEntity(id=thing.id, name=thing.name)
                subject = retrieval.Subject("entity", thing.id)
        found = (
            await retrieval.history(conn, subject, now=now, mode=question.mode)
            if subject is not None
            else []
        )
        narrower: list[NarrowerHistory] = []
        related: list[str] = []
        if subject is not None and subject.kind == "topic":
            for below in await retrieval.narrower(conn, subject.id):
                told = await retrieval.history(
                    conn,
                    retrieval.Subject("topic", below.topic.id),
                    now=now,
                    mode=question.mode,
                )
                narrower.append(
                    NarrowerHistory(
                        topic=_topic(below.topic), via=list(below.via), items=_all(told)
                    )
                )
            related = [t.slug for t in await retrieval.related(conn, subject.id)]
    asked = question.topic if question.topic is not None else question.entity
    assert asked is not None
    answer = HistoryAnswer(
        mode=question.mode,
        asked=asked,
        known=subject is not None,
        topic=answered_topic,
        entity=answered_entity,
        items=_all(found),
        narrower=narrower,
        related=related,
    )
    return JSONResponse(answer.model_dump(mode="json"))


class NowAnswer(BaseModel):
    mode: Mode
    items: list[AnswerItem]


@router.post(
    "/now",
    response_model=NowAnswer,
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(body=NowQuestion),
)
@authenticated
async def ask_now(request: Request, claims: Claims) -> Response:
    """What is going on now for this person: their active states, ordered
    by mode, each flagged stale, asked about now, once it has gone 60 days
    unsupported. 422 for an unknown mode or field."""
    question = await body_as(request, NowQuestion)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await retrieval.now(conn, now=now, mode=question.mode)
    answer = NowAnswer(mode=question.mode, items=_all(found))
    return JSONResponse(answer.model_dump(mode="json"))

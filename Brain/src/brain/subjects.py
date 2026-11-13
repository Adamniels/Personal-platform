"""The API for the brain's subjects, what a user's memory can be about:
topics and named things, under `/brain/api/`. Every route needs a valid
token, and acts for the account it names, through `brain.db.transaction`.
What each does is in `brain.topics` and `brain.entities`; this module only
reads the request and shapes the answer. A feature is never a subject, so
nothing here names one.
The routes are listed in "What it serves" in Brain/README.md."""

from datetime import UTC, datetime
from typing import Annotated, Literal
from uuid import UUID

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import BaseModel, StringConstraints

from brain import db, entities, topics
from brain.edge import (
    CONFLICT,
    NOT_FOUND,
    REFUSED,
    UNAUTHORIZED,
    authenticated,
    body_as,
    documented,
    path_id,
)
from brain.errors import Refused

router = APIRouter(prefix="/brain/api")

# A name as a person types it: surrounding space dropped, then 1 to 200
# characters.
Name = Annotated[
    str, StringConstraints(strip_whitespace=True, min_length=1, max_length=200)
]
Slug = Annotated[
    str, StringConstraints(pattern=topics.SLUG_PATTERN, max_length=topics.SLUG_MAX)
]


# A label, such as `project`: free text, surrounding space dropped, then 1 to
# 80 characters.
Label = Annotated[
    str, StringConstraints(strip_whitespace=True, min_length=1, max_length=80)
]


class NewTopic(BaseModel):
    name: Name
    # Without one, the slug is made from the name, see `topics.slug_from`.
    slug: Slug | None = None


class SameAs(BaseModel):
    target_id: UUID


class Topic(BaseModel):
    id: UUID
    slug: str
    name: str
    state: Literal["canonical", "provisional"]
    # The topic this one was marked the same as, or null.
    alias_of: UUID | None
    # The topic this one is read as: alias_of when it is an alias, else id.
    resolves_to: UUID
    created_at: datetime

    @classmethod
    def of(cls, topic: topics.Topic) -> Topic:
        return cls(
            id=topic.id,
            slug=topic.slug,
            name=topic.name,
            state=topic.state,
            alias_of=topic.alias_of,
            resolves_to=topic.resolves_to,
            created_at=topic.created_at,
        )


class NewEntity(BaseModel):
    name: Name
    label: Label | None = None


class Entity(BaseModel):
    id: UUID
    name: str
    label: str | None
    created_at: datetime

    @classmethod
    def of(cls, entity: entities.Entity) -> Entity:
        return cls(
            id=entity.id,
            name=entity.name,
            label=entity.label,
            created_at=entity.created_at,
        )


def answer(
    model: BaseModel | list[Topic] | list[Entity], status: int = 200
) -> Response:
    if isinstance(model, list):
        return JSONResponse([item.model_dump(mode="json") for item in model])
    return JSONResponse(model.model_dump(mode="json"), status_code=status)


@router.get("/topics", response_model=list[Topic], responses=UNAUTHORIZED)
@authenticated
async def list_topics(request: Request, claims: Claims) -> Response:
    """Every topic the user has, aliases included, by slug."""
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await topics.everything(conn)
    return answer([Topic.of(topic) for topic in found])


@router.post(
    "/topics",
    status_code=201,
    response_model=Topic,
    responses={**UNAUTHORIZED, **REFUSED, **CONFLICT},
    openapi_extra=documented(body=NewTopic),
)
@authenticated
async def create_topic(request: Request, claims: Claims) -> Response:
    """A new canonical topic. A slug that is not given is made from the name;
    one the user already has is 409."""
    body = await body_as(request, NewTopic)
    slug = body.slug or topics.slug_from(body.name)
    if slug is None:
        raise Refused("the name has no letters or digits to make a slug from")
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        topic = await topics.create(conn, body.name, slug, now=now)
    return answer(Topic.of(topic), status=201)


@router.post(
    "/topics/{id}/promote",
    response_model=Topic,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def promote_topic(request: Request, claims: Claims) -> Response:
    """Makes a provisional topic canonical. An alias is 422."""
    topic_id = path_id(request)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        topic = await topics.promote(conn, topic_id)
    return answer(Topic.of(topic))


@router.post(
    "/topics/{id}/same-as",
    response_model=Topic,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED},
    openapi_extra=documented(body=SameAs, path=("id",)),
)
@authenticated
async def same_as_topic(request: Request, claims: Claims) -> Response:
    """Marks the topic the same as the target, making it an alias that is
    read as the target. Nothing that names it changes. 422 when the target
    is itself, is not there, is an alias or is provisional, or when the topic
    is an alias or has aliases of its own."""
    topic_id = path_id(request)
    body = await body_as(request, SameAs)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        topic = await topics.same_as(conn, topic_id, body.target_id)
    return answer(Topic.of(topic))


@router.post(
    "/topics/{id}/separate",
    response_model=Topic,
    responses={**UNAUTHORIZED, **NOT_FOUND},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def separate_topic(request: Request, claims: Claims) -> Response:
    """Makes an alias its own topic again. A topic that is not an alias is
    answered as it is."""
    topic_id = path_id(request)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        topic = await topics.separate(conn, topic_id)
    return answer(Topic.of(topic))


class Broader(BaseModel):
    broader_id: UUID


class TopicRelation(BaseModel):
    id: UUID
    # Broader than to_topic, for kind broader_than.
    from_topic: UUID
    to_topic: UUID
    kind: str
    created_at: datetime
    # Null while it holds.
    ended_at: datetime | None

    @classmethod
    def of(cls, relation: topics.TopicRelation) -> TopicRelation:
        return cls(
            id=relation.id,
            from_topic=relation.from_topic,
            to_topic=relation.to_topic,
            kind=relation.kind,
            created_at=relation.created_at,
            ended_at=relation.ended_at,
        )


@router.post(
    "/topics/{id}/broader",
    status_code=201,
    response_model=TopicRelation,
    responses={**UNAUTHORIZED, **NOT_FOUND, **REFUSED, **CONFLICT},
    openapi_extra=documented(body=Broader, path=("id",)),
)
@authenticated
async def add_broader(request: Request, claims: Claims) -> Response:
    """Places the topic under a broader one, a broader_than relation from the
    broader topic to this one. 422 when the broader topic is not there or the
    relation would close a loop, read through aliases; 409 when it already
    holds."""
    topic_id = path_id(request)
    body = await body_as(request, Broader)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        relation = await topics.add_broader(conn, topic_id, body.broader_id, now=now)
    return answer(TopicRelation.of(relation), status=201)


@router.post(
    "/topic-relations/{id}/end",
    response_model=TopicRelation,
    responses={**UNAUTHORIZED, **NOT_FOUND},
    openapi_extra=documented(path=("id",)),
)
@authenticated
async def end_relation(request: Request, claims: Claims) -> Response:
    """Ends a relation, which stays, with the time it ended. One already
    ended is answered as it is."""
    relation_id = path_id(request)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        relation = await topics.end_relation(conn, relation_id, now=now)
    return answer(TopicRelation.of(relation))


@router.get("/entities", response_model=list[Entity], responses=UNAUTHORIZED)
@authenticated
async def list_entities(request: Request, claims: Claims) -> Response:
    """Every named thing the user has, by name."""
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await entities.everything(conn)
    return answer([Entity.of(entity) for entity in found])


@router.post(
    "/entities",
    status_code=201,
    response_model=Entity,
    responses={**UNAUTHORIZED, **REFUSED, **CONFLICT},
    openapi_extra=documented(body=NewEntity),
)
@authenticated
async def create_entity(request: Request, claims: Claims) -> Response:
    """A new named thing, with an optional label such as project. A name the
    user already has, in any letter case, is 409."""
    body = await body_as(request, NewEntity)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        entity = await entities.create(conn, body.name, body.label, now=now)
    return answer(Entity.of(entity), status=201)

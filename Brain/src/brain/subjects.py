"""The API for the brain's subjects, the topics a user's memory can be about,
under `/brain/api/`. Every route needs a valid token, and acts for the account
it names, through `brain.db.transaction`. What each does is in
`brain.topics`; this module only reads the request and shapes the answer.
The routes are listed in "What it serves" in Brain/README.md."""

from datetime import UTC, datetime
from typing import Annotated, Literal
from uuid import UUID

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import BaseModel, StringConstraints

from brain import db, topics
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


def answer(model: BaseModel | list[Topic], status: int = 200) -> Response:
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

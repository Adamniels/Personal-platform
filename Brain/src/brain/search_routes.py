"""Searching memory by its words, `GET /brain/api/search`: "search, meaning
search memory" in "The two contracts" in
`High-level/docs/feature-contract.md`. The route needs a valid token and
answers for the account it names, through `brain.db.transaction`; the search
itself is `brain.retrieval.search`, and every item in the answer is the
shared item shape the `ask` answers give, with an excerpt. The route is
listed in "What it serves" in Brain/README.md."""

from datetime import UTC, datetime
from typing import Annotated, Literal

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from platform_auth.verify import Claims
from pydantic import BaseModel, ConfigDict, Field, StringConstraints

from brain import db, retrieval
from brain.ask_routes import AnswerItem
from brain.edge import REFUSED, UNAUTHORIZED, authenticated, documented, query_as

router = APIRouter(prefix="/brain/api")

# The longest search, once its surrounding whitespace is dropped.
QUERY_MAX = 200
# How many items a search answers with when it does not say, and at most.
LIMIT_DEFAULT = 50
LIMIT_MAX = 200


class SearchQuery(BaseModel):
    """What a search asks. A field it does not know is refused, so a
    misspelt one is not silently lost."""

    model_config = ConfigDict(extra="forbid")

    # The words, each required, read as plain words: nothing in them is an
    # operator. 1 to 200 characters once surrounding whitespace is dropped.
    q: Annotated[
        str,
        StringConstraints(strip_whitespace=True, min_length=1, max_length=QUERY_MAX),
    ]
    # Repeated for more than one: an item of any of them. Every kind when
    # left out.
    kind: list[
        Literal[
            "profile",
            "rule",
            "topic_level",
            "topic_focus",
            "state",
            "history",
            "decision",
        ]
    ] = Field(default_factory=list)
    # Retired items and older versions of edited ones too.
    include_inactive: bool = False
    limit: Annotated[int, Field(ge=1, le=LIMIT_MAX)] = LIMIT_DEFAULT


class SearchItem(AnswerItem):
    """An item a search found, as every answer gives it, with where its
    words are."""

    # HTML: about 15 to 35 words of the item's own text where that holds the
    # words, otherwise of the evidence of the newest event behind it that
    # does, each matched word in <mark>, every other character escaped.
    excerpt: str


@router.get(
    "/search",
    response_model=list[SearchItem],
    responses={**UNAUTHORIZED, **REFUSED},
    openapi_extra=documented(query=SearchQuery),
)
@authenticated
async def search(request: Request, claims: Claims) -> Response:
    """The user's items holding every word of q, in what an item says, its
    content, value, category and the text in its details, or in the evidence
    of an event behind it; word forms match, `indexing` finding `indexes`,
    but not a word inside another. Narrowed to any of kind, when given, and
    to active items unless include_inactive. Newest last_supported_at first,
    at most limit, 50 by default, 1 to 200, with no rank: an excerpt shows
    where the words are. A query of operators or common words alone finds
    nothing. 422 for a q blank or over 200 characters, an unknown kind, a
    limit off its range, or an unknown field."""
    query = query_as(request, SearchQuery)
    now = datetime.now(UTC)
    async with db.transaction(request.app.state.pool, claims.account_id) as conn:
        found = await retrieval.search(
            conn,
            query.q,
            now=now,
            kinds=query.kind,
            inactive=query.include_inactive,
            limit=query.limit,
        )
    return JSONResponse(
        [
            SearchItem(
                **dict(AnswerItem.of(each.answered)), excerpt=each.excerpt
            ).model_dump(mode="json")
            for each in found
        ]
    )

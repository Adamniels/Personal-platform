"""The brain's edge, shared by every module with routes: who is asking,
through `platform-auth`, reading a request's address and body once the asker
is known, and the status each of `brain.errors` is answered with. See "Who is
asking" in Brain/README.md."""

from collections.abc import Awaitable, Callable
from typing import Any, get_origin
from uuid import UUID

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response
from platform_auth import auth
from pydantic import BaseModel, ValidationError

from brain.errors import Conflict, NotFound, Refused

# What every route behind a token may also answer, for the schema.
UNAUTHORIZED: dict[int | str, dict[str, Any]] = {
    401: {
        "description": (
            "No valid token: missing, expired or failing verification, all"
            " alike. The body is empty; the caller refreshes and retries."
        )
    }
}

# What a route that reads an address or a body may answer besides, by the
# error that leads to it. Each is `{"detail": ...}`, FastAPI's own shape.
NOT_FOUND: dict[int | str, dict[str, Any]] = {
    404: {"description": "Nothing of this user's at that address."}
}
REFUSED: dict[int | str, dict[str, Any]] = {
    422: {"description": "A body that does not parse, or a request breaking a rule."}
}
CONFLICT: dict[int | str, dict[str, Any]] = {
    409: {"description": "It would be the user's second of something unique."}
}


Handler = Callable[[Request], Awaitable[Response]]


def _guarded(
    guard: Callable[[auth.Endpoint], Handler], endpoint: auth.Endpoint
) -> Handler:
    """An endpoint behind one of the library's wrappers, as a handler FastAPI
    calls with the request alone.

    FastAPI decides a handler's parameters from its signature, following
    `__wrapped__`, which the wrappers point at the endpoint's
    `(request, claims)`, and would read `claims` as a request body. The
    handler returned here takes only the request, which is all FastAPI then
    passes."""
    wrapped = guard(endpoint)

    async def handler(request: Request) -> Response:
        return await wrapped(request)

    # The route's name and description in the schema are the endpoint's.
    handler.__name__ = endpoint.__name__
    handler.__qualname__ = endpoint.__qualname__
    handler.__doc__ = endpoint.__doc__
    return handler


def authenticated(endpoint: auth.Endpoint) -> Handler:
    """An API endpoint that needs a valid token, through the library's `api`
    wrapper: called with the claims, or answered 401 with an empty body."""
    return _guarded(auth.api, endpoint)


def page(endpoint: auth.Endpoint) -> Handler:
    """A page a browser loads, through the library's `page` wrapper: called
    with the claims, or answered with a 303 to the core's refresh endpoint and
    the way back."""
    return _guarded(auth.page, endpoint)


# Because the handler takes only the request, FastAPI neither reads its body
# nor its address, nor documents them. An endpoint reads both itself, after
# the token is checked, so a request without one is 401 whatever its body;
# `documented` puts them in the schema.


async def body_as[M: BaseModel](request: Request, model: type[M]) -> M:
    """The request's JSON body as `model`, or a 422 in FastAPI's own shape."""
    try:
        data = await request.json()
    except ValueError as err:
        raise RequestValidationError(
            [{"type": "json_invalid", "loc": ("body",), "msg": "the body is not JSON"}]
        ) from err
    try:
        return model.model_validate(data)
    except ValidationError as err:
        raise RequestValidationError(
            err.errors(include_url=False, include_context=False)
        ) from err


def query_as[M: BaseModel](request: Request, model: type[M]) -> M:
    """The request's query string as `model`, or a 422 in FastAPI's own
    shape. A field of `model` that is a list takes every value its name is
    given, in order; any other name given twice is read as its last value."""
    params = request.query_params
    repeatable = {
        name
        for name, field in model.model_fields.items()
        if get_origin(field.annotation) is list
    }
    data = {
        name: params.getlist(name) if name in repeatable else params[name]
        for name in params
    }
    try:
        return model.model_validate(data)
    except ValidationError as err:
        raise RequestValidationError(
            err.errors(include_url=False, include_context=False)
        ) from err


def path_id(request: Request, name: str = "id") -> UUID:
    """The id in the request's address. One that is not a UUID names nothing,
    so it is a 404 like any other id that is not there."""
    try:
        return UUID(request.path_params[name])
    except ValueError as err:
        raise NotFound from err


def documented(
    *,
    body: type[BaseModel] | None = None,
    path: tuple[str, ...] = (),
    query: type[BaseModel] | None = None,
) -> dict[str, Any]:
    """The schema for what an endpoint reads itself: a JSON body of `body`,
    the ids in its address, each a UUID, and the fields of `query` as its
    query string."""
    extra: dict[str, Any] = {}
    parameters = [
        {
            "name": name,
            "in": "path",
            "required": True,
            "schema": {"type": "string", "format": "uuid"},
        }
        for name in path
    ]
    if query is not None:
        schema = query.model_json_schema()
        required = set(schema.get("required", ()))
        parameters.extend(
            {
                "name": name,
                "in": "query",
                "required": name in required,
                "schema": field,
            }
            for name, field in schema["properties"].items()
        )
    if parameters:
        extra["parameters"] = parameters
    if body is not None:
        extra["requestBody"] = {
            "required": True,
            "content": {"application/json": {"schema": _inlined(body)}},
        }
    return extra


def _inlined(model: type[BaseModel]) -> dict[str, Any]:
    """`model`'s JSON schema with every model it nests written in place.

    Pydantic puts nested models under `$defs` and points at them with
    `#/$defs/<name>`, which inside the schema document would point at nothing,
    since that document keeps its models under `components`. A union's
    `discriminator` is dropped with them: its mapping names those same
    addresses, and each branch already fixes its tag with a `const`."""
    schema = model.model_json_schema()
    defs: dict[str, Any] = schema.pop("$defs", {})

    def place(node: Any) -> Any:
        if isinstance(node, list):
            return [place(each) for each in node]
        if not isinstance(node, dict):
            return node
        ref = node.get("$ref")
        if isinstance(ref, str) and ref.startswith("#/$defs/"):
            return place(defs[ref.removeprefix("#/$defs/")])
        return {
            key: place(value)
            for key, value in node.items()
            if not (key == "discriminator" and "oneOf" in node)
        }

    placed: dict[str, Any] = place(schema)
    return placed


def answer_errors(app: FastAPI) -> None:
    """Each of `brain.errors`, wherever it is raised in a route, answered with
    its status and its message as the detail."""

    def answer(status: int) -> Callable[[Request, Exception], Awaitable[Response]]:
        async def handler(request: Request, exc: Exception) -> Response:
            detail = str(exc) or {404: "Not Found"}.get(status, "")
            return JSONResponse({"detail": detail}, status_code=status)

        return handler

    app.add_exception_handler(NotFound, answer(404))
    app.add_exception_handler(Refused, answer(422))
    app.add_exception_handler(Conflict, answer(409))

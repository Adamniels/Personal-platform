"""Who is asking, at a service's edge: the token read from the request,
verified against the core's keys, and what a request without a valid one
gets. The rules are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`.

A route takes one of two requirements. `page` sends a request without a valid
token to the core's refresh endpoint with the way back, and `api` answers it
401 with an empty body, since a script cannot follow a redirect. A missing
token, an expired one and one that fails verification are the same to both:
the service never decides between refreshing and logging in, the core does.

Both find the key cache at `request.app.state.keys`, which the service sets
when it starts.
"""

import time
from collections.abc import Awaitable, Callable
from functools import wraps
from urllib.parse import quote

import jwt
from starlette.requests import Request
from starlette.responses import RedirectResponse, Response

from platform_auth.keys import KeyCache
from platform_auth.verify import Claims, Refused, verify

# Set by the core at login and refresh, see "The session" in
# Core/docs/core-architecture.md.
ACCESS_COOKIE = "__Host-access"

# The core's deciding endpoint. Relative, since every service sits on the
# platform's one origin.
REFRESH_PATH = "/auth/refresh"

Endpoint = Callable[[Request, Claims], Awaitable[Response]]


def bearer_token(request: Request) -> str | None:
    """The token the request carries, or None if it carries none that could
    be valid. An `Authorization` header, when there is one, is the only place
    looked: a header that is present but malformed is a refusal, never a
    reason to fall back to the cookie."""
    headers = request.headers.getlist("authorization")
    if headers:
        if len(headers) != 1:
            return None
        scheme, _, token = headers[0].partition(" ")
        if scheme.lower() != "bearer" or not token or " " in token:
            return None
        return token
    return request.cookies.get(ACCESS_COOKIE) or None


async def authenticate(request: Request, keys: KeyCache, now: int) -> Claims | None:
    """The claims of the request's token if it is valid at `now`, otherwise
    None. A core that cannot be reached, with the key not cached, is one more
    way of having no valid token."""
    token = bearer_token(request)
    if token is None:
        return None
    # Reading the `kid` here only picks which key to fetch, so a token with no
    # usable one costs no key set request. It is not a second rule: `verify`
    # makes the same check and is the one that decides.
    try:
        key_id = jwt.get_unverified_header(token).get("kid")
    except jwt.PyJWTError:
        return None
    if not isinstance(key_id, str):
        return None
    key = await keys.find(key_id, now)
    try:
        return verify(token, {key_id: key} if key is not None else {}, now)
    except Refused:
        return None


def refresh_location(request: Request) -> str:
    """`/auth/refresh` with the path and query exactly as the browser sent
    them, encoded once more as `return_to`. The service does not judge the
    path; the core checks it once, after decoding."""
    path = request.scope.get("raw_path") or request.scope["path"].encode()
    if query := request.scope["query_string"]:
        path += b"?" + query
    return f"{REFRESH_PATH}?return_to={quote(path, safe='')}"


async def _claims(request: Request) -> Claims | None:
    # The time is read once here, at the edge, and passed down.
    return await authenticate(request, request.app.state.keys, int(time.time()))


def page(endpoint: Endpoint) -> Callable[[Request], Awaitable[Response]]:
    """For a page a browser loads: without a valid token, a 303 to the core's
    refresh endpoint, which refreshes or sends the browser to log in, and
    back here either way."""

    @wraps(endpoint)
    async def required(request: Request) -> Response:
        if (claims := await _claims(request)) is None:
            return RedirectResponse(refresh_location(request), status_code=303)
        return await endpoint(request, claims)

    return required


def api(endpoint: Endpoint) -> Callable[[Request], Awaitable[Response]]:
    """For a route a script calls: without a valid token, 401 with an empty
    body, the same whatever the reason, and the frontend refreshes and
    retries."""

    @wraps(endpoint)
    async def required(request: Request) -> Response:
        if (claims := await _claims(request)) is None:
            return Response(status_code=401)
        return await endpoint(request, claims)

    return required

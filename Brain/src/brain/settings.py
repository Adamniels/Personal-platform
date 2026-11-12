"""The brain's settings, read from the process environment once, at import.

Unset is the default, and anything set must parse, so a typo stops the brain
at startup instead of quietly becoming the default. The database's
connection string has no default and must be set. The brain loads no `.env`
file; `Brain/.env.example` lists the names for whoever exports them.

The parsing rules are canary's, in `canary/src/canary/settings.py`, written
out again rather than imported, since no service imports from another's
directory.
"""

import os
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from urllib.parse import SplitResult, urlsplit

DEFAULT_CORE_URL = "http://127.0.0.1:8080"
DEFAULT_PUBLIC_ORIGIN = "https://platform.test"

DEFAULT_PORTS = {"http": 80, "https": 443}


class SettingsError(ValueError):
    """A setting that is set but does not parse, or a required one that is not
    set. The brain refuses to start."""


@dataclass(frozen=True)
class Settings:
    # The core's own address, never the proxy's.
    core_url: str
    # The platform's origin as a browser sends it, for the Origin check.
    public_origin: str
    # The brain's database, as brain_app. It holds a password, so it is left
    # out of the repr and never repeated in an error.
    app_database_url: str = field(repr=False)


def load(environ: Mapping[str, str]) -> Settings:
    return Settings(
        core_url=_setting(environ, "BRAIN_CORE_URL", DEFAULT_CORE_URL, parse_core_url),
        public_origin=_setting(
            environ, "BRAIN_PUBLIC_ORIGIN", DEFAULT_PUBLIC_ORIGIN, parse_origin
        ),
        app_database_url=_required(
            environ, "BRAIN_APP_DATABASE_URL", parse_database_url
        ),
    )


def _required[T](environ: Mapping[str, str], name: str, parse: Callable[[str], T]) -> T:
    if name not in environ:
        raise SettingsError(f"{name} is not set, see Brain/README.md")
    return _parse(name, environ[name], parse)


def _setting[T](
    environ: Mapping[str, str], name: str, default: T, parse: Callable[[str], T]
) -> T:
    if name not in environ:
        return default
    return _parse(name, environ[name], parse)


def _parse[T](name: str, text: str, parse: Callable[[str], T]) -> T:
    try:
        return parse(text)
    except ValueError as err:
        raise SettingsError(f"{name}: {err}") from None


def parse_core_url(text: str) -> str:
    """A scheme, a host and an optional port, with nothing after it, such as
    `http://127.0.0.1:8080`, so that a path can be appended."""
    if _split(text) is None:
        raise ValueError(
            f"{text!r} is not a URL like http://127.0.0.1:8080: scheme, host and optional port, nothing after it"
        )
    return text


def parse_origin(text: str) -> str:
    """Only the form a browser sends: scheme and host, lowercase, with a port
    only when it is not the scheme's default, and nothing after it. Anything
    else would never match and silently refuse every request it guards. The
    same rule as `PublicOrigin::parse` in `Core/src/origin.rs`."""
    parts = _split(text)
    if (
        parts is None
        or parts.port == DEFAULT_PORTS[parts.scheme]
        or text != text.lower()
    ):
        raise ValueError(
            f"{text!r} is not an origin like https://platform.test: scheme and host, lowercase, no path, no default port"
        )
    return text


def parse_database_url(text: str) -> str:
    """A Postgres URL naming a host and a database, such as
    `postgres://brain_app:<password>@127.0.0.1:5432/brain`. Checked only for
    its shape, since connecting is the pool's job, and never repeated in the
    error, since it holds the password."""
    try:
        parts = urlsplit(text)
        parts.port  # noqa: B018, raises on a port that is not a number
    except ValueError:
        parts = None
    if (
        parts is None
        or parts.scheme not in ("postgres", "postgresql")
        or not parts.hostname
        or not parts.path.removeprefix("/")
    ):
        raise ValueError(
            "not a connection string like postgres://brain_app:<password>@127.0.0.1:5432/brain"
        )
    return text


def _split(text: str) -> SplitResult | None:
    """`text` split, if it is exactly scheme://host[:port]: rebuilt from its
    parts it has to come back byte for byte, which rules out a path, a query,
    a fragment, user info, a trailing colon and surrounding whitespace."""
    try:
        parts = urlsplit(text)
        port = parts.port
    except ValueError:
        return None
    if parts.scheme not in DEFAULT_PORTS or not parts.hostname:
        return None
    host = f"[{parts.hostname}]" if ":" in parts.hostname else parts.hostname
    authority = host if port is None else f"{host}:{port}"
    if text.lower() != f"{parts.scheme}://{authority}":
        return None
    return parts


SETTINGS = load(os.environ)

"""What the brain's own modules raise when a request cannot be done, each
answered by the app with one status, see `brain.edge.answer_errors`. The
modules that raise them know nothing of HTTP."""


class NotFound(Exception):
    """What the request names by its address is not there for this user. A
    row of another user's is not there either: row level security hides it,
    so the two cannot be told apart."""


class Refused(Exception):
    """The request is well formed but breaks a rule, such as an alias of an
    alias. The message says which, and is shown to the caller."""


class Conflict(Exception):
    """The request would make a second of something there may be only one
    of, such as a slug the user already has."""

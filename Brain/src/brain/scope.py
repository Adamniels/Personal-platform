"""Scope, and comparing two: what decides which of two conflicting claims
an answer ranks first, and when neither can be. See "Scope is topic and
entity, never feature" in docs/trust-model.md, and "Conflicting decisions
before consolidation" in docs/event-model.md.

A scope is the set of an item's subjects, each a canonical topic or a named
thing, by id. The empty set is global. More qualifiers make a narrower
claim, so one scope is narrower than another when it holds every qualifier
of the other and more. That is a partial order, not a line: two scopes can
be incomparable, a topic against a named thing, and then neither wins. So
`compare` answers which of four relations holds and never names a winner;
what follows from each is the caller's, see `brain.retrieval.conflicts`.

Pure: no database, no clock. A topic is compared as the topic it resolves
to, so the caller resolves aliases first, through `Scope.of`.
"""

from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from typing import Literal
from uuid import UUID

type Comparison = Literal["narrower", "broader", "equal", "incomparable"]


@dataclass(frozen=True)
class Scope:
    """The canonical topics and the named things a claim is about. A topic
    and a named thing are never the same qualifier, so the two are kept
    apart rather than mixed in one set of ids."""

    topics: frozenset[UUID]
    entities: frozenset[UUID]

    @classmethod
    def of(
        cls,
        topics: Iterable[UUID],
        entities: Iterable[UUID],
        *,
        alias_of: Mapping[UUID, UUID] | None = None,
    ) -> Scope:
        """The scope naming `topics` and `entities`, each topic read as the
        topic `alias_of` maps it to, when it is an alias. An item's subjects
        are read through their aliases already, see `brain.items.Item`."""
        aliases = alias_of or {}
        return cls(
            topics=frozenset(aliases.get(topic, topic) for topic in topics),
            entities=frozenset(entities),
        )

    def holds(self, other: Scope) -> bool:
        """Whether this scope holds every qualifier of `other`."""
        return self.topics >= other.topics and self.entities >= other.entities


def compare(a: Scope, b: Scope) -> Comparison:
    """How `a` stands to `b`: `narrower` when `a` holds every qualifier of
    `b` and more, `broader` for the reverse, `equal`, or `incomparable` when
    neither holds the other. Global, the empty scope, is broader than any
    other."""
    if a == b:
        return "equal"
    if a.holds(b):
        return "narrower"
    if b.holds(a):
        return "broader"
    return "incomparable"

"""Conflicts and related topics: asking about a topic brings the topics under
it, and two items the owner marked as contradicting come back as a conflict,
both kept, ordered and never quietly decided. Through the API on a database
of the test's own, as brain_app, except the scope comparison, which is a
pure function. Why scopes form a partial order is "Scope is topic and
entity, never feature" in `Brain/docs/trust-model.md`; why recency only
orders is "Conflicting decisions before consolidation" in
`Brain/docs/event-model.md`."""

import uuid

from brain.scope import Scope, compare

# The scope comparison.


def test_a_scope_is_narrower_when_it_holds_every_qualifier_of_the_other_and_more() -> (
    None
):
    postgres, indexing, alias = uuid.uuid7(), uuid.uuid7(), uuid.uuid7()
    rollout = uuid.uuid7()
    global_ = Scope.of([], [])
    on_postgres = Scope.of([postgres], [])
    on_both = Scope.of([postgres], [rollout])

    assert compare(on_both, on_postgres) == "narrower"
    assert compare(on_postgres, on_both) == "broader"
    assert compare(on_postgres, global_) == "narrower"
    assert compare(global_, on_postgres) == "broader"
    assert compare(global_, Scope.of([], [])) == "equal"
    assert compare(on_postgres, Scope.of([postgres], [])) == "equal"
    # A topic and a named thing, or two topics, neither holding the other.
    assert compare(on_postgres, Scope.of([], [rollout])) == "incomparable"
    assert compare(on_postgres, Scope.of([indexing], [])) == "incomparable"
    assert compare(Scope.of([postgres, indexing], []), on_both) == "incomparable"
    # Aliases resolved first: an alias of postgres is postgres.
    aliased = Scope.of([alias], [], alias_of={alias: postgres})
    assert compare(aliased, on_postgres) == "equal"
    assert compare(
        Scope.of([alias, postgres], [], alias_of={alias: postgres}), on_postgres
    ) == ("equal")
    assert compare(on_both, aliased) == "narrower"
    # A topic and a named thing are never the same qualifier, whatever ids.
    same = uuid.uuid7()
    assert compare(Scope.of([same], []), Scope.of([], [same])) == "incomparable"

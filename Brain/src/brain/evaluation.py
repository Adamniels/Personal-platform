"""The evaluation: a frozen set of situations, each a question asked of the
fictional person's memory, `brain.seed`, and what must and must not come
back. See "Evaluation exists from early on" in docs/brain-architecture.md
and "Evaluating" in Brain/README.md.

`uv run brain eval` makes a database of its own, loads the seed into it,
asks every case in `eval/cases/` as the seed's person at `seed.ASKED_AT`,
through the functions the `ask` and search routes answer with, writes
`eval/report.md` and drops the database. Every case must pass exactly:
recall and precision 1.0, and its order, conflicts and stale flags as it
writes them. It is a hard gate because everything the brain answers today is
deterministic; when ranking or search by meaning arrive, some cases become
scored against a threshold, and the report is where the scores are tracked.

The tests run the same code, `tests/test_evaluation.py`, so CI needs no step
of its own."""

import asyncio
import json
import os
import sys
import time
from collections.abc import Iterable, Sequence
from collections.abc import Set as AbstractSet
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated, Literal, get_args
from uuid import UUID

import psycopg
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from brain import ask_routes, db, migrate, retrieval, scratch, search_routes, seed

EVAL = Path(__file__).resolve().parents[2] / "eval"
CASES = EVAL / "cases"
REPORT = EVAL / "report.md"

# What a case is about. The tests hold the fewest cases each must have.
Group = Literal[
    "profile",
    "topics",
    "aliases",
    "broader_and_narrower",
    "history",
    "now_and_stale",
    "now_versus_history",
    "replaced_decisions",
    "retired_and_edited",
    "conflicts",
    "search",
    "another_user",
]
GROUPS: tuple[str, ...] = get_args(Group)


# A case's question: one of the `ask` calls or search, by `call`, with the
# fields the route's own question takes, refused as the route refuses them.
class AskProfile(ask_routes.ProfileQuestion):
    call: Literal["profile"]


class AskTopics(ask_routes.TopicsQuestion):
    call: Literal["topics"]


class AskHistory(ask_routes.HistoryQuestion):
    call: Literal["history"]


class AskNow(ask_routes.NowQuestion):
    call: Literal["now"]


class Search(search_routes.SearchQuery):
    call: Literal["search"]


type Question = Annotated[
    AskProfile | AskTopics | AskHistory | AskNow | Search,
    Field(discriminator="call"),
]


class ExpectedConflict(BaseModel):
    """Two items the answer must list as a conflict, either way round, and
    the one it prefers, or null for neither."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    items: tuple[seed.Key, seed.Key]
    prefer: seed.Key | None


class Case(BaseModel):
    """One case file. Items are named by their keys in the seed. A field it
    does not know is refused, so a misspelt expectation is never silently
    not checked."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    # The file's name without .json.
    id: seed.Key
    group: Group
    question: Question
    # Each must come back.
    must_include: list[seed.Key]
    # None may come back. An item in neither list is not judged.
    must_exclude: list[seed.Key]
    # Included items that must come back in this order, not necessarily
    # next to each other, in the order the answer gives its lists.
    order: list[seed.Key] = []
    # When given, exactly the conflicts the answer must list.
    conflicts: list[ExpectedConflict] | None = None
    # Included items and whether each must be flagged stale.
    flags: dict[seed.Key, bool] = {}


class CaseError(Exception):
    """A case file that is not well formed: the message names the file."""


def _problems(case: Case, stem: str, keys: AbstractSet[str]) -> list[str]:
    """What is wrong with `case`, read from the file `stem`.json, against
    the seed's item keys `keys`."""
    problems = []
    if case.id != stem:
        problems.append(f"the id {case.id} is not the file's name, {stem}")
    if not case.must_include and not case.must_exclude:
        problems.append(
            "the case expects nothing: must_include and must_exclude are empty"
        )
    named = [
        *case.must_include,
        *case.must_exclude,
        *case.order,
        *(key for c in case.conflicts or [] for key in c.items),
        *(c.prefer for c in case.conflicts or [] if c.prefer is not None),
        *case.flags,
    ]
    problems += [
        f"{key} is not an item in the seed"
        for key in dict.fromkeys(named)
        if key not in keys
    ]
    for listed in (case.must_include, case.must_exclude, case.order):
        problems += [f"{key} is listed twice" for key in _twice(listed)]
    included = set(case.must_include)
    problems += [
        f"{key} is both included and excluded"
        for key in included & set(case.must_exclude)
    ]
    for where, listed in (
        ("order", case.order),
        ("conflicts", [k for c in case.conflicts or [] for k in c.items]),
        ("flags", list(case.flags)),
    ):
        problems += [
            f"{key} is in {where} but not in must_include"
            for key in dict.fromkeys(listed)
            if key in keys and key not in included
        ]
    for conflict in case.conflicts or []:
        if conflict.prefer is not None and conflict.prefer not in conflict.items:
            problems.append(
                f"a conflict prefers {conflict.prefer}, not one of its pair"
            )
    if isinstance(case.question, Search) and case.conflicts is not None:
        problems.append("a search answer has no conflicts to expect")
    return problems


def _twice(keys: Sequence[str]) -> list[str]:
    seen: set[str] = set()
    again = []
    for key in keys:
        if key in seen:
            again.append(key)
        seen.add(key)
    return again


def load_cases(directory: Path, keys: AbstractSet[str]) -> list[Case]:
    """Every case in `directory`, by file name, checked against the seed's
    item keys `keys`. A file that is not JSON, is not a case, or names what
    the seed does not have is a `CaseError` naming it: never a case that
    passes because nothing in it was checked."""
    cases = []
    for path in sorted(directory.glob("*.json")):
        try:
            case = Case.model_validate(json.loads(path.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError, ValidationError) as err:
            raise CaseError(f"{path.name}: {err}") from err
        problems = _problems(case, path.stem, keys)
        if problems:
            raise CaseError(f"{path.name}: {'; '.join(problems)}")
        cases.append(case)
    return cases


@dataclass(frozen=True)
class Score:
    """Recall: of what must come back, the share that did. Precision: of what
    came back and is judged, in must_include or must_exclude, the share that
    must come back. Each 1.0 when there is nothing to take a share of."""

    recall: float
    precision: float


def score(
    must_include: AbstractSet[str],
    must_exclude: AbstractSet[str],
    returned: Iterable[str],
) -> Score:
    came = set(returned)
    hits = len(came & must_include)
    judged = len(came & (must_include | must_exclude))
    return Score(
        recall=hits / len(must_include) if must_include else 1.0,
        precision=hits / judged if judged else 1.0,
    )


@dataclass(frozen=True)
class Returned:
    """An item an answer gave, by its seed key, and its stale flag."""

    key: str
    stale: bool


@dataclass(frozen=True)
class Result:
    case: Case
    score: Score
    # Every way the answer differs from the case; empty when it passed.
    problems: tuple[str, ...]

    @property
    def passed(self) -> bool:
        return not self.problems


def judge(
    case: Case,
    returned: Sequence[Returned],
    conflicts: Iterable[tuple[frozenset[str], str | None]] | None,
) -> Result:
    """`case` against the answer: the items it gave, in its order, and the
    conflicts it listed, each pair and the one preferred, or None for an
    answer that lists none, a search."""
    first: dict[str, Returned] = {}
    for each in returned:
        first.setdefault(each.key, each)
    keys = list(first)
    include, exclude = set(case.must_include), set(case.must_exclude)
    scored = score(include, exclude, keys)
    problems = []
    if missing := [key for key in case.must_include if key not in first]:
        problems.append(f"missing {', '.join(missing)}")
    if wrong := [key for key in keys if key in exclude]:
        problems.append(f"returned what must not come back: {', '.join(wrong)}")
    in_order = [key for key in keys if key in case.order]
    if in_order != [key for key in case.order if key in first]:
        problems.append(f"order {', '.join(case.order)}, came {', '.join(in_order)}")
    if case.conflicts is not None:
        expected = {(frozenset(c.items), c.prefer) for c in case.conflicts}
        listed = set(conflicts or [])
        if listed != expected:
            problems.append(
                f"conflicts {_conflicts(expected)}, came {_conflicts(listed)}"
            )
    for key, stale in case.flags.items():
        if key in first and first[key].stale != stale:
            problems.append(f"{key} stale {first[key].stale}, should be {stale}")
    return Result(case=case, score=scored, problems=tuple(problems))


def _conflicts(found: Iterable[tuple[frozenset[str], str | None]]) -> str:
    shown = sorted(f"{'+'.join(sorted(pair))} prefer {p}" for pair, p in found)
    return "[" + "; ".join(shown) + "]"


type _Answered = tuple[list[ask_routes.AnswerItem], list[ask_routes.Conflict] | None]


def _known(
    answers: Iterable[ask_routes.AnswerItem | None],
) -> list[ask_routes.AnswerItem]:
    return [a for a in answers if a is not None]


async def ask(conn: retrieval.Connection, question: Question) -> _Answered:
    """The answer to `question` at `seed.ASKED_AT`, from the function its
    route answers with: every item in it, in the order the answer gives its
    lists, and its conflicts."""
    now = seed.ASKED_AT
    match question:
        case AskProfile():
            profile = await ask_routes.answer_profile(conn, question, now=now)
            grouped = [a for group in profile.categories for a in group.items]
            return [*grouped, *profile.rules], profile.conflicts
        case AskTopics():
            topics = await ask_routes.answer_topics(conn, question, now=now)
            found = []
            for topic in topics.topics:
                found += _known(
                    [topic.level, topic.focus, *topic.misconceptions, *topic.history]
                )
                for below in topic.narrower:
                    found += _known(
                        [
                            below.level,
                            below.focus,
                            *below.misconceptions,
                            *below.history,
                        ]
                    )
            return found, topics.conflicts
        case AskHistory():
            history = await ask_routes.answer_history(conn, question, now=now)
            under = [a for below in history.narrower for a in below.items]
            return [*history.items, *under], history.conflicts
        case AskNow():
            current = await ask_routes.answer_now(conn, question, now=now)
            return list(current.items), current.conflicts
        case Search():
            return list(await search_routes.answer(conn, question, now=now)), None


async def run_cases(
    pool: db.Pool, loaded: seed.Loaded, cases: Sequence[Case]
) -> list[Result]:
    """Every case asked as the seed's person, each in a transaction of its
    own, as a request would be."""
    key_of: dict[UUID, str] = {item: key for key, item in loaded.items.items()}
    results = []
    for case in cases:
        async with db.transaction(pool, seed.account_id(seed.ASKED_AS)) as conn:
            found, conflicts = await ask(conn, case.question)
        # An id the seed did not make cannot be in any case: it is shown as
        # it is, so a precision below 1.0 can be traced to it.
        returned = [Returned(key_of.get(a.id, str(a.id)), a.stale) for a in found]
        listed = (
            [
                (
                    frozenset(key_of.get(i, str(i)) for i in c.items),
                    key_of.get(c.prefer, str(c.prefer)) if c.prefer else None,
                )
                for c in conflicts
            ]
            if conflicts is not None
            else None
        )
        results.append(judge(case, returned, listed))
    return results


async def _acting_as_app(pool: db.Pool) -> None:
    """Refuses to run unless the pool's connections are brain_app, so row
    level security holds as it does for the running brain: a run as the
    superuser would let another user's memory through unseen."""
    async with db.transaction(pool, seed.account_id(seed.ASKED_AS)) as conn:
        cursor = await conn.execute("SELECT current_user::text")
        row = await cursor.fetchone()
    if row is None or row[0] != "brain_app":
        raise RuntimeError(f"the evaluation must run as brain_app, not {row}")


async def evaluate(admin_url: str) -> list[Result]:
    """Every case, by file name, on a database made for this run as the
    superuser `admin_url` and dropped after it: migrated as brain_owner,
    loaded and asked as brain_app. The seed and the cases are read first, so
    a malformed one stops the run before anything is made."""
    person = seed.read(seed.PERSON)
    cases = load_cases(CASES, person.item_keys)
    name = f"brain_eval_{int(time.time() * 1000)}_{os.getpid()}"
    scratch.create(admin_url, name)
    try:
        with scratch.owner_connection(admin_url, name) as conn:
            migrate.run(conn, migrate.MIGRATIONS)
        pool = await db.open_pool(scratch.as_app(admin_url, name))
        try:
            await _acting_as_app(pool)
            loaded = await seed.load(pool, person)
            return await run_cases(pool, loaded, cases)
        finally:
            await pool.close()
    finally:
        scratch.drop(admin_url, name)


_ASKED_AT = seed.ASKED_AT.isoformat().replace("+00:00", "Z")


def report(results: Sequence[Result]) -> str:
    """The report the runner writes, `eval/report.md`: a table of every case,
    by file name, then how many pass. It changes only when a case's answer
    does, so a change in behaviour shows as a diff in review."""
    passed = sum(result.passed for result in results)
    lines = [
        "# Evaluation report",
        "",
        (
            'Written by `uv run brain eval`, see "Evaluating" in Brain/README.md:'
            " every case in `eval/cases/` asked of the seed, `eval/person.json`,"
            f" at {_ASKED_AT}. Do not edit it by hand."
        ),
        "",
        "| Case | Group | Recall | Precision | Pass |",
        "| --- | --- | --- | --- | --- |",
        *(
            f"| {r.case.id} | {r.case.group} | {r.score.recall:.2f}"
            f" | {r.score.precision:.2f} | {'yes' if r.passed else 'no'} |"
            for r in results
        ),
        "",
        f"{len(results)} cases, {passed} pass.",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    """`brain eval`: runs every case, writes the report, prints each failing
    case and what differed, and exits non zero on any failure."""
    url = os.environ.get("BRAIN_TEST_ADMIN_DATABASE_URL")
    if not url:
        print(
            "BRAIN_TEST_ADMIN_DATABASE_URL is not set, see Evaluating in"
            " Brain/README.md",
            file=sys.stderr,
        )
        return 1
    try:
        results = asyncio.run(evaluate(url))
    except (seed.SeedError, CaseError) as err:
        print(f"evaluation refused: {err}", file=sys.stderr)
        return 1
    except psycopg.Error as err:
        # psycopg names the host, the role and the reason, never the password.
        print(f"evaluation failed: {err}", file=sys.stderr)
        return 1
    REPORT.write_text(report(results))
    for result in results:
        if not result.passed:
            print(f"FAIL {result.case.id}: {'; '.join(result.problems)}")
    passed = sum(result.passed for result in results)
    print(f"{passed} of {len(results)} cases pass, written to eval/report.md")
    return 0 if passed == len(results) else 1

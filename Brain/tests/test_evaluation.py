"""The evaluation: the fictional person's memory, `eval/person.json`, loaded
into a database of its own, and every case in `eval/cases/` asked of it at
the seed's fixed time, each passing exactly. See "Evaluating" in
Brain/README.md and "Evaluation exists from early on" in
docs/brain-architecture.md.

The cases run through `brain.evaluation.evaluate`, the code `uv run brain
eval` runs, so CI needs no step of its own for them."""

import asyncio
import json
import os
from collections import Counter
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import pytest

from brain import evaluation, seed
from tests.harness import admin_url

# The fewest cases each group holds, as the slice settled them.
MINIMUMS = {
    "profile": 2,
    "topics": 4,
    "aliases": 2,
    "broader_and_narrower": 3,
    "history": 3,
    "now_and_stale": 3,
    "now_versus_history": 2,
    "replaced_decisions": 2,
    "retired_and_edited": 3,
    "conflicts": 3,
    "search": 4,
    "another_user": 2,
}


@pytest.fixture(scope="module")
def evaluated() -> Iterator[list[evaluation.Result]]:
    """Every case, run once for this module, on a database the run makes
    and drops itself."""
    yield asyncio.run(evaluation.evaluate(admin_url()))


def test_every_evaluation_case_passes(evaluated: list[evaluation.Result]) -> None:
    failed = {
        result.case.id: list(result.problems)
        for result in evaluated
        if not result.passed
    }
    assert failed == {}, f"cases that failed: {json.dumps(failed, indent=2)}"
    assert len(evaluated) == len(list(evaluation.CASES.glob("*.json")))


def test_the_committed_report_matches_a_fresh_run(
    evaluated: list[evaluation.Result],
) -> None:
    fresh = evaluation.report(evaluated)
    if os.environ.get("UPDATE_EVAL_REPORT") == "1":
        evaluation.REPORT.write_text(fresh)
    assert evaluation.REPORT.read_text() == fresh, (
        "eval/report.md is not what the runner writes now: run `uv run brain eval`,"
        " or the tests with UPDATE_EVAL_REPORT=1, and commit the diff"
    )


def a_case(**changes: Any) -> dict[str, Any]:
    """A well formed case, with `changes` applied; a value of None removes
    the key."""
    case: dict[str, Any] = {
        "id": "made-up",
        "group": "profile",
        "question": {"call": "profile", "mode": "now"},
        "must_include": ["p-name"],
        "must_exclude": ["p-cello"],
    }
    for key, value in changes.items():
        if value is None:
            case.pop(key, None)
        else:
            case[key] = value
    return case


def loaded(tmp_path: Path, case: dict[str, Any]) -> list[evaluation.Case]:
    (tmp_path / f"{case.get('id', 'made-up')}.json").write_text(json.dumps(case))
    return evaluation.load_cases(tmp_path, seed.read(seed.PERSON).item_keys)


def test_a_well_formed_case_loads(tmp_path: Path) -> None:
    [case] = loaded(tmp_path, a_case())
    assert case.id == "made-up"


@pytest.mark.parametrize(
    ("changes", "reason"),
    [
        pytest.param({"musts_include": ["p-name"]}, "musts_include", id="unknown key"),
        pytest.param(
            {"question": {"call": "profile", "modes": "now"}},
            "modes",
            id="unknown key in the question",
        ),
        pytest.param({"must_exclude": None}, "must_exclude", id="missing expectation"),
        pytest.param(
            {"must_include": [], "must_exclude": []},
            "expects nothing",
            id="no expectation at all",
        ),
        pytest.param(
            {"must_include": ["p-nobody"]}, "p-nobody", id="an id not in the seed"
        ),
        pytest.param(
            {"order": ["p-name", "p-student"]},
            "p-student",
            id="order naming an item not included",
        ),
        pytest.param(
            {"flags": {"p-ghost": True}}, "p-ghost", id="flag not in the seed"
        ),
        pytest.param({"group": "astrology"}, "group", id="unknown group"),
        pytest.param({"id": "another-name"}, "file", id="id not the file's name"),
        pytest.param(
            {"question": {"call": "search", "q": "x"}, "conflicts": []},
            "search",
            id="conflicts on a search",
        ),
    ],
)
def test_a_case_file_that_is_malformed_fails_loudly(
    tmp_path: Path, changes: dict[str, Any], reason: str
) -> None:
    case = a_case(**changes)
    # The file keeps the well formed name, so a wrong id is caught.
    (tmp_path / "made-up.json").write_text(json.dumps(case))
    with pytest.raises(evaluation.CaseError, match="made-up.json") as raised:
        evaluation.load_cases(tmp_path, seed.read(seed.PERSON).item_keys)
    assert reason in str(raised.value)


def test_a_case_file_that_is_not_json_fails_loudly(tmp_path: Path) -> None:
    (tmp_path / "broken.json").write_text("{not json")
    with pytest.raises(evaluation.CaseError, match="broken.json"):
        evaluation.load_cases(tmp_path, seed.read(seed.PERSON).item_keys)


def test_the_runner_scores_recall_and_precision() -> None:
    # Half of what must come back did; of the judged items that came back,
    # two of three were wanted. z is in neither set, so it is not judged.
    scored = evaluation.score({"a", "b", "c", "d"}, {"x", "y"}, ["a", "b", "x", "z"])
    assert scored.recall == 0.5
    assert scored.precision == pytest.approx(2 / 3)

    everything = evaluation.score({"a", "b"}, {"x"}, ["b", "a"])
    assert (everything.recall, everything.precision) == (1.0, 1.0)

    # Nothing wanted and nothing excluded came back: nothing is missed, and
    # nothing judged was wrong.
    assert evaluation.score(set(), {"x"}, ["z"]) == evaluation.Score(1.0, 1.0)
    assert evaluation.score({"a"}, {"x"}, []) == evaluation.Score(0.0, 1.0)
    assert evaluation.score({"a"}, {"x"}, ["x"]) == evaluation.Score(0.0, 0.0)


def test_the_report_shows_what_each_answer_gave() -> None:
    # Three answers that all pass the same case, with the same recall and
    # precision: one gives an item the case does not judge, one flags an item
    # stale. Each must still change the report, or a change in behaviour that
    # keeps every case passing would leave it byte for byte the same.
    case = evaluation.Case.model_validate(a_case())
    plain = evaluation.judge(case, [evaluation.Returned("p-name", False)], [])
    extra = evaluation.judge(
        case,
        [evaluation.Returned("p-name", False), evaluation.Returned("p-job", False)],
        [],
    )
    stale = evaluation.judge(case, [evaluation.Returned("p-name", True)], [])
    assert plain.passed and extra.passed and stale.passed
    assert plain.score == extra.score == stale.score

    reports = {evaluation.report([r]) for r in (plain, extra, stale)}
    assert len(reports) == 3
    assert "p-job" in evaluation.report([extra])


def test_there_are_at_least_thirty_cases_and_every_group_is_covered() -> None:
    cases = evaluation.load_cases(evaluation.CASES, seed.read(seed.PERSON).item_keys)
    assert len(cases) >= 30
    counted = Counter[str](case.group for case in cases)
    short = {
        group: (counted[group], least)
        for group, least in MINIMUMS.items()
        if counted[group] < least
    }
    assert short == {}, "groups with fewer cases than settled: (have, need)"
    assert set(counted) <= set(MINIMUMS)

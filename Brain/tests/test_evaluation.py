"""The evaluation: the fictional person's memory, `eval/person.json`, loaded
into a database of its own, and every case in `eval/cases/` asked of it at
the seed's fixed time, each passing exactly. See "Evaluating" in
Brain/README.md and "Evaluation exists from early on" in
docs/brain-architecture.md.

The cases run through `brain.evaluation.evaluate`, the code `uv run brain
eval` runs, so CI needs no step of its own for them."""

import json
from pathlib import Path
from typing import Any

import pytest

from brain import evaluation, seed


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

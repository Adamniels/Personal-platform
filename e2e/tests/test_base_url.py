"""The suite is not tied to one copy of the platform: the address is the one
setting `base_url`, so the same tests judge the dev stack and the hosted copy,
see README.md."""

from conftest import ROOT

# Built from two parts, so this file does not contain what it looks for.
DEV_HOST = "platform" + ".test"


def test_no_test_spells_out_the_address():
    files = [ROOT / "conftest.py"] + [
        path
        for path in (ROOT / "tests").rglob("*")
        if path.is_file() and "__pycache__" not in path.parts
    ]
    spelled_out = [
        str(path.relative_to(ROOT))
        for path in files
        if DEV_HOST in path.read_text(errors="replace")
    ]
    assert spelled_out == []

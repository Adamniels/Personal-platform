"""The brain's HTTP schema, pinned by `contracts/brain/openapi.json`, so a
change to what the brain serves shows up as a diff to review rather than as a
caller failing at runtime. See contracts/README.md."""

import json
import os
from pathlib import Path

from brain.app import create_app
from brain.settings import load
from tests.test_settings import DATABASE

SCHEMA = Path(__file__).resolve().parents[2] / "contracts/brain/openapi.json"


def canonical(document: object) -> str:
    """Keys sorted, two space indent, a trailing newline."""
    return json.dumps(document, sort_keys=True, indent=2) + "\n"


def test_the_committed_schema_matches_the_app() -> None:
    generated = canonical(create_app(load(DATABASE)).openapi())

    # Regenerated on purpose, and its diff reviewed, like the token fixture.
    if os.environ.get("UPDATE_CONTRACTS") == "1":
        SCHEMA.parent.mkdir(exist_ok=True)
        SCHEMA.write_text(generated)
        return

    assert SCHEMA.exists(), f"{SCHEMA} is missing, write it with UPDATE_CONTRACTS=1"
    assert SCHEMA.read_text() == generated, (
        "the brain's schema changed, regenerate contracts/brain/openapi.json "
        "with UPDATE_CONTRACTS=1 and review the diff"
    )

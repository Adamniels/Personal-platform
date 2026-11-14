"""The suite is not tied to one copy of the platform: the address is the one
setting `base_url`, and the core's command can be pointed at a container. So
the same tests judge the dev stack and the hosted copy, see README.md."""

from conftest import CORE, REPOSITORY, ROOT, core_command

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


def test_the_core_command_comes_from_the_environment():
    assert core_command(["invite"], {}) == (
        ["cargo", "run", "-q", "--", "invite"],
        CORE,
    )

    command = "docker compose -p hostedcheck --env-file '/tmp/a dir/env' exec -T core platform_core"
    assert core_command(["reset", "a@example.com"], {"E2E_CORE_COMMAND": command}) == (
        [
            "docker",
            "compose",
            "-p",
            "hostedcheck",
            "--env-file",
            "/tmp/a dir/env",
            "exec",
            "-T",
            "core",
            "platform_core",
            "reset",
            "a@example.com",
        ],
        REPOSITORY,
    )

    # Set but empty is the same as unset, rather than running the arguments
    # alone as a command.
    assert core_command(["invite"], {"E2E_CORE_COMMAND": ""}) == (
        ["cargo", "run", "-q", "--", "invite"],
        CORE,
    )

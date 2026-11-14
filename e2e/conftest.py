"""Fixtures shared by the acceptance tests. See README.md."""

import base64
import json
import os
import re
import shlex
import subprocess
import uuid
from collections.abc import Callable, Mapping, Sequence
from pathlib import Path

import pytest
from playwright.sync_api import Page, expect

ROOT = Path(__file__).parent
REPOSITORY = ROOT.parent
CORE = REPOSITORY / "Core"
SCREENSHOTS = ROOT / "screenshots"
COMMAND_TIMEOUT_SECONDS = 600

VIEWPORTS = {
    "desktop": {"width": 1280, "height": 800},
    "phone": {"width": 390, "height": 844},
}


@pytest.fixture(scope="session")
def browser_context_args(browser_context_args):
    # Caddy's local CA is trusted by the laptop's keychain but not by CI, so
    # the browser ignores certificate errors rather than assuming either.
    return {**browser_context_args, "ignore_https_errors": True}


def core_command(
    args: Sequence[str], environ: Mapping[str, str]
) -> tuple[list[str], Path]:
    """The command line that runs one of the core's commands, and the folder
    it runs from. E2E_CORE_COMMAND, when set, is the command to run instead
    of the dev core's, split as a shell would and run from the repository
    root with the arguments appended, such as a docker compose exec into the
    hosted copy's core. Set but empty counts as unset."""
    configured = environ.get("E2E_CORE_COMMAND", "")
    if not configured.strip():
        return ["cargo", "run", "-q", "--", *args], CORE
    return [*shlex.split(configured), *args], REPOSITORY


def _core_command(*args: str) -> str:
    """Runs one of the core's commands and returns its stdout, stripped. A
    failure or a hang fails the test, not the whole run."""
    argv, cwd = core_command(args, os.environ)
    command = shlex.join(argv)
    # The first call may compile the core, so the timeout is generous. It is
    # there so a hung build fails the test instead of the whole CI job.
    try:
        result = subprocess.run(
            argv,
            cwd=cwd,
            capture_output=True,
            text=True,
            check=False,
            timeout=COMMAND_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        pytest.fail(
            f"{command} did not finish in {COMMAND_TIMEOUT_SECONDS}s",
            pytrace=False,
        )
    if result.returncode != 0:
        tail = "\n".join(result.stderr.splitlines()[-20:])
        pytest.fail(
            f"{command} failed, last 20 lines of stderr:\n{tail}",
            pytrace=False,
        )
    return result.stdout.strip()


@pytest.fixture
def invite_code() -> str:
    """A fresh single use invite code, minted by the core's own command."""
    return _core_command("invite")


@pytest.fixture
def another_invite_code() -> str:
    """A second fresh invite code, for a test that registers two accounts."""
    return _core_command("invite")


@pytest.fixture
def reset_token() -> Callable[[str], str]:
    """A function that mints a reset token for the account with an email, by
    the core's own command, as the owner would. A function, since the account
    usually exists only once the test has registered it."""
    return lambda email: _core_command("reset", email)


@pytest.fixture(params=list(VIEWPORTS), ids=lambda name: name)
def viewport(request, page) -> str:
    """Runs the test once per viewport and sizes the page to it."""
    page.set_viewport_size(VIEWPORTS[request.param])
    return request.param


def _viewport_name(page) -> str:
    size = page.viewport_size
    for name, dimensions in VIEWPORTS.items():
        if size == dimensions:
            return name
    raise ValueError(f"page viewport {size} is not one of {list(VIEWPORTS)}")


def capture(page, name: str) -> Path:
    """Full page screenshot to screenshots/<name>-<viewport>.png."""
    SCREENSHOTS.mkdir(exist_ok=True)
    path = SCREENSHOTS / f"{name}-{_viewport_name(page)}.png"
    page.screenshot(path=str(path), full_page=True)
    return path


def register(page: Page, invite_code: str) -> tuple[str, str]:
    """Registers a fresh account with the code and returns its email and
    password. Ends on the login page, not logged in."""
    email = f"e2e-{uuid.uuid4()}@example.com"
    password = uuid.uuid4().hex[:20]
    page.goto("/register")
    page.fill("[name=code]", invite_code)
    page.fill("[name=email]", email)
    page.fill("[name=password]", password)
    page.click("button[type=submit]")
    expect(page).to_have_url(re.compile(r"/login$"))
    return email, password


def log_in(page: Page, email: str, password: str) -> None:
    """Fills and submits the login form the page is on. Where it ends depends
    on the return path the login page carries."""
    page.fill("[name=email]", email)
    page.fill("[name=password]", password)
    page.click("button[type=submit]")


def register_and_log_in(page: Page, invite_code: str, base_url: str) -> str:
    """Registers a fresh account with the code, logs in, and returns its email.

    Ends on the front page. test_registration.py walks the same steps as the
    thing under test; this is for tests that only need to be logged in.
    """
    email, password = register(page, invite_code)
    log_in(page, email, password)
    expect(page).to_have_url(f"{base_url}/")
    return email


def account_in_access_cookie(page: Page) -> str:
    """The `sub` of the access token the core set on this browser, read
    without verifying it: the reference for who this browser is, taken from
    the core rather than from the library a service verifies with."""
    (token,) = [
        cookie["value"]
        for cookie in page.context.cookies()
        if cookie["name"] == "__Host-access"
    ]
    payload = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))[
        "sub"
    ]

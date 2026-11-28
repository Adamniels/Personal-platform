"""Fixtures shared by the acceptance tests. See README.md."""

import base64
import fcntl
import json
import os
import re
import shlex
import subprocess
import tempfile
import uuid
from collections.abc import Callable, Iterator, Mapping, Sequence
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

ROOT = Path(__file__).parent
REPOSITORY = ROOT.parent
CORE = REPOSITORY / "Core"
SCREENSHOTS = ROOT / "screenshots"
COMMAND_TIMEOUT_SECONDS = 600

VIEWPORTS = {
    "desktop": {"width": 1280, "height": 800},
    "phone": {"width": 390, "height": 844},
}
# A wide desktop window, which a test sets itself where a criterion names it.
# capture() names it too, but the viewport fixture never runs a test at it.
WIDE = {"width": 1920, "height": 1080}
CAPTURED = {**VIEWPORTS, "wide": WIDE}


# One lock for every test marked serial, whichever worker runs it.
SERIAL_LOCK = Path(tempfile.gettempdir()) / "platform-e2e-serial.lock"


@pytest.fixture(autouse=True)
def _serial(request: pytest.FixtureRequest) -> Iterator[None]:
    """Holds an exclusive lock on SERIAL_LOCK for the whole of a test marked
    serial, its fixtures included, so no two of them run at once on any of
    the workers. Autouse fixtures are set up first and torn down last, so a
    fixture that restores shared state, such as backup_status, has restored
    it before the next one starts. A lock rather than xdist_group, which
    pytest-xdist 3.8.0 cannot apply under pytest 9: it renames the node id
    in a field pytest 9 no longer reads."""
    if request.node.get_closest_marker("serial") is None:
        yield
        return
    with SERIAL_LOCK.open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


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


def _run(argv: Sequence[str], cwd: Path, stdin: str | None = None) -> str:
    """Runs a command and returns its stdout as it is. A failure or a hang
    fails the test, not the whole run."""
    command = shlex.join(argv)
    # The first call of the core's command may compile the core, so the
    # timeout is generous. It is there so a hung build fails the test instead
    # of the whole CI job.
    try:
        result = subprocess.run(
            argv,
            cwd=cwd,
            input=stdin,
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
    return result.stdout


def _core_command(*args: str) -> str:
    """Runs one of the core's commands and returns its stdout, stripped."""
    argv, cwd = core_command(args, os.environ)
    return _run(argv, cwd).strip()


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


def backup_status_commands(
    environ: Mapping[str, str],
) -> tuple[list[str], list[str]]:
    """The commands that read the hosted copy's backup status file to stdout
    and write it from stdin, E2E_BACKUP_STATUS_READ and
    E2E_BACKUP_STATUS_WRITE, each split as a shell would and run from the
    repository root. infra/hosted/check.sh sets both; without them a test
    that needs the file fails saying so. Set but empty counts as unset."""
    commands = []
    for name in ("E2E_BACKUP_STATUS_READ", "E2E_BACKUP_STATUS_WRITE"):
        configured = environ.get(name, "")
        if not configured.strip():
            pytest.fail(
                f"{name} is not set: this test needs the hosted copy's backup "
                "status file, which infra/hosted/check.sh gives it; see "
                "e2e/README.md",
                pytrace=False,
            )
        commands.append(shlex.split(configured))
    read, write = commands
    return read, write


@pytest.fixture
def backup_status() -> Iterator[Callable[[dict], None]]:
    """A function that replaces the hosted copy's backup status file with
    the JSON of the state a test asks for, such as a last success two days
    old. The real file is read first and written back after the test."""
    read, write = backup_status_commands(os.environ)
    original = _run(read, REPOSITORY)

    def replace(status: dict) -> None:
        _run(write, REPOSITORY, stdin=json.dumps(status))

    yield replace
    _run(write, REPOSITORY, stdin=original)


@pytest.fixture(params=list(VIEWPORTS), ids=lambda name: name)
def viewport(request, page) -> str:
    """Runs the test once per viewport and sizes the page to it."""
    page.set_viewport_size(VIEWPORTS[request.param])
    return request.param


def _viewport_name(page) -> str:
    size = page.viewport_size
    for name, dimensions in CAPTURED.items():
        if size == dimensions:
            return name
    raise ValueError(f"page viewport {size} is not one of {list(CAPTURED)}")


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


def sections(page: Page, label: str) -> Locator:
    """The control that picks among a page's tabs named `label`: the tab list,
    or, on phone when there are more than five tabs, the select `Tabs` shows
    in its place. Only the one shown matches, since a role locator skips what
    is hidden. See "Tabs" in design/ui/README.md."""
    return page.get_by_role("tablist", name=label, exact=True).or_(
        page.get_by_role("combobox", name=label, exact=True)
    )


def open_section(page: Page, label: str, name: str) -> None:
    """Opens the section `name` among the tabs named `label`: clicks its tab
    when the tab list is shown, and chooses its option when the select is, so
    a test passes at both viewports."""
    expect(sections(page, label)).to_be_visible()
    tabs = page.get_by_role("tablist", name=label, exact=True)
    if tabs.is_visible():
        tabs.get_by_role("tab", name=name, exact=True).click()
    else:
        page.get_by_role("combobox", name=label, exact=True).select_option(label=name)


@pytest.fixture
def devflow_world(page: Page, base_url: str) -> Callable[[dict], None]:
    """A function that replaces Dev flow's fake code host's world for the
    account the page is logged in as, through the page's own session, so it
    lands in this test's account and no other. The world is a dictionary,
    usually built with devflow_worlds.py. Call it after logging in."""

    def stage(world: dict) -> None:
        response = page.request.put(
            "/devflow/api/fake/world",
            data=world,
            # A change, so it carries the platform's origin, as the page's own
            # requests do.
            headers={"Origin": base_url},
        )
        assert response.status == 204, (
            f"staging the world answered {response.status}: {response.text()}"
        )

    return stage


def link_github(page: Page, base_url: str) -> None:
    """Links the logged in account's GitHub from Dev flow's Projects tab, which
    the page must be on, through the fake's sign in page and back."""
    page.get_by_role("article", name="GitHub").get_by_role(
        "button", name="Link GitHub"
    ).click()
    expect(page).to_have_url(re.compile(r"/devflow/fake-github/authorize\?"))
    expect(page.get_by_role("heading", name="Fake GitHub", level=1)).to_be_visible()
    expect(page.get_by_text("@octo-owner")).to_be_visible()
    page.get_by_role("button", name="Authorize").click()
    expect(page).to_have_url(f"{base_url}/devflow/projects")


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


def fake_check(
    page: Page,
    base_url: str,
    project: str,
    exit_code: int,
    output: str,
    pictures: dict[str, bytes] | None = None,
) -> dict:
    """The fake worker takes the project's next job, the check of its setup,
    and ends it as a check's worker does: the check ran with `exit_code` and
    `output`, and, with `pictures`, the project's address taken at both sizes.
    What it answered."""
    result: dict = {
        "outcome": "succeeded",
        "report": None,
        "failure": None,
        "check": {"exit_code": exit_code, "output": output},
        "auth_method": None,
        "usage": {},
        "duration_ms": 61000,
        "stderr": "",
    }
    if pictures is not None:
        result["screenshots"] = {"names": list(pictures), "note": None}
        result["pictures"] = [
            {"name": name, "png": base64.b64encode(content).decode()}
            for name, content in pictures.items()
        ]
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "result": result, "for_check": True},
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def registered(page: Page, base_url: str, project: str) -> None:
    """The fake worker passes the check of the project's setup, so the project
    is registered and its page shows its cards rather than its setup steps.
    See "Setting up a project" in devflow/README.md. The project is read first,
    since a project whose default branch was not watched lately hands out no
    job."""
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    passed = fake_check(page, base_url, project, 0, "Tests  1 passed")
    assert passed["kind"] == "check", passed

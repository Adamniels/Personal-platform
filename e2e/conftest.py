"""Fixtures shared by the acceptance tests. See README.md."""

import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).parent
CORE = ROOT.parent / "Core"
SCREENSHOTS = ROOT / "screenshots"
INVITE_TIMEOUT_SECONDS = 600

VIEWPORTS = {
    "desktop": {"width": 1280, "height": 800},
    "phone": {"width": 390, "height": 844},
}


@pytest.fixture(scope="session")
def browser_context_args(browser_context_args):
    # Caddy's local CA is trusted by the laptop's keychain but not by CI, so
    # the browser ignores certificate errors rather than assuming either.
    return {**browser_context_args, "ignore_https_errors": True}


@pytest.fixture
def invite_code() -> str:
    """A fresh single use invite code, minted by the core's own command."""
    # The first call may compile the core, so the timeout is generous. It is
    # there so a hung build fails the test instead of the whole CI job.
    try:
        result = subprocess.run(
            ["cargo", "run", "-q", "--", "invite"],
            cwd=CORE,
            capture_output=True,
            text=True,
            check=False,
            timeout=INVITE_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        pytest.fail(
            f"cargo run -- invite did not finish in {INVITE_TIMEOUT_SECONDS}s",
            pytrace=False,
        )
    if result.returncode != 0:
        tail = "\n".join(result.stderr.splitlines()[-20:])
        pytest.fail(
            f"cargo run -- invite failed, last 20 lines of stderr:\n{tail}",
            pytrace=False,
        )
    return result.stdout.strip()


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

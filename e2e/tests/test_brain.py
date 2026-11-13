"""The brain, a service rather than a feature, reached through the same proxy
under its own prefix. See Brain/README.md."""

import base64
import json

from playwright.sync_api import Browser, Page

from conftest import register_and_log_in


def test_brain_health_through_the_proxy(page: Page):
    # No login: the health endpoint is public. The body is the brain's, so a
    # 200 from the core's fallback route would not pass.
    health = page.request.get("/brain/api/health")

    assert health.status == 200
    assert health.json() == {"status": "ok"}


def account_in_access_cookie(page: Page) -> str:
    """The `sub` of the access token the core set on this browser, read
    without verifying it: the reference for who this browser is, taken from
    the core rather than from the library the brain verifies with."""
    (token,) = [
        cookie["value"]
        for cookie in page.context.cookies()
        if cookie["name"] == "__Host-access"
    ]
    payload = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))[
        "sub"
    ]


def test_brain_knows_who_i_am(
    page: Page, browser: Browser, base_url: str, invite_code: str
):
    register_and_log_in(page, invite_code)

    whoami = page.request.get("/brain/api/whoami")

    assert whoami.status == 200
    assert whoami.json() == {"account_id": account_in_access_cookie(page)}

    # A browser with no session: 401 with an empty body, never a redirect.
    stranger = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        refused = stranger.request.get("/brain/api/whoami", max_redirects=0)
        assert refused.status == 401
        assert refused.body() == b""
    finally:
        stranger.close()

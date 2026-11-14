"""Browsing on after the access token is gone: the deciding endpoint swaps the
refresh token for a new pair and sends the browser back. See "The refresh
endpoint is the one place that decides between refreshing and logging in" and
"Refresh is always a POST" in Core/docs/core-architecture.md.

A browser drops the access cookie when its Max-Age runs out, so deleting it is
the same request the browser makes after thirty minutes, without the wait.
"""

import pytest
from playwright.sync_api import Browser, BrowserContext, Page, expect

from conftest import VIEWPORTS, capture, register_and_log_in

ACCESS = "__Host-access"
REFRESH = "__Secure-refresh"


def _cookie(context: BrowserContext, name: str) -> str | None:
    for cookie in context.cookies():
        if cookie["name"] == name:
            return cookie["value"]
    return None


def _expire_access_token(context: BrowserContext) -> str:
    """Deletes the access cookie, as expiry does, and returns the refresh token."""
    refresh = _cookie(context, REFRESH)
    assert refresh, "logging in set no refresh cookie"
    context.clear_cookies(name=ACCESS)
    assert _cookie(context, ACCESS) is None
    return refresh


def test_browsing_continues_after_the_access_token_is_gone(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    refresh = _expire_access_token(page.context)

    page.goto("/?a=1")

    expect(page).to_have_url(f"{base_url}/?a=1")
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()
    assert _cookie(page.context, REFRESH) not in (None, refresh), (
        "the refresh token did not rotate"
    )
    assert _cookie(page.context, ACCESS), "no access cookie after the refresh"


@pytest.mark.parametrize("viewport_name", list(VIEWPORTS))
def test_refresh_works_without_javascript(
    browser: Browser, base_url: str, invite_code: str, viewport_name: str
):
    context = browser.new_context(
        java_script_enabled=False,
        ignore_https_errors=True,
        base_url=base_url,
        viewport=VIEWPORTS[viewport_name],
    )
    try:
        page = context.new_page()
        register_and_log_in(page, invite_code, base_url)
        refresh = _expire_access_token(context)

        page.goto("/?a=1")

        # Without scripts the page stays put until the button is pressed.
        expect(page).to_have_url(f"{base_url}/auth/refresh?return_to=%2F%3Fa%3D1")
        button = page.get_by_role("button", name="Continue")
        expect(button).to_be_visible()
        capture(page, "refresh-no-script")

        button.click()

        expect(page).to_have_url(f"{base_url}/?a=1")
        expect(page.get_by_role("heading", name="Platform")).to_be_visible()
        assert _cookie(context, REFRESH) not in (None, refresh), (
            "the refresh token did not rotate"
        )
        assert _cookie(context, ACCESS), "no access cookie after the refresh"
    finally:
        context.close()

"""Logging out from the nav: a link to a page whose button posts the logout,
which revokes the session family and clears both cookies. See "The session
lives in two cookies" in Core/docs/core-architecture.md and "Navigation" in
High-level/docs/platform-architecture.md."""

import re

from playwright.sync_api import BrowserContext, Page, expect

from conftest import capture, register_and_log_in

ACCESS = "__Host-access"
REFRESH = "__Secure-refresh"


def _cookie_names(context: BrowserContext) -> set[str]:
    return {cookie["name"] for cookie in context.cookies()}


def test_log_out_from_the_nav(page: Page, invite_code: str, viewport: str):
    register_and_log_in(page, invite_code)
    assert {ACCESS, REFRESH} <= _cookie_names(page.context)

    nav = page.get_by_role("navigation")
    nav.get_by_role("link", name="Log out", exact=True).click()

    expect(page).to_have_url("https://platform.test/auth/logout")
    expect(page.get_by_role("heading", name="Log out")).to_be_visible()
    expect(page.get_by_role("link", name="Cancel", exact=True)).to_have_attribute(
        "href", "/"
    )
    # Opening the page changes nothing: the session is still there.
    assert {ACCESS, REFRESH} <= _cookie_names(page.context)
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "logout")

    page.get_by_role("main").get_by_role("button", name="Log out").click()

    expect(page).to_have_url("https://platform.test/login")
    names = _cookie_names(page.context)
    assert ACCESS not in names, "the access cookie survived the logout"
    assert REFRESH not in names, "the refresh cookie survived the logout"

    # With both cookies gone the front page sends the browser to log in.
    page.goto("/")
    expect(page).to_have_url(re.compile(r"/login(\?|$)"))
    expect(page.get_by_role("heading", name="Log in")).to_be_visible()

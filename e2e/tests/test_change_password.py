"""Changing a password while logged in, from "Change password" in the nav's
user menu: the password changes, every other session ends, and the one it
was changed in keeps working. See "Password reset, and changing a password" in
Core/docs/core-architecture.md.

The other session's access token keeps working until it expires, so the test
deletes that cookie, as expiry does, to see that it can no longer refresh.
"""

import re
import uuid

from playwright.sync_api import Browser, Page, expect

from conftest import capture, log_in, register

ACCESS = "__Host-access"


def test_change_password_keeps_this_session_and_ends_the_other(
    page: Page, browser: Browser, base_url: str, invite_code: str, viewport: str
):
    email, old_password = register(page, invite_code)
    log_in(page, email, old_password)
    expect(page).to_have_url("https://platform.test/")

    other = browser.new_context(ignore_https_errors=True, base_url=base_url)
    elsewhere = other.new_page()
    elsewhere.goto("/login")
    log_in(elsewhere, email, old_password)
    expect(elsewhere).to_have_url("https://platform.test/")

    nav = page.get_by_role("navigation")
    nav.get_by_role("button", name=email, exact=True).click()
    nav.get_by_role("link", name="Change password", exact=True).click()

    expect(page).to_have_url("https://platform.test/settings/password")
    expect(page.get_by_role("heading", name="Change your password")).to_be_visible()
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "settings-password")

    new_password = uuid.uuid4().hex[:20]
    page.get_by_label("Current password").fill(old_password)
    page.get_by_label("New password").fill(new_password)
    page.get_by_role("button", name="Change password").click()

    expect(page).to_have_url("https://platform.test/settings/password?changed=1")
    expect(page.get_by_role("status")).to_have_text(
        "Password changed. Your other sessions were signed out."
    )
    capture(page, "settings-password-changed")

    # This session goes on, and can still refresh once its access token is
    # gone, since its family was kept.
    page.goto("/")
    expect(page).to_have_url("https://platform.test/")
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()
    page.context.clear_cookies(name=ACCESS)
    page.goto("/")
    expect(page).to_have_url("https://platform.test/")
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()

    # The other cannot refresh, so without its access token it ends up
    # logging in, where the old password is refused and the new one works.
    other.clear_cookies(name=ACCESS)
    elsewhere.goto("/")
    expect(elsewhere).to_have_url(re.compile(r"/login(\?|$)"))
    expect(elsewhere.get_by_role("heading", name="Log in")).to_be_visible()
    log_in(elsewhere, email, old_password)
    expect(elsewhere.get_by_role("alert")).to_have_text(
        "Error: Email or password is incorrect."
    )
    log_in(elsewhere, email, new_password)
    expect(elsewhere).to_have_url("https://platform.test/")
    other.close()

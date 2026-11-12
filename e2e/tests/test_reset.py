"""Resetting a password with a token from the core's reset command: the
reset page sets the new password and sends you to log in with it, without
logging you in. See "Password reset, and changing a password" in
Core/docs/core-architecture.md."""

import uuid
from collections.abc import Callable

from playwright.sync_api import Page, expect

from conftest import capture, log_in, register


def test_reset_then_log_in_with_the_new_password(
    page: Page,
    invite_code: str,
    reset_token: Callable[[str], str],
    viewport: str,
):
    email, old_password = register(page, invite_code)
    token = reset_token(email)
    new_password = uuid.uuid4().hex[:20]

    page.goto("/reset")
    expect(page.get_by_role("heading", name="Choose a new password")).to_be_visible()
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "reset")

    page.get_by_label("Reset token").fill(token)
    page.get_by_label("New password").fill(new_password)
    page.get_by_role("button", name="Set password").click()

    expect(page).to_have_url("https://platform.test/login")
    names = {cookie["name"] for cookie in page.context.cookies()}
    assert not names & {"__Host-access", "__Secure-refresh"}, "the reset logged in"

    log_in(page, email, old_password)
    expect(page.get_by_role("alert")).to_have_text("Email or password is incorrect.")

    log_in(page, email, new_password)
    expect(page).to_have_url("https://platform.test/")

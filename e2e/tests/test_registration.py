import re
import uuid

from playwright.sync_api import Page, expect

from conftest import capture


def test_register_then_log_in(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = f"e2e-{uuid.uuid4()}@example.com"
    password = uuid.uuid4().hex[:20]

    # /register does not prefill the code from a query string, so fill it.
    page.goto("/register")
    page.fill("[name=code]", invite_code)
    page.fill("[name=email]", email)
    page.fill("[name=password]", password)
    page.click("button[type=submit]")
    expect(page).to_have_url(re.compile(r"/login$"))

    page.fill("[name=email]", email)
    page.fill("[name=password]", password)
    page.click("button[type=submit]")
    expect(page).to_have_url(f"{base_url}/")
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()

    capture(page, "front-signed-in")

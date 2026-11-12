"""The shared nav bar on the core's pages. See "Navigation" in
High-level/docs/platform-architecture.md."""

from playwright.sync_api import Page, expect

from conftest import capture, register_and_log_in


def test_nav_shows_home_features_and_user(page: Page, invite_code: str, viewport: str):
    email = register_and_log_in(page, invite_code)

    nav = page.get_by_role("navigation")
    home = nav.get_by_role("link", name="Platform", exact=True)
    expect(home).to_have_attribute("href", "/")
    expect(home).to_have_attribute("aria-current", "page")
    canary = nav.get_by_role("link", name="Canary", exact=True)
    expect(canary).to_have_attribute("href", "/canary")
    expect(canary).not_to_have_attribute("aria-current", "page")
    expect(nav.get_by_text(email, exact=True)).to_be_visible()
    # The heading and the account line stay until the feature cards.
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()

    # The bar never makes the page scroll sideways, however long the email.
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "front-signed-in")


def test_nav_marks_the_current_feature(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    nav = page.get_by_role("navigation")
    home = nav.get_by_role("link", name="Platform", exact=True)
    canary = nav.get_by_role("link", name="Canary", exact=True)
    expect(canary).to_be_visible()

    page.locator("platform-nav").evaluate("el => el.setAttribute('current', 'canary')")

    expect(canary).to_have_attribute("aria-current", "page")
    expect(home).not_to_have_attribute("aria-current", "page")


def test_login_and_register_pages_have_no_nav(page: Page):
    for path in ["/login", "/register"]:
        page.goto(path)
        expect(page.get_by_role("heading", level=1)).to_be_visible()
        expect(page.locator("platform-nav")).to_have_count(0)
        expect(page.get_by_role("navigation")).to_have_count(0)

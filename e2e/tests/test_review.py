"""Review, the platform's first C# feature, reached through the proxy under
/review and opened from the nav bar. See review/README.md."""

from playwright.sync_api import Browser, Page, expect

from conftest import account_in_access_cookie, capture, register_and_log_in

TABS = ["Study", "Cards", "Decks"]


def test_review_health_through_the_proxy(page: Page):
    # No login: the health endpoint is public. The body is review's, so a 200
    # from the core's fallback route would not pass.
    health = page.request.get("/review/api/health")

    assert health.status == 200
    assert health.json() == {"status": "ok"}


def test_review_is_in_the_navigation_and_opens(
    page: Page, invite_code: str, viewport: str
):
    register_and_log_in(page, invite_code)
    links = page.locator("platform-nav .links")
    review = links.get_by_role("link", name="Review", exact=True)
    expect(review).to_have_attribute("href", "/review")

    review.click()

    expect(page).to_have_url("https://platform.test/review")
    expect(
        page.locator("platform-nav .links").get_by_role(
            "link", name="Review", exact=True
        )
    ).to_have_attribute("aria-current", "page")
    expect(page.get_by_role("heading", name="Review", level=1)).to_be_visible()
    expect(
        page.get_by_text(
            "Cards that come back to you, so what you mark as worth remembering sticks."
        )
    ).to_be_visible()
    tabs = page.get_by_role("tablist", name="Review sections").get_by_role("tab")
    expect(tabs).to_have_text(TABS)
    expect(tabs.first).to_have_attribute("aria-selected", "true")
    expect(
        page.get_by_role("tabpanel").get_by_text("Nothing to study yet")
    ).to_be_visible()

    capture(page, "review-shell")


def test_each_tab_has_its_address(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)

    for path, tab, empty in [
        ("/review/cards", "Cards", "No cards yet"),
        ("/review/decks", "Decks", "No decks yet"),
        ("/review/study", "Study", "Nothing to study yet"),
    ]:
        page.goto(path)
        expect(page.get_by_role("tab", name=tab, exact=True)).to_have_attribute(
            "aria-selected", "true"
        )
        expect(page.get_by_role("tabpanel").get_by_text(empty)).to_be_visible()


def test_review_knows_who_i_am(
    page: Page, browser: Browser, base_url: str, invite_code: str
):
    register_and_log_in(page, invite_code)

    whoami = page.request.get("/review/api/whoami")

    assert whoami.status == 200
    assert whoami.json() == {"account_id": account_in_access_cookie(page)}

    # A browser with no session: 401 with an empty body, never a redirect.
    stranger = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        refused = stranger.request.get("/review/api/whoami", max_redirects=0)
        assert refused.status == 401
        assert refused.body() == b""
    finally:
        stranger.close()

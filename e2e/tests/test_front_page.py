"""The front page, composed by the core from every feature's display data.
Check 4 of the feature: canary's card, rendered by the core from what canary
sent with the user's token. See "What a feature exposes for the front page"
in Core/docs/core-architecture.md."""

from playwright.sync_api import Page, expect

from conftest import capture, register_and_log_in


def test_front_page_shows_canarys_card(page: Page, invite_code: str, viewport: str):
    register_and_log_in(page, invite_code)

    # Canary's own API, asked with the page's cookies, is the reference for
    # who this is, so the test does not trust the card to say it.
    whoami = page.request.get("/canary/api/whoami")
    assert whoami.status == 200
    account_id = whoami.json()["account_id"]

    expect(page.get_by_role("heading", name="Platform", level=1)).to_be_visible()
    card = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Canary", level=2)
    )
    expect(card).to_have_count(1)
    expect(card.get_by_role("link", name="Canary")).to_have_attribute("href", "/canary")
    expect(card.get_by_text(account_id)).to_be_visible()
    expect(card.get_by_text("Verified your token in Python")).to_be_visible()
    expect(page.get_by_text("could not be reached")).to_have_count(0)

    # The cards never make the page scroll sideways, at phone width included.
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "front-signed-in")

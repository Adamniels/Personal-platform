"""The front page, composed by the core from every feature's display data.
Check 4 of the feature: canary's card, rendered by the core from what canary
sent with the user's token. See "What a feature exposes for the front page"
in Core/docs/core-architecture.md."""

import re

from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in


def test_front_page_shows_canarys_card(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)

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


def review(page: Page, method: str, path: str, body: dict) -> dict:
    """Calls review's API from the page, as its own page does, so the
    session's cookie and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/review/api${path}`, {
                method,
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: JSON.stringify(body),
            });
            return [response.status, await response.json()];
        }""",
        [method, path, body],
    )
    assert status in (200, 201), answer
    return answer


def add_card(page: Page, deck: str, front: str) -> str:
    topics = [{"slug": "home", "name": "Home"}]
    card = {"deck_id": deck, "front": front, "back": "Back", "topics": topics}
    return review(page, "POST", "/cards", card)["id"]


def add_deck(page: Page, name: str) -> str:
    return review(page, "POST", "/decks", {"name": name})["id"]


def review_card(page: Page) -> Locator:
    return page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Review", level=2)
    )


def test_the_home_page_shows_what_is_due_in_review(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    """Review comes to the owner: the home page, the first thing seen after
    logging in, says how many cards are due and in which decks."""
    register_and_log_in(page, invite_code, base_url)
    compilers = add_deck(page, "Compilers")
    databases = add_deck(page, "Databases")
    # A new card is due at once.
    add_card(page, compilers, "What does an LL(1) parser look ahead at?")
    add_card(page, compilers, "What does an LR parser build?")
    add_card(page, databases, "What does MVCC keep per row?")

    page.goto("/")

    card = review_card(page)
    expect(card).to_have_count(1)
    expect(card.locator(".key-fact")).to_have_text("3 due now")
    decks = card.get_by_role("listitem")
    expect(decks).to_have_count(2)
    expect(decks.nth(0)).to_have_text(re.compile(r"Compilers\s*2"))
    expect(decks.nth(1)).to_have_text(re.compile(r"Databases\s*1"))
    expect(page.get_by_text("could not be reached")).to_have_count(0)
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "home-review-due")

    card.get_by_role("link", name="Start review").click()
    expect(page).to_have_url(re.compile(r"/review$"))


def test_the_home_page_says_when_nothing_is_due_in_review(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/")

    # With no cards at all, the card offers to add some.
    card = review_card(page)
    expect(card.locator(".key-fact")).to_have_text("Nothing due")
    expect(card.get_by_text("No cards yet")).to_be_visible()
    expect(card.get_by_role("link", name="Add cards")).to_have_attribute(
        "href", "/review/cards"
    )

    # A card graded Good comes back in minutes, so nothing is due until then.
    deck = add_deck(page, "Databases")
    graded = add_card(page, deck, "What does MVCC keep per row?")
    review(page, "POST", f"/cards/{graded}/grades", {"rating": 3, "version": 1})
    page.goto("/")

    card = review_card(page)
    expect(card.locator(".key-fact")).to_have_text("Nothing due")
    expect(card.get_by_text(re.compile(r"^Next in \d+ minutes?$"))).to_be_visible()
    expect(card.get_by_role("link", name="Start review")).to_have_count(0)
    capture(page, "home-review-nothing")

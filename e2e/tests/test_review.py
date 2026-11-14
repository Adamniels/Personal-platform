"""Review, the platform's first C# feature, reached through the proxy under
/review and opened from the nav bar. See review/README.md."""

from playwright.sync_api import Browser, Locator, Page, expect

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
        ("/review/cards", "Cards", "Make a deck first"),
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


def _add_deck(page: Page, name: str) -> None:
    page.get_by_role("button", name="Add deck").click()
    page.get_by_role("textbox", name="Name").fill(name)
    page.get_by_role("button", name="Save").click()
    expect(page.get_by_role("list", name="Decks").get_by_text(name)).to_be_visible()


def _card_row(page: Page, front: str) -> Locator:
    return (
        page.get_by_role("table", name="Cards")
        .get_by_role("row")
        .filter(has_text=front)
    )


def test_adding_a_card_by_hand(page: Page, invite_code: str, viewport: str):
    register_and_log_in(page, invite_code)
    page.goto("/review/decks")
    _add_deck(page, "Real Time Systems")
    _add_deck(page, "Compilers")

    page.get_by_role("tab", name="Cards").click()
    expect(page).to_have_url("https://platform.test/review/cards")
    page.get_by_role("button", name="Add card").click()
    form = page.get_by_role("form", name="New card")
    form.get_by_role("textbox", name="Front").fill("What does EDF schedule by?")
    form.get_by_role("textbox", name="Back").fill("The earliest absolute deadline")
    form.get_by_role("combobox", name="Deck").select_option(label="Real Time Systems")
    topics = form.get_by_role("combobox", name="Topics")
    topics.fill("Scheduling")
    expect(form.get_by_role("option", name="Create topic Scheduling")).to_be_visible()
    topics.press("Enter")
    expect(form.get_by_role("button", name="Remove Scheduling")).to_be_visible()
    form.get_by_role("button", name="Save").click()

    # The form stays open for the next card, its deck and topic kept.
    front = form.get_by_role("textbox", name="Front")
    expect(front).to_have_value("")
    expect(front).to_be_focused()
    expect(form.get_by_role("textbox", name="Back")).to_have_value("")
    expect(
        form.get_by_role("combobox", name="Deck").locator("option:checked")
    ).to_have_text("Real Time Systems")
    expect(form.get_by_role("button", name="Remove Scheduling")).to_be_visible()

    front.fill("What does RMS give the highest priority?")
    form.get_by_role("textbox", name="Back").fill("The task with the shortest period")
    topics.fill("Dead")
    expect(form.get_by_role("listbox", name="Topics")).to_be_visible()
    expect(form.get_by_role("option", name="Create topic Dead")).to_be_visible()
    capture(page, "review-add-card")
    topics.press("Escape")
    topics.fill("")
    form.get_by_role("button", name="Save").click()
    expect(front).to_have_value("")
    form.get_by_role("button", name="Close").click()
    expect(form).to_have_count(0)

    expect(_card_row(page, "What does EDF schedule by?")).to_be_visible()
    rms = _card_row(page, "What does RMS give the highest priority?")
    expect(rms).to_be_visible()
    capture(page, "review-cards")

    _card_row(page, "What does EDF schedule by?").get_by_role(
        "button", name="Edit"
    ).click()
    edit = page.get_by_role("form", name="Edit card")
    expect(edit.get_by_role("button", name="Remove Scheduling")).to_be_visible()
    capture(page, "review-edit-card")
    edit.get_by_role("textbox", name="Front").fill("What does EDF order tasks by?")
    edit.get_by_role("button", name="Save").click()
    expect(_card_row(page, "What does EDF order tasks by?")).to_be_visible()
    expect(_card_row(page, "What does EDF schedule by?")).to_have_count(0)

    rms.get_by_role("button", name="Delete").click()
    dialog = page.get_by_role("dialog", name="Delete this card?")
    dialog.get_by_role("button", name="Delete card").click()
    expect(page.get_by_role("status").filter(has_text="Card deleted")).to_be_visible()
    expect(rms).to_have_count(0)

    topics_in_brain = page.request.get("/brain/api/topics")
    assert topics_in_brain.status == 200
    assert [topic["name"] for topic in topics_in_brain.json()] == ["Scheduling"]

    page.get_by_role("tab", name="Decks").click()
    decks = page.get_by_role("list", name="Decks").get_by_role("listitem")
    expect(decks).to_have_count(2)
    expect(decks.nth(1)).to_contain_text("1 card")
    capture(page, "review-decks")

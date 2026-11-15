"""Studying in review: the Study tab's start view and a session at
/review/study. Cards are made through review's API, and nothing waits on real
time: a card coming back within a session is covered by Vitest against a fake
API. See "Studying" in review/README.md. A grade also reaches the owner's
brain, sent by review's backend after the grade's response, see "What Review
tells the brain" there."""

import re
import time
from datetime import datetime

from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture, register_and_log_in

PARSING = {"slug": "parsing", "name": "Parsing"}
MVCC = {"slug": "mvcc", "name": "MVCC"}

UNITS = {"m": 60, "h": 3600, "d": 86400, "mo": 30 * 86400, "y": 365 * 86400}


def call(page: Page, method: str, path: str, body: dict) -> dict:
    """Calls review from the page, as the page does, so the session's cookie
    and the platform's origin go with it."""
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


def add_deck(page: Page, name: str) -> str:
    return call(page, "POST", "/decks", {"name": name})["id"]


def add_card(page: Page, deck: str, front: str, back: str, topic: dict) -> str:
    card = {"deck_id": deck, "front": front, "back": back, "topics": [topic]}
    return call(page, "POST", "/cards", card)["id"]


def seconds(label: str) -> int:
    """The interval a grade button says, "Good · 10m", in seconds."""
    match = re.fullmatch(r"\w+ · (\d+)(m|h|d|mo|y)", label)
    assert match, label
    return int(match[1]) * UNITS[match[2]]


def grade(page: Page, name: str) -> tuple[float, int]:
    """Presses a grade button, and returns when, and the interval it said."""
    button = page.get_by_role("button", name=re.compile(rf"^{name} · "))
    said = seconds(button.get_attribute("aria-label") or "")
    pressed = time.time()
    button.click()
    return pressed, said


def due_row(page: Page, front: str) -> Locator:
    return (
        page.get_by_role("table", name="Cards")
        .get_by_role("row")
        .filter(has_text=front)
    )


def test_studying_a_deck(page: Page, invite_code: str, base_url: str, viewport: str):
    register_and_log_in(page, invite_code, base_url)
    compilers = add_deck(page, "Compilers")
    databases = add_deck(page, "Databases")
    first = "What does an LL(1) parser look ahead at?"
    second = "What does an LR parser build bottom up?"
    add_card(page, compilers, first, "One token", PARSING)
    add_card(page, compilers, second, "A rightmost derivation, in reverse", PARSING)
    add_card(page, databases, "What does MVCC keep per row?", "Versions", MVCC)

    page.goto("/review")
    expect(page.get_by_role("heading", name="3 cards due", level=2)).to_be_visible()
    by_deck = page.get_by_role("list", name="By deck")
    expect(by_deck.get_by_role("listitem")).to_have_count(2)
    expect(page.get_by_role("link", name="Start everything")).to_be_visible()
    capture(page, "review-study-start")

    by_deck.get_by_role("listitem").filter(has_text="Compilers").get_by_role(
        "link", name="Start"
    ).click()

    expect(page).to_have_url(f"{base_url}/review/study?deck={compilers}")
    card = page.get_by_role("article", name="Compilers")
    expect(page.get_by_text("1 of 2", exact=True)).to_be_visible()
    expect(card.get_by_text(first)).to_be_visible()
    expect(card.get_by_text("One token")).to_have_count(0)
    capture(page, "review-study-front")

    page.get_by_role("button", name="Show answer").click()
    expect(card.get_by_text("One token")).to_be_visible()
    for name in ["Again", "Hard", "Good", "Easy"]:
        expect(
            page.get_by_role("button", name=re.compile(rf"^{name} · "))
        ).to_be_visible()
    capture(page, "review-study-answer")
    graded_first = grade(page, "Good")

    expect(page.get_by_text("2 of 2", exact=True)).to_be_visible()
    expect(card.get_by_text(second)).to_be_visible()
    page.get_by_role("button", name="Show answer").click()
    graded_second = grade(page, "Again")

    # The second is not due again until its learning step has passed.
    expect(page.get_by_role("heading", name="Done for now")).to_be_visible()
    expect(page.get_by_text("You reviewed 2 cards.")).to_be_visible()
    expect(page.get_by_text("The next card is due in 1 minute.")).to_be_visible()
    capture(page, "review-study-done")

    # Cards shows each card's exact next due in a time element at desktop
    # width; on phone it folds into a relative line.
    page.set_viewport_size(VIEWPORTS["desktop"])
    page.get_by_role("link", name="Go to Cards").click()
    expect(page).to_have_url(f"{base_url}/review/cards")
    for front, (pressed, said) in [(first, graded_first), (second, graded_second)]:
        shown = due_row(page, front).locator("time")
        expect(shown).to_be_visible()
        due = datetime.fromisoformat(shown.get_attribute("datetime") or "")
        assert abs(due.timestamp() - (pressed + said)) < 60, (front, due, said)


TITLE_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        textTransform: style.textTransform,
    };
}"""

# The value a property takes when set to a token, through a probe in the
# page, so it is the one the page's own tokens.css gives.
RESOLVE = """([token, property]) => {
    const probe = document.createElement("div");
    probe.style.setProperty(property, `var(${token})`);
    document.body.append(probe);
    const value = getComputedStyle(probe).getPropertyValue(property);
    probe.remove();
    return value;
}"""


def test_a_card_title_on_a_page_is_the_label(
    page: Page, invite_code: str, base_url: str
):
    """A card's title on a feature page is the card's label from @platform/ui,
    not the page's heading style: page.css gives a page's h2 the heading font
    in capitals, and the card's own class decides."""
    register_and_log_in(page, invite_code, base_url)
    deck = add_deck(page, "Compilers")
    add_card(page, deck, "What does an LL(1) parser look ahead at?", "One", PARSING)

    page.goto("/review")
    card = page.get_by_role("article", name="By deck")
    title = card.get_by_role("heading", name="By deck", level=2)
    expect(title).to_be_visible()
    style = title.evaluate(TITLE_STYLE)
    assert style["fontFamily"] == page.evaluate(RESOLVE, ["--font-ui", "font-family"])
    assert style["fontSize"] == page.evaluate(
        RESOLVE, ["--text-label-size", "font-size"]
    )
    assert style["textTransform"] == "none"


def test_studying_with_the_keyboard(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)
    deck = add_deck(page, "Compilers")
    fronts = ["What is a token?", "What is a lexeme?", "What is a grammar?"]
    for front in fronts:
        add_card(page, deck, front, f"The answer to {front}", PARSING)

    page.goto("/review/study")
    for n, front in enumerate(fronts, start=1):
        expect(page.get_by_text(f"{n} of 3", exact=True)).to_be_visible()
        expect(page.get_by_text(front)).to_be_visible()
        page.keyboard.press("Space")
        expect(page.get_by_text(f"The answer to {front}")).to_be_visible()
        page.keyboard.press("3")

    expect(page.get_by_role("heading", name="Done for now")).to_be_visible()
    expect(page.get_by_text("You reviewed 3 cards.")).to_be_visible()


INDEXES = {"slug": "indexes", "name": "Indexes"}


def assessments(page: Page) -> list[dict]:
    """The owner's events from review, newest first, as the brain lists them."""
    answer = page.request.get("/brain/api/events?limit=200")
    assert answer.status == 200, answer.text()
    return [event for event in answer.json() if event["origin"] == "review"]


def test_grading_a_card_records_an_assessment_in_the_brain(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    deck = add_deck(page, "Databases")
    add_card(page, deck, "What does a B tree keep sorted?", "Its keys", INDEXES)

    page.goto(f"/review/study?deck={deck}")
    page.get_by_role("button", name="Show answer").click()
    page.get_by_role("button", name=re.compile(r"^Good · ")).click()
    expect(page.get_by_role("heading", name="Done for now")).to_be_visible()

    deadline = time.monotonic() + 10
    while not (found := assessments(page)) and time.monotonic() < deadline:
        time.sleep(0.25)

    assert len(found) == 1, found
    event = found[0]
    assert event["verb"] == "recalled"
    assert event["source"] == "grade"
    assert event["topic"]["slug"] == "indexes"
    assert event["evidence"] == (
        'Reviewed a card in "Databases": "What does a B tree keep sorted?", '
        "recalled it (Good), its first review."
    )
    assert event["payload"]["rating_name"] == "good"
    assert event["idempotency_key"] == f"{event['run']}:indexes"

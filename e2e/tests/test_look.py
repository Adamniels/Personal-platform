"""Every page of the platform against the look: the checks of look.py on each
page, at desktop and phone width and in both themes, and design/DESIGN.md
describing only what is applied. See "The look checks" in README.md.

The pages are written out below, one entry per page with how it is opened,
so a page added later is added to PAGES by the slice that adds it. The tests
of each page's own look stay beside the page's other tests; these hold the
rules every page shares."""

import re
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in
from look import (
    assert_active_is_inverted,
    assert_headings_are_capitals,
    assert_nothing_active_is_amber,
    assert_nothing_scrolls_sideways,
)

THEMES = ["light", "dark"]
STORAGE_KEY = "platform-theme"
DESIGN = Path(__file__).parents[2] / "design"


@dataclass(frozen=True)
class Entry:
    """A page: where it is, whether it needs a logged in account, what shows
    once it has drawn, and the name of its screenshot, if it has one."""

    name: str
    path: str
    signed_in: bool
    drawn: Callable[[Page], Locator]
    shot: str | None = None


def heading(name: str) -> Callable[[Page], Locator]:
    return lambda page: page.get_by_role("heading", name=name, level=1)


def text(words: str) -> Callable[[Page], Locator]:
    return lambda page: page.get_by_text(words, exact=True)


def account(page: Page) -> Locator:
    """The bar's account pill, drawn once the bar has the account."""
    return page.get_by_role("button", name=re.compile("@"))


# Every page of the platform. A page that needs an account is opened as a
# fresh one, with one Review card due, so the front page has a card that
# needs the person and Review's sections are not empty. The states each page
# can be in are checked by its own look test, test_style.py,
# test_memory_look.py, test_review_look.py and test_devflow_look.py.
PAGES = [
    Entry("login", "/login", False, heading("Log in")),
    Entry("register", "/register", False, heading("Create your account")),
    Entry("reset", "/reset", False, heading("Choose a new password")),
    Entry(
        "gallery",
        "/design/gallery/",
        False,
        lambda page: page.get_by_role("region", name="Button"),
        shot="look-gallery",
    ),
    Entry(
        "front page",
        "/",
        True,
        lambda page: page.locator(".card.needs-you"),
        shot="look-front",
    ),
    Entry(
        "change password", "/settings/password", True, heading("Change your password")
    ),
    Entry("log out", "/auth/logout", True, heading("Log out")),
    Entry("canary", "/canary", True, heading("Canary")),
    Entry(
        "memory: profile",
        "/brain",
        True,
        lambda page: page.get_by_role("button", name="Add entry"),
        shot="look-memory",
    ),
    Entry("memory: rules", "/brain/rules", True, text("No rules yet")),
    Entry("memory: knowledge", "/brain/knowledge", True, text("No topics yet")),
    Entry("memory: now", "/brain/now", True, text("Nothing going on yet")),
    Entry(
        "memory: history", "/brain/history", True, text("Nothing in your history yet")
    ),
    Entry(
        "memory: everything",
        "/brain/everything",
        True,
        text("Nothing in your memory yet"),
    ),
    Entry("memory: timeline", "/brain/timeline", True, text("Nothing recorded yet")),
    Entry("memory: ask", "/brain/ask", True, text("Nothing asked yet")),
    Entry(
        "review: study",
        "/review",
        True,
        lambda page: page.get_by_role("link", name="Start everything"),
        shot="look-review",
    ),
    Entry(
        "review: cards",
        "/review/cards",
        True,
        lambda page: page.get_by_role("table", name="Cards"),
    ),
    Entry(
        "review: decks",
        "/review/decks",
        True,
        lambda page: page.get_by_role("list", name="Decks"),
    ),
    Entry(
        "dev flow: waiting for you",
        "/devflow",
        True,
        text("Connect a repository under Projects to see what waits for you."),
        shot="look-devflow",
    ),
    # A fresh account runs nothing; the tab with two busy workers is held to
    # the look in test_devflow.py.
    Entry(
        "dev flow: running now",
        "/devflow/running",
        True,
        text("Your workers are waiting for work."),
    ),
    Entry(
        "dev flow: projects",
        "/devflow/projects",
        True,
        lambda page: page.get_by_role("button", name="Link GitHub"),
    ),
    # A fresh account has no feature, so its page is opened in the state it
    # shows one it does not have; the page with a feature's cards and decisions
    # is held to the look in test_devflow.py.
    Entry(
        "dev flow: a feature",
        "/devflow/projects/00000000-0000-4000-8000-000000000000/features/1",
        True,
        text("There is no such feature"),
    ),
    # Likewise a project's settings, held to the look with a project's in
    # test_devflow_look.py.
    Entry(
        "dev flow: a project's settings",
        "/devflow/projects/00000000-0000-4000-8000-000000000000/settings",
        True,
        text("There is no such project"),
    ),
]


def add_due_review_card(page: Page, base_url: str) -> None:
    """A deck and a card due now, through Review's API with the page's
    session, as test_style.py adds one."""
    headers = {"Origin": base_url}
    deck = page.request.post(
        "/review/api/decks", data={"name": "Compilers"}, headers=headers
    )
    assert deck.status in (200, 201), deck.text()
    card = page.request.post(
        "/review/api/cards",
        data={
            "deck_id": deck.json()["id"],
            "front": "What does an LR parser build?",
            "back": "A rightmost derivation in reverse",
            "topics": [{"slug": "parsing", "name": "Parsing"}],
        },
        headers=headers,
    )
    assert card.status in (200, 201), card.text()


def open_entry(page: Page, entry: Entry, theme: str) -> None:
    response = page.goto(entry.path)
    assert response is not None and response.status == 200, entry.name
    if entry.path.startswith("/design/gallery/"):
        # The gallery picks its theme with its own control.
        page.get_by_label("Theme").select_option(label=theme.capitalize())
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    expect(entry.drawn(page).first).to_be_visible()
    if entry.signed_in:
        expect(account(page)).to_be_visible()
    page.evaluate("document.fonts.ready.then(() => null)")


def visit_every_page(
    page: Page,
    invite_code: str,
    base_url: str,
    theme: str,
    check: Callable[[Page, Entry], None],
) -> None:
    """Opens each entry of PAGES in `theme`, those without an account first,
    and calls `check` on it once it has drawn."""
    page.goto("/login")
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    for entry in PAGES:
        if not entry.signed_in:
            open_entry(page, entry, theme)
            check(page, entry)

    register_and_log_in(page, invite_code, base_url)
    add_due_review_card(page, base_url)
    for entry in PAGES:
        if entry.signed_in:
            open_entry(page, entry, theme)
            check(page, entry)


def checked(page: Page, entry: Entry, *checks: Callable[[Page], None]) -> None:
    for check in checks:
        try:
            check(page)
        except AssertionError as error:
            raise AssertionError(f"{entry.name} ({entry.path}): {error}") from None


@pytest.mark.parametrize("theme", THEMES)
def test_every_page_follows_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    def check(page: Page, entry: Entry) -> None:
        checked(
            page,
            entry,
            assert_headings_are_capitals,
            assert_nothing_scrolls_sideways,
            assert_active_is_inverted,
        )
        if entry.shot:
            capture(page, f"{entry.shot}-{theme}")

    visit_every_page(page, invite_code, base_url, theme, check)


@pytest.mark.parametrize("theme", THEMES)
def test_no_page_marks_what_is_active_with_amber(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    def check(page: Page, entry: Entry) -> None:
        checked(page, entry, assert_nothing_active_is_amber)

    visit_every_page(page, invite_code, base_url, theme, check)


def test_the_amber_check_names_what_breaks_it(page: Page):
    """assert_nothing_active_is_amber fails on each way an active element
    can be marked in amber, naming it, so a page that passes it passes for a
    reason."""
    page.goto("/login")
    expect(page.get_by_role("heading", name="Log in", level=1)).to_be_visible()
    assert_nothing_active_is_amber(page)

    page.locator("main").evaluate(
        """main => main.insertAdjacentHTML("beforeend",
            '<a id="bordered" href="/" aria-current="page"'
            + ' style="border: 1px solid var(--accent-border)">Bordered</a>'
            + '<button id="outlined" aria-selected="true"'
            + ' style="outline: 2px solid var(--accent)">Outlined</button>'
            + '<table><tr id="shadowed" aria-selected="true"'
            + ' style="box-shadow: inset 0 0 0 2px var(--accent)">'
            + '<td>Shadowed</td></tr></table>')"""
    )
    for offender in ("a#bordered", "button#outlined", "tr#shadowed"):
        with pytest.raises(AssertionError, match=offender):
            assert_nothing_active_is_amber(page)


def test_the_design_file_has_nothing_in_transition():
    design = (DESIGN / "DESIGN.md").read_text()
    tokens = (DESIGN / "tokens.css").read_text()
    headings = [line for line in design.splitlines() if line.startswith("#")]
    assert not [line for line in headings if "in transition" in line.lower()], headings
    assert "--nav-h" not in design
    assert "--nav-h" not in tokens

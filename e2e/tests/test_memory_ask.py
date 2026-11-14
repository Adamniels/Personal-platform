"""The memory centre's Ask section: one of the brain's four questions, about
now or about history, and exactly what a feature asking the same thing would
get, with the reasons and any conflicts. See "The brain owns its own surface"
and "Retrieval: per feature calls over shared primitives" in
Brain/docs/brain-architecture.md, "Conflicting decisions before
consolidation" in Brain/docs/event-model.md and "Asking" in Brain/README.md."""

import json
import re
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
DISAGREE = "The brain holds things that disagree"


def open_ask(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/brain/ask")
    expect(ask(page)).to_be_visible()


def ask(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="Ask")


def question(page: Page, name: str) -> Locator:
    """Chooses a question, and returns its panel."""
    ask(page).get_by_role("tab", name=name, exact=True).click()
    panel = page.get_by_role("tabpanel", name=name)
    expect(panel).to_be_visible()
    return panel


def press_ask(panel: Locator) -> None:
    panel.get_by_role("button", name="Ask", exact=True).click()
    expect(panel.get_by_text("Nothing asked yet")).to_have_count(0)


def item(scope: Locator, text: str) -> Locator:
    """An item of the answer, the card named by its text."""
    return scope.get_by_role("article", name=text, exact=True)


def facts(card: Locator) -> Locator:
    return card.get_by_role("list", name="Facts").get_by_role("listitem")


def call(page: Page, method: str, path: str, body: dict | None = None) -> dict:
    """Calls the brain from the page, as the memory centre does, so the
    session's cookie and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/brain/api${path}`, {
                method,
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: body === null ? undefined : JSON.stringify(body),
            });
            return [response.status, await response.json()];
        }""",
        [method, path, body],
    )
    assert status in (200, 201), answer
    return answer


def add(page: Page, body: dict) -> dict:
    return call(page, "POST", "/items", body)


def topic(page: Page, name: str) -> dict:
    return call(page, "POST", "/topics", {"name": name})


def under(page: Page, narrower: dict, broader: dict) -> None:
    call(
        page, "POST", f"/topics/{narrower['id']}/broader", {"broader_id": broader["id"]}
    )


def ninety_days_ago() -> str:
    return (datetime.now(UTC) - timedelta(days=90)).isoformat()


def who_i_am(page: Page) -> None:
    add(
        page,
        {"kind": "profile", "category": "identity", "content": "A student in Uppsala"},
    )
    add(
        page,
        {
            "kind": "profile",
            "category": "values",
            "content": "Correctness before agreement",
        },
    )
    add(page, {"kind": "rule", "content": "List the problems before the praise"})


def what_i_know(page: Page) -> None:
    topic(page, "Postgres")
    add(page, {"kind": "topic_level", "topic": "postgres", "value": "solid"})
    add(page, {"kind": "topic_focus", "topic": "postgres", "value": "learning"})
    add(
        page,
        {
            "kind": "history",
            "content": "Tuned the query planner for the thesis",
            "occurred_at": "2025-03-01T00:00:00Z",
            "occurred_precision": "month",
            "topics": ["postgres"],
        },
    )


def two_decisions(page: Page) -> tuple[dict, dict]:
    """Two decisions about Postgres that the owner marked as contradicting:
    one about Postgres alone, one about it in a named thing, the narrower.
    The topic must exist already."""
    broad = add(
        page,
        {
            "kind": "decision",
            "content": "Use Postgres for every service",
            "details": {"reasoning": "One database to run."},
            "topics": ["postgres"],
        },
    )
    narrow = add(
        page,
        {
            "kind": "decision",
            "content": "Use SQLite inside Operation Rollout",
            "details": {"reasoning": "The game ships offline."},
            "topics": ["postgres"],
            "entities": ["Operation Rollout"],
        },
    )
    call(
        page, "POST", f"/items/{broad['id']}/contradicts", {"contradicts": narrow["id"]}
    )
    return broad, narrow


def test_asking_who_i_am_returns_my_profile_and_rules(
    page: Page, invite_code: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    expect(
        ask(page).get_by_text(
            "Ask what a feature would ask, and see exactly what it gets.", exact=True
        )
    ).to_be_visible()
    expect(ask(page).get_by_text("Nothing asked yet", exact=True)).to_be_visible()
    who_i_am(page)

    panel = question(page, "Who is this person?")
    press_ask(panel)

    identity = panel.get_by_role("list", name="Identity")
    student = item(identity, "A student in Uppsala")
    expect(student).to_be_visible()
    expect(facts(student)).to_have_text(
        [
            "Profile",
            "Profile store",
            "Entered by hand",
            re.compile(r"^Supported by 1 entry, last on \d+ \w{3}$"),
        ]
    )
    expect(
        item(panel.get_by_role("list", name="Values"), "Correctness before agreement")
    ).to_be_visible()
    expect(panel.get_by_text("Nothing in Goals", exact=True)).to_be_visible()
    rule = item(
        panel.get_by_role("list", name="Rules"), "List the problems before the praise"
    )
    expect(facts(rule).nth(1)).to_have_text("Procedural store")
    expect(
        panel.get_by_role("heading", level=2).filter(has_not_text=DISAGREE)
    ).to_have_text(
        [
            "Identity",
            "Goals",
            "Values",
            "Interests",
            "Preferences",
            "Working style",
            "Rules",
        ]
    )

    # Why opens the events behind it.
    why = student.get_by_role("button", name="Why", exact=True)
    expect(why).to_have_attribute("aria-expanded", "false")
    why.click()
    expect(student.get_by_role("list", name="Why")).to_contain_text("You entered this")


def test_asking_about_a_topic_returns_its_level_focus_and_history(
    page: Page, invite_code: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    what_i_know(page)
    topic(page, "Haskell")
    page.reload()

    panel = question(page, "What do they know?")
    pick = panel.get_by_label("Topic", exact=True)
    pick.select_option(label="Postgres")
    pick.select_option(label="Haskell")
    expect(
        panel.get_by_role("list", name="Topics asked").get_by_role("listitem")
    ).to_have_text(["Postgres", "Haskell"])
    press_ask(panel)

    asked = panel.get_by_role("list", name="postgres")
    expect(item(asked, "Level for Postgres: solid")).to_be_visible()
    expect(item(asked, "Focus for Postgres: learning")).to_be_visible()
    expect(item(asked, "Tuned the query planner for the thesis")).to_be_visible()
    level = item(asked, "Level for Postgres: solid")
    expect(facts(level)).to_have_text(
        ["Level", "Knowledge store", "Entered by hand", re.compile("^Supported by")]
    )
    # In the order asked, a topic with nothing in it said plainly.
    expect(panel.get_by_role("heading", level=2)).to_have_text(["postgres", "haskell"])
    expect(
        panel.get_by_text("Nothing held about haskell yet", exact=True)
    ).to_be_visible()


def test_asking_about_a_broad_topic_includes_the_narrower_ones_and_says_through_which(
    page: Page, invite_code: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    databases = topic(page, "Databases")
    postgres = topic(page, "Postgres")
    indexing = topic(page, "Postgres indexing")
    under(page, postgres, databases)
    under(page, indexing, postgres)
    add(
        page,
        {
            "kind": "history",
            "content": "Read about B-tree indexes",
            "occurred_at": "2026-02-01T00:00:00Z",
            "occurred_precision": "month",
            "topics": ["postgres-indexing"],
        },
    )
    page.reload()

    panel = question(page, "What do they know?")
    panel.get_by_label("Topic", exact=True).select_option(label="Databases")
    press_ask(panel)

    expect(panel.get_by_role("heading", level=2)).to_have_text(["databases"])
    expect(panel.get_by_role("heading", level=3)).to_have_text(
        ["postgres", "postgres-indexing"]
    )
    expect(panel.get_by_text("Through databases", exact=True)).to_be_visible()
    expect(
        panel.get_by_text("Through databases › postgres", exact=True)
    ).to_be_visible()
    expect(
        item(
            panel.get_by_role("list", name="postgres-indexing"),
            "Read about B-tree indexes",
        )
    ).to_be_visible()

    # The history of the broad topic brings the narrower ones too.
    panel = question(page, "What is the history?")
    panel.get_by_label("Subject", exact=True).select_option(label="Databases")
    press_ask(panel)
    expect(
        panel.get_by_text("Through databases › postgres", exact=True)
    ).to_be_visible()
    expect(item(panel, "Read about B-tree indexes")).to_be_visible()


def test_asking_about_now_flags_a_stale_entry_and_asking_about_history_does_not(
    page: Page, invite_code: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    add(
        page,
        {
            "kind": "state",
            "content": "Finishing the thesis draft",
            "occurred_at": ninety_days_ago(),
        },
    )

    panel = question(page, "What is going on now?")
    expect(panel.get_by_label("About", exact=True)).to_have_value("now")
    press_ask(panel)
    draft = item(panel, "Finishing the thesis draft")
    expect(draft.get_by_text("Still true?", exact=True)).to_be_visible()

    panel.get_by_label("About", exact=True).select_option(label="History")
    # A new mode is a new question, so the answer is gone until Ask.
    expect(panel.get_by_text("Nothing asked yet", exact=True)).to_be_visible()
    press_ask(panel)
    draft = item(panel, "Finishing the thesis draft")
    expect(draft.get_by_text("Active", exact=True)).to_be_visible()
    expect(draft.get_by_text("Still true?")).to_have_count(0)


def test_two_contradicting_decisions_come_back_as_a_conflict(
    page: Page, invite_code: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    topic(page, "Postgres")
    two_decisions(page)
    page.reload()

    panel = question(page, "What is the history?")
    panel.get_by_label("Subject", exact=True).select_option(label="Postgres")
    press_ask(panel)

    box = panel.get_by_role("article", name=DISAGREE)
    expect(box).to_be_visible()
    conflict = box.get_by_role("listitem")
    expect(conflict).to_have_count(1)
    expect(conflict.get_by_role("link")).to_have_text(
        ["Use Postgres for every service", "Use SQLite inside Operation Rollout"]
    )
    expect(conflict.get_by_text("Narrower scope", exact=True)).to_be_visible()
    expect(
        conflict.get_by_text(
            "Preferred: Use SQLite inside Operation Rollout", exact=True
        )
    ).to_be_visible()
    # Both stay in the answer, and each link goes down to its item.
    expect(item(panel, "Use Postgres for every service")).to_be_visible()
    expect(item(panel, "Use SQLite inside Operation Rollout")).to_be_visible()
    conflict.get_by_role("link", name="Use Postgres for every service").click()
    expect(page).to_have_url(re.compile(r"/brain/ask#answer-"))
    expect(page.locator(":target")).to_contain_text("Use Postgres for every service")


def test_a_retired_item_never_comes_back(page: Page, invite_code: str, base_url: str):
    open_ask(page, invite_code, base_url)
    add(page, {"kind": "state", "content": "Writing the Ask section"})
    gone = add(page, {"kind": "state", "content": "Learning Haskell"})
    call(page, "POST", f"/items/{gone['id']}/retire")

    panel = question(page, "What is going on now?")
    for mode in ["Now", "History"]:
        panel.get_by_label("About", exact=True).select_option(label=mode)
        press_ask(panel)
        expect(item(panel, "Writing the Ask section")).to_be_visible()
        expect(panel.get_by_text("Learning Haskell")).to_have_count(0)


def test_the_raw_answer_can_be_shown_and_copied(
    page: Page, invite_code: str, base_url: str
):
    page.context.grant_permissions(
        ["clipboard-read", "clipboard-write"], origin=base_url
    )
    open_ask(page, invite_code, base_url)
    add(page, {"kind": "state", "content": "Writing the Ask section"})

    panel = question(page, "What is going on now?")
    press_ask(panel)
    expect(item(panel, "Writing the Ask section")).to_be_visible()
    received = call(page, "POST", "/ask/now", {"mode": "now"})

    show = panel.get_by_role("button", name="Show what a feature receives")
    expect(show).to_have_attribute("aria-expanded", "false")
    show.click()
    raw = panel.get_by_label("What a feature receives", exact=True)
    expect(raw).to_be_visible()
    expect(raw).to_have_css("font-family", re.compile("JetBrains Mono"))
    assert json.loads(raw.inner_text()) == received

    panel.get_by_role("button", name="Copy", exact=True).click()
    expect(page.get_by_role("status").filter(has_text="Answer copied")).to_be_visible()
    copied = page.evaluate("navigator.clipboard.readText()")
    assert json.loads(copied) == received


def violations(page: Page) -> list[dict]:
    page.evaluate("document.fonts.ready.then(() => null)")
    if not page.evaluate("typeof axe !== 'undefined'"):
        page.add_script_tag(path=str(AXE))
    return page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )


def rested(page: Page) -> None:
    """Waits for the bar to draw with the account and the toasts to leave,
    so the page is checked and captured as it rests."""
    expect(page.get_by_role("button", name=re.compile("@"))).to_be_visible()
    expect(page.get_by_role("status").filter(has_text=re.compile("."))).to_have_count(
        0, timeout=10_000
    )


@pytest.mark.parametrize("theme", THEMES)
def test_ask_passes_the_accessibility_check(
    page: Page, invite_code: str, viewport: str, theme: str, base_url: str
):
    open_ask(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    who_i_am(page)
    what_i_know(page)
    two_decisions(page)
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)

    panel = question(page, "Who is this person?")
    press_ask(panel)
    student = item(panel, "A student in Uppsala")
    student.get_by_role("button", name="Why", exact=True).click()
    rested(page)
    assert violations(page) == []
    capture(page, f"memory-ask-{theme}")

    panel = question(page, "What do they know?")
    panel.get_by_label("Topic", exact=True).select_option(label="Postgres")
    press_ask(panel)
    panel.get_by_role("button", name="Show what a feature receives").click()
    rested(page)
    assert violations(page) == []
    capture(page, f"memory-ask-topics-{theme}")

    panel = question(page, "What is the history?")
    panel.get_by_label("Subject", exact=True).select_option(label="Postgres")
    press_ask(panel)
    expect(panel.get_by_role("article", name=DISAGREE)).to_be_visible()
    rested(page)
    assert violations(page) == []
    capture(page, f"memory-ask-conflict-{theme}")

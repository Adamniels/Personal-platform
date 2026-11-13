"""The memory centre's Knowledge section: the topics the owner knows, with a
level and a focus each, a topic's own page, and the vocabulary's changes,
same as, separate, broader and promoting. See "The memory centre" in
Brain/README.md, "How level is represented" in Brain/docs/knowledge-model.md
and "Topic vocabulary" in Brain/docs/event-model.md."""

import re
import uuid
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
PROVISIONAL = "Topics that arrived on their own"
# "13 Nov 2026, 09:41", the form a version's date is shown in.
WHEN = re.compile(r"^\d{1,2} [A-Z][a-z]{2,3} \d{4}, \d{2}:\d{2}$")


def open_knowledge(page: Page, invite_code: str) -> None:
    register_and_log_in(page, invite_code)
    page.goto("/brain/knowledge")
    expect(knowledge(page)).to_be_visible()


def knowledge(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="Knowledge")


def table(page: Page) -> Locator:
    return knowledge(page).get_by_role("table", name="Topics")


def row(page: Page, topic: str) -> Locator:
    return (
        table(page)
        .get_by_role("row")
        .filter(has=page.get_by_role("link", name=topic, exact=True))
    )


def filters(page: Page) -> Locator:
    return knowledge(page).get_by_role("search", name="Filter topics")


def card(page: Page, title: str) -> Locator:
    return knowledge(page).get_by_role("article", name=title)


def item(scope: Locator, text: str) -> Locator:
    return scope.get_by_role("listitem").filter(has_text=text)


def add_topic(
    page: Page, name: str, level: str | None = None, focus: str | None = None
) -> None:
    """Adds a topic through the section's primary button, with a level and a
    focus when given."""
    panel = knowledge(page)
    panel.get_by_role("button", name="Add topic", exact=True).click()
    form = panel.get_by_role("form", name="Add topic")
    form.get_by_label("Name", exact=True).fill(name)
    if level:
        form.get_by_label("Level", exact=True).select_option(label=level)
    if focus:
        form.get_by_label("Focus", exact=True).select_option(label=focus)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()
    expect(row(page, name)).to_be_visible()


def open_topic(page: Page, name: str) -> None:
    table(page).get_by_role("link", name=name, exact=True).click()
    expect(knowledge(page).get_by_role("heading", name=name, level=2)).to_be_visible()


def back_to_knowledge(page: Page) -> None:
    knowledge(page).get_by_role("navigation", name="Breadcrumb").get_by_role(
        "link", name="Knowledge", exact=True
    ).click()
    expect(table(page)).to_be_visible()


def set_on_page(page: Page, level: str | None = None, focus: str | None = None):
    """Sets the open topic's level, focus or both, with its Edit."""
    panel = knowledge(page)
    panel.get_by_role("button", name="Edit", exact=True).click()
    form = panel.get_by_role("form", name="Level and focus")
    if level:
        form.get_by_label("Level", exact=True).select_option(label=level)
    if focus:
        form.get_by_label("Focus", exact=True).select_option(label=focus)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()


def choose_other(page: Page, button: str, label: str, other: str) -> None:
    """Presses a button that asks for another topic, and chooses it."""
    panel = knowledge(page)
    panel.get_by_role("button", name=button, exact=True).click()
    form = panel.get_by_role("form", name=button)
    form.get_by_label(label, exact=True).select_option(label=other)
    form.get_by_role("button", name="Save", exact=True).click()


def treat_as_one(page: Page, one: str, other: str) -> None:
    dialog = page.get_by_role("dialog", name="Treat these as one topic?")
    expect(dialog).to_be_visible()
    expect(dialog).to_contain_text(one)
    expect(dialog).to_contain_text(other)
    expect(dialog).to_contain_text("separate")
    dialog.get_by_role("button", name="Treat as one", exact=True).click()
    expect(dialog).to_be_hidden()


def record_event(page: Page, topic: str) -> None:
    """Sends an event about a topic, as a feature would, from the page, so
    the session's cookie and the platform's origin go with it."""
    status = page.evaluate(
        """async ([topic, key]) => {
            const response = await fetch("/brain/api/events", {
                method: "POST",
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: JSON.stringify({
                    verb: "opened",
                    origin: "e2e",
                    evidence: `Opened a page about ${topic}`,
                    topic,
                    payload: {},
                    payload_version: 1,
                    idempotency_key: key,
                }),
            });
            return response.status;
        }""",
        [topic, str(uuid.uuid4())],
    )
    assert status == 201


def test_i_can_add_a_topic_and_set_its_level_and_focus(page: Page, invite_code: str):
    open_knowledge(page, invite_code)
    expect(knowledge(page).get_by_text("No topics yet", exact=True)).to_be_visible()

    add_topic(page, "Postgres", level="Working", focus="Learning")
    add_topic(page, "Rust")

    postgres = row(page, "Postgres")
    expect(postgres.get_by_role("cell").nth(1)).to_have_text("Working")
    expect(postgres.get_by_role("cell").nth(2)).to_have_text("Learning")
    expect(row(page, "Rust").get_by_role("cell").nth(1)).to_have_text("Not set")
    expect(filters(page)).to_contain_text("2 topics")

    page.reload()
    postgres = row(page, "Postgres")
    expect(postgres.get_by_role("cell").nth(1)).to_have_text("Working")
    expect(postgres.get_by_role("cell").nth(2)).to_have_text("Learning")

    # And set in place, from the row's Edit.
    row(page, "Rust").get_by_role("button", name="Edit Rust").click()
    rust = row(page, "Rust")
    rust.get_by_label("Level", exact=True).select_option(label="Shallow")
    expect(rust.get_by_text("Knows the vocabulary", exact=True)).to_be_visible()
    rust.get_by_label("Focus", exact=True).select_option(label="Steady")
    rust.get_by_role("button", name="Save", exact=True).click()
    expect(rust.get_by_role("cell").nth(1)).to_have_text("Shallow")
    expect(rust.get_by_role("cell").nth(2)).to_have_text("Steady")
    page.reload()
    expect(row(page, "Rust").get_by_role("cell").nth(1)).to_have_text("Shallow")


def test_changing_a_level_keeps_the_earlier_one(page: Page, invite_code: str):
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres", level="Working")
    open_topic(page, "Postgres")
    expect(page).to_have_url("https://platform.test/brain/knowledge/postgres")

    set_on_page(page, level="Solid")

    history = card(page, "Level over time").get_by_role("listitem")
    expect(history).to_have_count(2)
    expect(history.nth(0)).to_contain_text("Working")
    expect(history.nth(1)).to_contain_text("Solid")
    for index in range(2):
        date = history.nth(index).get_by_text(WHEN)
        expect(date).to_be_visible()
        expect(date).to_have_css("font-family", re.compile("JetBrains Mono"))

    # The address alone opens the page, and the table shows the new level.
    page.reload()
    expect(card(page, "Level over time").get_by_role("listitem")).to_have_count(2)
    back_to_knowledge(page)
    expect(row(page, "Postgres").get_by_role("cell").nth(1)).to_have_text("Solid")


def test_i_can_filter_by_level_and_focus(page: Page, invite_code: str):
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres", level="Working", focus="Learning")
    add_topic(page, "Rust", level="Working", focus="Steady")
    add_topic(page, "Haskell", level="Shallow", focus="Not pursuing")
    search = filters(page)

    search.get_by_label("Level", exact=True).select_option(label="Working")
    expect(table(page).get_by_role("link")).to_have_text(["Postgres", "Rust"])
    expect(search).to_contain_text("2 topics")

    search.get_by_label("Focus", exact=True).select_option(label="Steady")
    expect(table(page).get_by_role("link")).to_have_text(["Rust"])
    expect(search).to_contain_text("1 topic")

    search.get_by_label("Level", exact=True).select_option(label="Any")
    search.get_by_label("Focus", exact=True).select_option(label="Any")
    search.get_by_label("Search topics", exact=True).fill("has")
    expect(table(page).get_by_role("link")).to_have_text(["Haskell"])

    search.get_by_label("Search topics", exact=True).fill("nothing like it")
    expect(table(page).get_by_role("link")).to_have_count(0)
    expect(knowledge(page).get_by_text("No topics match", exact=True)).to_be_visible()


def test_i_can_mark_two_topics_as_the_same_and_separate_them_again(
    page: Page, invite_code: str
):
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres", level="Solid")
    add_topic(page, "PostgreSQL")
    open_topic(page, "PostgreSQL")

    choose_other(page, "Same as…", "Same as", "Postgres")
    treat_as_one(page, "PostgreSQL", "Postgres")

    # An alias answers under its target: its page is the target's.
    expect(
        knowledge(page).get_by_role("heading", name="Postgres", level=2)
    ).to_be_visible()
    also = card(page, "Also called")
    expect(item(also, "PostgreSQL")).to_be_visible()
    back_to_knowledge(page)
    expect(table(page).get_by_role("link")).to_have_text(["Postgres"])

    open_topic(page, "Postgres")
    item(card(page, "Also called"), "PostgreSQL").get_by_role(
        "button", name="Separate", exact=True
    ).click()
    expect(item(card(page, "Also called"), "PostgreSQL")).to_have_count(0)
    back_to_knowledge(page)
    expect(table(page).get_by_role("link")).to_have_text(["Postgres", "PostgreSQL"])
    page.reload()
    expect(table(page).get_by_role("link")).to_have_text(["Postgres", "PostgreSQL"])


def test_i_can_place_a_topic_under_another_and_take_it_out(
    page: Page, invite_code: str
):
    open_knowledge(page, invite_code)
    add_topic(page, "Databases")
    add_topic(page, "Postgres")
    open_topic(page, "Postgres")

    choose_other(page, "Place under…", "Place under", "Databases")

    under = card(page, "Under")
    expect(item(under, "Databases")).to_be_visible()
    under.get_by_role("link", name="Databases", exact=True).click()
    expect(
        knowledge(page).get_by_role("heading", name="Databases", level=2)
    ).to_be_visible()
    includes = card(page, "Includes")
    expect(item(includes, "Postgres")).to_be_visible()

    item(includes, "Postgres").get_by_role("button", name="Remove", exact=True).click()
    expect(item(card(page, "Includes"), "Postgres")).to_have_count(0)
    page.reload()
    expect(item(card(page, "Includes"), "Postgres")).to_have_count(0)
    open_from = knowledge(page).get_by_role("navigation", name="Breadcrumb")
    open_from.get_by_role("link", name="Knowledge", exact=True).click()
    open_topic(page, "Postgres")
    expect(item(card(page, "Under"), "Databases")).to_have_count(0)


def test_i_can_record_a_misconception_on_a_topic(page: Page, invite_code: str):
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres")
    open_topic(page, "Postgres")
    misconceptions = card(page, "Misconceptions")
    expect(misconceptions.get_by_text("None recorded.", exact=True)).to_be_visible()

    misconceptions.get_by_role("button", name="Add misconception", exact=True).click()
    form = misconceptions.get_by_role("form", name="Add misconception")
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form.get_by_text("Error: Write what you believed")).to_be_visible()
    form.get_by_label("What I believed", exact=True).fill(
        "An index is always used when it exists"
    )
    form.get_by_label("What is right", exact=True).fill(
        "The planner skips an index when a scan is cheaper"
    )
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()

    entry = item(misconceptions, "An index is always used")
    expect(entry).to_contain_text("What I believed")
    expect(entry).to_contain_text("The planner skips an index when a scan is cheaper")
    page.reload()
    expect(
        item(card(page, "Misconceptions"), "An index is always used")
    ).to_be_visible()
    # It counts in the table's History column.
    back_to_knowledge(page)
    expect(row(page, "Postgres").get_by_role("cell").nth(3)).to_have_text("1")


def test_provisional_topics_have_their_own_list(page: Page, invite_code: str):
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres")
    expect(card(page, PROVISIONAL)).to_have_count(0)

    record_event(page, "rust")
    record_event(page, "postgresql")
    page.reload()

    provisional = card(page, PROVISIONAL)
    expect(provisional).to_contain_text(
        "Features create these when they send something about a topic"
    )
    expect(provisional.get_by_role("listitem")).to_have_count(2)
    # They are not in the table until they are promoted.
    expect(table(page).get_by_role("link")).to_have_text(["Postgres"])

    item(provisional, "rust").get_by_role("button", name="Promote", exact=True).click()
    expect(table(page).get_by_role("link")).to_have_text(["Postgres", "rust"])
    expect(provisional.get_by_role("listitem")).to_have_count(1)

    entry = item(provisional, "postgresql")
    entry.get_by_role("button", name="Same as…", exact=True).click()
    form = entry.get_by_role("form", name="Same as…")
    form.get_by_label("Same as", exact=True).select_option(label="Postgres")
    form.get_by_role("button", name="Save", exact=True).click()
    treat_as_one(page, "postgresql", "Postgres")

    # The last one gone, the card goes with it.
    expect(card(page, PROVISIONAL)).to_have_count(0)
    open_topic(page, "Postgres")
    expect(item(card(page, "Also called"), "postgresql")).to_be_visible()


def test_a_provisional_topics_page_offers_promote_and_no_level(
    page: Page, invite_code: str, viewport: str
):
    # Only canonical topics carry a level, "Topic vocabulary" in
    # Brain/docs/event-model.md, so the page offers Promote in Edit's place.
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres")
    record_event(page, "rust")
    page.reload()
    card(page, PROVISIONAL).get_by_role("link", name="rust", exact=True).click()
    panel = knowledge(page)
    expect(panel.get_by_role("heading", name="rust", level=2)).to_be_visible()
    expect(panel.get_by_role("button", name="Edit", exact=True)).to_have_count(0)
    expect(panel).to_contain_text("Promote it to give it a level and a focus.")
    capture(page, "memory-provisional-topic")

    panel.get_by_role("button", name="Promote", exact=True).click()
    expect(panel.get_by_role("button", name="Edit", exact=True)).to_be_visible()
    expect(panel.get_by_role("button", name="Promote", exact=True)).to_have_count(0)


def test_on_phone_the_table_shows_the_topic_with_level_and_focus_under_it(
    page: Page, invite_code: str
):
    page.set_viewport_size({"width": 390, "height": 844})
    open_knowledge(page, invite_code)
    add_topic(page, "Postgres", level="Working", focus="Learning")
    add_topic(page, "Distributed systems and consensus", level="Shallow")

    first = row(page, "Postgres").get_by_role("cell").first
    expect(first).to_contain_text("Working · Learning")
    expect(
        row(page, "Distributed systems and consensus").get_by_role("cell").first
    ).to_contain_text("Shallow · Not set")
    # Only the topic is a column on phone.
    expect(table(page).get_by_role("columnheader", name="Level")).to_be_hidden()
    assert page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    ), "the page scrolls sideways"
    assert table(page).evaluate(
        "e => e.closest('section').scrollWidth <= e.closest('section').clientWidth"
    ), "the table scrolls sideways"


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
def test_knowledge_passes_the_accessibility_check(
    page: Page, invite_code: str, viewport: str, theme: str
):
    open_knowledge(page, invite_code)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    add_topic(page, "Databases", level="Solid", focus="Steady")
    add_topic(page, "Postgres", level="Working", focus="Learning")
    add_topic(page, "Distributed systems", level="Shallow", focus="Not pursuing")
    add_topic(page, "Rust")

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-knowledge-{theme}")

    open_topic(page, "Postgres")
    set_on_page(page, level="Solid")
    choose_other(page, "Place under…", "Place under", "Databases")
    expect(item(card(page, "Under"), "Databases")).to_be_visible()
    misconceptions = card(page, "Misconceptions")
    misconceptions.get_by_role("button", name="Add misconception", exact=True).click()
    form = misconceptions.get_by_role("form", name="Add misconception")
    form.get_by_label("What I believed", exact=True).fill(
        "An index is always used when it exists"
    )
    form.get_by_label("What is right", exact=True).fill(
        "The planner skips an index when a scan is cheaper"
    )
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-topic-{theme}")

    back_to_knowledge(page)
    record_event(page, "kubernetes")
    record_event(page, "postgresql")
    page.reload()
    expect(card(page, PROVISIONAL).get_by_role("listitem")).to_have_count(2)
    item(card(page, PROVISIONAL), "postgresql").get_by_role(
        "button", name="Same as…", exact=True
    ).click()
    if viewport == "desktop":
        # On phone a row has no Edit: its topic's page edits it.
        row(page, "Rust").get_by_role("button", name="Edit Rust").click()
    else:
        expect(row(page, "Rust").get_by_role("button", name="Edit Rust")).to_be_hidden()

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-provisional-{theme}")

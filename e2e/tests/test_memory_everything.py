"""The memory centre's Everything section: every item the brain holds, active
or not, searched by its words, each with a page saying where it came from and
why, where it is corrected, retired, brought back or marked as contradicting
another. See "The memory centre" in Brain/README.md, and "Nothing is
destroyed, and only the owner retires" and "Editing changes provenance, and
every edit records a judgment" in Brain/docs/trust-model.md."""

import re
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
# "13 Nov 2026", the form a last supported date is shown in.
DAY = re.compile(r"^\d{1,2} [A-Z][a-z]{2} \d{4}$")


def open_everything(page: Page, invite_code: str) -> None:
    register_and_log_in(page, invite_code)
    page.goto("/brain/everything")
    expect(page.get_by_role("tablist", name="Memory sections")).to_be_visible()


def everything(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="Everything")


def table(page: Page) -> Locator:
    return everything(page).get_by_role("table", name="Everything")


def row(page: Page, text: str) -> Locator:
    return (
        table(page)
        .get_by_role("row")
        .filter(has=page.get_by_role("link", name=text, exact=True))
    )


def entries(page: Page) -> Locator:
    """Each row's entry, the link to its page."""
    return table(page).get_by_role("row").get_by_role("link")


def toast(page: Page, text: str) -> Locator:
    return page.get_by_role("status").filter(has_text=text)


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


def rule(page: Page, text: str) -> dict:
    return add(page, {"kind": "rule", "content": text})


def decision(page: Page, text: str) -> dict:
    return add(
        page,
        {"kind": "decision", "content": text, "details": {"reasoning": "Because."}},
    )


def one_of_each(page: Page) -> None:
    """A profile entry, a rule, a level, a state, a history entry and a
    decision, entered through the API the sections use."""
    call(page, "POST", "/topics", {"name": "Rust"})
    add(
        page,
        {
            "kind": "profile",
            "category": "values",
            "content": "Honesty over comfort",
            "topics": ["rust"],
        },
    )
    rule(page, "Answer in English")
    add(page, {"kind": "topic_level", "topic": "rust", "value": "working"})
    add(page, {"kind": "state", "content": "Writing the thesis"})
    add(
        page,
        {
            "kind": "history",
            "content": "Took the compilers course",
            "occurred_at": "2023-09-01T00:00:00Z",
            "occurred_precision": "month",
        },
    )
    decision(page, "Keep the brain its own service")


def open_item(page: Page, text: str) -> None:
    row(page, text).get_by_role("link", name=text, exact=True).click()
    expect(
        everything(page).get_by_role("heading", name=text, level=2, exact=True)
    ).to_be_visible()


def section(page: Page, title: str) -> Locator:
    return everything(page).get_by_role("region", name=title)


def head(page: Page) -> Locator:
    """The item's text and its state, beside it."""
    return everything(page).get_by_role("heading", level=2).locator("..")


def test_everything_lists_every_kind_i_entered(page: Page, invite_code: str):
    open_everything(page, invite_code)
    one_of_each(page)
    page.reload()

    for text, kind in [
        ("Honesty over comfort", "Profile"),
        ("Answer in English", "Rule"),
        ("Level for Rust: working", "Level"),
        ("Writing the thesis", "Now"),
        ("Took the compilers course", "History"),
        ("Keep the brain its own service", "Decision"),
    ]:
        found = row(page, text)
        expect(found).to_have_count(1)
        expect(found.get_by_role("cell").nth(1)).to_have_text(kind)
        expect(found.get_by_text("Active", exact=True)).to_be_visible()
    expect(entries(page)).to_have_count(6)
    expect(row(page, "Honesty over comfort").get_by_text("Rust")).to_be_visible()
    date = row(page, "Answer in English").locator("time")
    expect(date).to_have_text(DAY)
    expect(date).to_have_css("font-family", re.compile("JetBrains Mono"))
    # Newest support first: the course happened in 2023.
    expect(entries(page).last).to_have_text("Took the compilers course")


def test_retired_and_replaced_items_show_only_when_asked_for(
    page: Page, invite_code: str
):
    open_everything(page, invite_code)
    retired = rule(page, "Keep answers short")
    call(page, "POST", f"/items/{retired['id']}/retire")
    first = rule(page, "Answer in English")
    call(
        page,
        "POST",
        f"/items/{first['id']}/edit",
        {"kind": "rule", "content": "Answer in English, always"},
    )
    page.reload()

    expect(entries(page)).to_have_text(["Answer in English, always"])
    state = everything(page).get_by_label("State", exact=True)
    expect(state).to_have_value("current")

    state.select_option(label="Retired")
    expect(entries(page)).to_have_text(["Keep answers short"])
    expect(row(page, "Keep answers short").get_by_text("Retired")).to_be_visible()

    state.select_option(label="Replaced")
    expect(entries(page)).to_have_text(["Answer in English"])
    expect(row(page, "Answer in English").get_by_text("Replaced")).to_be_visible()

    state.select_option(label="Any")
    expect(entries(page)).to_have_count(3)


def test_still_true_shows_on_a_state_not_confirmed_for_sixty_days(
    page: Page, invite_code: str
):
    open_everything(page, invite_code)
    said = datetime.now(UTC) - timedelta(days=90)
    add(
        page,
        {
            "kind": "state",
            "content": "Finishing the draft",
            "occurred_at": said.isoformat(),
        },
    )
    page.reload()

    expect(row(page, "Finishing the draft").get_by_text("Still true?")).to_be_visible()
    everything(page).get_by_label("State", exact=True).select_option(label="Active")
    expect(entries(page)).to_have_count(0)
    everything(page).get_by_label("State", exact=True).select_option(
        label="Still true?"
    )
    expect(entries(page)).to_have_text(["Finishing the draft"])


def test_i_can_search_by_a_word_in_the_evidence(page: Page, invite_code: str):
    open_everything(page, invite_code)
    held = add(page, {"kind": "state", "content": "Writing the thesis"})
    add(page, {"kind": "state", "content": "Reading about compilers"})
    # Confirming records an event whose evidence says so: the word is in
    # the evidence behind the entry and not in the entry itself.
    call(page, "POST", f"/items/{held['id']}/confirm")
    page.reload()
    expect(entries(page)).to_have_count(2)

    everything(page).get_by_label("Search", exact=True).fill("confirmed")

    expect(entries(page)).to_have_text(["Writing the thesis"])
    excerpt = row(page, "Writing the thesis").locator("mark")
    expect(excerpt).to_have_text(["Confirmed"])
    expect(row(page, "Writing the thesis")).to_contain_text(
        'Confirmed by hand: "Writing the thesis"'
    )
    expect(page).to_have_url(re.compile(r"/brain/everything\?q=confirmed$"))
    # A word nothing holds finds nothing, and says so.
    everything(page).get_by_label("Search", exact=True).fill("zeppelin")
    expect(everything(page).get_by_text("Nothing matches")).to_be_visible()


def test_i_can_narrow_by_kind_and_subject_and_remove_a_filter(
    page: Page, invite_code: str
):
    open_everything(page, invite_code)
    one_of_each(page)
    page.reload()
    filters = everything(page).get_by_role("search", name="Filter everything")

    filters.get_by_label("Kind", exact=True).select_option(label="Rule")
    expect(entries(page)).to_have_text(["Answer in English"])
    kind = filters.get_by_role("button", name="Remove filter Kind: Rule")
    expect(kind).to_be_visible()

    filters.get_by_label("Kind", exact=True).select_option(label="Any")
    filters.get_by_label("Subject", exact=True).select_option(label="Rust")
    expect(entries(page)).to_have_text(
        ["Level for Rust: working", "Honesty over comfort"]
    )
    filters.get_by_label("Kind", exact=True).select_option(label="Profile")
    expect(entries(page)).to_have_text(["Honesty over comfort"])

    filters.get_by_role("button", name="Remove filter Subject: Rust").click()
    expect(filters.get_by_label("Subject", exact=True)).to_have_value("")
    expect(entries(page)).to_have_text(["Honesty over comfort"])
    filters.get_by_role("button", name="Remove filter Kind: Profile").click()
    expect(entries(page)).to_have_count(6)
    expect(
        filters.get_by_role("button", name=re.compile("^Remove filter"))
    ).to_have_count(0)


def test_an_item_shows_where_it_came_from_and_why(page: Page, invite_code: str):
    open_everything(page, invite_code)
    call(page, "POST", "/topics", {"name": "Rust"})
    add(
        page,
        {
            "kind": "profile",
            "category": "values",
            "content": "Honesty over comfort",
            "topics": ["rust"],
            "entities": ["Uppsala"],
        },
    )
    page.reload()

    open_item(page, "Honesty over comfort")

    facts = everything(page).get_by_role("list", name="Facts")
    expect(facts).to_contain_text("Profile")
    expect(facts).to_contain_text("Profile store")
    expect(facts).to_contain_text("Entered by hand")
    expect(facts).to_have_css("font-family", re.compile("JetBrains Mono"))
    expect(head(page).get_by_text("Active", exact=True)).to_be_visible()
    about = everything(page).get_by_role("list", name="About")
    expect(about).to_contain_text("Rust")
    expect(about).to_contain_text("Uppsala")

    why = section(page, "Why the brain holds this")
    expect(why.get_by_role("listitem")).to_have_count(1)
    line = why.get_by_role("listitem")
    expect(line).to_contain_text("You entered this")
    expect(line).to_contain_text('Entered by hand as profile: "Honesty over comfort"')
    expect(line).to_contain_text("From the memory centre")
    # When, in the Timeline's mono line: "13 Nov 2026, 09:41".
    expect(line).to_contain_text(
        re.compile(r"\d{1,2} [A-Z][a-z]{2} \d{4}, \d{2}:\d{2}")
    )
    # One version, so no list of them.
    expect(section(page, "Versions")).to_have_count(0)

    everything(page).get_by_role("link", name="Everything", exact=True).click()
    expect(table(page)).to_be_visible()


def test_i_can_correct_retire_and_bring_back_from_an_items_page(
    page: Page, invite_code: str
):
    open_everything(page, invite_code)
    rule(page, "Answer in English")
    page.reload()
    open_item(page, "Answer in English")
    panel = everything(page)

    panel.get_by_role("button", name="Edit", exact=True).click()
    field = panel.get_by_label("Entry", exact=True)
    expect(field).to_have_value("Answer in English")
    field.fill("Answer in English, always")
    panel.get_by_role("button", name="Save", exact=True).click()
    expect(toast(page, "Entry saved")).to_be_visible()
    heading = panel.get_by_role("heading", name="Answer in English, always", level=2)
    expect(heading).to_be_visible()
    versions = section(page, "Versions").get_by_role("listitem")
    expect(versions).to_have_count(2)
    expect(versions.first).to_contain_text("Answer in English")
    why = section(page, "Why the brain holds this")
    expect(why).to_contain_text("You entered this")

    panel.get_by_role("button", name="Retire", exact=True).click()
    dialog = page.get_by_role("dialog", name="Retire this entry?")
    expect(
        dialog.get_by_text(
            "It stops being used. It stays in Everything, where you can bring it back."
        )
    ).to_be_visible()
    dialog.get_by_role("button", name="Retire entry", exact=True).click()
    expect(dialog).to_be_hidden()
    expect(toast(page, "Entry retired")).to_be_visible()
    expect(head(page).get_by_text("Retired", exact=True)).to_be_visible()
    expect(panel.get_by_role("button", name="Edit", exact=True)).to_have_count(0)
    expect(why).to_contain_text("You retired this")

    panel.get_by_role("button", name="Bring back", exact=True).click()
    expect(toast(page, "Entry brought back")).to_be_visible()
    expect(head(page).get_by_text("Active", exact=True)).to_be_visible()
    expect(why).to_contain_text("You brought this back")

    # The earlier version is replaced: it can be read, and offers nothing.
    section(page, "Versions").get_by_role(
        "link", name="Answer in English", exact=True
    ).click()
    expect(head(page).get_by_text("Replaced", exact=True)).to_be_visible()
    expect(section(page, "Why the brain holds this")).to_contain_text(
        "You corrected this"
    )
    for name in ["Edit", "Retire", "Bring back"]:
        expect(panel.get_by_role("button", name=name, exact=True)).to_have_count(0)
    related = section(page, "Related")
    expect(related).to_contain_text("Replaced by")
    expect(
        related.get_by_role("link", name="Answer in English, always")
    ).to_be_visible()


def test_i_can_mark_two_decisions_as_contradicting_and_take_it_back(
    page: Page, invite_code: str
):
    open_everything(page, invite_code)
    decision(page, "Write the brain in Python")
    decision(page, "Write the brain in Rust")
    page.reload()
    open_item(page, "Write the brain in Python")
    panel = everything(page)

    panel.get_by_role("button", name="Mark as contradicting…", exact=True).click()
    form = panel.get_by_role("form", name="Mark as contradicting…")
    form.get_by_label("Contradicting decision", exact=True).select_option(
        label="Write the brain in Rust"
    )
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()
    expect(toast(page, "Marked as contradicting")).to_be_visible()

    related = section(page, "Related")
    expect(related).to_contain_text("Contradicts")
    other = related.get_by_role("link", name="Write the brain in Rust")
    expect(other).to_be_visible()

    # It reads from the other side too.
    other.click()
    related = section(page, "Related")
    expect(
        related.get_by_role("link", name="Write the brain in Python")
    ).to_be_visible()

    related.get_by_role("button", name="Take back", exact=True).click()
    expect(toast(page, "Contradiction taken back")).to_be_visible()
    expect(section(page, "Related")).to_have_count(0)
    page.reload()
    expect(section(page, "Related")).to_have_count(0)


def test_nothing_on_the_page_is_called_delete(page: Page, invite_code: str):
    open_everything(page, invite_code)
    one_of_each(page)
    page.reload()
    expect(entries(page)).to_have_count(6)
    panel = everything(page)

    def no_delete() -> None:
        # The text, and every name and label in the markup.
        assert "delete" not in panel.inner_text().lower()
        assert "delete" not in panel.inner_html().lower()

    no_delete()
    open_item(page, "Keep the brain its own service")
    no_delete()
    panel.get_by_role("button", name="Retire", exact=True).click()
    dialog = page.get_by_role("dialog", name="Retire this entry?")
    expect(dialog).to_be_visible()
    assert "delete" not in dialog.inner_html().lower()


def test_on_phone_the_table_folds_and_the_page_does_not_scroll_sideways(
    page: Page, invite_code: str
):
    page.set_viewport_size({"width": 390, "height": 844})
    open_everything(page, invite_code)
    one_of_each(page)
    rule(
        page,
        "A rule long enough that its one line has to be cut short on a phone screen,"
        " since a row never wraps its entry",
    )
    page.reload()

    first = row(page, "Answer in English").get_by_role("cell").first
    expect(first).to_contain_text(re.compile(r"Rule · \d{1,2} [A-Z][a-z]{2} \d{4}"))
    # Only the entry and its state are columns on phone.
    expect(table(page).get_by_role("columnheader", name="Kind")).to_be_hidden()
    expect(table(page).get_by_role("columnheader", name="About")).to_be_hidden()
    expect(table(page).get_by_role("columnheader", name="State")).to_be_visible()
    assert page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    ), "the page scrolls sideways"
    assert table(page).evaluate(
        "e => e.closest('section').scrollWidth <= e.closest('section').clientWidth"
    ), "the table scrolls sideways"

    open_item(page, "Honesty over comfort")
    assert page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    ), "an item's page scrolls sideways"


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
def test_everything_passes_the_accessibility_check(
    page: Page, invite_code: str, viewport: str, theme: str
):
    open_everything(page, invite_code)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    one_of_each(page)
    decision(page, "Write the brain in Rust")
    retired = rule(page, "Keep answers short")
    call(page, "POST", f"/items/{retired['id']}/retire")
    said = datetime.now(UTC) - timedelta(days=90)
    held = add(
        page,
        {
            "kind": "state",
            "content": "Finishing the draft",
            "occurred_at": said.isoformat(),
        },
    )
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    everything(page).get_by_label("State", exact=True).select_option(label="Any")
    expect(entries(page)).to_have_count(9)

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-everything-{theme}")

    call(page, "POST", f"/items/{held['id']}/confirm")
    everything(page).get_by_label("Search", exact=True).fill("confirmed")
    expect(entries(page)).to_have_text(["Finishing the draft"])

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-everything-search-{theme}")

    everything(page).get_by_label("Search", exact=True).fill("")
    open_item(page, "Keep the brain its own service")
    expect(section(page, "Why the brain holds this")).to_be_visible()

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-item-{theme}")

    # And with the form open, which asks for the other decision.
    everything(page).get_by_role(
        "button", name="Mark as contradicting…", exact=True
    ).click()
    expect(
        everything(page).get_by_role("form", name="Mark as contradicting…")
    ).to_be_visible()
    assert violations(page) == []

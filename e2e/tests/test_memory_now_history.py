"""The memory centre's Now and History sections: what is going on now, with
anything not confirmed for sixty days flagged and one press to say it still
holds, and what happened and what was decided, in the order it happened. See
"The memory centre" in Brain/README.md, "Claim versus record" and "Timestamps"
in Brain/docs/knowledge-model.md, and "Decisions" in
Brain/docs/event-model.md."""

import re
from datetime import UTC, date, datetime, timedelta
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
NOW_LINE = (
    "What you are working on, what is blocking you, what is coming up. "
    "The brain asks again after sixty days."
)


def open_section(page: Page, invite_code: str, base_url: str, path: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto(path)
    expect(page.get_by_role("tablist", name="Memory sections")).to_be_visible()


def now(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="Now")


def history(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="History")


def timeline(page: Page) -> Locator:
    return history(page).get_by_role("list", name="History")


def entry(scope: Locator, text: str) -> Locator:
    return scope.get_by_role("listitem").filter(has_text=text)


def call(page: Page, method: str, path: str, body: dict) -> dict:
    """Calls the brain from the page, as the memory centre does, so the
    session's cookie and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/brain/api${path}`, {
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


def add_now(
    page: Page, text: str, topic: str | None = None, named: str | None = None
) -> None:
    panel = now(page)
    panel.get_by_role("button", name="Add", exact=True).click()
    form = panel.get_by_role("form", name="Add")
    form.get_by_label("What is going on").fill(text)
    if topic:
        form.get_by_label("Topic", exact=True).select_option(label=topic)
    if named:
        form.get_by_label("Named thing", exact=True).fill(named)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()
    expect(entry(panel, text)).to_be_visible()


def add_happened(
    page: Page,
    text: str,
    *,
    year: str | None = None,
    day: str | None = None,
    topic: str | None = None,
    named: str | None = None,
) -> None:
    panel = history(page)
    panel.get_by_role("button", name="Add what happened", exact=True).click()
    form = panel.get_by_role("form", name="Add what happened")
    form.get_by_label("What happened").fill(text)
    if year:
        form.get_by_label("Precision").select_option(label="Year")
        form.get_by_label("Year", exact=True).fill(year)
    else:
        form.get_by_label("Day", exact=True).fill(day or date.today().isoformat())
    if topic:
        form.get_by_label("Topic", exact=True).select_option(label=topic)
    if named:
        form.get_by_label("Named thing", exact=True).fill(named)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()
    expect(entry(timeline(page), text)).to_be_visible()


def add_decision(
    page: Page, what: str, why: str, rejected: list[str] | None = None
) -> None:
    panel = history(page)
    panel.get_by_role("button", name="Add decision", exact=True).click()
    form = panel.get_by_role("form", name="Add decision")
    form.get_by_label("What was decided").fill(what)
    form.get_by_label("Why", exact=True).fill(why)
    options = form.get_by_role("group", name="What was rejected")
    for index, option in enumerate(rejected or []):
        if index > 0:
            options.get_by_role("button", name="Add another", exact=True).click()
        options.get_by_label(f"Option {index + 1}", exact=True).fill(option)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(form).to_be_hidden()
    expect(timeline(page).get_by_role("link", name=what, exact=True)).to_be_visible()


def open_decision(page: Page, what: str) -> None:
    timeline(page).get_by_role("link", name=what, exact=True).click()
    expect(
        history(page).get_by_role("heading", name=what, level=2, exact=True)
    ).to_be_visible()


def fact(page: Page, term: str) -> Locator:
    """The value of one of the decision page's facts, by its term."""
    return history(page).locator(f"dt:text-is('{term}') + dd")


def test_i_can_add_what_is_going_on_now(page: Page, invite_code: str, base_url: str):
    open_section(page, invite_code, base_url, "/brain/now")
    expect(now(page).get_by_text(NOW_LINE, exact=True)).to_be_visible()

    add_now(page, "Writing the memory centre's Now section")

    added = entry(now(page), "Writing the memory centre's Now section")
    expect(added.get_by_text("Confirmed today", exact=True)).to_be_visible()
    expect(added.get_by_text("Confirmed today", exact=True)).to_have_css(
        "font-family", re.compile("JetBrains Mono")
    )
    expect(added.get_by_text("Still true?")).to_have_count(0)
    page.reload()
    expect(
        entry(now(page), "Writing the memory centre's Now section").get_by_text(
            "Confirmed today", exact=True
        )
    ).to_be_visible()


def test_an_old_entry_is_flagged_and_one_press_confirms_it(
    page: Page, invite_code: str, base_url: str
):
    open_section(page, invite_code, base_url, "/brain/now")
    said = datetime.now(UTC) - timedelta(days=90)
    call(
        page,
        "POST",
        "/items",
        {
            "kind": "state",
            "content": "Finishing the thesis draft",
            "occurred_at": said.isoformat(),
        },
    )
    page.reload()
    old = entry(now(page), "Finishing the thesis draft")
    expect(old.get_by_text("Still true?", exact=True)).to_be_visible()
    expect(old.get_by_text("Confirmed today")).to_have_count(0)

    old.get_by_role("button", name="Still true", exact=True).click()

    expect(old.get_by_text("Still true?")).to_have_count(0)
    expect(old.get_by_role("button", name="Still true")).to_have_count(0)
    expect(old.get_by_text("Confirmed today", exact=True)).to_be_visible()
    page.reload()
    held = entry(now(page), "Finishing the thesis draft")
    expect(held.get_by_text("Confirmed today", exact=True)).to_be_visible()
    expect(held.get_by_text("Still true?")).to_have_count(0)


def test_i_can_record_something_that_happened_years_ago(
    page: Page, invite_code: str, base_url: str
):
    open_section(page, invite_code, base_url, "/brain/history")
    this_year = date.today().replace(month=1, day=2).isoformat()
    add_happened(page, "Started the platform", day=this_year)

    add_happened(page, "Took the distributed systems course", year="2021")

    course = entry(timeline(page), "Took the distributed systems course")
    expect(course).to_contain_text("2021")
    # Newest first by when it happened: entered last, it comes after this
    # year's entry, since it happened before it.
    expect(timeline(page).get_by_role("listitem")).to_have_text(
        [re.compile("Started the platform"), re.compile("^2021.*course")]
    )
    page.reload()
    expect(timeline(page).get_by_role("listitem")).to_have_text(
        [re.compile("Started the platform"), re.compile("^2021.*course")]
    )


def test_i_can_record_a_decision_with_its_reasoning_and_what_i_rejected(
    page: Page, invite_code: str, base_url: str
):
    open_section(page, invite_code, base_url, "/brain/history")

    add_decision(
        page,
        "The brain is its own service",
        "Splitting memory across languages failed in v1",
        ["A memory module in each feature", "A shared library"],
    )

    decided = entry(timeline(page), "The brain is its own service")
    expect(decided.get_by_text("Decision", exact=True)).to_be_visible()
    expect(
        decided.get_by_text("Splitting memory across languages failed in v1")
    ).to_be_visible()

    open_decision(page, "The brain is its own service")
    expect(page).to_have_url(re.compile(r"/brain/history/[0-9a-f-]{36}$"))
    why = history(page).get_by_role("article", name="Why")
    expect(why).to_contain_text("Splitting memory across languages failed in v1")
    rejected = history(page).get_by_role("article", name="What was rejected")
    expect(rejected.get_by_role("listitem")).to_have_text(
        ["A memory module in each feature", "A shared library"]
    )
    page.reload()
    expect(why).to_contain_text("Splitting memory across languages failed in v1")


def test_i_can_replace_a_decision_and_still_read_the_old_one(
    page: Page, invite_code: str, base_url: str
):
    open_section(page, invite_code, base_url, "/brain/history")
    add_decision(page, "One database per feature", "Isolation")
    add_decision(page, "One Postgres, a database per service", "Fewer moving parts")

    open_decision(page, "One database per feature")
    history(page).get_by_role(
        "button", name="Replace with a newer decision…", exact=True
    ).click()
    form = history(page).get_by_role("form", name="Replace with a newer decision…")
    form.get_by_label("Newer decision", exact=True).select_option(
        label="One Postgres, a database per service"
    )
    form.get_by_role("button", name="Save", exact=True).click()

    expect(history(page).get_by_text("Replaced", exact=True)).to_be_visible()
    expect(
        history(page).get_by_role("button", name="Replace with a newer decision…")
    ).to_have_count(0)
    # The old one is still all there.
    expect(history(page).get_by_role("article", name="Why")).to_contain_text(
        "Isolation"
    )
    replaced_by = fact(page, "Replaced by").get_by_role(
        "link", name="One Postgres, a database per service", exact=True
    )
    expect(replaced_by).to_be_visible()

    replaced_by.click()
    expect(
        history(page).get_by_role(
            "heading", name="One Postgres, a database per service", level=2
        )
    ).to_be_visible()
    replaces = fact(page, "Replaces").get_by_role(
        "link", name="One database per feature", exact=True
    )
    expect(replaces).to_be_visible()
    expect(history(page).get_by_text("Replaced", exact=True)).to_have_count(0)

    # Both stay on the timeline, the old one marked.
    history(page).get_by_role("navigation", name="Breadcrumb").get_by_role(
        "link", name="History", exact=True
    ).click()
    expect(
        entry(timeline(page), "One database per feature").get_by_text(
            "Replaced", exact=True
        )
    ).to_be_visible()
    expect(
        entry(timeline(page), "One Postgres, a database per service").get_by_text(
            "Replaced", exact=True
        )
    ).to_have_count(0)


def test_history_can_be_narrowed_to_a_topic_or_a_named_thing(
    page: Page, invite_code: str, base_url: str
):
    open_section(page, invite_code, base_url, "/brain/history")
    call(page, "POST", "/topics", {"name": "Rust"})
    page.reload()
    add_happened(page, "Read the Rust book", topic="Rust")
    add_happened(page, "Started the platform", named="Personal platform")
    add_happened(page, "Went to a conference")
    filters = history(page).get_by_role("search", name="Filter history")

    filters.get_by_label("Topic", exact=True).select_option(label="Rust")
    expect(timeline(page).get_by_role("listitem")).to_have_count(1)
    expect(entry(timeline(page), "Read the Rust book")).to_be_visible()

    filters.get_by_label("Topic", exact=True).select_option(label="Any")
    filters.get_by_label("Named thing", exact=True).select_option(
        label="Personal platform"
    )
    expect(timeline(page).get_by_role("listitem")).to_have_count(1)
    expect(entry(timeline(page), "Started the platform")).to_be_visible()

    filters.get_by_label("Named thing", exact=True).select_option(label="Any")
    expect(timeline(page).get_by_role("listitem")).to_have_count(3)


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
def test_now_and_history_pass_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_section(page, invite_code, base_url, "/brain/now")
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    call(page, "POST", "/topics", {"name": "Rust"})
    call(
        page,
        "POST",
        "/items",
        {
            "kind": "state",
            "content": "Finishing the thesis draft before the January deadline",
            "occurred_at": (datetime.now(UTC) - timedelta(days=90)).isoformat(),
        },
    )
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    add_now(page, "Learning Rust ownership", topic="Rust")
    add_now(page, "Blocked on the reviewer", named="Personal platform")

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-now-{theme}")

    page.get_by_role("tab", name="History", exact=True).click()
    add_happened(page, "Took the distributed systems course", year="2021")
    add_happened(page, "Read the Rust book", topic="Rust")
    add_decision(
        page,
        "The brain is its own service",
        "Splitting memory across languages failed in v1",
        ["A memory module in each feature", "A shared library"],
    )
    add_decision(page, "Use one Postgres", "Fewer moving parts")

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-history-{theme}")

    history(page).get_by_role("button", name="Add decision", exact=True).click()
    rested(page)
    assert violations(page) == []
    capture(page, f"memory-history-adding-{theme}")
    history(page).get_by_role("button", name="Cancel", exact=True).click()

    open_decision(page, "Use one Postgres")
    history(page).get_by_role(
        "button", name="Replace with a newer decision…", exact=True
    ).click()
    history(page).get_by_label("Newer decision", exact=True).select_option(
        label="The brain is its own service"
    )
    history(page).get_by_role("button", name="Save", exact=True).click()
    expect(history(page).get_by_text("Replaced", exact=True)).to_be_visible()
    fact(page, "Replaced by").get_by_role("link").click()
    expect(fact(page, "Replaces").get_by_role("link")).to_be_visible()

    rested(page)
    assert violations(page) == []
    capture(page, f"memory-decision-{theme}")

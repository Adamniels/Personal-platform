"""The memory centre's Timeline section: every event the brain has recorded,
newest recorded first, under the day each was recorded, each what was done,
its evidence, where it came from and when, and a link to the item it is
about. See "The memory centre" and "Events" in Brain/README.md, and "The
shape" in Brain/docs/event-model.md."""

import re
import uuid
from datetime import date
from pathlib import Path

import pytest
from playwright.sync_api import Browser, Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
MONTHS = [
    "Jan",
    "Feb",
    "Mar",
    "Apr",
    "May",
    "Jun",
    "Jul",
    "Aug",
    "Sep",
    "Oct",
    "Nov",
    "Dec",
]
# "14:05", the time an entry shows when it happened the day it was recorded.
CLOCK = re.compile(r"^\d{2}:\d{2}$")


def open_memory(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/brain/timeline")
    expect(page.get_by_role("tablist", name="Memory sections")).to_be_visible()


def timeline(page: Page) -> Locator:
    return page.get_by_role("tabpanel", name="Timeline")


def entries(page: Page) -> Locator:
    return timeline(page).get_by_role("listitem")


def lines(page: Page) -> Locator:
    """Each entry's line: what was done, then its evidence."""
    return entries(page).locator("p:first-of-type")


def entry(page: Page, text: str) -> Locator:
    return entries(page).filter(has_text=text)


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


def sent(page: Page, **fields: object) -> dict:
    """An event a feature sends through the events endpoint."""
    body = {
        "verb": "exposed",
        "origin": "news",
        "evidence": "Shown the article 'Index only scans and when they stop working'.",
        "payload": {"rank": 3},
        "payload_version": 1,
        "idempotency_key": str(uuid.uuid4()),
    }
    body.update(fields)
    return call(page, "POST", "/events", body)


def today() -> str:
    """Today as an entry's recorded day is written, "13 Nov 2026"."""
    now = date.today()
    return f"{now.day} {MONTHS[now.month - 1]} {now.year}"


def enter_correct_and_retire(page: Page) -> dict:
    """A profile entry, entered, corrected and retired, as the Profile
    section does it, through the same routes. Returns the corrected one."""
    made = call(
        page,
        "POST",
        "/items",
        {"kind": "profile", "category": "identity", "content": "Lives in Uppsala"},
    )
    newer = call(
        page,
        "POST",
        f"/items/{made['id']}/edit",
        {"kind": "profile", "content": "Lives in Stockholm"},
    )
    call(page, "POST", f"/items/{newer['id']}/retire")
    return newer


def test_what_i_do_shows_on_the_timeline(page: Page, invite_code: str, base_url: str):
    open_memory(page, invite_code, base_url)
    enter_correct_and_retire(page)
    page.reload()

    # Newest first. A correction is a new version, so it records two events,
    # that it was corrected and the new version entered, at the same time.
    expect(lines(page)).to_have_text(
        [
            'You retired this Retired by hand: "Lives in Stockholm"',
            'You entered this Entered by hand as profile: "Lives in Stockholm"',
            (
                'You corrected this Corrected by hand from "Lives in Uppsala"'
                ' to "Lives in Stockholm"'
            ),
            'You entered this Entered by hand as profile: "Lives in Uppsala"',
        ]
    )
    today_heading = timeline(page).get_by_role("heading", name="Today", level=2)
    expect(today_heading).to_be_visible()
    expect(timeline(page).get_by_role("list", name="Today")).to_be_visible()
    for n in range(4):
        found = entries(page).nth(n)
        expect(found.locator("span[class*='time']")).to_have_text(CLOCK)
        expect(found).to_contain_text("Memory")
    # Nothing on the section changes an event.
    expect(timeline(page).get_by_role("button")).to_have_count(0)


def test_an_event_links_to_the_item_it_is_about(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)
    rule = call(page, "POST", "/items", {"kind": "rule", "content": "Answer briefly"})
    call(page, "POST", f"/items/{rule['id']}/retire")
    page.reload()

    retired = timeline(page).get_by_role("link", name=re.compile("You retired this"))
    expect(retired).to_have_attribute("href", f"/brain/everything/{rule['id']}")
    # The event that made it links to it too.
    entered = timeline(page).get_by_role("link", name=re.compile("You entered this"))
    expect(entered).to_have_attribute("href", f"/brain/everything/{rule['id']}")

    retired.click()

    expect(page).to_have_url(f"{base_url}/brain/everything/{rule['id']}")
    everything = page.get_by_role("tabpanel", name="Everything")
    expect(
        everything.get_by_role("heading", name="Answer briefly", level=2)
    ).to_be_visible()


def test_older_events_load_on_request(page: Page, invite_code: str, base_url: str):
    open_memory(page, invite_code, base_url)
    # Sixty events, one after another, so each is recorded after the last.
    page.evaluate(
        """async () => {
            for (let n = 0; n < 60; n += 1) {
                const response = await fetch("/brain/api/events", {
                    method: "POST",
                    credentials: "same-origin",
                    headers: {"content-type": "application/json"},
                    body: JSON.stringify({
                        verb: "opened",
                        origin: "news",
                        evidence: `Opened article ${n}.`,
                        payload: {},
                        payload_version: 1,
                        idempotency_key: `article-${n}`,
                    }),
                });
                if (response.status !== 201) throw new Error(await response.text());
            }
        }"""
    )
    page.reload()

    expect(entries(page)).to_have_count(50)
    expect(entries(page).first).to_contain_text("Opened article 59.")
    expect(entries(page).last).to_contain_text("Opened article 10.")
    more = timeline(page).get_by_role("button", name="Show earlier", exact=True)
    more.scroll_into_view_if_needed()
    scrolled = page.evaluate("window.scrollY")

    more.click()

    expect(entries(page)).to_have_count(60)
    # The place is kept: what was shown stays, the page does not move, and
    # focus is on the first of the earlier ones.
    expect(entries(page).first).to_contain_text("Opened article 59.")
    expect(entries(page).nth(50)).to_contain_text("Opened article 9.")
    expect(entries(page).last).to_contain_text("Opened article 0.")
    expect(entries(page).nth(50)).to_be_focused()
    assert page.evaluate("window.scrollY") == scrolled
    # Fewer than fifty came back, so there is nothing earlier.
    expect(more).to_have_count(0)


def test_an_event_from_a_feature_names_the_feature(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)
    sent(page, topic="postgres-indexing")
    sent(
        page,
        verb="asserted",
        evidence="Says he took a distributed systems course in 2021.",
        occurred_at="2021-01-01T00:00:00Z",
        occurred_precision="year",
    )
    page.reload()

    course = entry(page, "distributed systems course")
    expect(course.locator("p").first).to_have_text(
        "Asserted in news Says he took a distributed systems course in 2021."
    )
    # Happened long before it was recorded, so both times show.
    expect(course.locator("span[class*='time']")).to_have_text(
        f"Happened 2021 · Recorded {today()}"
    )

    shown = entry(page, "Index only scans")
    expect(shown.locator("p").first).to_have_text(
        "Exposed in news Shown the article 'Index only scans and when they stop"
        " working'."
    )
    detail = shown.locator("p").nth(1)
    expect(detail).to_contain_text("news")
    expect(detail).not_to_contain_text("Memory")
    # A topic a feature names first is made provisional, named by its slug.
    expect(detail.locator("[class*='badge']")).to_have_text("postgres-indexing")
    # The payload is for features, never shown, and the event is about no item.
    expect(shown).not_to_contain_text("rank")
    expect(shown.get_by_role("link")).to_have_count(0)


def test_another_account_sees_none_of_it(
    page: Page,
    browser: Browser,
    base_url: str,
    invite_code: str,
    another_invite_code: str,
):
    open_memory(page, invite_code, base_url)
    enter_correct_and_retire(page)
    sent(page)
    page.reload()
    expect(entries(page)).to_have_count(5)

    other = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        stranger = other.new_page()
        open_memory(stranger, another_invite_code, base_url)
        expect(
            timeline(stranger).get_by_text("Nothing recorded yet", exact=True)
        ).to_be_visible()
        expect(
            timeline(stranger).get_by_text(
                "What you enter and correct shows here.", exact=True
            )
        ).to_be_visible()
        expect(entries(stranger)).to_have_count(0)
        assert call(stranger, "GET", "/events") == []
    finally:
        other.close()


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


@pytest.mark.parametrize("theme", THEMES)
def test_the_timeline_passes_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_memory(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    enter_correct_and_retire(page)
    sent(page, topic="postgres-indexing")
    sent(
        page,
        verb="asserted",
        evidence="Says he took a distributed systems course in 2021.",
        occurred_at="2021-01-01T00:00:00Z",
        occurred_precision="year",
    )
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    expect(entries(page)).to_have_count(6)
    expect(page.get_by_role("button", name=re.compile("@"))).to_be_visible()

    assert violations(page) == []
    # On phone too, nothing makes the page scroll sideways.
    assert page.evaluate(
        "document.documentElement.scrollWidth <= document.documentElement.clientWidth"
    ), "the timeline scrolls sideways"
    capture(page, f"memory-timeline-{theme}")

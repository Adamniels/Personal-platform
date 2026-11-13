"""The memory centre, the brain's own application at /brain, opened from the
nav bar. See "The brain owns its own surface" in
Brain/docs/brain-architecture.md and "The memory centre" in Brain/README.md."""

import re
from pathlib import Path
from urllib.parse import urlparse

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
HOST = "platform.test"

# Each section's tab and its address, in order.
SECTIONS = [
    ("Profile", "/brain"),
    ("Rules", "/brain/rules"),
    ("Knowledge", "/brain/knowledge"),
    ("Now", "/brain/now"),
    ("History", "/brain/history"),
    ("Everything", "/brain/everything"),
    ("Timeline", "/brain/timeline"),
    ("Ask", "/brain/ask"),
]


def links_bar(page: Page) -> Locator:
    return page.locator("platform-nav .links")


def sections(page: Page) -> Locator:
    return page.get_by_role("tablist", name="Memory sections")


def open_memory(page: Page, invite_code: str) -> None:
    register_and_log_in(page, invite_code)
    page.goto("/brain")
    expect(sections(page)).to_be_visible()


def test_memory_opens_from_the_navigation(page: Page, invite_code: str, viewport: str):
    register_and_log_in(page, invite_code)
    links = links_bar(page).get_by_role("link")
    # Memory is fixed, after Home and before the features.
    expect(links).to_have_text(["Home", "Memory", "Canary"])
    memory = links_bar(page).get_by_role("link", name="Memory", exact=True)
    expect(memory).to_have_attribute("href", "/brain")
    expect(memory).not_to_have_attribute("aria-current", "page")
    if viewport == "phone":
        expect(memory.locator("svg")).to_be_visible()

    memory.click()

    expect(page).to_have_url("https://platform.test/brain")
    memory = links_bar(page).get_by_role("link", name="Memory", exact=True)
    expect(memory).to_have_attribute("aria-current", "page")
    expect(
        links_bar(page).get_by_role("link", name="Home", exact=True)
    ).not_to_have_attribute("aria-current", "page")
    expect(page.get_by_role("heading", name="Memory", level=1)).to_be_visible()
    expect(
        page.get_by_text(
            "What the platform knows about you. Correct anything that is wrong."
        )
    ).to_be_visible()
    tabs = sections(page).get_by_role("tab")
    expect(tabs).to_have_text([name for name, _ in SECTIONS])
    expect(tabs.first).to_have_attribute("aria-selected", "true")


def test_each_section_says_it_is_empty(page: Page, invite_code: str):
    open_memory(page, invite_code)

    for name, path in SECTIONS:
        sections(page).get_by_role("tab", name=name, exact=True).click()

        expect(page).to_have_url(f"https://platform.test{path}")
        panel = page.get_by_role("tabpanel", name=name)
        expect(panel).to_be_visible()
        expect(panel.get_by_text(f"Nothing in {name} yet", exact=True)).to_be_visible()

        # The address alone opens the same section.
        page.goto(path)
        expect(page.get_by_role("tabpanel", name=name)).to_be_visible()

    # Back returns to the section before.
    page.go_back()
    expect(page).to_have_url(f"https://platform.test{SECTIONS[-2][1]}")


def test_an_unknown_section_says_so(page: Page, invite_code: str):
    open_memory(page, invite_code)

    page.goto("/brain/no-such-section")

    expect(page.get_by_text("There is no such section", exact=True)).to_be_visible()
    page.get_by_role("link", name="Go to Profile", exact=True).click()
    expect(page.get_by_role("tabpanel", name="Profile")).to_be_visible()


def test_without_a_session_the_core_decides(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    # An expired access token: refreshed, and back to the section.
    page.context.clear_cookies(name="__Host-access")

    page.goto("/brain/knowledge")

    expect(page).to_have_url("https://platform.test/brain/knowledge")
    expect(page.get_by_role("tabpanel", name="Knowledge")).to_be_visible()

    # No session at all: the login page, with the way back.
    page.context.clear_cookies()
    page.goto("/brain/rules")
    expect(page).to_have_url(re.compile(r"/login\?return_to=%2Fbrain%2Frules$"))


def test_the_memory_centre_loads_nothing_from_outside(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    requested: list[str] = []
    page.on("request", lambda request: requested.append(request.url))

    page.goto("/brain")
    page.evaluate("document.fonts.ready.then(() => null)")
    for name, _ in SECTIONS:
        sections(page).get_by_role("tab", name=name, exact=True).click()
        expect(page.get_by_role("tabpanel", name=name)).to_be_visible()

    assert any(urlparse(url).path.startswith("/brain/assets/") for url in requested)
    outside = [url for url in requested if urlparse(url).hostname != HOST]
    assert outside == []


@pytest.mark.parametrize("theme", THEMES)
def test_the_memory_centre_passes_the_accessibility_check(
    page: Page, invite_code: str, viewport: str, theme: str
):
    open_memory(page, invite_code)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    expect(page.get_by_role("tabpanel", name="Profile")).to_be_visible()
    # The bar has drawn once more with the account, so it is checked whole.
    expect(page.get_by_role("button", name=re.compile("@"))).to_be_visible()
    page.evaluate("document.fonts.ready.then(() => null)")
    page.add_script_tag(path=str(AXE))

    violations = page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )

    assert violations == []
    capture(page, f"memory-frame-{theme}")


def test_the_tabs_scroll_on_phone_and_the_page_does_not(page: Page, invite_code: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_memory(page, invite_code)
    row = sections(page)

    tabs = row.get_by_role("tab")
    expect(tabs).to_have_count(len(SECTIONS))
    tops = {
        tabs.nth(index).evaluate("e => e.offsetTop") for index in range(len(SECTIONS))
    }
    assert len(tops) == 1, "the row wrapped"
    assert row.evaluate("e => e.scrollWidth > e.clientWidth"), "the row does not scroll"
    assert page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    ), "the page scrolls sideways"

    # The last tab is reached by scrolling the row, and still opens.
    last, path = SECTIONS[-1]
    tabs.last.click()
    expect(page).to_have_url(f"https://platform.test{path}")
    expect(page.get_by_role("tabpanel", name=last)).to_be_visible()
    assert page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    ), "the page scrolls sideways"

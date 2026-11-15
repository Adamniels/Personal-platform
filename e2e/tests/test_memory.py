"""The memory centre, the brain's own application at /brain, opened from the
nav bar. See "The brain owns its own surface" in
Brain/docs/brain-architecture.md and "The memory centre" in Brain/README.md."""

import re
from pathlib import Path
from urllib.parse import urlparse

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture, open_section, register_and_log_in
from conftest import sections as tabs_or_select

THEMES = ["light", "dark"]
LABEL = "Memory sections"
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"

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


def nav_link(page: Page, name: str) -> Locator:
    return page.get_by_role("navigation").get_by_role("link", name=name, exact=True)


def sections(page: Page) -> Locator:
    """The tab list on desktop, and the select on phone."""
    return tabs_or_select(page, LABEL)


def page_scrolls_sideways(page: Page) -> bool:
    return page.evaluate(
        "() => document.documentElement.scrollWidth > window.innerWidth"
    )


def open_memory(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/brain")
    expect(sections(page)).to_be_visible()


def test_memory_opens_from_the_navigation(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    # Memory is fixed, after Home and before the features: the links in the
    # order a reader meets them, leaving out the mark, which sits in the rail
    # on desktop and in the top bar on phone.
    expect(nav_link(page, "Dev flow")).to_be_visible()
    names = (
        page.get_by_role("navigation")
        .get_by_role("link")
        .evaluate_all("links => links.map(link => link.getAttribute('aria-label'))")
    )
    links = [name for name in names if name != "Platform"]
    assert links == ["Home", "Memory", "Canary", "Review", "Dev flow"]
    memory = nav_link(page, "Memory")
    expect(memory).to_have_attribute("href", "/brain")
    expect(memory).not_to_have_attribute("aria-current", "page")
    if viewport == "phone":
        expect(memory.locator("svg")).to_be_visible()

    memory.click()

    expect(page).to_have_url(f"{base_url}/brain")
    memory = nav_link(page, "Memory")
    expect(memory).to_have_attribute("aria-current", "page")
    expect(nav_link(page, "Home")).not_to_have_attribute("aria-current", "page")
    expect(page.get_by_role("heading", name="Memory", level=1)).to_be_visible()
    expect(
        page.get_by_text(
            "What the platform knows about you. Correct anything that is wrong."
        )
    ).to_be_visible()
    names = [name for name, _ in SECTIONS]
    if viewport == "desktop":
        tabs = page.get_by_role("tablist", name=LABEL).get_by_role("tab")
        expect(tabs).to_have_text(names)
        expect(tabs.first).to_have_attribute("aria-selected", "true")
    else:
        select = page.get_by_role("combobox", name=LABEL, exact=True)
        expect(select.locator("option")).to_have_text(names)
        expect(select).to_have_value("profile")


def test_each_section_opens_from_its_tab_and_its_address(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)

    for name, path in SECTIONS[1:]:
        open_section(page, LABEL, name)

        expect(page).to_have_url(f"{base_url}{path}")
        expect(page.get_by_role("tabpanel", name=name)).to_be_visible()

        # The address alone opens the same section.
        page.goto(path)
        expect(page.get_by_role("tabpanel", name=name)).to_be_visible()

    # Back returns to the section before the last one opened.
    page.go_back()
    expect(page).to_have_url(f"{base_url}{SECTIONS[-2][1]}")


def test_an_unknown_section_says_so(page: Page, invite_code: str, base_url: str):
    open_memory(page, invite_code, base_url)

    page.goto("/brain/no-such-section")

    expect(page.get_by_text("There is no such section", exact=True)).to_be_visible()
    page.get_by_role("link", name="Go to Profile", exact=True).click()
    expect(page.get_by_role("tabpanel", name="Profile")).to_be_visible()


def test_without_a_session_the_core_decides(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    # An expired access token: refreshed, and back to the section.
    page.context.clear_cookies(name="__Host-access")

    page.goto("/brain/knowledge")

    expect(page).to_have_url(f"{base_url}/brain/knowledge")
    expect(page.get_by_role("tabpanel", name="Knowledge")).to_be_visible()

    # No session at all: the login page, with the way back.
    page.context.clear_cookies()
    page.goto("/brain/rules")
    expect(page).to_have_url(re.compile(r"/login\?return_to=%2Fbrain%2Frules$"))


def test_the_memory_centre_loads_nothing_from_outside(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    requested: list[str] = []
    page.on("request", lambda request: requested.append(request.url))

    page.goto("/brain")
    page.evaluate("document.fonts.ready.then(() => null)")
    for name, _ in SECTIONS:
        open_section(page, LABEL, name)
        expect(page.get_by_role("tabpanel", name=name)).to_be_visible()

    assert any(urlparse(url).path.startswith("/brain/assets/") for url in requested)
    # Another host, or another port of this one, is another origin.
    platform = urlparse(base_url).netloc
    outside = [url for url in requested if urlparse(url).netloc != platform]
    assert outside == []


@pytest.mark.parametrize("theme", THEMES)
def test_the_memory_centre_passes_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_memory(page, invite_code, base_url)
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


def test_on_phone_the_sections_are_a_select_and_the_page_does_not_scroll(
    page: Page, invite_code: str, base_url: str
):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_memory(page, invite_code, base_url)

    expect(page.get_by_role("tablist", name=LABEL)).to_have_count(0)
    select = page.get_by_role("combobox", name=LABEL, exact=True)
    expect(select).to_be_visible()
    expect(select.locator("option")).to_have_text([name for name, _ in SECTIONS])
    assert not page_scrolls_sideways(page), "the page scrolls sideways"

    select.select_option(label="Rules")

    expect(page).to_have_url(f"{base_url}/brain/rules")
    expect(select).to_have_value("rules")
    expect(page.get_by_role("tabpanel", name="Rules")).to_be_visible()
    assert not page_scrolls_sideways(page), "the page scrolls sideways"

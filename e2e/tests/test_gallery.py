"""The component gallery at /design/gallery/, where each component of
@platform/ui is shown in every state. See design/ui/README.md.

The tests read the styles the browser computed and compare them with the
token as the same page resolves it, so no colour or size is written here: a
change to tokens.css changes both sides, and only a component that stops
using its token fails."""

from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture

GALLERY = "/design/gallery/"
THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"

# Each kind of "Button" in section 4 of design/DESIGN.md: the label the
# gallery gives it, and the token each property takes. None is transparent.
KINDS = {
    "primary": ("Primary", "--accent", "--on-accent", "--accent-border", "600"),
    "secondary": ("Secondary", "--surface", "--text", "--border-strong", "500"),
    "ghost": ("Ghost", None, "--text-2", None, "500"),
    "destructive": ("Destructive", "--danger-soft", "--danger", "--danger", "600"),
    "disabled": ("Disabled", "--surface-2", "--text-3", "--border", None),
}

# Resolves a token through a probe element in the page, so the value is the
# one the page's own tokens.css and theme give, in the browser's format.
RESOLVE = """([token, property, read]) => {
    const probe = document.createElement("div");
    probe.style.setProperty(property, token ? `var(${token})` : "transparent");
    document.body.append(probe);
    const value = getComputedStyle(probe).getPropertyValue(read);
    probe.remove();
    return value;
}"""

STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        background: style.backgroundColor,
        color: style.color,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        weight: style.fontWeight,
        height: style.height,
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        outline: [style.outlineStyle, style.outlineWidth, style.outlineColor],
        outlineOffset: style.outlineOffset,
    };
}"""


def resolve(page: Page, token: str | None, prop: str, read: str | None = None) -> str:
    """The value `prop` takes when set to `var(<token>)`, or to transparent
    when the token is None. With `read`, that longhand is read instead, for a
    token that is a shorthand."""
    return page.evaluate(RESOLVE, [token, prop, read or prop])


def open_gallery(page: Page, theme: str) -> None:
    response = page.goto(GALLERY)
    assert response is not None and response.status == 200
    page.get_by_label("Theme").select_option(label=theme.capitalize())
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    page.evaluate("document.fonts.ready.then(() => null)")


def button_section(page: Page) -> Locator:
    return page.get_by_role("region", name="Button")


def button(page: Page, label: str) -> Locator:
    return button_section(page).get_by_role("button", name=label, exact=True)


@pytest.mark.parametrize("theme", THEMES)
def test_the_gallery_is_served(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)

    section = button_section(page)
    expect(section).to_be_visible()
    expect(section.get_by_role("heading", name="Button")).to_be_visible()
    capture(page, f"gallery-button-{theme}")


@pytest.mark.parametrize("theme", THEMES)
def test_the_primary_button_uses_the_accent_values(page: Page, theme: str):
    open_gallery(page, theme)

    style = button(page, "Primary").evaluate(STYLE)

    assert style["background"] == resolve(page, "--accent", "background-color")
    assert style["color"] == resolve(page, "--on-accent", "color")
    assert style["borders"] == [resolve(page, "--accent-border", "color")] * 4
    assert style["height"] == resolve(page, "--control-h", "height")
    assert (
        style["radii"] == [resolve(page, "--radius-md", "border-top-left-radius")] * 4
    )


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", ["secondary", "ghost", "destructive", "disabled"])
def test_each_button_kind_reads_its_own_values(page: Page, theme: str, kind: str):
    label, fill, text, border, weight = KINDS[kind]
    open_gallery(page, theme)

    style = button(page, label).evaluate(STYLE)

    assert style["background"] == resolve(page, fill, "background-color")
    assert style["color"] == resolve(page, text, "color")
    assert style["borders"] == [resolve(page, border, "color")] * 4
    assert style["height"] == resolve(page, "--control-h", "height")
    assert (
        style["radii"] == [resolve(page, "--radius-md", "border-top-left-radius")] * 4
    )
    if weight is not None:
        assert style["weight"] == weight


def test_the_button_kinds_differ(page: Page):
    """Guards the test above: were the gallery to render every kind alike,
    the comparisons could all pass against one shared token."""
    open_gallery(page, "light")

    backgrounds = {
        kind: button(page, label).evaluate(STYLE)["background"]
        for kind, (label, *_rest) in KINDS.items()
    }
    assert len(set(backgrounds.values())) == len(KINDS), backgrounds


def test_a_focused_button_shows_the_ring(page: Page):
    open_gallery(page, "light")
    primary = button(page, "Primary")

    # A click on the heading, which is not focusable, puts the start of the
    # Tab order there, so the next Tab reaches the first button after it.
    button_section(page).get_by_role("heading", name="Button").click()
    page.keyboard.press("Tab")

    expect(primary).to_be_focused()
    style = primary.evaluate(STYLE)
    ring = [
        resolve(page, "--focus-ring", "outline", read)
        for read in ["outline-style", "outline-width", "outline-color"]
    ]
    assert ring[0] == "solid"
    assert style["outline"] == ring
    assert style["outlineOffset"] == resolve(page, "--focus-offset", "outline-offset")


def test_buttons_are_44px_tall_on_phone(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")

    buttons = button_section(page).get_by_role("button")
    assert buttons.count() >= len(KINDS)
    for index in range(buttons.count()):
        box = buttons.nth(index).bounding_box()
        assert box is not None and box["height"] == 44, buttons.nth(index)


@pytest.mark.parametrize("theme", THEMES)
def test_the_gallery_passes_the_accessibility_check(page: Page, theme: str):
    open_gallery(page, theme)
    page.add_script_tag(path=str(AXE))

    violations = page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )
    assert violations == []

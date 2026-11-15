"""The component gallery at /design/gallery/, where each component of
@platform/ui is shown in every state. See design/ui/README.md.

The tests read the styles the browser computed and compare them with the
token as the same page resolves it, so no colour or size is written here: a
change to tokens.css changes both sides, and only a component that stops
using its token fails."""

import re
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
    "secondary": ("Secondary", None, "--text", "--border-strong", "500"),
    "ghost": ("Ghost", None, "--text-2", None, "500"),
    "destructive": ("Destructive", "--danger-soft", "--danger", "--danger", "600"),
    "disabled": ("Disabled", None, "--text-3", "--border", None),
}

# The side padding of each kind that sets one, from the same section.
BUTTON_PADDING = {
    "primary": "18px",
    "secondary": "18px",
    "ghost": "14px",
    "destructive": "18px",
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
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        padding: [style.paddingLeft, style.paddingRight],
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
@pytest.mark.parametrize("kind", list(KINDS))
def test_a_button_is_a_pill(page: Page, theme: str, kind: str):
    label = KINDS[kind][0]
    open_gallery(page, theme)

    style = button(page, label).evaluate(STYLE)

    assert style["height"] == resolve(page, "--control-h", "height")
    assert (
        style["radii"] == [resolve(page, "--radius-full", "border-top-left-radius")] * 4
    )
    if kind in BUTTON_PADDING:
        assert style["padding"] == [BUTTON_PADDING[kind]] * 2
    if kind != "ghost":
        assert style["borderWidths"] == ["1px"] * 4


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", list(KINDS))
def test_a_button_reads_its_values(page: Page, theme: str, kind: str):
    label, fill, text, border, weight = KINDS[kind]
    open_gallery(page, theme)

    style = button(page, label).evaluate(STYLE)

    assert style["background"] == resolve(page, fill, "background-color")
    assert style["color"] == resolve(page, text, "color")
    assert style["borders"] == [resolve(page, border, "color")] * 4
    if weight is not None:
        assert style["weight"] == weight


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", ["secondary", "ghost"])
def test_a_secondary_or_ghost_button_fills_on_hover(page: Page, theme: str, kind: str):
    open_gallery(page, theme)
    target = button(page, KINDS[kind][0])

    target.hover()

    assert target.evaluate(STYLE)["background"] == resolve(
        page, "--surface-2", "background-color"
    )


def test_the_button_kinds_differ(page: Page):
    """Guards the test above: were the gallery to render every kind alike,
    the comparisons could all pass against one shared token. Three kinds are
    transparent, so a kind is told apart by its fill, text and border."""
    open_gallery(page, "light")

    looks = {}
    for kind, (label, *_rest) in KINDS.items():
        style = button(page, label).evaluate(STYLE)
        looks[kind] = (style["background"], style["color"], style["borders"][0])
    assert len(set(looks.values())) == len(KINDS), looks


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


HEADING_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        family: style.fontFamily,
        weight: style.fontWeight,
        transform: style.textTransform,
    };
}"""


@pytest.mark.parametrize("theme", THEMES)
def test_gallery_headings_are_capitals(page: Page, theme: str):
    """Hard rule 12 of design/DESIGN.md, for the gallery's own headings: its
    title and the heading of each component's section. A component's own
    heading, such as a card's title, is its slice's to test."""
    open_gallery(page, theme)
    family = resolve(page, "--font-heading", "font-family")

    headings = page.locator("h1, main > section > h2")
    assert headings.count() == 1 + page.locator("main > section").count()
    for index in range(headings.count()):
        heading = headings.nth(index)
        style = heading.evaluate(HEADING_STYLE)
        assert style == {
            "family": family,
            "weight": "600",
            "transform": "uppercase",
        }, heading.text_content()


@pytest.mark.parametrize("opened", ["page", "dialog", "toasts"])
@pytest.mark.parametrize("theme", THEMES)
def test_the_gallery_passes_the_accessibility_check(
    page: Page, viewport: str, theme: str, opened: str
):
    open_gallery(page, theme)
    if opened == "dialog":
        open_dialog(page)
    elif opened == "toasts":
        raise_toasts(page)
    page.add_script_tag(path=str(AXE))

    violations = page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )
    assert violations == []


# The form controls of "Text field, textarea, select" and "Checkbox" in
# section 4 of design/DESIGN.md. Each field section shows the same states,
# each field labelled with its state.
FIELDS = {
    "text field": ("Text field", "textbox"),
    "text area": ("Text area", "textbox"),
    "select": ("Select", "combobox"),
}

FIELD_STYLE = """element => {
    const style = getComputedStyle(element);
    const placeholder = getComputedStyle(element, "::placeholder");
    return {
        background: style.backgroundColor,
        color: style.color,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        height: style.height,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        padding: [style.paddingTop, style.paddingRight,
                  style.paddingBottom, style.paddingLeft],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        placeholder: placeholder.color,
        accent: style.accentColor,
    };
}"""

TEXT_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        color: style.color,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        weight: style.fontWeight,
        decoration: style.textDecorationLine,
    };
}"""


def field(page: Page, kind: str, label: str) -> Locator:
    title, role = FIELDS[kind]
    region = page.get_by_role("region", name=title)
    return region.get_by_role(role, name=label, exact=True)


def described_by(control: Locator) -> Locator:
    """The first element the control's aria-describedby names."""
    first = control.get_attribute("aria-describedby")
    assert first, "the control has no description"
    return control.page.locator(f"[id='{first.split()[0]}']")


@pytest.mark.parametrize("theme", THEMES)
def test_the_form_controls_are_shown(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)

    for title in ["Text field", "Text area", "Select", "Checkbox"]:
        expect(page.get_by_role("region", name=title)).to_be_visible()
    capture(page, f"gallery-form-controls-{theme}")


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", list(FIELDS))
def test_a_field_reads_its_values(page: Page, theme: str, kind: str):
    open_gallery(page, theme)
    control = field(page, kind, "Empty")

    style = control.evaluate(FIELD_STYLE)

    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["color"] == resolve(page, "--text", "color")
    assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    if kind == "text area":
        # A text area is as tall as its rows, and never shorter than a field.
        assert float(style["height"].removesuffix("px")) >= float(
            resolve(page, "--control-h", "height").removesuffix("px")
        )
        assert style["placeholder"] == resolve(page, "--text-3", "color")
    elif kind == "text field":
        assert style["height"] == resolve(page, "--control-h", "height")
        assert style["placeholder"] == resolve(page, "--text-3", "color")
    else:
        assert style["height"] == resolve(page, "--control-h", "height")

    label = page.get_by_role("region", name=FIELDS[kind][0]).locator(
        f"label[for='{control.get_attribute('id')}']"
    )
    text = label.evaluate(TEXT_STYLE)
    assert text["color"] == resolve(page, "--text-2", "color")
    assert text["fontSize"] == resolve(page, "--text-caption-size", "font-size")
    assert text["lineHeight"] == resolve(page, "--text-caption-line", "line-height")
    assert text["weight"] == "500"
    # The label sits above the field.
    label_box, control_box = label.bounding_box(), control.bounding_box()
    assert label_box is not None and control_box is not None
    assert label_box["y"] + label_box["height"] <= control_box["y"]

    help_text = described_by(field(page, kind, "With help")).evaluate(TEXT_STYLE)
    assert help_text["color"] == resolve(page, "--text-3", "color")
    assert help_text["fontSize"] == resolve(page, "--text-caption-size", "font-size")


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", ["text field", "select"])
def test_a_field_is_a_pill(page: Page, theme: str, kind: str):
    open_gallery(page, theme)

    style = field(page, kind, "Empty").evaluate(FIELD_STYLE)

    assert style["height"] == resolve(page, "--control-h", "height")
    assert (
        style["radii"] == [resolve(page, "--radius-full", "border-top-left-radius")] * 4
    )
    assert [style["padding"][1], style["padding"][3]] == ["16px"] * 2
    assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["fontSize"] == resolve(page, "--input-font-size", "font-size")


@pytest.mark.parametrize("theme", THEMES)
def test_a_text_area_has_inset_corners(page: Page, theme: str):
    open_gallery(page, theme)

    style = field(page, "text area", "Empty").evaluate(FIELD_STYLE)

    assert (
        style["radii"] == [resolve(page, "--radius-md", "border-top-left-radius")] * 4
    )
    assert style["padding"] == ["12px", "16px", "12px", "16px"]
    assert style["fontSize"] == resolve(page, "--text-body-lg-size", "font-size")
    assert style["lineHeight"] == resolve(page, "--text-body-lg-line", "line-height")


# Where a control's text starts, measured from its border box: the left edge
# of its content box, and its top for a text area, which starts its text at
# the top, or its middle for a one line field, which centres its text.
TEXT_ORIGIN = """element => {
    const style = getComputedStyle(element);
    const px = value => parseFloat(value);
    const top = px(style.borderTopWidth) + px(style.paddingTop);
    const bottom = px(style.borderBottomWidth) + px(style.paddingBottom);
    const height = element.getBoundingClientRect().height;
    return {
        height,
        left: px(style.borderLeftWidth) + px(style.paddingLeft),
        top,
        middle: (top + height - bottom) / 2,
    };
}"""


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", list(FIELDS))
def test_a_field_with_an_error_keeps_its_size(page: Page, theme: str, kind: str):
    open_gallery(page, theme)
    plain = field(page, kind, "Empty")
    wrong = field(page, kind, "With an error")

    style = wrong.evaluate(FIELD_STYLE)
    assert style["borders"] == [resolve(page, "--danger", "color")] * 4
    assert style["borderWidths"] == ["2px"] * 4

    before, after = plain.evaluate(TEXT_ORIGIN), wrong.evaluate(TEXT_ORIGIN)
    assert after["height"] == before["height"]
    assert after["left"] == before["left"]
    where = "top" if kind == "text area" else "middle"
    assert after[where] == before[where]


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("kind", list(FIELDS))
def test_a_field_with_an_error_reads_its_values(page: Page, theme: str, kind: str):
    open_gallery(page, theme)
    control = field(page, kind, "With an error")

    expect(control).to_have_attribute("aria-invalid", "true")
    message = described_by(control)
    expect(message).to_have_text(re.compile(r"^Error: "))
    text = message.evaluate(TEXT_STYLE)
    assert text["color"] == resolve(page, "--danger", "color")
    assert text["weight"] == "500"


@pytest.mark.parametrize("kind", list(FIELDS))
def test_fields_are_16px_and_44px_on_phone(page: Page, kind: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")

    title, role = FIELDS[kind]
    controls = page.get_by_role("region", name=title).get_by_role(role)
    assert controls.count() >= 5
    for index in range(controls.count()):
        control = controls.nth(index)
        style = control.evaluate(FIELD_STYLE)
        assert style["fontSize"] == "16px", control
        box = control.bounding_box()
        assert box is not None
        if kind == "text area":
            assert box["height"] >= 44, control
        else:
            assert box["height"] == 44, control


@pytest.mark.parametrize("theme", THEMES)
def test_the_checkbox_is_18px(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Checkbox")
    box = region.get_by_role("checkbox", name="Unchecked", exact=True)

    size = box.bounding_box()
    assert size is not None and size["width"] == 18 and size["height"] == 18

    # `has` is matched inside each label, so it is not scoped to the region.
    row = region.locator(
        "label", has=page.get_by_role("checkbox", name="Unchecked", exact=True)
    )
    row_box = row.bounding_box()
    assert row_box is not None
    assert f"{row_box['height']:g}px" == resolve(page, "--row-h", "height")


@pytest.mark.parametrize("theme", THEMES)
def test_a_checkbox_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Checkbox")
    box = region.get_by_role("checkbox", name="Unchecked", exact=True)

    assert box.evaluate(FIELD_STYLE)["accent"] == resolve(
        page, "--info", "accent-color"
    )

    done = region.locator("label", has_text="Done").evaluate(TEXT_STYLE)
    assert done["color"] == resolve(page, "--text-3", "color")
    assert done["decoration"] == "line-through"


@pytest.mark.parametrize(
    ("title", "role", "label"),
    [
        ("Text field", "textbox", "Empty"),
        ("Text area", "textbox", "Empty"),
        ("Select", "combobox", "Empty"),
        ("Checkbox", "checkbox", "Unchecked"),
    ],
)
def test_each_control_shows_the_focus_ring(
    page: Page, title: str, role: str, label: str
):
    open_gallery(page, "light")
    region = page.get_by_role("region", name=title)
    control = region.get_by_role(role, name=label, exact=True)

    # As for the button: a click on the heading puts the start of the Tab
    # order there, so the next Tab reaches the section's first control.
    region.get_by_role("heading", name=title).click()
    page.keyboard.press("Tab")

    expect(control).to_be_focused()
    style = control.evaluate(STYLE)
    ring = [
        resolve(page, "--focus-ring", "outline", read)
        for read in ["outline-style", "outline-width", "outline-color"]
    ]
    assert ring[0] == "solid"
    assert style["outline"] == ring
    assert style["outlineOffset"] == resolve(page, "--focus-offset", "outline-offset")


# The labels and meters of "Badge and status label" and "Progress bar and
# meter" in section 4 of design/DESIGN.md. The gallery shows each status tone
# with the design's first example word, which is how a test finds it.
TONES = {
    "running": ("Running", "--info-soft", "--info"),
    "needs you": ("Needs you", "--accent-soft", "--accent-text"),
    "done": ("Done", "--ok-soft", "--ok"),
    "failed": ("Failed", "--danger-soft", "--danger"),
    "idle": ("Idle", "--surface-3", "--text-2"),
}

LABEL_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        background: style.backgroundColor,
        color: style.color,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        height: style.height,
        width: style.width,
        paddings: [style.paddingLeft, style.paddingRight],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        fontFamily: style.fontFamily,
        weight: style.fontWeight,
        children: element.children.length,
    };
}"""


def status(page: Page, tone: str) -> Locator:
    word = TONES[tone][0]
    region = page.get_by_role("region", name="Status label")
    return region.get_by_text(word, exact=True)


def radii(page: Page, token: str) -> list[str]:
    return [resolve(page, token, "border-top-left-radius")] * 4


def assert_caption(page: Page, style: dict) -> None:
    assert style["fontSize"] == resolve(page, "--text-caption-size", "font-size")
    assert style["lineHeight"] == resolve(page, "--text-caption-line", "line-height")
    assert style["weight"] == "500"


def channels(colour: str) -> tuple[float, float, float]:
    """The red, green and blue of a computed `rgb(…)` colour, from 0 to 1."""
    match = re.fullmatch(r"rgba?\((\d+), (\d+), (\d+)(?:, [\d.]+)?\)", colour)
    assert match, f"not an opaque rgb colour: {colour}"
    red, green, blue = (int(value) / 255 for value in match.groups())
    return red, green, blue


def luminance(colour: str) -> float:
    """Relative luminance, as WCAG 2 defines it."""

    def linear(value: float) -> float:
        return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4

    red, green, blue = (linear(value) for value in channels(colour))
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue


def contrast(text: str, fill: str) -> float:
    lighter, darker = sorted([luminance(text), luminance(fill)], reverse=True)
    return (lighter + 0.05) / (darker + 0.05)


@pytest.mark.parametrize("theme", THEMES)
def test_the_labels_and_meters_are_shown(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)

    for title in ["Badge", "Status label", "Tool chip", "Progress bar", "Meter"]:
        expect(page.get_by_role("region", name=title)).to_be_visible()
    capture(page, f"gallery-labels-and-meters-{theme}")


@pytest.mark.parametrize("theme", THEMES)
@pytest.mark.parametrize("tone", list(TONES))
def test_a_status_label_is_a_26px_pill(page: Page, theme: str, tone: str):
    _word, fill, text = TONES[tone]
    open_gallery(page, theme)
    label = status(page, tone)

    style = label.evaluate(LABEL_STYLE)
    assert style["background"] == resolve(page, fill, "background-color")
    assert style["color"] == resolve(page, text, "color")
    assert style["height"] == "26px"
    assert style["paddings"] == ["10px", "10px"]
    assert style["radii"] == radii(page, "--radius-full")
    assert_caption(page, style)

    # The dot: 6px, round, in the text colour, and hidden from assistive
    # technology, so the word alone is read.
    dot = label.locator("[aria-hidden='true']")
    expect(dot).to_have_count(1)
    dot_style = dot.evaluate(LABEL_STYLE)
    assert dot_style["width"] == dot_style["height"] == "6px"
    assert dot_style["background"] == resolve(page, text, "background-color")
    assert dot_style["radii"] == radii(page, "--radius-full")


def test_the_status_tones_differ(page: Page):
    """Guards the test above, as for the buttons: five tones rendered alike
    could all pass against one shared token."""
    open_gallery(page, "light")

    backgrounds = {
        tone: status(page, tone).evaluate(LABEL_STYLE)["background"] for tone in TONES
    }
    assert len(set(backgrounds.values())) == len(TONES), backgrounds


@pytest.mark.parametrize("theme", THEMES)
def test_a_badge_is_a_26px_pill_on_the_third_surface(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Badge")

    for text in ["Topic", "14"]:
        style = region.get_by_text(text, exact=True).evaluate(LABEL_STYLE)
        assert style["background"] == resolve(page, "--surface-3", "background-color")
        assert style["color"] == resolve(page, "--text-2", "color")
        assert style["height"] == "26px"
        assert style["paddings"] == ["10px", "10px"]
        assert style["radii"] == radii(page, "--radius-full")
        assert style["children"] == 0, "a badge has no dot"
        assert_caption(page, style)


@pytest.mark.parametrize("theme", THEMES)
def test_the_tool_chip_is_a_22px_outlined_pill(page: Page, theme: str):
    open_gallery(page, theme)

    region = page.get_by_role("region", name="Tool chip")
    chip = region.get_by_text("Claude", exact=True)
    style = chip.evaluate(LABEL_STYLE)
    assert style["background"] == resolve(page, None, "background-color")
    assert style["color"] == resolve(page, "--text-2", "color")
    assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["height"] == "22px"
    assert style["paddings"] == ["10px", "10px"]
    assert style["radii"] == radii(page, "--radius-full")
    assert style["children"] == 0, "a chip is its text alone"
    assert_caption(page, style)


def bar(page: Page, name: str) -> Locator:
    region = page.get_by_role("region", name="Progress bar")
    return region.get_by_role("img", name=name, exact=True)


@pytest.mark.parametrize("theme", THEMES)
def test_the_progress_track_has_no_border(page: Page, theme: str):
    open_gallery(page, theme)

    for kind, fill in [("Progress", "--accent"), ("Budget", "--info")]:
        track = bar(page, f"{kind}: 62%")
        style = track.evaluate(LABEL_STYLE)
        assert style["height"] == "6px"
        assert style["background"] == resolve(page, "--surface-3", "background-color")
        assert style["borderWidths"] == ["0px"] * 4
        assert style["radii"] == radii(page, "--radius-full")

        fill_style = track.locator("div").evaluate(LABEL_STYLE)
        assert fill_style["background"] == resolve(page, fill, "background-color")

    # The fill is as wide as the value, across the whole track.
    for value in [0, 62, 100]:
        track = bar(page, f"Progress: {value}%")
        inner = track.evaluate("element => element.clientWidth")
        width = track.locator("div").evaluate(
            "element => element.getBoundingClientRect().width"
        )
        assert width == pytest.approx(inner * value / 100, abs=0.5), value


def test_a_meter_reads_its_values(page: Page):
    open_gallery(page, "light")
    region = page.get_by_role("region", name="Meter")

    expect(region.get_by_role("img", name="Context used: 62%")).to_be_visible()
    label = region.get_by_text("CTX", exact=True).evaluate(LABEL_STYLE)
    value = region.get_by_text("62%", exact=True).evaluate(LABEL_STYLE)
    for style in [label, value]:
        assert style["fontFamily"] == resolve(page, "--font-mono", "font-family")
        assert style["fontSize"] == resolve(page, "--text-mono-size", "font-size")
    assert label["width"] == "28px"


@pytest.mark.parametrize("theme", THEMES)
def test_status_text_keeps_its_contrast(page: Page, theme: str):
    open_gallery(page, theme)

    ratios = {}
    for tone in TONES:
        style = status(page, tone).evaluate(LABEL_STYLE)
        ratios[tone] = contrast(style["color"], style["background"])
    assert all(ratio >= 4.5 for ratio in ratios.values()), ratios


# The layout pieces of "Tabs", "Card", "Empty state" and "Timeline entry" in
# section 4 of design/DESIGN.md. The gallery names each example by what it
# shows, which is how a test finds it.
BOX_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        background: style.backgroundColor,
        color: style.color,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        borderStyles: [style.borderTopStyle, style.borderRightStyle,
                       style.borderBottomStyle, style.borderLeftStyle],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        padding: [style.paddingTop, style.paddingRight,
                  style.paddingBottom, style.paddingLeft],
        height: style.height,
        width: style.width,
        gap: style.columnGap,
        rowGap: style.rowGap,
        shadow: style.boxShadow,
        weight: style.fontWeight,
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        textAlign: style.textAlign,
        textTransform: style.textTransform,
        overflowX: style.overflowX,
        flexWrap: style.flexWrap,
        whiteSpace: style.whiteSpace,
        outline: [style.outlineStyle, style.outlineWidth, style.outlineColor],
    };
}"""


def tablist(page: Page, name: str) -> Locator:
    region = page.get_by_role("region", name="Tabs")
    return region.get_by_role("tablist", name=name, exact=True)


def card(page: Page, name: str) -> Locator:
    region = page.get_by_role("region", name="Card")
    return region.get_by_role("article", name=name, exact=True)


@pytest.mark.parametrize("theme", THEMES)
def test_the_layout_pieces_are_shown(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)

    for title in ["Tabs", "Card", "Empty state", "Timeline entry"]:
        expect(page.get_by_role("region", name=title)).to_be_visible()
    capture(page, f"gallery-tabs-card-empty-timeline-{theme}")


@pytest.mark.parametrize("theme", THEMES)
def test_the_selected_segment_is_inverted(page: Page, theme: str):
    open_gallery(page, theme)
    row = tablist(page, "Four tabs")

    selected = row.get_by_role("tab", selected=True)
    expect(selected).to_have_count(1)
    expect(selected).to_have_attribute("aria-selected", "true")
    style = selected.evaluate(BOX_STYLE)
    assert style["background"] == resolve(page, "--text", "background-color")
    assert style["color"] == resolve(page, "--bg", "color")
    assert style["weight"] == "600"

    accent = resolve(page, "--accent-border", "color")
    tabs = row.get_by_role("tab")
    for index in range(tabs.count()):
        style = tabs.nth(index).evaluate(BOX_STYLE)
        assert style["borderWidths"] == ["0px"] * 4
        assert accent not in style["borders"]
        assert style["outline"][0] == "none" or style["outline"][2] != accent

    others = row.get_by_role("tab", selected=False)
    expect(others).to_have_count(3)
    for index in range(others.count()):
        style = others.nth(index).evaluate(BOX_STYLE)
        assert style["background"] == resolve(page, None, "background-color")
        assert style["color"] == resolve(page, "--text-2", "color")
        assert style["weight"] == "500"


@pytest.mark.parametrize("theme", THEMES)
def test_tabs_are_a_track(page: Page, theme: str):
    open_gallery(page, theme)
    row = tablist(page, "Four tabs")

    style = row.evaluate(BOX_STYLE)
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borders"] == [resolve(page, "--border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["radii"] == radii(page, "--radius-full")
    assert style["padding"] == ["4px"] * 4
    assert style["gap"] == "normal" or style["gap"] == "0px"

    tabs = row.get_by_role("tab")
    for index in range(tabs.count()):
        style = tabs.nth(index).evaluate(BOX_STYLE)
        assert style["height"] == resolve(page, "--control-h", "height")
        assert style["radii"] == radii(page, "--radius-full")
        assert style["padding"][1] == style["padding"][3] == "16px"

    # On desktop the track is as wide as its tabs, with no gap between them,
    # and not as wide as the page.
    track = row.bounding_box()
    first = tabs.first.bounding_box()
    last = tabs.last.bounding_box()
    holder = row.locator("..").bounding_box()
    assert track and first and last and holder
    assert track["width"] == pytest.approx(
        last["x"] + last["width"] - first["x"] + 2 * (4 + 1), abs=0.5
    )
    assert track["width"] < holder["width"]

    # The count after a label is a badge.
    count = row.get_by_role("tab", name="Decisions 2").get_by_text("2", exact=True)
    badge = count.evaluate(BOX_STYLE)
    assert badge["background"] == resolve(page, "--surface-3", "background-color")
    assert badge["height"] == "26px"


def test_tabs_follow_the_keyboard_in_the_browser(page: Page):
    open_gallery(page, "light")
    row = tablist(page, "Four tabs")
    first = row.get_by_role("tab").first
    last = row.get_by_role("tab").last

    first.focus()
    page.keyboard.press("ArrowLeft")
    expect(last).to_be_focused()
    expect(last).to_have_attribute("aria-selected", "true")
    expect(
        page.get_by_role("region", name="Tabs").get_by_role(
            "tabpanel", name=last.inner_text()
        )
    ).to_be_visible()
    page.keyboard.press("Home")
    expect(first).to_be_focused()
    expect(first).to_have_attribute("aria-selected", "true")


def picker(page: Page, name: str) -> Locator:
    region = page.get_by_role("region", name="Tabs")
    return region.get_by_role("combobox", name=name, exact=True)


def page_scrolls_sideways(page: Page) -> bool:
    return page.evaluate(
        "() => document.documentElement.scrollWidth > window.innerWidth"
    )


def test_on_phone_up_to_five_tabs_fill_the_width(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")
    row = tablist(page, "Four tabs")
    expect(row).to_be_visible()
    expect(picker(page, "Four tabs")).to_have_count(0)

    track = row.bounding_box()
    holder = row.locator("..").bounding_box()
    assert track and holder
    assert track["width"] == pytest.approx(holder["width"], abs=0.5)

    tabs = row.get_by_role("tab")
    widths = []
    for index in range(tabs.count()):
        box = tabs.nth(index).bounding_box()
        assert box is not None
        widths.append(box["width"])
        style = tabs.nth(index).evaluate(BOX_STYLE)
        assert style["fontSize"] == "13px"
        assert style["padding"][1] == style["padding"][3] == "4px"
    assert max(widths) - min(widths) < 1, widths

    assert not row.evaluate("e => e.scrollWidth > e.clientWidth"), "the row scrolls"
    assert not page_scrolls_sideways(page), "the page scrolls sideways"


def test_on_phone_more_than_five_tabs_are_a_select(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")
    region = page.get_by_role("region", name="Tabs")

    expect(tablist(page, "Eight tabs")).to_have_count(0)
    select = picker(page, "Eight tabs")
    expect(select).to_be_visible()
    expect(select.locator("option")).to_have_count(8)

    select.select_option(label="Lessons")

    expect(select).to_have_value("lessons")
    expect(region.get_by_role("tabpanel", name="Lessons")).to_be_visible()
    expect(region.get_by_text("The overview panel.")).to_be_hidden()
    assert not page_scrolls_sideways(page), "the page scrolls sideways"
    box = select.bounding_box()
    holder = select.locator("..").bounding_box()
    assert box and holder
    assert box["width"] == pytest.approx(holder["width"], abs=0.5)


def test_on_desktop_eight_tabs_are_one_track(page: Page):
    open_gallery(page, "light")
    row = tablist(page, "Eight tabs")

    expect(picker(page, "Eight tabs")).to_have_count(0)
    tabs = row.get_by_role("tab")
    expect(tabs).to_have_count(8)
    tops = {tabs.nth(index).evaluate("e => e.offsetTop") for index in range(8)}
    assert len(tops) == 1, "the track wrapped"
    assert row.evaluate(BOX_STYLE)["radii"] == radii(page, "--radius-full")
    assert not row.evaluate("e => e.scrollWidth > e.clientWidth"), "the row scrolls"


def test_a_track_that_wraps_takes_the_large_corners(page: Page):
    # Just above the phone breakpoint, so the tab list shows and eight tabs
    # do not fit on one line.
    page.set_viewport_size({"width": 660, "height": 800})
    open_gallery(page, "light")
    row = tablist(page, "Eight tabs")

    tabs = row.get_by_role("tab")
    tops = {tabs.nth(index).evaluate("e => e.offsetTop") for index in range(8)}
    assert len(tops) > 1, "the track did not wrap"
    assert row.evaluate(BOX_STYLE)["radii"] == radii(page, "--radius-lg")
    assert not row.evaluate("e => e.scrollWidth > e.clientWidth"), "the row scrolls"
    assert not page_scrolls_sideways(page), "the page scrolls sideways"


@pytest.mark.parametrize("theme", THEMES)
def test_a_card_has_the_card_padding_and_a_label_for_a_title(page: Page, theme: str):
    open_gallery(page, theme)

    style = card(page, "Plain").evaluate(BOX_STYLE)
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borders"] == [resolve(page, "--border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["radii"] == radii(page, "--radius-lg")
    assert style["padding"] == [resolve(page, "--card-pad", "padding-top")] * 4
    assert style["rowGap"] == resolve(page, "--space-4", "row-gap")
    assert style["shadow"] == "none"

    # The title is the card's label: still its h2, but in the UI font and
    # quiet, so the heading font stays for the page title and section headings.
    heading = card(page, "Plain").get_by_role("heading", level=2).evaluate(BOX_STYLE)
    assert heading["fontFamily"] == resolve(page, "--font-ui", "font-family")
    assert heading["fontSize"] == resolve(page, "--text-label-size", "font-size")
    assert heading["lineHeight"] == resolve(page, "--text-label-line", "line-height")
    assert heading["weight"] == "500"
    assert heading["color"] == resolve(page, "--text-2", "color")
    assert heading["textTransform"] == "none"


@pytest.mark.parametrize("theme", THEMES)
def test_a_card_that_needs_you_has_one_accent_border(page: Page, theme: str):
    open_gallery(page, theme)

    # One 1px border in the accent border, in place of the plain one, and no
    # ring outside it.
    style = card(page, "Needing you").evaluate(BOX_STYLE)
    assert style["borders"] == [resolve(page, "--accent-border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["shadow"] == "none"
    hidden = card(page, "Needing you").get_by_text("Needs you", exact=True)
    expect(hidden).to_be_attached()
    box = hidden.bounding_box()
    assert box is not None and box["width"] <= 1 and box["height"] <= 1


def test_cards_in_a_row_align_their_actions(page: Page):
    open_gallery(page, "light")
    region = page.get_by_role("region", name="Card")

    footers = [
        card(page, name).get_by_role("button").bounding_box()
        for name in ["Review", "Lessons", "Briefing"]
    ]
    assert all(box is not None for box in footers)
    tops = {round(box["y"]) for box in footers if box}
    assert len(tops) == 1, footers
    bodies = [
        card(page, name).evaluate("e => e.getBoundingClientRect().height")
        for name in ["Review", "Lessons", "Briefing"]
    ]
    assert len(set(bodies)) == 1, "the cards in the row differ in height"
    expect(region.get_by_role("article")).to_have_count(7)


@pytest.mark.parametrize("theme", THEMES)
def test_the_empty_state_title_is_in_capitals(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Empty state")

    # The first line, what is empty, in the heading font, which is always
    # weight 600 and in capitals (hard rule 12).
    title = region.get_by_text("No cards due", exact=True)
    first = title.evaluate(BOX_STYLE)
    assert first["fontFamily"] == resolve(page, "--font-heading", "font-family")
    assert first["fontSize"] == resolve(page, "--text-h2-size", "font-size")
    assert first["lineHeight"] == resolve(page, "--text-h2-line", "line-height")
    assert first["weight"] == "600"
    assert first["textTransform"] == "uppercase"

    root = title.locator("..")
    style = root.evaluate(BOX_STYLE)
    assert style["borderStyles"] == ["dashed"] * 4
    assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["radii"] == radii(page, "--radius-lg")
    vertical = resolve(page, "--space-7", "padding-top")
    side = resolve(page, "--space-4", "padding-top")
    assert style["padding"] == [vertical, side, vertical, side]
    assert style["textAlign"] == "center"

    detail = root.locator("p").nth(1).evaluate(BOX_STYLE)
    assert detail["color"] == resolve(page, "--text-2", "color")
    assert detail["fontSize"] == resolve(page, "--text-small-size", "font-size")
    assert detail["lineHeight"] == resolve(page, "--text-small-line", "line-height")

    action = root.get_by_role("button")
    expect(action).to_have_count(1)
    assert (
        action.evaluate(BOX_STYLE)["borders"]
        == [resolve(page, "--border-strong", "color")] * 4
    ), "the action is a secondary button"

    # The other example has no action.
    bare = region.get_by_text("Nothing in review", exact=True).locator("..")
    expect(bare.get_by_role("button")).to_have_count(0)


@pytest.mark.parametrize("theme", THEMES)
def test_a_timeline_entry_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Timeline entry")
    timeline = region.get_by_role("list", name="Recent")
    items = timeline.get_by_role("listitem")
    expect(items).to_have_count(3)

    rails = [items.nth(index).locator("[aria-hidden='true']") for index in range(3)]
    # Every entry has a ring, and a line down to the next one but the last.
    assert [rail.locator("span").count() for rail in rails] == [2, 2, 1]

    rail = rails[1].evaluate(BOX_STYLE)
    assert rail["width"] == "10px"
    ring = rails[1].locator("span").first.evaluate(BOX_STYLE)
    assert ring["width"] == ring["height"] == "8px"
    assert ring["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert ring["borderWidths"] == ["2px"] * 4
    assert ring["background"] == resolve(page, "--surface", "background-color")
    assert ring["radii"] == radii(page, "--radius-full")
    line = rails[1].locator("span").nth(1)
    assert line.evaluate(BOX_STYLE)["width"] == "1px"
    assert line.evaluate(BOX_STYLE)["background"] == resolve(
        page, "--border", "background-color"
    )

    # The line reaches the next entry's ring.
    line_box = line.bounding_box()
    next_ring = rails[2].locator("span").first.bounding_box()
    assert line_box is not None and next_ring is not None
    assert line_box["y"] + line_box["height"] == pytest.approx(next_ring["y"], abs=1)

    # 14px between entries, and space-3 between the rail and the content.
    first, second = items.nth(0).bounding_box(), items.nth(1).bounding_box()
    assert first is not None and second is not None
    assert second["y"] - (first["y"] + first["height"]) == pytest.approx(14, abs=0.5)
    assert items.nth(1).evaluate(BOX_STYLE)["gap"] == resolve(
        page, "--space-3", "column-gap"
    )

    time = items.nth(1).get_by_text("10 Nov 21:05", exact=True).evaluate(BOX_STYLE)
    assert time["fontFamily"] == resolve(page, "--font-mono", "font-family")
    assert time["fontSize"] == resolve(page, "--text-mono-size", "font-size")
    assert time["color"] == resolve(page, "--text-3", "color")

    detail = items.nth(0).locator("p").last.evaluate(BOX_STYLE)
    assert detail["color"] == resolve(page, "--text-2", "color")
    assert detail["fontSize"] == resolve(page, "--text-small-size", "font-size")

    # The entry that needs the user: a filled accent dot in an accent ring.
    dot = rails[0].locator("span").first.evaluate(BOX_STYLE)
    assert dot["background"] == resolve(page, "--accent", "background-color")
    assert dot["borders"] == [resolve(page, "--accent-border", "color")] * 4


# The "Table" of section 4 of design/DESIGN.md. The gallery's "Sessions"
# table has columns of all three priorities: Session and Status are 1,
# Project and Hours (numeric) are 2, Notes is 3. It is sorted by Session, the
# second row is selected and the third is editing. "Hours by month" is a wide
# table of twelve months: Project is priority 1, January to June 2 and July
# to December 3, so it fits the page at both widths.
TABLE_STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        background: style.backgroundColor,
        color: style.color,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        padding: [style.paddingTop, style.paddingRight,
                  style.paddingBottom, style.paddingLeft],
        overflowX: style.overflowX,
        weight: style.fontWeight,
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        textAlign: style.textAlign,
        numerals: style.fontVariantNumeric,
        shadow: style.boxShadow,
    };
}"""

PRIORITY_1 = ["Session", "Status"]
PRIORITY_2 = ["Project", "Hours"]
PRIORITY_3 = ["Notes"]


def table_section(page: Page) -> Locator:
    return page.get_by_role("region", name="Table", exact=True)


def sessions(page: Page) -> Locator:
    return table_section(page).get_by_role("table", name="Sessions", exact=True)


def body_rows(table: Locator) -> Locator:
    return table.locator("tbody > tr")


def column_index(table: Locator, name: str) -> int:
    names = table.locator("thead th").all_inner_texts()
    return [text.strip() for text in names].index(name)


def no_sideways_scroll(page: Page) -> bool:
    return page.evaluate(
        "() => document.documentElement.scrollWidth <= window.innerWidth"
    )


@pytest.mark.parametrize("theme", THEMES)
def test_the_table_is_shown(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)
    section = table_section(page)

    expect(sessions(page)).to_be_visible()
    expect(section.get_by_role("navigation", name="Pages")).to_be_visible()
    body_rows(sessions(page)).first.hover()
    capture(page, f"gallery-table-{theme}")

    # Editing another row moves the edit there, with focus in its field.
    section.get_by_role("button", name="Edit Write the next slice").click()
    expect(section.get_by_role("textbox", name="Session", exact=True)).to_be_focused()
    capture(page, f"gallery-table-editing-{theme}")


@pytest.mark.parametrize("theme", THEMES)
def test_the_table_header_has_no_fill(page: Page, theme: str):
    open_gallery(page, theme)
    table = sessions(page)
    transparent = resolve(page, None, "background-color")

    # The frame: the surface, 1px border and the large radius, and it keeps
    # its scroll for a table too wide for it.
    frame = table.locator("..").evaluate(TABLE_STYLE)
    assert frame["background"] == resolve(page, "--surface", "background-color")
    assert frame["borders"] == [resolve(page, "--border", "color")] * 4
    assert frame["borderWidths"] == ["1px"] * 4
    assert frame["radii"] == radii(page, "--radius-lg")
    assert frame["overflowX"] == "auto"

    # The header row and its cells: no fill, caption style, text-3.
    assert table.locator("thead tr").evaluate(TABLE_STYLE)["background"] == transparent
    for name in ["Project", "Hours", "Status", "Notes"]:
        plain = table.locator("thead th", has_text=name).evaluate(TABLE_STYLE)
        assert plain["background"] == transparent, name
        assert plain["color"] == resolve(page, "--text-3", "color"), name
        assert_caption(page, plain)

    # The sorted column: text at 600, with its arrow and aria-sort.
    sorted_header = table.locator("thead th[aria-sort]")
    expect(sorted_header).to_have_count(1)
    expect(sorted_header).to_have_text("Session")
    expect(sorted_header).to_have_attribute("aria-sort", "ascending")
    expect(sorted_header.locator("svg")).to_have_count(1)
    style = sorted_header.evaluate(TABLE_STYLE)
    assert style["background"] == transparent
    assert style["color"] == resolve(page, "--text", "color")
    assert style["weight"] == "600"


@pytest.mark.parametrize("theme", THEMES)
def test_a_table_row_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    table = sessions(page)
    rows = body_rows(table)

    # Body rows: row-h tall, padding 6px 12px, and a 1px border line between
    # them, drawn inside the row so a row holding a button is still row-h.
    row_h = resolve(page, "--row-h", "height")
    expect(rows).to_have_count(8)
    for index in [0, 1, 3, 7]:
        box = rows.nth(index).bounding_box()
        assert box is not None and f"{box['height']:g}px" == row_h, index
    cell = rows.nth(3).locator("td").first.evaluate(TABLE_STYLE)
    assert cell["padding"] == ["6px", "12px", "6px", "12px"]
    border = resolve(page, "--border", "color")
    assert cell["shadow"] == f"{border} 0px 1px 0px 0px inset"

    # The numeric column: right aligned, mono, tabular figures.
    hours = column_index(table, "Hours")
    number = rows.nth(0).locator("td").nth(hours).evaluate(TABLE_STYLE)
    assert number["textAlign"] == "right"
    assert number["fontFamily"] == resolve(page, "--font-mono", "font-family")
    assert number["numerals"] == "tabular-nums"

    # The folded line is the phone's; on desktop it is not shown.
    expect(rows.nth(0).locator("td").first.get_by_text("·")).to_be_hidden()

    # Hover: surface-2. Selected and editing: info-soft.
    plain_row = rows.nth(3)
    plain_row.hover()
    assert plain_row.evaluate(TABLE_STYLE)["background"] == resolve(
        page, "--surface-2", "background-color"
    )
    selected = rows.nth(1)
    expect(selected).to_have_attribute("aria-selected", "true")
    editing = rows.nth(2)
    expect(editing).to_have_attribute("aria-selected", "true")
    expect(editing.get_by_role("textbox", name="Session", exact=True)).to_be_visible()
    page.mouse.move(0, 0)
    for row in [selected, editing]:
        assert row.evaluate(TABLE_STYLE)["background"] == resolve(
            page, "--info-soft", "background-color"
        )
    # A selected row keeps its fill under the pointer.
    selected.hover()
    assert selected.evaluate(TABLE_STYLE)["background"] == resolve(
        page, "--info-soft", "background-color"
    )


def test_a_table_sorts_and_edits_in_the_browser(page: Page):
    open_gallery(page, "light")
    section = table_section(page)
    table = sessions(page)

    section.get_by_role("button", name="Hours", exact=True).click()
    header = table.locator("thead th[aria-sort]")
    expect(header).to_have_text("Hours")
    expect(header).to_have_attribute("aria-sort", "ascending")

    edit = section.get_by_role("button", name="Edit Write the next slice")
    edit.click()
    field = section.get_by_role("textbox", name="Session", exact=True)
    expect(field).to_be_focused()
    page.keyboard.press("Escape")
    expect(field).to_have_count(0)
    expect(edit).to_be_focused()


def test_below_640px_only_priority_one_columns_show(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")
    table = sessions(page)
    rows = body_rows(table)

    for name in PRIORITY_1:
        expect(table.locator("thead th", has_text=name)).to_be_visible()
    for name in PRIORITY_2 + PRIORITY_3:
        index = column_index(table, name)
        expect(table.locator("thead th").nth(index)).to_be_hidden()
        for row in range(rows.count()):
            expect(rows.nth(row).locator("td").nth(index)).to_be_hidden()

    # The priority 2 values, folded into one mono line under the first cell.
    first = rows.nth(3).locator("td").first
    fold = first.get_by_text("Platform · 2.0 h", exact=True)
    expect(fold).to_be_visible()
    style = fold.evaluate(TABLE_STYLE)
    assert style["fontFamily"] == resolve(page, "--font-mono", "font-family")
    assert style["fontSize"] == resolve(page, "--text-mono-size", "font-size")
    title = first.get_by_text("Review the merge gate", exact=True).bounding_box()
    fold_box = fold.bounding_box()
    assert title is not None and fold_box is not None
    assert fold_box["y"] >= title["y"] + title["height"] - 0.5, "not under the cell"
    # The priority 3 value is not folded in.
    expect(first).not_to_contain_text("Waits on CI")

    assert no_sideways_scroll(page), "the page scrolls sideways"


def test_no_table_in_the_gallery_scrolls_sideways(page: Page, viewport: str):
    open_gallery(page, "light")
    tables = page.locator("table")
    assert tables.count() >= 2, "the gallery shows its tables"

    # Each table is planned, through column priority, to fit its frame.
    for index in range(tables.count()):
        frame = tables.nth(index).locator("..")
        name = tables.nth(index).get_attribute("aria-label")
        widths = frame.evaluate("e => [e.scrollWidth, e.clientWidth]")
        assert widths[0] <= widths[1], f"{name} scrolls sideways: {widths}"

    assert no_sideways_scroll(page), "the page scrolls sideways"


def test_a_table_too_wide_scrolls_in_its_frame(page: Page):
    page.set_viewport_size(VIEWPORTS["desktop"])
    open_gallery(page, "light")
    wide = table_section(page).get_by_role("table", name="Hours by month")
    frame = wide.locator("..")

    # A column wider than the page, added to the gallery's wide table.
    page_width = page.evaluate("() => window.innerWidth")
    wide.evaluate(
        """(table, width) => {
            const header = document.createElement("th");
            const content = document.createElement("div");
            content.style.width = `${width}px`;
            content.textContent = "Too wide";
            header.append(content);
            table.querySelector("thead tr").append(header);
        }""",
        page_width + 200,
    )

    # It scrolls inside its frame, and the page does not.
    assert frame.evaluate(TABLE_STYLE)["overflowX"] == "auto"
    assert frame.evaluate("e => e.scrollWidth > e.clientWidth"), "it does not scroll"
    frame.evaluate("e => { e.scrollLeft = e.scrollWidth; }")
    assert frame.evaluate("e => e.scrollLeft") > 0
    assert no_sideways_scroll(page), "the page scrolls sideways"


def test_rows_are_the_row_height_on_phone(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")
    section = table_section(page)
    row_h = float(resolve(page, "--row-h", "height").removesuffix("px"))
    assert row_h >= 44, "a row on phone is a touch target"

    # The header row is exactly --row-h; a body row, with the folded line
    # under its first cell, is taller, never shorter.
    wide = section.get_by_role("table", name="Hours by month")
    for table in [wide, sessions(page)]:
        header = table.locator("thead tr").bounding_box()
        assert header is not None and header["height"] == row_h
        rows = body_rows(table)
        for index in range(rows.count()):
            box = rows.nth(index).bounding_box()
            assert box is not None and box["height"] >= row_h, index

    # The controls in the table are touch targets too.
    for name in ["Session", "Edit Review the merge gate", "Save", "Cancel"]:
        box = section.get_by_role("button", name=name, exact=True).bounding_box()
        assert box is not None and box["height"] >= 44, name


def remove_target_reaches(page: Page, remove: Locator, reach: float) -> bool:
    """Whether a press `reach` pixels above, below, left and right of the
    remove button's centre still lands on the button. `elementFromPoint`
    sees only the viewport, so the button is scrolled into it first."""
    remove.scroll_into_view_if_needed()
    return page.evaluate(
        """([element, reach]) => {
            const box = element.getBoundingClientRect();
            const x = box.left + box.width / 2;
            const y = box.top + box.height / 2;
            return [[0, -reach], [0, reach], [-reach, 0], [reach, 0]].every(
                ([dx, dy]) => element.contains(document.elementFromPoint(x + dx, y + dy))
            );
        }""",
        [remove.element_handle(), reach],
    )


@pytest.mark.parametrize("theme", THEMES)
def test_a_filter_pill_is_filled_and_32px(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)
    section = table_section(page)

    # The pill: 32px high, fully round, the third surface, no border, 13px in
    # text, 14px on the left and 4px on the right.
    pill = section.get_by_text("Status: Done", exact=True).locator("..")
    style = pill.evaluate(TABLE_STYLE)
    assert pill.evaluate("e => e.getBoundingClientRect().height") == 32
    assert style["radii"] == radii(page, "--radius-full")
    assert style["background"] == resolve(page, "--surface-3", "background-color")
    assert style["borderWidths"] == ["0px"] * 4
    assert style["fontSize"] == resolve(page, "--text-small-size", "font-size")
    assert style["color"] == resolve(page, "--text", "color")
    assert style["padding"][1] == "4px" and style["padding"][3] == "14px"

    # Its remove button: a 26px round ghost button inside it, named as before.
    remove = section.get_by_role("button", name="Remove filter Status: Done")
    box, outer = remove.bounding_box(), pill.bounding_box()
    assert box is not None and outer is not None
    assert box["width"] == 26 and box["height"] == 26
    assert (
        outer["x"] <= box["x"]
        and box["x"] + box["width"] <= outer["x"] + outer["width"]
    )
    assert (
        outer["y"] <= box["y"]
        and box["y"] + box["height"] <= outer["y"] + outer["height"]
    )
    button_style = remove.evaluate(TABLE_STYLE)
    assert button_style["radii"] == radii(page, "--radius-full")
    assert button_style["background"] == resolve(page, None, "background-color")
    assert button_style["color"] == resolve(page, "--text-2", "color")

    # On phone its target reaches 44px (hard rule 10).
    if viewport == "phone":
        assert remove_target_reaches(page, remove, 21.5), "the target is under 44px"

    count = section.get_by_text("8 of 214", exact=True).evaluate(TABLE_STYLE)
    assert count["fontFamily"] == resolve(page, "--font-mono", "font-family")

    remove.click()
    expect(section.get_by_text("Status: Done", exact=True)).to_have_count(0)


@pytest.mark.parametrize("theme", THEMES)
def test_paging_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    paging = table_section(page).get_by_role("navigation", name="Pages")
    previous = paging.get_by_role("button", name="Previous")
    next_button = paging.get_by_role("button", name="Next")

    # The line on the left in text-2.
    where = paging.get_by_text("Rows 1 to 8 of 214", exact=True)
    expect(where).to_be_visible()
    assert where.evaluate(TABLE_STYLE)["color"] == resolve(page, "--text-2", "color")
    where_box, next_box = where.bounding_box(), next_button.bounding_box()
    assert where_box is not None and next_box is not None
    assert where_box["x"] < next_box["x"], "the line is not on the left"

    # Previous and Next are secondary buttons, read as the gallery's own
    # Secondary and Disabled buttons are; Previous is disabled on page one.
    def assert_kind(target: Locator, kind: str) -> None:
        expected = button(page, KINDS[kind][0]).evaluate(STYLE)
        actual = target.evaluate(STYLE)
        for key in ["background", "color", "borders", "borderWidths", "height"]:
            assert actual[key] == expected[key], (kind, key)
        assert actual["radii"] == radii(page, "--radius-full")
        assert actual["padding"] == [BUTTON_PADDING["secondary"]] * 2

    expect(previous).to_be_disabled()
    assert_kind(previous, "disabled")
    assert_kind(next_button, "secondary")

    next_button.click()
    expect(paging.get_by_text("Rows 9 to 16 of 214", exact=True)).to_be_visible()
    expect(previous).to_be_enabled()
    page.mouse.move(0, 0)
    assert_kind(previous, "secondary")


# "Dialog" and "Toast" in section 4 of design/DESIGN.md, the two floating
# layers and the only components with a shadow (hard rule 3). The gallery's
# Dialog section opens a destructive dialog and a primary one from a button
# each, and its Toast section raises a success and an error.
FLOATING_STYLE = """element => {
    const style = getComputedStyle(element);
    const backdrop = getComputedStyle(element, "::backdrop");
    return {
        background: style.backgroundColor,
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        shadow: style.boxShadow,
        position: style.position,
        maxWidth: style.maxWidth,
        backdrop: backdrop.backgroundColor,
    };
}"""

INSIDE = "element => element.contains(document.activeElement)"


def dialog_section(page: Page) -> Locator:
    return page.get_by_role("region", name="Dialog", exact=True)


def toast_section(page: Page) -> Locator:
    return page.get_by_role("region", name="Toast", exact=True)


def open_dialog(page: Page, opener: str = "Delete 3 entries…") -> Locator:
    dialog_section(page).get_by_role("button", name=opener, exact=True).click()
    dialog = page.get_by_role("dialog")
    expect(dialog).to_be_visible()
    return dialog


def raise_toasts(page: Page) -> tuple[Locator, Locator]:
    section = toast_section(page)
    section.get_by_role("button", name="Show a success", exact=True).click()
    section.get_by_role("button", name="Show an error", exact=True).click()
    success = page.get_by_role("status").filter(has_text="Entry saved")
    error = page.get_by_role("alert").filter(has_text="Error: could not reach")
    expect(success).to_be_visible()
    expect(error).to_be_visible()
    return success, error


def rgba(colour: str) -> tuple[float, float, float, float]:
    """The red, green, blue and alpha of a computed colour, from 0 to 1, in
    either form the browser writes: `rgb(…)`, `rgba(…)` or `color(srgb …)`."""
    legacy = re.fullmatch(r"rgba?\((\d+), (\d+), (\d+)(?:, ([\d.]+))?\)", colour)
    if legacy:
        red, green, blue = (int(value) / 255 for value in legacy.groups()[:3])
        return red, green, blue, float(legacy.group(4) or 1)
    modern = re.fullmatch(
        r"color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: / ([\d.]+))?\)", colour
    )
    assert modern, f"not an sRGB colour: {colour}"
    red, green, blue = (float(value) for value in modern.groups()[:3])
    return red, green, blue, float(modern.group(4) or 1)


@pytest.mark.parametrize("theme", THEMES)
def test_the_dialog_and_the_toast_are_shown(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)

    dialog = open_dialog(page)
    expect(dialog.get_by_role("heading", name="Delete 3 entries?")).to_be_visible()
    capture(page, f"gallery-dialog-{theme}")
    dialog.get_by_role("button", name="Cancel", exact=True).click()
    expect(dialog).to_be_hidden()

    raise_toasts(page)
    capture(page, f"gallery-toast-{theme}")


@pytest.mark.parametrize("theme", THEMES)
def test_a_dialog_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    dialog = open_dialog(page)

    style = dialog.evaluate(FLOATING_STYLE)
    box = dialog.bounding_box()
    assert box is not None and box["width"] <= 480
    assert style["maxWidth"] == "480px"
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borders"] == [resolve(page, "--border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["radii"] == radii(page, "--radius-xl")
    assert style["shadow"] == resolve(page, "--shadow-2", "box-shadow")

    # The backdrop is --bg at 70 percent.
    *backdrop, alpha = rgba(style["backdrop"])
    *bg, _ = rgba(resolve(page, "--bg", "background-color"))
    assert backdrop == pytest.approx(bg, abs=1 / 255)
    assert alpha == pytest.approx(0.7, abs=0.01)

    # The actions: right aligned, cancel first, the verb repeated.
    cancel = dialog.get_by_role("button", name="Cancel", exact=True)
    confirm = dialog.get_by_role("button", name="Delete entries", exact=True)
    cancel_box, confirm_box = cancel.bounding_box(), confirm.bounding_box()
    assert cancel_box is not None and confirm_box is not None
    assert cancel_box["x"] < confirm_box["x"]

    # Inside the 1px border, --space-6 on every side: the title's top left
    # and the confirming button's bottom right are one border and one
    # padding from the dialog's corners.
    inset = 1 + float(resolve(page, "--space-6", "padding-top").removesuffix("px"))
    title = dialog.get_by_role("heading", name="Delete 3 entries?").bounding_box()
    assert title is not None
    assert title["x"] - box["x"] == pytest.approx(inset)
    assert title["y"] - box["y"] == pytest.approx(inset)
    right = box["x"] + box["width"]
    bottom = box["y"] + box["height"]
    assert right - (confirm_box["x"] + confirm_box["width"]) == pytest.approx(inset)
    assert bottom - (confirm_box["y"] + confirm_box["height"]) == pytest.approx(inset)


@pytest.mark.parametrize("theme", THEMES)
def test_the_dialog_title_is_in_capitals(page: Page, theme: str):
    open_gallery(page, theme)
    dialog = open_dialog(page)

    # An h2 in the heading font, which is always weight 600 and in capitals
    # (hard rule 12). Its name stays as written; only its look is capitals.
    title = dialog.get_by_role("heading", name="Delete 3 entries?", level=2)
    expect(title).to_be_visible()
    style = title.evaluate(BOX_STYLE)
    assert style["fontFamily"] == resolve(page, "--font-heading", "font-family")
    assert style["fontSize"] == resolve(page, "--text-h2-size", "font-size")
    assert style["lineHeight"] == resolve(page, "--text-h2-line", "line-height")
    assert style["weight"] == "600"
    assert style["textTransform"] == "uppercase"


def test_on_phone_a_dialog_is_a_sheet_at_the_bottom(page: Page):
    page.set_viewport_size(VIEWPORTS["phone"])
    open_gallery(page, "light")
    dialog = open_dialog(page)

    style = dialog.evaluate(FLOATING_STYLE)
    box = dialog.bounding_box()
    width, height = VIEWPORTS["phone"]["width"], VIEWPORTS["phone"]["height"]
    assert style["position"] == "fixed"
    assert box is not None
    assert box["x"] == 0 and box["width"] == width
    assert box["y"] + box["height"] == pytest.approx(height, abs=0.5)
    # It stays where it is when the page behind it has scrolled.
    page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
    moved = dialog.bounding_box()
    assert moved is not None and moved["y"] == pytest.approx(box["y"], abs=0.5)


def test_a_dialog_cancels_and_confirms_in_the_browser(page: Page):
    open_gallery(page, "light")
    opener = dialog_section(page).get_by_role(
        "button", name="Archive the project…", exact=True
    )

    # Escape cancels, and focus returns to the button that opened it.
    dialog = open_dialog(page, "Archive the project…")
    page.keyboard.press("Escape")
    expect(dialog).to_be_hidden()
    expect(opener).to_be_focused()

    # A click on the backdrop cancels; a click inside does not.
    dialog = open_dialog(page, "Archive the project…")
    dialog.get_by_text("Its sessions stay").click()
    expect(dialog).to_be_visible()
    page.mouse.click(4, 4)
    expect(dialog).to_be_hidden()

    # The confirming button is disabled while the work runs, then it closes.
    dialog = open_dialog(page, "Archive the project…")
    confirm = dialog.get_by_role("button", name="Archive project", exact=True)
    confirm.click()
    expect(confirm).to_be_disabled()
    expect(dialog).to_be_hidden()
    expect(opener).to_be_focused()
    expect(dialog_section(page).get_by_text("Archived.")).to_be_visible()


def test_focus_stays_inside_an_open_dialog(page: Page):
    open_gallery(page, "light")
    dialog = open_dialog(page)
    buttons = dialog.get_by_role("button")
    count = buttons.count()

    assert dialog.evaluate(INSIDE), "focus did not move into the dialog"
    seen = set()
    for _ in range(count * 3):
        page.keyboard.press("Tab")
        assert dialog.evaluate(INSIDE), "Tab left the dialog"
        seen.add(page.evaluate("document.activeElement.textContent"))
    for _ in range(count * 2):
        page.keyboard.press("Shift+Tab")
        assert dialog.evaluate(INSIDE), "Shift+Tab left the dialog"
    assert seen == {"Cancel", "Delete entries"}


@pytest.mark.parametrize("theme", THEMES)
def test_a_toast_reads_its_values(page: Page, viewport: str, theme: str):
    open_gallery(page, theme)
    success, error = raise_toasts(page)

    for toast in [success, error]:
        style = toast.evaluate(FLOATING_STYLE)
        assert style["background"] == resolve(page, "--surface", "background-color")
        assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
        assert style["borderWidths"] == ["1px"] * 4
        assert style["radii"] == radii(page, "--radius-md")
        assert style["shadow"] == resolve(page, "--shadow-1", "box-shadow")
        assert style["maxWidth"] == "400px"

    # The action is a text button in --info, weight 600.
    action = error.get_by_role("button", name="Retry", exact=True)
    action_style = action.evaluate(STYLE)
    assert action_style["color"] == resolve(page, "--info", "color")
    assert action_style["weight"] == "600"

    # Newest lowest: the error, raised second, is under the success.
    first, last = success.bounding_box(), error.bounding_box()
    assert first is not None and last is not None
    assert first["y"] + first["height"] <= last["y"]

    size = VIEWPORTS[viewport]
    tab_bar_h = float(resolve(page, "--tab-bar-h", "height").removesuffix("px"))
    bottom = last["y"] + last["height"]
    if viewport == "desktop":
        # Bottom right, a small gap from each edge.
        assert last["x"] > size["width"] / 2
        assert 0 < size["width"] - (last["x"] + last["width"]) <= 32
        assert 0 < size["height"] - bottom <= 32
    else:
        # Above the bottom tab bar, and inside the screen.
        assert bottom <= size["height"] - tab_bar_h
        assert last["x"] >= 0 and last["x"] + last["width"] <= size["width"]
        assert no_sideways_scroll(page), "the page scrolls sideways"


def test_a_toast_action_dismisses_it_in_the_browser(page: Page):
    open_gallery(page, "light")
    success, error = raise_toasts(page)

    error.get_by_role("button", name="Retry", exact=True).click()
    expect(error).to_have_count(0)
    success.get_by_role("button", name="Undo", exact=True).click()
    expect(success).to_have_count(0)

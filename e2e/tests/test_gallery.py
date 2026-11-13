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
    assert (
        style["radii"] == [resolve(page, "--radius-md", "border-top-left-radius")] * 4
    )
    assert style["fontSize"] == resolve(page, "--input-font-size", "font-size")
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
@pytest.mark.parametrize("kind", list(FIELDS))
def test_a_field_with_an_error_reads_its_values(page: Page, theme: str, kind: str):
    open_gallery(page, theme)
    control = field(page, kind, "With an error")

    expect(control).to_have_attribute("aria-invalid", "true")
    style = control.evaluate(FIELD_STYLE)
    assert style["borders"] == [resolve(page, "--danger", "color")] * 4
    assert style["borderWidths"] == ["2px"] * 4

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
def test_a_checkbox_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)
    region = page.get_by_role("region", name="Checkbox")
    box = region.get_by_role("checkbox", name="Unchecked", exact=True)

    size = box.bounding_box()
    assert size is not None and size["width"] == 16 and size["height"] == 16
    assert box.evaluate(FIELD_STYLE)["accent"] == resolve(
        page, "--info", "accent-color"
    )

    # `has` is matched inside each label, so it is not scoped to the region.
    row = region.locator(
        "label", has=page.get_by_role("checkbox", name="Unchecked", exact=True)
    )
    row_box = row.bounding_box()
    assert row_box is not None
    assert f"{row_box['height']:g}px" == resolve(page, "--row-h", "height")

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
    "idle": ("Idle", "--surface-2", "--text-2"),
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
def test_each_status_tone_reads_its_values(page: Page, theme: str, tone: str):
    _word, fill, text = TONES[tone]
    open_gallery(page, theme)
    label = status(page, tone)

    style = label.evaluate(LABEL_STYLE)
    assert style["background"] == resolve(page, fill, "background-color")
    assert style["color"] == resolve(page, text, "color")
    assert style["height"] == "22px"
    assert style["radii"] == radii(page, "--radius-sm")
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
def test_a_badge_and_a_chip_read_their_values(page: Page, theme: str):
    open_gallery(page, theme)

    badge = page.get_by_role("region", name="Badge").get_by_text("Topic", exact=True)
    style = badge.evaluate(LABEL_STYLE)
    assert style["background"] == resolve(page, "--surface-2", "background-color")
    assert style["color"] == resolve(page, "--text-2", "color")
    assert style["height"] == "22px"
    assert style["radii"] == radii(page, "--radius-sm")
    assert style["children"] == 0, "a badge has no dot"
    assert_caption(page, style)

    region = page.get_by_role("region", name="Tool chip")
    chip = region.get_by_text("Claude", exact=True)
    style = chip.evaluate(LABEL_STYLE)
    assert style["background"] == resolve(page, None, "background-color")
    assert style["color"] == resolve(page, "--text-2", "color")
    assert style["borders"] == [resolve(page, "--border-strong", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["height"] == "20px"
    assert style["radii"] == radii(page, "--radius-sm")
    assert_caption(page, style)


def bar(page: Page, name: str) -> Locator:
    region = page.get_by_role("region", name="Progress bar")
    return region.get_by_role("img", name=name, exact=True)


@pytest.mark.parametrize("theme", THEMES)
def test_a_progress_bar_reads_its_values(page: Page, theme: str):
    open_gallery(page, theme)

    for kind, fill in [("Progress", "--accent"), ("Budget", "--info")]:
        track = bar(page, f"{kind}: 62%")
        style = track.evaluate(LABEL_STYLE)
        assert style["height"] == "6px"
        assert style["background"] == resolve(page, "--surface-2", "background-color")
        assert style["borders"] == [resolve(page, "--border", "color")] * 4
        assert style["borderWidths"] == ["1px"] * 4
        assert style["radii"] == radii(page, "--radius-full")

        fill_style = track.locator("div").evaluate(LABEL_STYLE)
        assert fill_style["background"] == resolve(page, fill, "background-color")

    # The fill is as wide as the value, inside the track's border.
    for value in [0, 62, 100]:
        track = bar(page, f"Progress: {value}%")
        inner = track.evaluate("element => element.clientWidth")
        width = track.locator("div").evaluate(
            "element => element.getBoundingClientRect().width"
        )
        assert width == pytest.approx(inner * value / 100, abs=0.5), value


def test_a_meter_row_reads_its_values(page: Page):
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

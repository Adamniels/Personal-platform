"""Review's pages in the platform's look: the crumb in the top bar, the page
header, the links drawn as buttons, the inset rows of the due lists and the
deck list, the topic picker, the card table's edit row, the grade bar on
phone, and the three look checks of look.py. See "The page" in
review/README.md.

Colours and sizes are compared with the token as the page resolves it, as
test_memory_look.py does, so no value is written here but those DESIGN.md
gives in pixels: a row's and a button's padding, the pill's height and its
remove button's."""

from itertools import pairwise

from playwright.sync_api import Locator, Page, expect

from conftest import open_section, register_and_log_in, sections

THEMES = ["light", "dark"]
LABEL = "Review sections"
STORAGE_KEY = "platform-theme"
TRANSPARENT = "rgba(0, 0, 0, 0)"

PARSING = {"slug": "parsing", "name": "Parsing"}
MVCC = {"slug": "mvcc", "name": "MVCC"}

RESOLVE = """([token, property]) => {
    const probe = document.createElement("div");
    probe.style.setProperty(property, `var(${token})`);
    document.body.append(probe);
    const value = getComputedStyle(probe).getPropertyValue(property);
    probe.remove();
    return value;
}"""

STYLE = """element => {
    const style = getComputedStyle(element);
    return {
        display: style.display,
        flexWrap: style.flexWrap,
        flex: [style.flexGrow, style.flexShrink, style.flexBasis],
        position: style.position,
        minWidth: style.minWidth,
        minHeight: style.minHeight,
        height: style.height,
        width: style.width,
        background: style.backgroundColor,
        color: style.color,
        fontSize: style.fontSize,
        fontWeight: style.fontWeight,
        lineHeight: style.lineHeight,
        rowGap: style.rowGap,
        columnGap: style.columnGap,
        borderColors: [style.borderTopColor, style.borderRightColor,
                       style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        borderStyles: [style.borderTopStyle, style.borderRightStyle,
                       style.borderBottomStyle, style.borderLeftStyle],
        radii: [style.borderTopLeftRadius, style.borderTopRightRadius,
                style.borderBottomRightRadius, style.borderBottomLeftRadius],
        padding: [style.paddingTop, style.paddingRight,
                  style.paddingBottom, style.paddingLeft],
    };
}"""

# The element around a table that scrolls it sideways: its frame.
FRAME = """table => {
    for (let element = table.parentElement; element; element = element.parentElement) {
        const overflow = getComputedStyle(element).overflowX;
        if (overflow === "auto" || overflow === "scroll") {
            return [element.scrollWidth, element.clientWidth];
        }
    }
    return null;
}"""


def resolve(page: Page, token: str, prop: str) -> str:
    return page.evaluate(RESOLVE, [token, prop])


def px(page: Page, token: str) -> float:
    """A size token in pixels, as the page resolves it."""
    return float(resolve(page, token, "width").removesuffix("px"))


def style(locator: Locator) -> dict:
    return locator.evaluate(STYLE)


def call(page: Page, method: str, path: str, body: dict) -> dict:
    """Calls review from the page, as the page does, so the session's cookie
    and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/review/api${path}`, {
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


def add_deck(page: Page, name: str) -> str:
    return call(page, "POST", "/decks", {"name": name})["id"]


def add_card(page: Page, deck: str, front: str, back: str, topic: dict) -> str:
    card = {"deck_id": deck, "front": front, "back": back, "topics": [topic]}
    return call(page, "POST", "/cards", card)["id"]


def stage(page: Page) -> dict[str, str]:
    """Two decks and three cards, all due now, under two topics, as the
    study test stages them. Returns the decks' ids by name."""
    compilers = add_deck(page, "Compilers")
    databases = add_deck(page, "Databases")
    add_card(
        page,
        compilers,
        "What does an LL(1) parser look ahead at?",
        "One token",
        PARSING,
    )
    add_card(
        page,
        compilers,
        "What does an LR parser build bottom up?",
        "A rightmost derivation, in reverse",
        PARSING,
    )
    add_card(page, databases, "What does MVCC keep per row?", "Versions", MVCC)
    return {"Compilers": compilers, "Databases": databases}


def open_review(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/review")
    expect(sections(page, LABEL)).to_be_visible()


def rows(scope: Locator) -> Locator:
    """A list's own rows."""
    return scope.locator(":scope > li")


def assert_inset_rows(page: Page, entries: Locator, count: int, where: str) -> None:
    """Each row an inset row of "Inset list row" in section 4 of DESIGN.md,
    --inset-gap apart with no line between them."""
    expect(entries).to_have_count(count)
    fill = resolve(page, "--surface-2", "background-color")
    radius = resolve(page, "--radius-md", "border-top-left-radius")
    row_h = resolve(page, "--row-h", "min-height")
    for row in entries.all():
        look = style(row)
        assert look["background"] == fill, where
        assert look["radii"] == [radius] * 4, where
        assert look["padding"] == ["8px", "12px", "8px", "12px"], where
        assert look["minHeight"] == row_h, where
        assert look["borderWidths"] == ["0px"] * 4, where
    gap = px(page, "--inset-gap")
    boxes = [row.bounding_box() for row in entries.all()]
    for above, below in pairwise(boxes):
        between = below["y"] - (above["y"] + above["height"])
        assert abs(between - gap) < 0.5, f"{where}: {between}px between rows"


def open_card_form(page: Page) -> Locator:
    page.goto("/review/cards")
    page.get_by_role("button", name="Add card").click()
    return page.get_by_role("form", name="New card")


def test_the_top_bar_names_the_section(page: Page, invite_code: str, base_url: str):
    open_review(page, invite_code, base_url)
    crumb = page.locator("platform-nav .crumb")
    expect(crumb).to_have_text("Review / Study")

    # Opening a section moves the crumb with it, without a reload: the mark
    # set on the window survives.
    page.evaluate("window.notReloaded = true")
    open_section(page, LABEL, "Cards")
    expect(page).to_have_url(f"{base_url}/review/cards")
    expect(crumb).to_have_text("Review / Cards")
    open_section(page, LABEL, "Decks")
    expect(crumb).to_have_text("Review / Decks")
    assert page.evaluate("window.notReloaded === true")

    # A study session is Study's.
    page.goto("/review/study")
    expect(page.get_by_text("Nothing due")).to_be_visible()
    expect(crumb).to_have_text("Review / Study")


def test_the_page_header_follows_the_layout(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_review(page, invite_code, base_url)
    lede = page.get_by_text(
        "Cards that come back to you, so what you mark as worth remembering sticks."
    )
    look = style(lede)
    assert look["fontSize"] == resolve(page, "--text-body-lg-size", "font-size")
    assert look["lineHeight"] == resolve(page, "--text-body-lg-line", "line-height")
    assert look["color"] == resolve(page, "--text-2", "color")

    header = page.locator("main > header")
    header_box = header.bounding_box()
    tabs_box = sections(page, LABEL).bounding_box()
    gap = tabs_box["y"] - (header_box["y"] + header_box["height"])
    assert abs(gap - px(page, "--space-4")) < 0.5, gap


def test_a_link_drawn_as_a_button_is_a_pill(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_review(page, invite_code, base_url)
    stage(page)
    page.goto("/review")
    everything = page.get_by_role("link", name="Start everything")
    start = (
        rows(page.get_by_role("list", name="By deck"))
        .filter(has_text="Compilers")
        .get_by_role("link", name="Start")
    )
    expect(everything).to_be_visible()
    expect(start).to_be_visible()

    control_h = px(page, "--control-h")
    radius = resolve(page, "--radius-full", "border-top-left-radius")
    for link, where in [(everything, "Start everything"), (start, "Start")]:
        look = style(link)
        assert abs(link.bounding_box()["height"] - control_h) < 0.5, where
        assert look["radii"] == [radius] * 4, where
        assert look["padding"][1] == look["padding"][3] == "18px", where

    secondary = style(start)
    assert secondary["background"] == TRANSPARENT
    assert secondary["borderWidths"] == ["1px"] * 4
    assert secondary["borderStyles"] == ["solid"] * 4
    assert secondary["borderColors"] == [resolve(page, "--border-strong", "color")] * 4

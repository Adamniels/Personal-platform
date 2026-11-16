"""The core's pages and canary's page in the platform's style: the hard rules,
the layout and the button, text field and card rules of design/DESIGN.md,
drawn from design/tokens.css. See "The core's frontend" in
Core/docs/core-architecture.md.

Colours and sizes are compared with the token as the same page resolves it,
as test_gallery.py does, so no value is written here: a change to tokens.css
changes both sides, and only a page that stops using its token fails."""

import uuid
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture, log_in, register, register_and_log_in
from look import (
    assert_active_is_inverted,
    assert_headings_are_capitals,
    assert_nothing_scrolls_sideways,
)

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"

# The core's pages: those anyone can open, and those that need a session.
OPEN_PAGES = {"login": "/login", "register": "/register", "reset": "/reset"}
SIGNED_IN_PAGES = {
    "settings-password": "/settings/password",
    "logout": "/auth/logout",
    "front-signed-in": "/",
}

# Resolves a token through a probe element in the page, so the value is the
# one the page's own tokens.css and theme give, in the browser's format.
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
        background: style.backgroundColor,
        color: style.color,
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
        height: element.getBoundingClientRect().height,
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        fontWeight: style.fontWeight,
        boxShadow: style.boxShadow,
        textAlign: style.textAlign,
        maxWidth: style.maxWidth,
        gap: style.rowGap,
        textTransform: style.textTransform,
        lineHeight: style.lineHeight,
    };
}"""

# Every element that looks like a primary button: the accent fill.
PRIMARY_COUNT = """accent => [...document.querySelectorAll("body *")]
    .filter(element => getComputedStyle(element).backgroundColor === accent)
    .filter(element => element.getClientRects().length > 0)
    .length"""

AXE_CHECK = """() => axe.run().then(result => result.violations.map(v => ({
    id: v.id,
    targets: v.nodes.map(node => node.target.join(" ")),
})))"""


def resolve(page: Page, token: str, prop: str) -> str:
    return page.evaluate(RESOLVE, [token, prop])


def style(locator: Locator) -> dict:
    return locator.evaluate(STYLE)


def px(page: Page, token: str) -> float:
    """A size token in pixels, as the page resolves it."""
    return float(resolve(page, token, "width").removesuffix("px"))


def first_family(font_family: str) -> str:
    return font_family.split(",")[0].strip().strip("\"'")


def use_theme(page: Page, theme: str) -> None:
    """Remembers the theme the way the bar's toggle does, so theme.js sets it
    on the next load, on pages without the bar as well."""
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    page.evaluate("document.fonts.ready.then(() => null)")


def form_card(page: Page) -> Locator:
    """The card a form page sits in: the one that holds the page's heading."""
    return page.locator(".form-card").filter(has=page.get_by_role("heading", level=1))


def open_page(page: Page, path: str) -> None:
    response = page.goto(path)
    assert response is not None and response.status == 200, path


def assert_card_frame(page: Page, card: Locator) -> None:
    """The card rule: --surface, 1px --border, --radius-lg, no shadow."""
    look = style(card)
    assert look["background"] == resolve(page, "--surface", "background-color")
    border = resolve(page, "--border", "color")
    assert look["borderColors"] == [border] * 4
    assert look["borderWidths"] == ["1px"] * 4
    assert look["borderStyles"] == ["solid"] * 4
    radius = resolve(page, "--radius-lg", "border-top-left-radius")
    assert look["radii"] == [radius] * 4
    assert look["boxShadow"] == "none"


@pytest.mark.parametrize("theme", THEMES)
def test_the_login_page_uses_the_platform_values(page: Page, theme: str):
    open_page(page, "/login")
    use_theme(page, theme)

    body = style(page.locator("body"))
    assert body["background"] == resolve(page, "--bg", "background-color")

    card = form_card(page)
    expect(card).to_have_count(1)
    expect(card.locator("form")).to_have_count(1)
    assert_card_frame(page, card)
    assert card.bounding_box()["width"] <= 26 * 16

    for name in ["email", "password"]:
        field = style(page.locator(f"[name={name}]"))
        assert field["height"] == px(page, "--control-h"), name
        border = resolve(page, "--border-strong", "color")
        assert field["borderColors"] == [border] * 4, name
        assert field["borderWidths"] == ["1px"] * 4, name
        assert field["background"] == resolve(page, "--surface", "background-color")

    submit = style(page.get_by_role("button", name="Log in"))
    assert submit["background"] == resolve(page, "--accent", "background-color")
    assert submit["color"] == resolve(page, "--on-accent", "color")
    assert submit["height"] == px(page, "--control-h")

    heading = style(page.get_by_role("heading", name="Log in", level=1))
    assert first_family(heading["fontFamily"]) == "Barlow Condensed"


@pytest.mark.parametrize("theme", THEMES)
def test_the_core_buttons_and_fields_are_pills(page: Page, theme: str):
    """The button and text field rules: --control-h high, --radius-full,
    the fields with 16px at their sides and the button with 18px. The form
    card is padded --card-pad."""
    open_page(page, "/login")
    use_theme(page, theme)
    full = resolve(page, "--radius-full", "border-top-left-radius")
    height = px(page, "--control-h")

    for name in ["email", "password"]:
        field = style(page.locator(f"[name={name}]"))
        assert field["height"] == height, name
        assert field["radii"] == [full] * 4, name
        assert field["padding"][1] == field["padding"][3] == "16px", name

    button = style(page.get_by_role("button", name="Log in"))
    assert button["height"] == height
    assert button["radii"] == [full] * 4
    assert button["padding"][1] == button["padding"][3] == "18px"
    assert button["fontSize"] == "14px"

    card_pad = resolve(page, "--card-pad", "padding-top")
    assert style(form_card(page))["padding"] == [card_pad] * 4


def test_a_form_error_is_announced_and_styled(page: Page):
    open_page(page, "/login")
    log_in(page, f"e2e-{uuid.uuid4()}@example.com", uuid.uuid4().hex[:20])

    message = page.get_by_role("alert")
    expect(message).to_have_text("Error: Email or password is incorrect.")
    assert style(message)["color"] == resolve(page, "--danger", "color")
    assert style(message)["fontWeight"] == "500"

    field = page.locator("[name=password]")
    expect(field).to_have_attribute("aria-invalid", "true")
    # The field names its message, so a screen reader reads it with the field.
    message_id = message.get_attribute("id")
    assert message_id
    expect(field).to_have_attribute("aria-describedby", message_id)
    look = style(field)
    assert look["borderWidths"] == ["2px"] * 4
    assert look["borderColors"] == [resolve(page, "--danger", "color")] * 4
    # The 2px border does not change the field's size, nor where its text
    # starts: a pixel comes off each side's padding.
    assert look["height"] == px(page, "--control-h")
    assert look["padding"][1] == look["padding"][3] == "15px"

    # The email the person typed is kept and not marked: the message is
    # about the pair, and the core does not say which one was wrong.
    expect(page.locator("[name=email]")).not_to_have_attribute("aria-invalid", "true")


@pytest.mark.parametrize("theme", THEMES)
def test_the_home_page_cards_follow_the_card_rule(
    page: Page, invite_code: str, base_url: str, theme: str
):
    register_and_log_in(page, invite_code, base_url)
    use_theme(page, theme)

    card = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Canary", level=2)
    )
    expect(card).to_have_count(1)
    assert_card_frame(page, card)
    look = style(card)
    assert look["padding"] == [resolve(page, "--card-pad", "padding-top")] * 4

    # The card row: a wrapping flex row, each card 340px at its basis.
    row = page.locator(".cards").evaluate(
        """cards => {
            const style = getComputedStyle(cards);
            const card = getComputedStyle(cards.firstElementChild);
            return [style.display, style.flexWrap, style.rowGap, card.flexBasis,
                    card.flexGrow, card.flexShrink];
        }"""
    )
    gap = resolve(page, "--space-4", "row-gap")
    assert row == ["flex", "wrap", gap, "340px", "1", "1"]
    assert look["gap"] == resolve(page, "--space-4", "row-gap")

    # The title is the card's label: plain text in the UI font, not a link.
    heading = card.get_by_role("heading", level=2)
    title = style(heading)
    assert first_family(title["fontFamily"]) == "Inter"
    assert title["fontSize"] == resolve(page, "--text-label-size", "font-size")
    assert title["fontWeight"] == "500"
    assert title["color"] == resolve(page, "--text-2", "color")
    assert title["textTransform"] == "none"
    expect(heading.get_by_role("link")).to_have_count(0)

    # On its right, the arrow link that opens the feature: a 36px circle.
    arrow = card.get_by_role("link", name="Open Canary", exact=True)
    expect(arrow).to_have_attribute("href", "/canary")
    look = style(arrow)
    assert look["height"] == 36
    assert arrow.bounding_box()["width"] == 36
    full = resolve(page, "--radius-full", "border-top-left-radius")
    assert look["radii"] == [full] * 4
    assert look["background"] == resolve(page, "--surface-2", "background-color")
    assert look["color"] == resolve(page, "--text-2", "color")
    title_box = heading.bounding_box()
    arrow_box = arrow.bounding_box()
    assert arrow_box["x"] >= title_box["x"] + title_box["width"]
    # Flush with the card's padding on the right, inside its 1px border.
    card_box = card.bounding_box()
    inner_right = card_box["x"] + card_box["width"] - 1 - px(page, "--card-pad")
    assert abs(arrow_box["x"] + arrow_box["width"] - inner_right) <= 1

    # A feature the core could not reach. The e2e stack cannot take canary
    # away from one test, so the placeholder is the markup the core renders
    # for it, which Core/tests/front_page.rs pins, added to the real page.
    page.locator(".cards").evaluate(
        """cards => cards.insertAdjacentHTML("beforeend",
            '<article class="card unavailable"><h2>Desk</h2>'
            + '<p>Desk could not be reached.</p></article>')"""
    )
    placeholder = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Desk", level=2)
    )
    # The empty state rule: 1px dashed --border-strong, --radius-lg, padding
    # --space-7 --space-4, centred, a weight 600 line and a small --text-2 line.
    look = style(placeholder)
    assert look["borderStyles"] == ["dashed"] * 4
    assert look["borderWidths"] == ["1px"] * 4
    assert look["borderColors"] == [resolve(page, "--border-strong", "color")] * 4
    radius = resolve(page, "--radius-lg", "border-top-left-radius")
    assert look["radii"] == [radius] * 4
    vertical = resolve(page, "--space-7", "padding-top")
    side = resolve(page, "--space-4", "padding-top")
    assert look["padding"] == [vertical, side, vertical, side]
    assert look["textAlign"] == "center"
    assert look["boxShadow"] == "none"
    # Its first line in the heading font at the h2 size, in capitals.
    first = style(placeholder.get_by_role("heading", level=2))
    assert first_family(first["fontFamily"]) == "Barlow Condensed"
    assert first["fontSize"] == resolve(page, "--text-h2-size", "font-size")
    assert first["fontWeight"] == "600"
    assert first["textTransform"] == "uppercase"
    expect(placeholder.get_by_role("link")).to_have_count(0)
    line = style(placeholder.get_by_text("Desk could not be reached."))
    assert line["fontSize"] == resolve(page, "--text-small-size", "font-size")
    assert line["color"] == resolve(page, "--text-2", "color")


def test_core_pages_name_their_crumb(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)
    crumb = page.locator("platform-nav .crumb")
    for path, name in [
        ("/settings/password", "Settings / Password"),
        ("/auth/logout", "Log out"),
        ("/", "Home"),
    ]:
        open_page(page, path)
        expect(crumb).to_have_text(name)


@pytest.mark.parametrize("theme", THEMES)
def test_the_notice_is_a_dot_and_words(
    page: Page, invite_code: str, base_url: str, theme: str
):
    """What a form did, such as a changed password: --ok words on --ok-soft,
    after a 6px dot, so it is never colour alone."""
    register_and_log_in(page, invite_code, base_url)
    open_page(page, "/settings/password?changed=1")
    use_theme(page, theme)

    notice = page.get_by_role("status")
    expect(notice).to_have_text(
        "Password changed. Your other sessions were signed out."
    )
    look = style(notice)
    assert look["background"] == resolve(page, "--ok-soft", "background-color")
    assert look["color"] == resolve(page, "--ok", "color")
    assert look["fontWeight"] == "500"
    radius = resolve(page, "--radius-md", "border-top-left-radius")
    assert look["radii"] == [radius] * 4
    dot = notice.evaluate(
        """element => {
            const dot = getComputedStyle(element, "::before");
            return [dot.content, dot.width, dot.height, dot.backgroundColor];
        }"""
    )
    assert dot == ['""', "6px", "6px", look["color"]]


def review_card_with_cards_due(page: Page, invite_code: str, base_url: str) -> Locator:
    """Logs a fresh account in, gives it a card due in review, and returns
    the front page's Review card."""
    register_and_log_in(page, invite_code, base_url)
    add_due_review_card(page)
    open_page(page, "/")
    card = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Review", level=2)
    )
    expect(card).to_have_count(1)
    page.evaluate("document.fonts.ready.then(() => null)")
    return card


@pytest.mark.parametrize("theme", THEMES)
def test_the_card_number_is_a_thin_numeral(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    card = review_card_with_cards_due(page, invite_code, base_url)
    use_theme(page, theme)

    count = card.locator(".key-fact .count")
    expect(count).to_have_text("1")
    look = style(count)
    assert first_family(look["fontFamily"]) == "Inter"
    assert look["fontSize"] == resolve(page, "--text-num-xl-size", "font-size")
    assert look["fontWeight"] == "300"
    assert look["color"] == resolve(page, "--text", "color")

    # Its words to its right, their bottoms level.
    words = card.locator(".key-fact .words")
    expect(words).to_have_text("due now")
    count_box = count.bounding_box()
    words_box = words.bounding_box()
    assert words_box["x"] >= count_box["x"] + count_box["width"]
    count_bottom = count_box["y"] + count_box["height"]
    words_bottom = words_box["y"] + words_box["height"]
    assert abs(count_bottom - words_bottom) <= 1


@pytest.mark.parametrize("theme", THEMES)
def test_a_card_that_needs_you_has_one_accent_border(
    page: Page, invite_code: str, base_url: str, theme: str
):
    card = review_card_with_cards_due(page, invite_code, base_url)
    use_theme(page, theme)

    look = style(card)
    assert look["borderWidths"] == ["1px"] * 4
    assert look["borderStyles"] == ["solid"] * 4
    assert look["borderColors"] == [resolve(page, "--accent-border", "color")] * 4
    assert look["boxShadow"] == "none"


@pytest.mark.parametrize("theme", THEMES)
def test_the_cards_rows_are_label_and_value(
    page: Page, invite_code: str, base_url: str, theme: str
):
    card = review_card_with_cards_due(page, invite_code, base_url)
    use_theme(page, theme)

    rows = card.get_by_role("listitem")
    expect(rows).to_have_count(1)
    border = resolve(page, "--border", "color")
    for row in rows.all():
        look = style(row)
        assert look["height"] == 36
        assert look["borderWidths"] == ["0px", "0px", "1px", "0px"]
        assert look["borderColors"][2] == border
        name = style(row.locator(".name"))
        assert name["color"] == resolve(page, "--text-2", "color")
        number = style(row.locator(".number"))
        assert first_family(number["fontFamily"]) == "JetBrains Mono"
        assert number["color"] == resolve(page, "--text", "color")


def test_every_core_page_has_one_primary_button_at_most(
    page: Page, invite_code: str, base_url: str
):
    accent = None
    for path in OPEN_PAGES.values():
        open_page(page, path)
        accent = resolve(page, "--accent", "background-color")
        assert page.evaluate(PRIMARY_COUNT, accent) == 1, path
        submit = page.locator("form button[type=submit]")
        assert style(submit)["background"] == accent, path

    register_and_log_in(page, invite_code, base_url)
    for path in SIGNED_IN_PAGES.values():
        open_page(page, path)
        accent = resolve(page, "--accent", "background-color")
        assert page.evaluate(PRIMARY_COUNT, accent) <= 1, path
        for submit in page.locator("main form button[type=submit]").all():
            assert style(submit)["background"] == accent, path


@pytest.mark.parametrize("theme", THEMES)
def test_every_core_page_passes_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, theme: str
):
    def check(path: str) -> None:
        page.add_script_tag(path=str(AXE))
        assert page.evaluate(AXE_CHECK) == [], path

    for path in OPEN_PAGES.values():
        open_page(page, path)
        use_theme(page, theme)
        check(path)

    # A refused form, with its message and the marked field.
    open_page(page, "/login")
    log_in(page, f"e2e-{uuid.uuid4()}@example.com", uuid.uuid4().hex[:20])
    expect(page.get_by_role("alert")).to_be_visible()
    check("/login refused")

    register_and_log_in(page, invite_code, base_url)
    for path in SIGNED_IN_PAGES.values():
        open_page(page, path)
        check(path)


def test_fields_are_16px_and_44px_tall_on_phone(
    page: Page, invite_code: str, base_url: str
):
    page.set_viewport_size(VIEWPORTS["phone"])

    def check(path: str) -> None:
        fields = page.locator("main input:not([type=hidden])")
        assert fields.count() > 0, path
        for field in fields.all():
            look = style(field)
            assert look["fontSize"] == "16px", path
            assert look["height"] == 44, path
        for button in page.locator("main button").all():
            assert style(button)["height"] == 44, path

    for path in OPEN_PAGES.values():
        open_page(page, path)
        check(path)
    register_and_log_in(page, invite_code, base_url)
    open_page(page, "/settings/password")
    check("/settings/password")


def test_canary_page_uses_the_platform_values(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    open_page(page, "/canary")
    page.evaluate("document.fonts.ready.then(() => null)")

    # Canary writes no style of its own: everything comes from the design
    # files it links, as the core's pages do.
    expect(page.locator("head style")).to_have_count(0)

    body = style(page.locator("body"))
    assert body["background"] == resolve(page, "--bg", "background-color")
    assert body["color"] == resolve(page, "--text", "color")

    main = style(page.get_by_role("main"))
    assert main["maxWidth"] == resolve(page, "--content-max", "max-width")
    # Inside the shell: --space-4 above, and the shell's own sides.
    assert main["padding"][0] == resolve(page, "--space-4", "padding-top")

    heading = style(page.get_by_role("heading", name="Canary", level=1))
    assert first_family(heading["fontFamily"]) == "Barlow Condensed"
    assert heading["fontSize"] == resolve(page, "--text-display-size", "font-size")
    assert heading["fontWeight"] == "600"

    account = style(page.get_by_role("main").locator("code"))
    assert first_family(account["fontFamily"]) == "JetBrains Mono"


def add_due_review_card(page: Page) -> None:
    """Adds a deck and a card to review through its API, from the page, so
    the front page's Review card has a card due now."""
    status = page.evaluate(
        """async () => {
            const call = (path, body) => fetch(`/review/api${path}`, {
                method: "POST",
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: JSON.stringify(body),
            });
            const deck = await (await call("/decks", {name: "Compilers"})).json();
            const card = await call("/cards", {
                deck_id: deck.id,
                front: "What does an LR parser build?",
                back: "A rightmost derivation in reverse",
                topics: [{slug: "parsing", name: "Parsing"}],
            });
            return card.status;
        }"""
    )
    assert status in (200, 201), status


def assert_the_look(page: Page, name: str) -> None:
    """The three look checks of look.py, each naming the page it failed on."""
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
    ):
        try:
            check(page)
        except AssertionError as error:
            raise AssertionError(f"{name}: {error}") from None


def test_the_look_checks_name_what_breaks_them(page: Page):
    """Each look check fails on an element that breaks its rule, naming it,
    so a page that passes them passes for a reason."""
    open_page(page, "/login")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
    ):
        check(page)

    page.locator("main").evaluate(
        """main => main.insertAdjacentHTML("beforeend",
            '<h2 id="lower" style="text-transform: none">Lower</h2>'
            + '<div id="wide" style="overflow-x: auto; width: 100px">'
            + '<div style="width: 300px">Wide</div></div>'
            + '<a id="amber" href="/" aria-current="page"'
            + ' style="border: 1px solid var(--accent-border)">Amber</a>')"""
    )
    for check, offender in (
        (assert_headings_are_capitals, "h2#lower"),
        (assert_nothing_scrolls_sideways, "div#wide"),
        (assert_active_is_inverted, "a#amber"),
    ):
        with pytest.raises(AssertionError, match=offender):
            check(page)

    # The document itself scrolling sideways.
    page.locator("main").evaluate(
        """main => main.insertAdjacentHTML("beforeend",
            '<div style="width: 3000px">Too wide</div>')"""
    )
    with pytest.raises(AssertionError, match="the document"):
        assert_nothing_scrolls_sideways(page)


@pytest.mark.parametrize("theme", THEMES)
def test_the_core_pages_follow_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    for name, path in OPEN_PAGES.items():
        open_page(page, path)
        use_theme(page, theme)
        assert_the_look(page, name)

    # A refused form, with its message and the field marked.
    open_page(page, "/login")
    log_in(page, f"e2e-{uuid.uuid4()}@example.com", uuid.uuid4().hex[:20])
    expect(page.get_by_role("alert")).to_be_visible()
    assert_the_look(page, "login refused")

    register_and_log_in(page, invite_code, base_url)
    for name, path in SIGNED_IN_PAGES.items():
        open_page(page, path)
        expect(page.get_by_role("navigation").first).to_be_visible()
        assert_the_look(page, name)

    # The front page with a card that needs the person.
    add_due_review_card(page)
    open_page(page, "/")
    expect(page.locator(".card.needs-you")).to_have_count(1)
    assert_the_look(page, "front page, review due")


@pytest.mark.parametrize("theme", THEMES)
def test_each_page_in_each_theme(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    """Screenshots of every restyled page, in both themes, for the owner to
    look at: <page>-<theme>-<viewport>.png."""

    def shoot(name: str) -> None:
        scroll_width = page.evaluate("document.documentElement.scrollWidth")
        assert scroll_width <= page.viewport_size["width"], name
        capture(page, f"{name}-{theme}")

    for name, path in OPEN_PAGES.items():
        open_page(page, path)
        use_theme(page, theme)
        shoot(name)

    email, password = register(page, invite_code)
    log_in(page, email, password)
    expect(page).to_have_url(f"{base_url}/")
    for name, path in {**SIGNED_IN_PAGES, "canary": "/canary"}.items():
        open_page(page, path)
        expect(page.get_by_role("navigation").first).to_be_visible()
        shoot(name)

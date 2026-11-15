"""The shared nav bar, `<platform-nav>` from /design/nav.js, on the core's
pages: the rail and the top bar on desktop, the top bar and the tab bar on
phone, and the page shell from /design/page.css around them. See
"Navigation" in High-level/docs/platform-architecture.md and sections 2 and
3 of design/DESIGN.md.

Colours and sizes are compared with the token as the same page resolves it,
as test_gallery.py does, so no value is written here: a change to tokens.css
changes both sides, and only a bar that stops using its token fails."""

import re
from pathlib import Path

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import VIEWPORTS, capture, register_and_log_in

THEMES = ["light", "dark"]
TOKENS = Path(__file__).parents[2] / "design/tokens.css"
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
# The links of a fresh account, in registry order, which infra/features.toml
# makes Canary, Review and Dev flow, after the two the bar holds itself.
LINKS = ["Home", "Memory", "Canary", "Review", "Dev flow"]
WIDE = {"width": 1920, "height": 1080}
# The icons a link may draw, as lucide-react 1.72.0's icons/<name>.mjs gives
# them without their React keys: what nav.js must draw, written here apart
# from it so a test catches a copy that drifts.
LUCIDE = {
    "house": [
        ["path", {"d": "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"}],
        [
            "path",
            {
                "d": "M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"
            },
        ],
    ],
    "database": [
        ["ellipse", {"cx": "12", "cy": "5", "rx": "9", "ry": "3"}],
        ["path", {"d": "M3 5V19A9 3 0 0 0 21 19V5"}],
        ["path", {"d": "M3 12A9 3 0 0 0 21 12"}],
    ],
    "box": [
        [
            "path",
            {
                "d": "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"
            },
        ],
        ["path", {"d": "m3.3 7 8.7 5 8.7-5"}],
        ["path", {"d": "M12 22V12"}],
    ],
    "bird": [
        ["path", {"d": "M16 7h.01"}],
        ["path", {"d": "M3.4 18H12a8 8 0 0 0 8-8V7a4 4 0 0 0-7.28-2.3L2 20"}],
        ["path", {"d": "m20 7 2 .5-2 .5"}],
        ["path", {"d": "M10 18v3"}],
        ["path", {"d": "M14 17.75V21"}],
        ["path", {"d": "M7 18a6 6 0 0 0 3.84-10.61"}],
    ],
    "layers": [
        [
            "path",
            {
                "d": "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z"
            },
        ],
        [
            "path",
            {
                "d": "M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12"
            },
        ],
        [
            "path",
            {
                "d": "M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17"
            },
        ],
    ],
    "git-pull-request": [
        ["circle", {"cx": "18", "cy": "18", "r": "3"}],
        ["circle", {"cx": "6", "cy": "6", "r": "3"}],
        ["path", {"d": "M13 6h3a2 2 0 0 1 2 2v7"}],
        ["line", {"x1": "6", "x2": "6", "y1": "9", "y2": "21"}],
    ],
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
        borders: [style.borderTopColor, style.borderRightColor,
                  style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
        borderStyles: [style.borderTopStyle, style.borderRightStyle,
                       style.borderBottomStyle, style.borderLeftStyle],
        radius: style.borderTopLeftRadius,
        outlineStyle: style.outlineStyle,
        outlineColor: style.outlineColor,
        shadow: style.boxShadow,
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        fontWeight: style.fontWeight,
        textTransform: style.textTransform,
        textOverflow: style.textOverflow,
        whiteSpace: style.whiteSpace,
        position: style.position,
    };
}"""


def resolve(page: Page, token: str, prop: str) -> str:
    return page.evaluate(RESOLVE, [token, prop])


def size(page: Page, token: str) -> float:
    """A size token in pixels, as the page resolves it."""
    return float(resolve(page, token, "width").removesuffix("px"))


def dark_value(token: str) -> str:
    """The token's value in the `[data-theme="dark"]` block of tokens.css."""
    block = re.search(
        r'\[data-theme="dark"\]\s*\{(.*?)\}', TOKENS.read_text(), re.DOTALL
    )
    assert block is not None
    value = re.search(rf"{re.escape(token)}:\s*([^;]+);", block.group(1))
    assert value is not None, token
    return value.group(1).strip()


def nav_answer(features: list[dict], email: str = "someone@example.com") -> dict:
    return {"features": features, "user": {"email": email}}


def nav(page: Page) -> Locator:
    return page.get_by_role("navigation")


def links_bar(page: Page) -> Locator:
    """The list of Home, Memory and the features: in the rail on desktop, the
    tab bar at the bottom on phone."""
    return page.locator("platform-nav .links")


def rail(page: Page) -> Locator:
    return page.locator("platform-nav .rail")


def top_bar(page: Page) -> Locator:
    return page.locator("platform-nav .top")


def crumb(page: Page) -> Locator:
    return page.locator("platform-nav .crumb")


def pill(page: Page, email: str) -> Locator:
    return nav(page).get_by_role("button", name=email, exact=True)


def drawn(page: Page, email: str) -> None:
    """Waits for the bar's second drawing, once /api/nav has answered, which
    replaces whatever a box measured before it would read."""
    expect(pill(page, email)).to_be_visible()


def color_of_icon(link: Locator) -> str:
    return link.locator("svg").evaluate("el => getComputedStyle(el).color")


def shapes_of(link: Locator) -> list[list]:
    """The shapes a link's icon draws, each as its tag and its attributes,
    in the form LUCIDE holds them."""
    return link.locator("svg").evaluate(
        """svg => [...svg.children].map(shape => [
            shape.tagName,
            Object.fromEntries([...shape.attributes].map(a => [a.name, a.value])),
        ])"""
    )


def remember_theme(page: Page, theme: str) -> None:
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)


def add_tall_content(page: Page) -> Locator:
    """Content taller than the screen, so the page scrolls and its last
    element is only reached at the end, where the tab bar would cover it."""
    page.locator("main").evaluate(
        """main => {
            const tall = document.createElement("div");
            tall.style.height = "2000px";
            const last = document.createElement("p");
            last.id = "e2e-last";
            last.textContent = "The end of the page";
            main.append(tall, last);
        }"""
    )
    return page.locator("#e2e-last")


def test_the_rail_holds_the_mark_home_memory_and_the_features(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)

    links = links_bar(page).get_by_role("link")
    expect(links).to_have_count(len(LINKS))
    for index, (name, href) in enumerate(
        zip(LINKS, ["/", "/brain", "/canary", "/review", "/devflow"])
    ):
        expect(links.nth(index)).to_have_accessible_name(name)
        expect(links.nth(index)).to_have_attribute("href", href)
    # One mark shows, linking home: in the rail on desktop, in the top bar
    # on phone.
    mark = nav(page).get_by_role("link", name="Platform", exact=True)
    expect(mark).to_have_count(1)
    expect(mark).to_have_attribute("href", "/")
    expect(page.get_by_role("heading", name="Platform", level=1)).to_be_visible()
    # Never makes the page scroll sideways, however long the email.
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    if viewport == "phone":
        return

    height = page.viewport_size["height"]
    pad = size(page, "--page-pad")
    box = rail(page).bounding_box()
    assert box is not None
    assert box["width"] == size(page, "--rail-w")
    assert box["y"] == pad and box["height"] == height - 2 * pad
    style = rail(page).evaluate(STYLE)
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borders"] == [resolve(page, "--border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4
    assert style["radius"] == resolve(page, "--radius-lg", "border-top-left-radius")

    # The mark at the top, a square on the accent.
    expect(rail(page).get_by_role("link", name="Platform", exact=True)).to_be_visible()
    mark_box = rail(page).locator(".mark").bounding_box()
    assert mark_box is not None
    assert mark_box["width"] == 44 and mark_box["height"] == 44
    mark_style = rail(page).locator(".mark").evaluate(STYLE)
    assert mark_style["background"] == resolve(page, "--accent", "background-color")
    assert mark_style["radius"] == resolve(
        page, "--radius-sm", "border-top-left-radius"
    )

    # Each item a 44px circle under the mark, in a column.
    full = resolve(page, "--radius-full", "border-top-left-radius")
    previous = mark_box["y"] + mark_box["height"]
    for link in links.all():
        item = link.bounding_box()
        assert item is not None
        assert item["width"] == 44 and item["height"] == 44
        assert item["y"] >= previous
        assert link.evaluate(STYLE)["radius"] == full
        previous = item["y"] + item["height"]

    # The theme toggle at the bottom.
    toggle = rail(page).get_by_role("button", name=re.compile("^Switch to"))
    expect(toggle).to_be_visible()
    toggle_box = toggle.bounding_box()
    assert toggle_box is not None
    assert toggle_box["y"] >= previous
    bottom = box["y"] + box["height"]
    assert (
        0
        <= bottom - (toggle_box["y"] + toggle_box["height"])
        <= size(page, "--space-4") + 1
    )


def test_the_active_item_is_inverted(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    accent = resolve(page, "--accent-border", "color")

    links = links_bar(page).get_by_role("link")
    home = links_bar(page).get_by_role("link", name="Home", exact=True)
    expect(home).to_have_attribute("aria-current", "page")
    active = home.evaluate(STYLE)
    assert active["background"] == resolve(page, "--text", "background-color")
    assert color_of_icon(home) == resolve(page, "--bg", "color")
    if viewport == "phone":
        assert home.locator(".label").evaluate(STYLE)["color"] == resolve(
            page, "--bg", "color"
        )

    for link in links.all()[1:]:
        expect(link).not_to_have_attribute("aria-current", "page")
        style = link.evaluate(STYLE)
        if viewport == "desktop":
            assert style["background"] == resolve(
                page, "--surface-2", "background-color"
            )
        else:
            assert style["background"] != active["background"]
        assert color_of_icon(link) == resolve(page, "--text-2", "color")

    # Active is never marked with amber.
    for link in links.all():
        style = link.evaluate(STYLE)
        assert accent not in [
            border
            for border, line in zip(style["borders"], style["borderStyles"])
            if line != "none"
        ]
        assert style["outlineStyle"] == "none" or style["outlineColor"] != accent

    # The mark links home too, but is not the active item.
    mark = nav(page).get_by_role("link", name="Platform", exact=True)
    expect(mark).not_to_have_attribute("aria-current", "page")


def test_changing_current_moves_the_mark(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)
    home = links_bar(page).get_by_role("link", name="Home", exact=True)
    canary = links_bar(page).get_by_role("link", name="Canary", exact=True)
    expect(canary).to_be_visible()

    page.locator("platform-nav").evaluate("el => el.setAttribute('current', 'canary')")

    expect(canary).to_have_attribute("aria-current", "page")
    expect(home).not_to_have_attribute("aria-current", "page")


def test_the_top_bar_shows_the_crumb(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    bar_h = size(page, "--top-bar-h")

    box = top_bar(page).bounding_box()
    assert box is not None and box["height"] == bar_h
    style = top_bar(page).evaluate(STYLE)
    assert style["background"] == "rgba(0, 0, 0, 0)"
    assert style["borderWidths"] == ["0px"] * 4

    # No crumb attribute: the name of the link current names.
    expect(crumb(page)).to_have_text("Home")
    style = crumb(page).evaluate(STYLE)
    assert style["fontFamily"] == resolve(page, "--font-mono", "font-family")
    assert style["textTransform"] == "uppercase"
    assert style["color"] == resolve(page, "--text-3", "color")
    page.locator("platform-nav").evaluate("el => el.setAttribute('current', 'canary')")
    expect(crumb(page)).to_have_text("Canary")

    # A long crumb is cut with an ellipsis, on one line.
    long = "A crumb far longer than the bar has room for " * 6
    page.locator("platform-nav").evaluate(f"el => el.setAttribute('crumb', '{long}')")
    expect(crumb(page)).to_have_text(long)
    style = crumb(page).evaluate(STYLE)
    assert style["textOverflow"] == "ellipsis" and style["whiteSpace"] == "nowrap"
    assert crumb(page).evaluate("el => el.scrollWidth > el.clientWidth")
    assert top_bar(page).bounding_box()["height"] == bar_h
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    # Neither a crumb nor a current: empty.
    page.locator("platform-nav").evaluate(
        "el => { el.removeAttribute('crumb'); el.removeAttribute('current'); }"
    )
    expect(crumb(page)).to_have_text("")


def test_the_crumb_attribute_names_the_page(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    element = page.locator("platform-nav")

    element.evaluate("el => el.setAttribute('crumb', 'Memory / Rules')")
    expect(crumb(page)).to_have_text("Memory / Rules")

    element.evaluate("el => el.setAttribute('crumb', 'Memory / Timeline')")
    expect(crumb(page)).to_have_text("Memory / Timeline")
    # Without a reload: the bar the page loaded is still the one showing.
    assert page.evaluate("performance.getEntriesByType('navigation').length") == 1

    element.evaluate("el => el.removeAttribute('crumb')")
    expect(crumb(page)).to_have_text("Home")


def test_the_account_pill_opens_the_menu(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    button = pill(page, email)
    expect(button).to_be_visible()

    box = button.bounding_box()
    assert box is not None and box["height"] == 44
    style = button.evaluate(STYLE)
    assert style["radius"] == resolve(page, "--radius-full", "border-top-left-radius")
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borders"] == [resolve(page, "--border", "color")] * 4
    assert style["borderWidths"] == ["1px"] * 4

    avatar = button.locator(".avatar")
    expect(avatar).to_have_text(email[0].upper())
    avatar_box = avatar.bounding_box()
    assert avatar_box is not None
    assert avatar_box["width"] == 34 and avatar_box["height"] == 34
    avatar_style = avatar.evaluate(STYLE)
    assert avatar_style["background"] == resolve(
        page, "--surface-3", "background-color"
    )
    assert avatar_style["radius"] == resolve(
        page, "--radius-full", "border-top-left-radius"
    )
    shown = button.locator(".email")
    if viewport == "desktop":
        # The test's email is longer than 24 characters, so it is cut.
        assert len(email) > 24
        expect(shown).to_be_visible()
        expect(shown).to_have_text(email[:24] + "…")
    else:
        expect(shown).to_be_hidden()

    change = nav(page).get_by_role("link", name="Change password", exact=True)
    log_out = nav(page).get_by_role("link", name="Log out", exact=True)
    theme = nav(page).get_by_role("button", name=re.compile("^(Light|Dark) mode$"))
    expect(button).to_have_attribute("aria-expanded", "false")
    expect(change).to_be_hidden()

    button.click()
    expect(button).to_have_attribute("aria-expanded", "true")
    expect(change).to_be_visible()
    expect(change).to_have_attribute("href", "/settings/password")
    expect(log_out).to_be_visible()
    expect(log_out).to_have_attribute("href", "/auth/logout")
    # On phone the theme toggle is in the menu, since there is no rail.
    expect(theme).to_have_count(1 if viewport == "phone" else 0)

    menu = page.locator("platform-nav .menu")
    menu_style = menu.evaluate(STYLE)
    assert menu_style["background"] == resolve(page, "--surface", "background-color")
    assert menu_style["borders"] == [resolve(page, "--border", "color")] * 4
    assert menu_style["radius"] == resolve(
        page, "--radius-md", "border-top-left-radius"
    )
    assert menu_style["shadow"] == resolve(page, "--shadow-1", "box-shadow")
    row_h = size(page, "--row-h")
    for item in [change, log_out, *([theme] if viewport == "phone" else [])]:
        assert item.bounding_box()["height"] == row_h

    # Escape closes it and gives focus back to the pill.
    change.focus()
    page.keyboard.press("Escape")
    expect(change).to_be_hidden()
    expect(button).to_be_focused()
    expect(button).to_have_attribute("aria-expanded", "false")

    # A click outside closes it: on the page's left edge, which the menu,
    # opening to the left from the right edge, never covers.
    button.click()
    expect(change).to_be_visible()
    page.mouse.click(4, page.viewport_size["height"] // 2)
    expect(change).to_be_hidden()

    # So does focus leaving it, past its last item.
    button.click()
    expect(change).to_be_visible()
    for _ in range(4 if viewport == "phone" else 3):
        page.keyboard.press("Tab")
    expect(change).to_be_hidden()


def test_the_theme_toggle_overrides_the_system_and_is_remembered(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    page.emulate_media(color_scheme="light")
    email = register_and_log_in(page, invite_code, base_url)
    root = page.locator("html")
    expect(root).not_to_have_attribute("data-theme", re.compile("."))
    # The bar draws once more when /api/nav answers; press after that.
    drawn(page, email)
    bg = "getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()"

    def press(to: str) -> None:
        if viewport == "desktop":
            page.get_by_role("button", name=f"Switch to {to} mode").click()
        else:
            # On phone, from the account menu.
            pill(page, email).click()
            nav(page).get_by_role("button", name=f"{to.title()} mode").click()

    press("dark")
    expect(root).to_have_attribute("data-theme", "dark")
    assert page.evaluate(bg).lower() == dark_value("--bg").lower()

    page.reload()
    expect(root).to_have_attribute("data-theme", "dark")
    assert page.evaluate(bg).lower() == dark_value("--bg").lower()
    drawn(page, email)

    press("light")
    expect(root).to_have_attribute("data-theme", "light")
    assert page.evaluate(bg).lower() != dark_value("--bg").lower()
    page.reload()
    expect(root).to_have_attribute("data-theme", "light")


def test_no_flash_of_the_wrong_theme(page: Page, invite_code: str, base_url: str):
    page.emulate_media(color_scheme="light")
    register_and_log_in(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', 'dark')")
    # Runs before any script of the page's own, so what it reads at
    # DOMContentLoaded was set by a script in the head, before the body.
    page.add_init_script(
        """document.addEventListener("DOMContentLoaded", () => {
            window.themeAtLoad = document.documentElement.getAttribute("data-theme");
        });"""
    )

    page.reload()

    assert page.evaluate("window.themeAtLoad") == "dark"


def test_on_phone_the_links_are_a_tab_bar_at_the_bottom(
    page: Page, invite_code: str, base_url: str
):
    page.set_viewport_size(VIEWPORTS["phone"])
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    height = page.viewport_size["height"]

    # No rail: it draws no box, and neither its mark nor its toggle shows.
    assert rail(page).evaluate("el => el.getBoundingClientRect().width") == 0
    expect(rail(page).locator(".mark")).to_be_hidden()
    expect(page.get_by_role("button", name=re.compile("^Switch to"))).to_have_count(0)

    # The top bar holds the mark, the crumb and the avatar alone.
    top = top_bar(page)
    box = top.bounding_box()
    assert box is not None and box["y"] == 0
    expect(top.get_by_role("link")).to_have_count(1)
    expect(top.get_by_role("link", name="Platform", exact=True)).to_be_visible()
    expect(top.get_by_role("button")).to_have_count(1)
    expect(top.get_by_role("button", name=email, exact=True)).to_be_visible()
    expect(crumb(page)).to_be_visible()
    mark = top.locator(".mark").bounding_box()
    assert mark is not None and mark["width"] == 40 and mark["height"] == 40
    for item in [top.locator(".mark"), crumb(page), pill(page, email)]:
        inner = item.bounding_box()
        assert inner is not None
        assert inner["y"] >= box["y"]
        assert inner["y"] + inner["height"] <= box["y"] + box["height"]

    # The links: a bar fixed to the bottom, with the safe area under it,
    # which is none in the test's browser.
    bar = links_bar(page)
    bar_box = bar.bounding_box()
    assert bar_box is not None
    assert bar_box["y"] + bar_box["height"] == height
    assert bar_box["height"] == size(page, "--tab-bar-h")
    style = bar.evaluate(STYLE)
    assert style["position"] == "fixed"
    assert style["background"] == resolve(page, "--surface", "background-color")
    assert style["borderWidths"] == ["1px", "0px", "0px", "0px"]
    assert style["borders"][0] == resolve(page, "--border", "color")
    page.mouse.wheel(0, 400)
    assert bar.bounding_box()["y"] + bar.bounding_box()["height"] == height

    # Each tab a 20px icon over a 12px label, at least 56px high.
    radius = resolve(page, "--radius-md", "border-top-left-radius")
    for link in bar.get_by_role("link").all():
        tab = link.bounding_box()
        icon = link.locator("svg").bounding_box()
        label = link.locator(".label").bounding_box()
        assert tab is not None and icon is not None and label is not None
        assert tab["height"] >= 56
        assert icon["width"] == 20 and icon["height"] == 20
        assert icon["y"] + icon["height"] <= label["y"]
        label_style = link.locator(".label").evaluate(STYLE)
        assert label_style["fontSize"] == "12px"
        assert label_style["fontWeight"] == "500"
        assert link.evaluate(STYLE)["radius"] == radius


def test_more_than_five_links_fold_into_more(
    page: Page, invite_code: str, base_url: str
):
    page.set_viewport_size(VIEWPORTS["phone"])
    register_and_log_in(page, invite_code, base_url)
    features = [{"name": f"Feature {i}", "path": f"/feature{i}"} for i in range(1, 7)]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    bar = links_bar(page)
    # Home, Memory and six features: the first four show, and More holds the
    # rest.
    expect(bar.get_by_role("link")).to_have_text(
        ["Home", "Memory", "Feature 1", "Feature 2"]
    )
    more = bar.get_by_role("button", name="More", exact=True)
    expect(more).to_be_visible()
    expect(more).to_have_attribute("aria-expanded", "false")
    for i in range(3, 7):
        expect(page.get_by_role("link", name=f"Feature {i}", exact=True)).to_be_hidden()

    more.click()

    expect(more).to_have_attribute("aria-expanded", "true")
    sheet = page.locator("platform-nav .sheet")
    expect(sheet.get_by_role("link")).to_have_text(
        ["Feature 3", "Feature 4", "Feature 5", "Feature 6"]
    )
    expect(sheet.get_by_role("link").first).to_have_attribute("href", "/feature3")
    # Above the tab bar, so it covers none of it.
    sheet_box, bar_box = sheet.bounding_box(), bar.bounding_box()
    assert sheet_box is not None and bar_box is not None
    assert sheet_box["y"] + sheet_box["height"] <= bar_box["y"]
    capture(page, "nav-more")
    page.keyboard.press("Escape")
    expect(sheet).to_be_hidden()
    expect(more).to_be_focused()

    # On desktop every link is in the rail and there is no More.
    page.set_viewport_size(VIEWPORTS["desktop"])
    expect(bar.get_by_role("link")).to_have_count(8)
    expect(more).to_be_hidden()


def test_five_links_need_no_more(page: Page, invite_code: str, base_url: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    register_and_log_in(page, invite_code, base_url)
    # Home, Memory and three features.
    features = [{"name": f"Feature {i}", "path": f"/feature{i}"} for i in range(1, 4)]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    bar = links_bar(page)
    expect(bar.get_by_role("link")).to_have_count(5)
    expect(bar.get_by_role("button", name="More", exact=True)).to_be_hidden()


def test_the_page_content_is_not_covered_by_the_rail_or_either_bar(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    last = add_tall_content(page)
    first = page.locator("main > :first-child")
    height = page.viewport_size["height"]

    page.evaluate("window.scrollTo(0, 0)")
    top = top_bar(page).bounding_box()
    box = first.bounding_box()
    assert top is not None and box is not None
    assert box["y"] >= top["y"] + top["height"]
    if viewport == "desktop":
        side = rail(page).bounding_box()
        content = page.locator("main").bounding_box()
        assert side is not None and content is not None
        assert content["x"] == side["x"] + side["width"] + size(page, "--page-pad")
        # The top bar sits over the content column, not over the rail.
        assert top["x"] == content["x"]

    page.evaluate("window.scrollTo(0, document.documentElement.scrollHeight)")
    box = last.bounding_box()
    assert box is not None
    bottom = height
    if viewport == "phone":
        bar = links_bar(page).bounding_box()
        assert bar is not None
        bottom = bar["y"]
    assert box["y"] >= 0 and box["y"] + box["height"] <= bottom


def test_the_shell_is_centred_on_a_wide_window(
    page: Page, invite_code: str, base_url: str
):
    page.set_viewport_size(WIDE)
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    add_tall_content(page)
    pad = size(page, "--page-pad")

    side = rail(page).bounding_box()
    content = page.locator("main").bounding_box()
    assert side is not None and content is not None
    assert content["width"] == size(page, "--content-max")
    # As much room left of the rail as right of the content.
    assert side["x"] == WIDE["width"] - (content["x"] + content["width"])
    assert side["x"] > pad

    page.mouse.wheel(0, 600)
    page.wait_for_function("window.scrollY > 0")
    assert rail(page).bounding_box() == side


def test_the_page_keeps_the_rails_room_before_the_script_runs(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    email = register_and_log_in(page, invite_code, base_url)
    drawn(page, email)
    heading = page.get_by_role("heading", name="Platform", level=1)
    drawn_at = heading.bounding_box()

    page.route("**/design/nav.js", lambda route: route.abort())
    page.reload()

    expect(heading).to_be_visible()
    assert page.locator("platform-nav").bounding_box()["height"] == size(
        page, "--top-bar-h"
    )
    assert heading.bounding_box() == drawn_at


def test_the_bar_shows_home_and_memory_alone_when_the_nav_endpoint_fails(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)

    # A 401, a server error, and a request that never gets an answer. Each
    # waits for /api/nav to settle, since the bar shows the fixed links alone
    # until it does.
    failures = [
        ("response", lambda route: route.fulfill(status=401)),
        ("response", lambda route: route.fulfill(status=500)),
        ("requestfailed", lambda route: route.abort()),
    ]
    for event, handler in failures:
        page.unroute("**/api/nav")
        page.route("**/api/nav", handler)
        with page.expect_event(event, lambda r: r.url.endswith("/api/nav")):
            page.reload()

        expect(
            nav(page).get_by_role("link", name="Platform", exact=True)
        ).to_be_visible()
        expect(links_bar(page).get_by_role("link")).to_have_text(["Home", "Memory"])
        expect(nav(page).get_by_role("link")).to_have_count(3)
        expect(nav(page).get_by_role("button", name=re.compile("@"))).to_have_count(0)
        # Never sent anywhere: the bar fails quietly where the page is.
        expect(page).to_have_url(f"{base_url}/")


def test_a_link_from_the_endpoint_must_be_a_local_path(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    features = [
        {"name": "Local", "path": "/local"},
        {"name": "Offsite", "path": "//example.com/"},
        {"name": "Other origin", "path": "https://example.com/"},
        {"name": "Script", "path": "javascript:window.injected = true"},
        {"name": "Relative", "path": "relative"},
    ]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    expect(links_bar(page).get_by_role("link")).to_have_text(
        ["Home", "Memory", "Local"]
    )
    for name in ["Offsite", "Other origin", "Script", "Relative"]:
        expect(page.get_by_text(name, exact=True)).to_have_count(0)


def test_names_and_the_email_are_written_as_text(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    markup = '<img src="x" onerror="window.injected = true">'
    email = "<b>someone</b>@example.com"
    page.route(
        "**/api/nav",
        lambda route: route.fulfill(
            json=nav_answer([{"name": markup, "path": "/canary"}], email)
        ),
    )
    page.reload()

    bar = links_bar(page)
    expect(bar.get_by_role("link", name=markup, exact=True)).to_have_attribute(
        "href", "/canary"
    )
    expect(pill(page, email)).to_be_visible()
    page.locator("platform-nav").evaluate(f"el => el.setAttribute('crumb', '{markup}')")
    expect(crumb(page)).to_have_text(markup)
    expect(page.locator("platform-nav img")).to_have_count(0)
    expect(page.locator("platform-nav b")).to_have_count(0)
    assert page.evaluate("window.injected") is None


def test_a_feature_that_needs_you_shows_the_dot(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    features = [
        {"name": "Quiet", "path": "/quiet"},
        {"name": "Waiting", "path": "/waiting", "attention": True},
    ]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    bar = links_bar(page)
    waiting = bar.get_by_role("link", name=re.compile("^Waiting"))
    expect(waiting.locator(".dot")).to_have_count(1)
    # Colour is never the only signal: the dot carries words too.
    expect(waiting).to_have_accessible_name("Waiting, needs you")
    expect(
        bar.get_by_role("link", name="Quiet", exact=True).locator(".dot")
    ).to_have_count(0)
    dot = waiting.locator(".dot")
    assert dot.evaluate(STYLE)["background"] == resolve(
        page, "--accent", "background-color"
    )
    # 8px, on the item's top right corner.
    item, spot = waiting.bounding_box(), dot.bounding_box()
    assert item is not None and spot is not None
    assert spot["width"] == 8 and spot["height"] == 8
    assert item["x"] + item["width"] / 2 < spot["x"] < item["x"] + item["width"]
    assert item["y"] <= spot["y"] < item["y"] + item["height"] / 2


def test_an_unknown_icon_falls_back_to_the_box(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    # A name nav.js does not hold, none at all, and a name that is a key
    # every object inherits, which must not reach the drawing either; beside
    # them, one it holds, which draws its own.
    features = [
        {"name": "Unknown", "path": "/unknown", "icon": "comet"},
        {"name": "Plain", "path": "/plain"},
        {"name": "Inherited", "path": "/inherited", "icon": "constructor"},
        {"name": "Known", "path": "/known", "icon": "layers"},
    ]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    bar = links_bar(page)
    expect(bar.get_by_role("link")).to_have_text(
        ["Home", "Memory", "Unknown", "Plain", "Inherited", "Known"]
    )
    for name in ["Unknown", "Plain", "Inherited"]:
        link = bar.get_by_role("link", name=name, exact=True)
        assert shapes_of(link) == LUCIDE["box"], name
    known = bar.get_by_role("link", name="Known", exact=True)
    assert shapes_of(known) == LUCIDE["layers"]


def test_login_and_register_pages_have_no_nav(page: Page):
    for path in ["/login", "/register"]:
        page.goto(path)
        expect(page.get_by_role("heading", level=1)).to_be_visible()
        expect(page.locator("platform-nav")).to_have_count(0)
        expect(page.get_by_role("navigation")).to_have_count(0)


def test_the_bar_files_are_served(page: Page):
    for path, kind in [
        ("/design/nav.js", "text/javascript"),
        ("/design/theme.js", "text/javascript"),
        ("/design/page.css", "text/css"),
    ]:
        response = page.request.get(path)
        assert response.status == 200, path
        assert response.headers["content-type"].startswith(kind), path
        assert response.headers.get("cache-control") == "no-cache", path


@pytest.mark.parametrize("theme", THEMES)
def test_the_bar_passes_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    email = register_and_log_in(page, invite_code, base_url)
    remember_theme(page, theme)
    drawn(page, email)
    page.evaluate("document.fonts.ready.then(() => null)")
    page.add_script_tag(path=str(AXE))
    check = """() => axe.run({ include: [["platform-nav"]] }).then(result =>
        result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""

    assert page.evaluate(check) == []
    capture(page, f"nav-{theme}")

    pill(page, email).click()
    expect(page.get_by_role("link", name="Change password", exact=True)).to_be_visible()
    assert page.evaluate(check) == []
    capture(page, f"nav-user-menu-{theme}")

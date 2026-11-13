"""The shared nav bar, `<platform-nav>` from /design/nav.js, on the core's
pages. See "Navigation" in High-level/docs/platform-architecture.md and
section 3 of design/DESIGN.md.

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
        borderTopWidth: style.borderTopWidth,
        fontSize: style.fontSize,
    };
}"""


def resolve(page: Page, token: str, prop: str) -> str:
    return page.evaluate(RESOLVE, [token, prop])


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


def links_bar(page: Page) -> Locator:
    """The list of Home and the features: in the top bar on desktop, the tab
    bar at the bottom on phone."""
    return page.locator("platform-nav .links")


def remember_theme(page: Page, theme: str) -> None:
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)


def test_nav_shows_home_features_and_user(page: Page, invite_code: str, viewport: str):
    email = register_and_log_in(page, invite_code)

    nav = page.get_by_role("navigation")
    brand = nav.get_by_role("link", name="Platform", exact=True)
    expect(brand).to_have_attribute("href", "/")
    links = links_bar(page).get_by_role("link")
    # Home and Memory, which are fixed, then one link per registered feature
    # in registry order, which infra/features.toml makes Canary alone.
    expect(links).to_have_text(["Home", "Memory", "Canary"])
    expect(links.nth(0)).to_have_attribute("href", "/")
    expect(links.nth(1)).to_have_attribute("href", "/brain")
    expect(links.nth(2)).to_have_attribute("href", "/canary")
    expect(nav.get_by_role("button", name=email, exact=True)).to_be_visible()
    expect(page.get_by_role("heading", name="Platform", level=1)).to_be_visible()

    # Never makes the page scroll sideways, however long the email.
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "front-signed-in")


def test_the_active_link_is_marked(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)

    home = links_bar(page).get_by_role("link", name="Home", exact=True)
    canary = links_bar(page).get_by_role("link", name="Canary", exact=True)
    expect(home).to_have_attribute("aria-current", "page")
    expect(canary).not_to_have_attribute("aria-current", "page")
    memory = links_bar(page).get_by_role("link", name="Memory", exact=True)
    expect(memory).not_to_have_attribute("aria-current", "page")
    # The mark and the name link home too, but are not the active link.
    brand = page.get_by_role("link", name="Platform", exact=True)
    expect(brand).not_to_have_attribute("aria-current", "page")

    style = home.evaluate(STYLE)
    assert style["background"] == resolve(page, "--surface-2", "background-color")
    assert style["borders"] == [resolve(page, "--accent-border", "color")] * 4
    assert style["color"] == resolve(page, "--text", "color")
    other = canary.evaluate(STYLE)
    assert other["color"] == resolve(page, "--text-2", "color")


def test_changing_current_moves_the_mark(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    home = links_bar(page).get_by_role("link", name="Home", exact=True)
    canary = links_bar(page).get_by_role("link", name="Canary", exact=True)
    expect(canary).to_be_visible()

    page.locator("platform-nav").evaluate("el => el.setAttribute('current', 'canary')")

    expect(canary).to_have_attribute("aria-current", "page")
    expect(home).not_to_have_attribute("aria-current", "page")


def test_the_user_menu_holds_password_and_log_out(
    page: Page, invite_code: str, viewport: str
):
    email = register_and_log_in(page, invite_code)
    nav = page.get_by_role("navigation")
    button = nav.get_by_role("button", name=email, exact=True)
    change = nav.get_by_role("link", name="Change password", exact=True)
    log_out = nav.get_by_role("link", name="Log out", exact=True)

    expect(button).to_have_attribute("aria-expanded", "false")
    expect(change).to_be_hidden()

    button.click()
    expect(button).to_have_attribute("aria-expanded", "true")
    expect(change).to_be_visible()
    expect(change).to_have_attribute("href", "/settings/password")
    expect(log_out).to_be_visible()
    expect(log_out).to_have_attribute("href", "/auth/logout")

    # Escape closes it and gives focus back to the button.
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

    # So does focus leaving it.
    button.click()
    expect(change).to_be_visible()
    page.keyboard.press("Tab")
    page.keyboard.press("Tab")
    page.keyboard.press("Tab")
    expect(change).to_be_hidden()


def test_the_theme_toggle_overrides_the_system_and_is_remembered(
    page: Page, invite_code: str
):
    page.emulate_media(color_scheme="light")
    email = register_and_log_in(page, invite_code)
    root = page.locator("html")
    expect(root).not_to_have_attribute("data-theme", re.compile("."))
    # The bar draws once more when /api/nav answers; press after that.
    expect(page.get_by_role("button", name=email, exact=True)).to_be_visible()

    toggle = page.get_by_role("button", name="Switch to dark theme")
    toggle.click()

    expect(root).to_have_attribute("data-theme", "dark")
    bg = "getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()"
    assert page.evaluate(bg).lower() == dark_value("--bg").lower()

    page.reload()
    expect(root).to_have_attribute("data-theme", "dark")
    assert page.evaluate(bg).lower() == dark_value("--bg").lower()

    page.get_by_role("button", name="Switch to light theme").click()
    expect(root).to_have_attribute("data-theme", "light")
    assert page.evaluate(bg).lower() != dark_value("--bg").lower()
    page.reload()
    expect(root).to_have_attribute("data-theme", "light")


def test_no_flash_of_the_wrong_theme(page: Page, invite_code: str):
    page.emulate_media(color_scheme="light")
    register_and_log_in(page, invite_code)
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


def test_on_phone_the_links_are_a_bar_at_the_bottom(page: Page, invite_code: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    email = register_and_log_in(page, invite_code)
    # The bar draws once more when /api/nav answers, replacing what a box
    # measured before it would read; measure after that.
    expect(page.get_by_role("button", name=email, exact=True)).to_be_visible()
    height = page.viewport_size["height"]
    nav_h = float(resolve(page, "--nav-h", "height").removesuffix("px"))

    # The top bar: the mark and the name, and the menu.
    top = page.locator("platform-nav .top").bounding_box()
    assert top is not None and top["y"] == 0 and top["height"] == nav_h
    for item in [
        page.get_by_role("link", name="Platform", exact=True),
        page.get_by_role("button", name=email, exact=True),
    ]:
        box = item.bounding_box()
        assert box is not None
        assert box["y"] >= top["y"] and box["y"] + box["height"] <= top["height"]

    # The links: a bar fixed to the bottom.
    bar = links_bar(page)
    box = bar.bounding_box()
    assert box is not None
    assert box["y"] + box["height"] == height and box["height"] == nav_h
    assert bar.evaluate("el => getComputedStyle(el).position") == "fixed"
    page.mouse.wheel(0, 400)
    assert bar.bounding_box()["y"] + bar.bounding_box()["height"] == height

    # Each a 20px icon over a label.
    for link in bar.get_by_role("link").all():
        icon = link.locator("svg").bounding_box()
        label = link.locator(".label").bounding_box()
        assert icon is not None and label is not None
        assert icon["width"] == 20 and icon["height"] == 20
        assert icon["y"] + icon["height"] <= label["y"]
        assert link.locator(".label").evaluate(STYLE)["fontSize"] == "12px"

    # The active one marked with a 2px --accent line on its top edge.
    home = bar.get_by_role("link", name="Home", exact=True)
    expect(home).to_have_attribute("aria-current", "page")
    active = home.evaluate(STYLE)
    assert active["borderTopWidth"] == "2px"
    assert active["borders"][0] == resolve(page, "--accent", "color")
    assert active["color"] == resolve(page, "--text", "color")
    canary = bar.get_by_role("link", name="Canary", exact=True).evaluate(STYLE)
    assert canary["borders"][0] != active["borders"][0]


def test_more_than_five_links_fold_into_more(page: Page, invite_code: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    register_and_log_in(page, invite_code)
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
    capture(page, "nav-more")
    page.keyboard.press("Escape")
    expect(sheet).to_be_hidden()
    expect(more).to_be_focused()

    # On desktop every link is in the bar and there is no More.
    page.set_viewport_size(VIEWPORTS["desktop"])
    expect(bar.get_by_role("link")).to_have_count(8)
    expect(more).to_be_hidden()


def test_five_links_need_no_more(page: Page, invite_code: str):
    page.set_viewport_size(VIEWPORTS["phone"])
    register_and_log_in(page, invite_code)
    # Home, Memory and three features.
    features = [{"name": f"Feature {i}", "path": f"/feature{i}"} for i in range(1, 4)]
    page.route("**/api/nav", lambda route: route.fulfill(json=nav_answer(features)))
    page.reload()

    bar = links_bar(page)
    expect(bar.get_by_role("link")).to_have_count(5)
    expect(bar.get_by_role("button", name="More", exact=True)).to_be_hidden()


def test_the_page_content_is_not_covered_by_either_bar(
    page: Page, invite_code: str, viewport: str
):
    register_and_log_in(page, invite_code)
    # Content taller than the screen, so the last element is only reached by
    # scrolling, where the tab bar would cover it.
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
    first = page.locator("main > :first-child")
    last = page.locator("#e2e-last")
    height = page.viewport_size["height"]

    page.evaluate("window.scrollTo(0, 0)")
    top = page.locator("platform-nav .top").bounding_box()
    box = first.bounding_box()
    assert top is not None and box is not None
    assert box["y"] >= top["y"] + top["height"]

    page.evaluate("window.scrollTo(0, document.documentElement.scrollHeight)")
    box = last.bounding_box()
    assert box is not None
    bottom = height
    if viewport == "phone":
        bar = links_bar(page).bounding_box()
        assert bar is not None
        bottom = bar["y"]
    assert box["y"] >= 0 and box["y"] + box["height"] <= bottom


def test_the_page_keeps_the_bars_height_before_the_script_runs(
    page: Page, invite_code: str
):
    register_and_log_in(page, invite_code)
    nav_h = float(resolve(page, "--nav-h", "height").removesuffix("px"))
    page.route("**/design/nav.js", lambda route: route.abort())
    page.reload()

    expect(page.get_by_role("heading", name="Platform", level=1)).to_be_visible()
    assert page.locator("platform-nav").bounding_box()["height"] == nav_h


def test_the_bar_shows_home_and_memory_alone_when_the_nav_endpoint_fails(
    page: Page, invite_code: str
):
    register_and_log_in(page, invite_code)

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

        nav = page.get_by_role("navigation")
        expect(nav.get_by_role("link", name="Platform", exact=True)).to_be_visible()
        expect(links_bar(page).get_by_role("link")).to_have_text(["Home", "Memory"])
        expect(nav.get_by_role("link")).to_have_count(3)
        expect(nav.get_by_role("button", name=re.compile("@"))).to_have_count(0)
        # Never sent anywhere: the bar fails quietly where the page is.
        expect(page).to_have_url("https://platform.test/")


def test_a_link_from_the_endpoint_must_be_a_local_path(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
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


def test_names_and_the_email_are_written_as_text(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
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
    expect(page.get_by_role("button", name=email, exact=True)).to_be_visible()
    expect(page.locator("platform-nav img")).to_have_count(0)
    expect(page.locator("platform-nav b")).to_have_count(0)
    assert page.evaluate("window.injected") is None


def test_a_feature_that_needs_you_shows_the_dot(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
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
    dot = waiting.locator(".dot").evaluate(STYLE)
    assert dot["background"] == resolve(page, "--accent-text", "background-color")


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
    page: Page, invite_code: str, viewport: str, theme: str
):
    email = register_and_log_in(page, invite_code)
    remember_theme(page, theme)
    page.evaluate("document.fonts.ready.then(() => null)")
    page.add_script_tag(path=str(AXE))
    check = """() => axe.run({ include: [["platform-nav"]] }).then(result =>
        result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""

    assert page.evaluate(check) == []
    capture(page, f"nav-{theme}")

    page.get_by_role("button", name=email, exact=True).click()
    expect(page.get_by_role("link", name="Change password", exact=True)).to_be_visible()
    assert page.evaluate(check) == []
    capture(page, f"nav-user-menu-{theme}")

"""Canary, a feature in another language behind the same proxy, knowing who is
asking from the core's token. See "How a feature authenticates" in
High-level/docs/feature-contract.md."""

import re

from playwright.sync_api import Page, expect

from conftest import capture, log_in, register

# The top bar's height, --top-bar-h, as the page resolves it.
TOP_BAR_H = """() => {
    const probe = document.createElement("div");
    probe.style.height = "var(--top-bar-h)";
    document.body.append(probe);
    const height = probe.getBoundingClientRect().height;
    probe.remove();
    return height;
}"""


def test_canary_knows_who_i_am(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    email, password = register(page, invite_code)

    # Not logged in: canary sends the browser to the core, which sends it on
    # to the login page with the way back.
    page.goto("/canary")
    expect(page).to_have_url(re.compile(r"/login\?return_to=%2Fcanary$"))

    log_in(page, email, password)
    expect(page).to_have_url(f"{base_url}/canary")

    # The API, asked with the page's own cookies, is the reference for who
    # this is, so the test does not depend on what the core's pages print.
    whoami = page.request.get("/canary/api/whoami")
    assert whoami.status == 200
    account_id = whoami.json()["account_id"]

    expect(page.get_by_role("heading", name="Canary", level=1)).to_be_visible()
    expect(page.get_by_text(account_id)).to_be_visible()

    nav = page.get_by_role("navigation")
    expect(nav.get_by_role("link", name="Canary", exact=True)).to_have_attribute(
        "aria-current", "page"
    )
    expect(nav.get_by_text(email, exact=True)).to_be_visible()

    # The page takes the top bar's height from /design/page.css, not from the
    # core's stylesheet, and never scrolls sideways at phone width.
    top_bar_h = page.evaluate(TOP_BAR_H)
    assert page.locator("platform-nav").bounding_box()["height"] == top_bar_h
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "canary-signed-in")

    # The height is reserved before /design/nav.js renders the bar, so the
    # page does not jump: with the script never arriving, the empty element
    # is the top bar's height.
    page.route("**/design/nav.js", lambda route: route.abort())
    page.reload()
    expect(page.get_by_role("heading", name="Canary", level=1)).to_be_visible()
    assert page.locator("platform-nav").bounding_box()["height"] == top_bar_h

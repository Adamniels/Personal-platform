"""The shared nav bar on the core's pages. See "Navigation" in
High-level/docs/platform-architecture.md."""

from playwright.sync_api import Page, expect

from conftest import capture, register_and_log_in


def test_nav_shows_home_features_and_user(page: Page, invite_code: str, viewport: str):
    email = register_and_log_in(page, invite_code)

    nav = page.get_by_role("navigation")
    home = nav.get_by_role("link", name="Platform", exact=True)
    expect(home).to_have_attribute("href", "/")
    expect(home).to_have_attribute("aria-current", "page")
    canary = nav.get_by_role("link", name="Canary", exact=True)
    expect(canary).to_have_attribute("href", "/canary")
    expect(canary).not_to_have_attribute("aria-current", "page")
    expect(nav.get_by_text(email, exact=True)).to_be_visible()
    # The heading and the account line stay until the feature cards.
    expect(page.get_by_role("heading", name="Platform")).to_be_visible()

    # The bar keeps the 3rem the page reserves for it, so nothing jumps when
    # it renders, and never makes the page scroll sideways, however long the
    # email.
    assert page.locator("platform-nav").bounding_box()["height"] <= 48
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "front-signed-in")


def test_nav_marks_the_current_feature(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    nav = page.get_by_role("navigation")
    home = nav.get_by_role("link", name="Platform", exact=True)
    canary = nav.get_by_role("link", name="Canary", exact=True)
    expect(canary).to_be_visible()

    page.locator("platform-nav").evaluate("el => el.setAttribute('current', 'canary')")

    expect(canary).to_have_attribute("aria-current", "page")
    expect(home).not_to_have_attribute("aria-current", "page")


def test_login_and_register_pages_have_no_nav(page: Page):
    for path in ["/login", "/register"]:
        page.goto(path)
        expect(page.get_by_role("heading", level=1)).to_be_visible()
        expect(page.locator("platform-nav")).to_have_count(0)
        expect(page.get_by_role("navigation")).to_have_count(0)


def test_nav_shows_the_home_link_alone_when_api_nav_fails(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)

    # A 401, a server error, and a request that never gets an answer. Each
    # waits for /api/nav to settle, since the bar shows the home link alone
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
        expect(nav.get_by_role("link")).to_have_count(1)
        # Never sent anywhere: the bar fails quietly where the page is.
        expect(page).to_have_url("https://platform.test/")


def test_nav_writes_names_and_the_email_as_text(page: Page, invite_code: str):
    register_and_log_in(page, invite_code)
    markup = '<img src="x" onerror="window.injected = true">'
    page.route(
        "**/api/nav",
        lambda route: route.fulfill(
            json={
                "features": [
                    {"name": markup, "path": "/canary"},
                    {"name": "Elsewhere", "path": "javascript:window.injected = true"},
                    {"name": "Offsite", "path": "//example.com/"},
                ],
                "user": {"email": "<b>someone</b>@example.com"},
            }
        ),
    )
    page.reload()

    nav = page.get_by_role("navigation")
    expect(nav.get_by_role("link", name=markup, exact=True)).to_have_attribute(
        "href", "/canary"
    )
    expect(nav.get_by_text("<b>someone</b>@example.com", exact=True)).to_be_visible()
    expect(page.locator("platform-nav img")).to_have_count(0)
    expect(page.locator("platform-nav b")).to_have_count(0)
    # A path that is not on this origin is not linked at all.
    expect(nav.get_by_role("link")).to_have_count(2)
    assert page.evaluate("window.injected") is None

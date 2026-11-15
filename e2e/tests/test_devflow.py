"""Dev flow, the platform's feature for automating development on connected
repositories, reached through the proxy under /devflow and opened from the
nav bar. See devflow/README.md."""

from playwright.sync_api import Browser, Page, expect

from conftest import account_in_access_cookie, capture, register_and_log_in
from devflow_worlds import add_pull_request, check_run, comment, one_repository, status

TABS = ["Waiting for you", "Projects"]
BANNER = "Fake GitHub. Nothing here is real."


def test_devflow_health_through_the_proxy(page: Page):
    # No login: the health endpoint is public. The body is Dev flow's, so a
    # 200 from the core's fallback route would not pass.
    health = page.request.get("/devflow/api/health")

    assert health.status == 200
    assert health.json() == {"status": "ok"}


def test_devflow_is_in_the_navigation_and_opens(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    links = page.locator("platform-nav .links")
    devflow = links.get_by_role("link", name="Dev flow", exact=True)
    expect(devflow).to_have_attribute("href", "/devflow")

    devflow.click()

    expect(page).to_have_url(f"{base_url}/devflow")
    expect(
        page.locator("platform-nav .links").get_by_role(
            "link", name="Dev flow", exact=True
        )
    ).to_have_attribute("aria-current", "page")
    expect(page.get_by_role("heading", name="Dev flow", level=1)).to_be_visible()
    expect(
        page.get_by_text("What your agents built, and what waits for you.")
    ).to_be_visible()
    tabs = page.get_by_role("tablist", name="Dev flow sections").get_by_role("tab")
    expect(tabs).to_have_text(TABS)
    expect(
        page.get_by_role("tab", name="Waiting for you", exact=True)
    ).to_have_attribute("aria-selected", "true")
    expect(
        page.get_by_role("tabpanel").get_by_text("Nothing waits for you")
    ).to_be_visible()

    capture(page, "devflow-shell")


def test_each_devflow_tab_has_its_address(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)

    for path, tab, empty in [
        ("/devflow/projects", "Projects", "No project connected yet"),
        ("/devflow", "Waiting for you", "Nothing waits for you"),
    ]:
        page.goto(path)
        expect(page.get_by_role("tab", name=tab, exact=True)).to_have_attribute(
            "aria-selected", "true"
        )
        expect(page.get_by_role("tabpanel").get_by_text(empty)).to_be_visible()


def test_devflow_knows_who_i_am(
    page: Page, browser: Browser, base_url: str, invite_code: str
):
    register_and_log_in(page, invite_code, base_url)

    whoami = page.request.get("/devflow/api/whoami")

    assert whoami.status == 200
    assert whoami.json() == {"account_id": account_in_access_cookie(page)}

    # A browser with no session: 401 with an empty body, never a redirect.
    stranger = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        refused = stranger.request.get("/devflow/api/whoami", max_redirects=0)
        assert refused.status == 401
        assert refused.body() == b""
    finally:
        stranger.close()


def test_devflow_says_when_github_is_faked(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    # The dev stack sets no DEVFLOW_CODE_HOST, so Dev flow fakes GitHub, and
    # every page it serves says so.
    register_and_log_in(page, invite_code, base_url)

    for path in ["/devflow/projects", "/devflow"]:
        page.goto(path)
        expect(page.get_by_role("note")).to_have_text(BANNER)

    expect(page.get_by_role("heading", name="Dev flow", level=1)).to_be_visible()
    capture(page, "devflow-fake-banner")


def test_a_test_can_stage_a_pull_request(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_pull_request(
        world,
        304,
        title="The vault",
        labels=["owner-merge"],
        body="Closes #272",
        comments=[comment(1001, "Adamniels", "Why AES-GCM?")],
        statuses=[status("review-ok", "success")],
        check_runs=[check_run("ci-ok")],
    )

    devflow_world(world)

    read = page.request.get("/devflow/api/fake/world")
    assert read.status == 200
    assert read.json() == world

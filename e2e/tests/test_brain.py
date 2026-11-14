"""The brain, a service rather than a feature, reached through the same proxy
under its own prefix. See Brain/README.md."""

from playwright.sync_api import Browser, Page

from conftest import account_in_access_cookie, register_and_log_in


def test_brain_health_through_the_proxy(page: Page):
    # No login: the health endpoint is public. The body is the brain's, so a
    # 200 from the core's fallback route would not pass.
    health = page.request.get("/brain/api/health")

    assert health.status == 200
    assert health.json() == {"status": "ok"}


def test_brain_knows_who_i_am(
    page: Page, browser: Browser, base_url: str, invite_code: str
):
    register_and_log_in(page, invite_code)

    whoami = page.request.get("/brain/api/whoami")

    assert whoami.status == 200
    assert whoami.json() == {"account_id": account_in_access_cookie(page)}

    # A browser with no session: 401 with an empty body, never a redirect.
    stranger = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        refused = stranger.request.get("/brain/api/whoami", max_redirects=0)
        assert refused.status == 401
        assert refused.body() == b""
    finally:
        stranger.close()

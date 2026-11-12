"""The brain, a service rather than a feature, reached through the same proxy
under its own prefix. See Brain/README.md."""

from playwright.sync_api import Page


def test_brain_health_through_the_proxy(page: Page):
    # No login: the health endpoint is public. The body is the brain's, so a
    # 200 from the core's fallback route would not pass.
    health = page.request.get("/brain/api/health")

    assert health.status == 200
    assert health.json() == {"status": "ok"}

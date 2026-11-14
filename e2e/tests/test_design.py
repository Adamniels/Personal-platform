"""The design folder, served by the proxy at /design/, so every page takes the
platform's values, fonts and sprites from one place. See "The design folder"
in infra/README.md and section 7 of design/DESIGN.md."""

import http.client
import ssl
from urllib.parse import urlparse

from playwright.sync_api import Page

# A page no service serves: the test fulfils it itself, so it has the
# platform's origin while its stylesheets and fonts come from the proxy.
FONT_PAGE = "/e2e-font-check"
FONT_PAGE_HTML = """<!doctype html>
<html lang="sv">
<head>
  <meta charset="utf-8">
  <link rel="stylesheet" href="/design/tokens.css">
  <link rel="stylesheet" href="/design/fonts.css">
  <style>
    .ui { font-family: var(--font-ui); }
    .heading { font-family: var(--font-heading); font-weight: 600; }
    .mono { font-family: var(--font-mono); }
  </style>
</head>
<body>
  <p class="ui">Interface text</p>
  <h1 class="heading">Heading</h1>
  <p class="mono">12:30</p>
  <p class="ui" id="swedish">åäö</p>
</body>
</html>
"""


def _open_font_page(page: Page, base_url: str) -> list[str]:
    """Opens the font page and returns every URL it requested."""
    page.route(
        f"{base_url}{FONT_PAGE}",
        lambda route: route.fulfill(
            content_type="text/html; charset=utf-8", body=FONT_PAGE_HTML
        ),
    )
    requested: list[str] = []
    page.on("request", lambda request: requested.append(request.url))
    page.goto(FONT_PAGE)
    page.evaluate("document.fonts.ready.then(() => null)")
    return requested


def _assert_no_cache(response) -> None:
    assert response.headers.get("cache-control") == "no-cache", response.headers


def test_the_values_file_is_served(page: Page):
    response = page.request.get("/design/tokens.css")

    assert response.status == 200
    assert response.headers["content-type"].startswith("text/css")
    # A validator, so a browser revalidating under no-cache gets a 304.
    assert "etag" in response.headers or "last-modified" in response.headers
    _assert_no_cache(response)
    assert "--accent" in response.text()


def test_the_fonts_are_served_and_load(page: Page, base_url: str):
    response = page.request.get("/design/fonts.css")
    assert response.status == 200
    assert response.headers["content-type"].startswith("text/css")
    _assert_no_cache(response)

    _open_font_page(page, base_url)

    loaded = page.evaluate(
        """() => [...document.fonts]
            .filter(face => face.status === "loaded")
            .map(face => face.family.replaceAll('"', ""))"""
    )
    assert {"Inter", "Space Grotesk", "JetBrains Mono"} <= set(loaded), loaded


def test_no_page_asks_an_outside_host_for_a_font(page: Page, base_url: str):
    requested = _open_font_page(page, base_url)

    # The page did ask for fonts, so the check below is about something.
    assert any(url.endswith(".woff2") for url in requested), requested
    # Another host, or another port of this one, is another origin.
    platform = urlparse(base_url).netloc
    outside = [url for url in requested if urlparse(url).netloc != platform]
    assert outside == []


def test_a_sprite_is_served(page: Page):
    response = page.request.get("/design/sprites/builder.png")

    assert response.status == 200
    assert response.headers["content-type"] == "image/png"
    _assert_no_cache(response)


def _raw_status(base_url: str, path: str) -> int:
    """Sends the path exactly as written. A browser, and Playwright's request
    context, resolve ".." before sending, so they cannot ask for it."""
    # Certificate checks off, as the browser context has them, see conftest.
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    address = urlparse(base_url)
    connection = http.client.HTTPSConnection(
        address.hostname, address.port, context=context, timeout=10
    )
    try:
        connection.request("GET", path)
        return connection.getresponse().status
    finally:
        connection.close()


def test_nothing_else_under_design_is_served(page: Page, base_url: str):
    for path in [
        "/design/DESIGN.md",
        "/design/reference/home-desktop-light.png",
        "/design/ui/package.json",
        "/design/ui/gallery/dist/index.html",
    ]:
        assert page.request.get(path).status == 404, path

    assert _raw_status(base_url, "/design/../Caddyfile") == 404
    assert _raw_status(base_url, "/design/fonts/../../Caddyfile") == 404
    assert _raw_status(base_url, "/design/fonts/%2e%2e/DESIGN.md") == 404


def test_swedish_letters_use_the_real_font(page: Page, base_url: str):
    _open_font_page(page, base_url)

    assert page.evaluate("document.fonts.check('16px Inter', 'åäö')")

    # document.fonts.check also answers true when no face covers the text, so
    # ask the browser which font actually drew the letters.
    cdp = page.context.new_cdp_session(page)
    cdp.send("DOM.enable")
    cdp.send("CSS.enable")
    root = cdp.send("DOM.getDocument")["root"]["nodeId"]
    node = cdp.send("DOM.querySelector", {"nodeId": root, "selector": "#swedish"})
    fonts = cdp.send("CSS.getPlatformFontsForNode", {"nodeId": node["nodeId"]})
    used = [(font["familyName"], font["isCustomFont"]) for font in fonts["fonts"]]
    assert used and all(
        custom and family.startswith("Inter") for family, custom in used
    ), used

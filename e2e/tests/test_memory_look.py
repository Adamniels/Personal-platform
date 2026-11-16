"""The memory centre's frame and its Profile, Rules, Now and Timeline sections
in the platform's look: the crumb in the top bar, the page header, the panel,
the inset rows and the card row of design/DESIGN.md, and the three look
checks of look.py. See "The memory centre" in Brain/README.md.

Colours and sizes are compared with the token as the page resolves it, as
test_style.py does, so no value is written here but the row's padding, which
DESIGN.md gives in pixels."""

import uuid
from datetime import UTC, datetime, timedelta

from playwright.sync_api import Locator, Page, expect

from conftest import open_section, register_and_log_in, sections

THEMES = ["light", "dark"]
LABEL = "Memory sections"
STORAGE_KEY = "platform-theme"

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
        minWidth: style.minWidth,
        minHeight: style.minHeight,
        background: style.backgroundColor,
        color: style.color,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        textTransform: style.textTransform,
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


def resolve(page: Page, token: str, prop: str) -> str:
    return page.evaluate(RESOLVE, [token, prop])


def px(page: Page, token: str) -> float:
    """A size token in pixels, as the page resolves it."""
    return float(resolve(page, token, "width").removesuffix("px"))


def style(locator: Locator) -> dict:
    return locator.evaluate(STYLE)


def call(page: Page, method: str, path: str, body: dict | None = None) -> dict:
    """Calls the brain from the page, as the memory centre does, so the
    session's cookie and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/brain/api${path}`, {
                method,
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: body === null ? undefined : JSON.stringify(body),
            });
            return [response.status, await response.json()];
        }""",
        [method, path, body],
    )
    assert status in (200, 201), answer
    return answer


def open_memory(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/brain")
    expect(sections(page, LABEL)).to_be_visible()


def panel(page: Page, name: str) -> Locator:
    return page.get_by_role("tabpanel", name=name)


def entry_list(scope: Locator) -> Locator:
    """The first entry list in a card or a section."""
    return scope.get_by_role("list").first


def rows(entries: Locator) -> Locator:
    """An entry list's own rows, without the earlier versions under one."""
    return entries.locator(":scope > li")


def stage_profile(page: Page) -> None:
    for category, content in [
        ("values", "Honesty over comfort"),
        ("values", "Finish what I start"),
        ("working_style", "Long focused blocks in the morning, meetings after lunch"),
        ("goals", "A strong internship by the summer"),
    ]:
        call(
            page,
            "POST",
            "/items",
            {"kind": "profile", "category": category, "content": content},
        )


def stage_rules(page: Page) -> None:
    for content in [
        "Answer in English",
        "When I ask for a review, list the problems before the praise",
    ]:
        call(page, "POST", "/items", {"kind": "rule", "content": content})


def stage_now(page: Page) -> None:
    call(page, "POST", "/items", {"kind": "state", "content": "Learning Rust"})
    call(
        page,
        "POST",
        "/items",
        {
            "kind": "state",
            "content": "Finishing the thesis draft before the January deadline",
            "occurred_at": (datetime.now(UTC) - timedelta(days=90)).isoformat(),
        },
    )


def stage_timeline(page: Page) -> None:
    """An entry entered, corrected and retired, and two events a feature
    sends, as the Timeline's own test stages it."""
    made = call(
        page,
        "POST",
        "/items",
        {"kind": "profile", "category": "identity", "content": "Lives in Uppsala"},
    )
    newer = call(
        page,
        "POST",
        f"/items/{made['id']}/edit",
        {"kind": "profile", "content": "Lives in Stockholm"},
    )
    call(page, "POST", f"/items/{newer['id']}/retire")
    for fields in [
        {"verb": "exposed", "evidence": "Shown the article on index only scans."},
        {
            "verb": "asserted",
            "evidence": "Says he took a distributed systems course in 2021.",
            "occurred_at": "2021-01-01T00:00:00Z",
            "occurred_precision": "year",
        },
    ]:
        call(
            page,
            "POST",
            "/events",
            {
                "origin": "news",
                "payload": {},
                "payload_version": 1,
                "idempotency_key": str(uuid.uuid4()),
                **fields,
            },
        )


def test_the_top_bar_names_the_section(page: Page, invite_code: str, base_url: str):
    open_memory(page, invite_code, base_url)
    crumb = page.locator("platform-nav .crumb")
    expect(crumb).to_have_text("Memory / Profile")

    # Opening a section moves the crumb with it, without a reload: the mark
    # set on the window survives.
    page.evaluate("window.notReloaded = true")
    open_section(page, LABEL, "Rules")
    expect(page).to_have_url(f"{base_url}/brain/rules")
    expect(crumb).to_have_text("Memory / Rules")
    assert page.evaluate("window.notReloaded === true")

    # A sub page names the section it belongs to.
    call(page, "POST", "/topics", {"name": "Postgres"})
    page.goto("/brain/knowledge/postgres")
    expect(
        panel(page, "Knowledge").get_by_role("heading", name="Postgres", level=2)
    ).to_be_visible()
    expect(crumb).to_have_text("Memory / Knowledge")

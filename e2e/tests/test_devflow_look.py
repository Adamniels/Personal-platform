"""Dev flow's pages in the platform's look: the crumb in the top bar, the page
header, the item as a card and the rows inside it as inset rows, the
screenshots' row, the Projects tab, and the three look checks of look.py.
See "The pages" in devflow/README.md.

Colours and sizes are compared with the token as the page resolves it, as
test_review_look.py does, so no value is written here but those DESIGN.md
gives in pixels: an inset row's padding and the 1px borders."""

import re
from itertools import pairwise

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import (
    capture,
    link_github,
    open_section,
    register_and_log_in,
    sections,
)
from devflow_worlds import (
    INSTALLATION,
    REPOSITORY,
    add_pull_request,
    add_screenshot,
    check_run,
    comment,
    one_repository,
    png,
    reviewer_comment,
    status,
)
from look import (
    assert_active_is_inverted,
    assert_headings_are_capitals,
    assert_nothing_scrolls_sideways,
)

THEMES = ["light", "dark"]
LABEL = "Dev flow sections"
STORAGE_KEY = "platform-theme"
SLICE = 272
OWNER_LOGIN = "octo-owner"
LATER = "2026-11-15T10:00:00+00:00"

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
        listStyle: style.listStyleType,
        minHeight: style.minHeight,
        background: style.backgroundColor,
        color: style.color,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        boxShadow: style.boxShadow,
        borderColors: [style.borderTopColor, style.borderRightColor,
                       style.borderBottomColor, style.borderLeftColor],
        borderWidths: [style.borderTopWidth, style.borderRightWidth,
                       style.borderBottomWidth, style.borderLeftWidth],
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


def approved(world: dict, number: int, title: str, labels: list[str], **extra):
    """A pull request the reviewer approved for its head, with both checks
    green, closing the slice, as test_devflow.py stages one."""
    head = f"{number:040x}"
    points = extra.pop("for_the_owner", None)
    add_pull_request(
        world,
        number,
        title=title,
        labels=labels,
        head_commit=head,
        body=extra.pop(
            "body", f"## Summary\n\nAdds {title.lower()}.\n\nCloses #{SLICE}\n"
        ),
        comments=[
            reviewer_comment(9000 + number, "approve", head, for_the_owner=points)
        ]
        + extra.pop("comments", []),
        statuses=[status("review-ok", "success")],
        check_runs=[check_run("ci-ok")],
    )


DECISIONS = (
    "## Summary\n\nSeals every token in the `vault`.\n\nCloses #272\n\n"
    "## Decisions the agent made\n\n"
    "- **owner-decision** The vault seals with AES-GCM. Alternative: libsodium. "
    "Why: no new dependency.\n"
    "- **owner-decision** The key is read once at start. Alternative: on every "
    "use. Why: a key that changes mid run is a mistake.\n"
    "- Kept the key in memory. Alternative: a file. Why: simpler.\n"
)


def stage_items(world: dict) -> None:
    """One item of each kind: waiting with two decisions and two of the
    reviewer's points, stopped, and sent back."""
    approved(
        world,
        304,
        "The vault",
        ["owner-decision"],
        body=DECISIONS,
        for_the_owner=[
            "Agrees, the brain does the same.",
            "The key's file mode is worth a look.",
        ],
    )
    add_pull_request(
        world,
        306,
        title="Import a photo",
        labels=["owner-blocked"],
        comments=[
            comment(
                7002,
                "mac-mini-agent",
                "@Adamniels slice #272 is Blocked: The tests still fail after "
                "three rounds. Nothing more.",
            )
        ],
    )
    # The owner commented after the reviewer approved, so the runner has it.
    approved(
        world,
        307,
        "The board",
        ["owner-merge"],
        comments=[comment(7003, OWNER_LOGIN, "Please rename it.", LATER)],
    )


def stage_screenshots(world: dict, pairs: int) -> None:
    """A waiting item with `pairs` pairs of screenshots, each a desktop and a
    phone shot, so they cannot fit on one line."""
    lines = []
    for index in range(pairs):
        lines.append(
            add_screenshot(
                world,
                305,
                f"devflow-part{index}-desktop",
                png(640, 400, (96, 140, 200)),
            )
        )
        lines.append(
            add_screenshot(
                world, 305, f"devflow-part{index}-phone", png(195, 422, (200, 150, 90))
            )
        )
    body = (
        f"## Summary\n\nAdds the poll.\n\nCloses #{SLICE}\n\n## Verification\n\n"
        "Screenshots, desktop and 390px phone width:\n\n" + "\n".join(lines) + "\n"
    )
    approved(world, 305, "The poll", ["owner-merge"], body=body)


def connect_and_read(page: Page, base_url: str) -> str:
    """Links GitHub, connects the staged repository and reads it now. Answers
    the project's id."""
    page.goto("/devflow/projects")
    link_github(page, base_url)
    connected = page.request.post(
        "/devflow/api/projects",
        data={"installation_id": INSTALLATION, "repository_id": REPOSITORY},
        headers={"Origin": base_url},
    )
    assert connected.status == 201, connected.text()
    project = connected.json()["id"]
    read(page, base_url, project)
    return project


def read(page: Page, base_url: str, project: str) -> None:
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()


def open_devflow(page: Page, invite_code: str, base_url: str) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow")
    expect(sections(page, LABEL)).to_be_visible()


def assert_inset_rows(page: Page, entries: Locator, count: int, where: str) -> None:
    """Each row an inset row of "Inset list row" in section 4 of DESIGN.md,
    --inset-gap apart with no line between them."""
    expect(entries).to_have_count(count)
    fill = resolve(page, "--surface-2", "background-color")
    radius = resolve(page, "--radius-md", "border-top-left-radius")
    row_h = resolve(page, "--row-h", "min-height")
    for row in entries.all():
        look = style(row)
        assert look["background"] == fill, where
        assert look["radii"] == [radius] * 4, where
        assert look["padding"] == ["8px", "12px", "8px", "12px"], where
        assert look["minHeight"] == row_h, where
        assert look["borderWidths"] == ["0px"] * 4, where
        assert look["listStyle"] == "none", where
    gap = px(page, "--inset-gap")
    boxes = [row.bounding_box() for row in entries.all()]
    for above, below in pairwise(boxes):
        between = below["y"] - (above["y"] + above["height"])
        assert abs(between - gap) < 0.5, f"{where}: {between}px between rows"


def test_the_top_bar_names_the_tab(page: Page, invite_code: str, base_url: str):
    open_devflow(page, invite_code, base_url)
    crumb = page.locator("platform-nav .crumb")
    expect(crumb).to_have_text("Dev flow / Waiting for you")

    # Opening a tab moves the crumb with it, without a reload: the mark set on
    # the window survives.
    page.evaluate("window.notReloaded = true")
    open_section(page, LABEL, "Projects")
    expect(page).to_have_url(f"{base_url}/devflow/projects")
    expect(crumb).to_have_text("Dev flow / Projects")
    open_section(page, LABEL, "Waiting for you")
    expect(crumb).to_have_text("Dev flow / Waiting for you")
    assert page.evaluate("window.notReloaded === true")


def test_the_page_header_follows_the_layout(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_devflow(page, invite_code, base_url)
    lede = page.get_by_text("What your agents built, and what waits for you.")
    look = style(lede)
    assert look["fontSize"] == resolve(page, "--text-body-lg-size", "font-size")
    assert look["lineHeight"] == resolve(page, "--text-body-lg-line", "line-height")
    assert look["color"] == resolve(page, "--text-2", "color")

    header = page.locator("main > header")
    header_box = header.bounding_box()
    tabs_box = sections(page, LABEL).bounding_box()
    gap = tabs_box["y"] - (header_box["y"] + header_box["height"])
    assert abs(gap - px(page, "--space-4")) < 0.5, gap


def choose(page: Page, title: str) -> Locator:
    """Chooses the item titled `title` in the inbox's queue and answers its
    detail, the item's card beside the queue."""
    row = page.get_by_role("navigation", name="The queue").get_by_role(
        "link", name=re.compile(re.escape(title))
    )
    row.click()
    expect(row).to_have_attribute("aria-current", "true")
    item = page.get_by_role("article", name=title)
    expect(item).to_be_visible()
    return item


def test_an_item_follows_the_card_rule(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    stage_items(world)
    devflow_world(world)
    connect_and_read(page, base_url)

    page.goto("/devflow")

    surface = resolve(page, "--surface", "background-color")
    radius = resolve(page, "--radius-lg", "border-top-left-radius")
    pad = resolve(page, "--card-pad", "padding-top")
    accent = resolve(page, "--accent-border", "border-top-color")
    border = resolve(page, "--border", "border-top-color")

    # The queue is a card of its own, first, with the detail beside it.
    queue = page.get_by_role("navigation", name="The queue")
    look = style(queue)
    assert look["background"] == surface
    assert look["borderWidths"] == ["1px"] * 4
    assert look["borderColors"] == [border] * 4
    assert look["radii"] == [radius] * 4
    assert look["boxShadow"] == "none"

    fill = resolve(page, "--surface-2", "background-color")
    strong = resolve(page, "--border-strong", "border-top-color")
    for name, outline in (
        ("The vault", accent),
        ("Import a photo", border),
        ("The board", border),
    ):
        item = choose(page, name)
        look = style(item)
        assert look["background"] == surface, name
        assert look["borderWidths"] == ["1px"] * 4, name
        assert look["radii"] == [radius] * 4, name
        assert look["padding"] == [pad] * 4, name
        assert look["borderColors"] == [outline] * 4, name
        assert look["boxShadow"] == "none", name
        # The detail is the item alone, to the right of the queue.
        expect(page.get_by_role("article")).to_have_count(1)
        beside, detail = queue.bounding_box(), item.bounding_box()
        assert detail["x"] >= beside["x"] + beside["width"], name
        assert abs(detail["y"] - beside["y"]) < 0.5, name
        # The chosen row on --surface-2, ringed 1px; the others plain.
        for row in queue.get_by_role("link").all():
            look = style(row)
            if row.get_attribute("aria-current") == "true":
                assert look["background"] == fill, name
                assert look["borderColors"] == [strong] * 4, name
            else:
                assert look["background"] == "rgba(0, 0, 0, 0)", name
            assert look["borderWidths"] == ["1px"] * 4, name


def test_rows_inside_an_item_are_inset_rows(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    stage_items(world)
    devflow_world(world)
    connect_and_read(page, base_url)

    page.goto("/devflow")

    choose(page, "Import a photo")
    vault = choose(page, "The vault")
    decided = vault.get_by_role("list", name="The agent decided")
    assert_inset_rows(page, decided.locator(":scope > li"), 2, "the decisions")
    reviewer = vault.get_by_role("list", name="The reviewer")
    assert_inset_rows(page, reviewer.locator(":scope > li"), 2, "the reviewer's points")


def test_screenshots_in_an_item_do_not_scroll_sideways(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    stage_screenshots(world, 5)
    devflow_world(world)
    connect_and_read(page, base_url)

    page.goto("/devflow")

    poll = page.get_by_role("article", name="The poll")
    shots = poll.get_by_role("list", name="Screenshots")
    expect(shots.get_by_role("listitem")).to_have_count(5)
    thumbnails = shots.get_by_role("img")
    expect(thumbnails).to_have_count(10)
    for thumbnail in thumbnails.all():
        page.wait_for_function(
            "img => img.complete && img.naturalWidth > 0",
            arg=thumbnail.element_handle(),
        )

    # They wrap onto further lines, each inside the item.
    item = poll.bounding_box()
    boxes = [thumbnail.bounding_box() for thumbnail in thumbnails.all()]
    assert len({round(box["y"]) for box in boxes}) > 1, boxes
    for box in boxes:
        assert box["x"] >= item["x"] - 0.5, box
        assert box["x"] + box["width"] <= item["x"] + item["width"] + 0.5, box
    assert poll.evaluate("e => e.scrollWidth <= e.clientWidth")
    assert_nothing_scrolls_sideways(page)
    capture(page, "devflow-item-screenshots-wrapped")


def test_the_projects_tab_follows_the_look(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    page.goto("/devflow/projects")
    github = page.get_by_role("article", name="GitHub")
    border = resolve(page, "--border", "border-top-color")
    accent = resolve(page, "--accent-border", "border-top-color")

    # Not linked: the state in words, on a plain card.
    expect(github.get_by_text("Link your GitHub account")).to_be_visible()
    assert style(github)["borderColors"] == [border] * 4

    connect_and_read(page, base_url)
    page.goto("/devflow/projects")

    # Linked: the state in words, on a plain card.
    expect(github.get_by_text("Linked as @octo-owner")).to_be_visible()
    assert style(github)["borderColors"] == [border] * 4

    # Each project's row holds its name, how it is read and Disconnect, more
    # than a label and a value, so it is an inset row, with no line.
    projects = page.get_by_role("region", name="Projects")
    expect(projects.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    fill = resolve(page, "--surface-2", "background-color")
    cells = projects.locator("tbody td")
    expect(cells).to_have_count(2)
    for cell in cells.all():
        look = style(cell)
        assert look["boxShadow"] == "none", look
        assert look["borderWidths"] == ["0px"] * 4, look
        assert look["background"] == fill, look

    # Expired: the state in words, with the needs you outline and the words
    # "Needs you" for assistive technology, never the colour alone.
    revoked = page.request.post(
        "/devflow/api/fake/revoke-token", headers={"Origin": base_url}
    )
    assert revoked.status == 204, revoked.text()
    assert page.request.get("/devflow/api/github/repositories").status == 409
    page.goto("/devflow/projects")
    expect(github.get_by_text("Your GitHub link has expired.")).to_be_visible()
    expect(github.get_by_text("Needs you")).to_be_attached()
    assert style(github)["borderColors"] == [accent] * 4


def assert_the_look(page: Page, where: str) -> None:
    # The bar has drawn with the account.
    expect(page.get_by_role("button", name=re.compile("@"))).to_be_visible()
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
    ):
        try:
            check(page)
        except AssertionError as error:
            raise AssertionError(f"{where}: {error}") from None


@pytest.mark.parametrize("theme", THEMES)
def test_devflow_follows_the_look(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    theme: str,
    devflow_world,
):
    open_devflow(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")

    # Nothing connected yet: the frame alone.
    page.goto("/devflow")
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    expect(page.get_by_text("Connect a repository under Projects")).to_be_visible()
    assert_the_look(page, "/devflow with nothing connected")
    capture(page, f"devflow-shell-{theme}")

    page.goto("/devflow/projects")
    expect(page.get_by_role("button", name="Link GitHub")).to_be_visible()
    assert_the_look(page, "/devflow/projects not linked")
    capture(page, f"devflow-projects-unlinked-{theme}")

    # Connected, with nothing waiting.
    devflow_world(one_repository())
    project = connect_and_read(page, base_url)
    page.goto("/devflow")
    expect(
        page.get_by_text("Your agents have nothing for you to decide.")
    ).to_be_visible()
    assert_the_look(page, "/devflow with nothing waiting")
    capture(page, f"devflow-nothing-waiting-{theme}")

    page.goto("/devflow/projects")
    expect(
        page.get_by_role("region", name="Projects").get_by_text(
            "Adamniels/Personal-platform"
        )
    ).to_be_visible()
    assert_the_look(page, "/devflow/projects linked with a project")
    capture(page, f"devflow-projects-{theme}")

    # Waiting, stopped and sent back, and one with screenshots.
    world = one_repository()
    stage_items(world)
    stage_screenshots(world, 2)
    devflow_world(world)
    read(page, base_url, project)
    page.goto("/devflow")
    expect(page.get_by_role("article")).to_have_count(4)
    for thumbnail in (
        page.get_by_role("list", name="Screenshots").get_by_role("img").all()
    ):
        page.wait_for_function(
            "img => img.complete && img.naturalWidth > 0",
            arg=thumbnail.element_handle(),
        )
    assert_the_look(page, "/devflow with waiting, stopped and sent back items")
    capture(page, f"devflow-waiting-{theme}")

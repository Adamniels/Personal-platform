"""Dev flow's pages in the platform's look: the crumb in the top bar, the page
header, the item as a card and the rows inside it as inset rows, the
screenshots' row, the Projects tab, a project's page with its cards first, a
project setting up, and the three look checks of look.py.
See "The pages" in devflow/README.md.

Colours and sizes are compared with the token as the page resolves it, as
test_review_look.py does, so no value is written here but those DESIGN.md
gives in pixels: an inset row's padding and the 1px borders."""

import json
import re
from itertools import pairwise

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import (
    capture,
    link_github,
    open_section,
    register_and_log_in,
    registered,
    sections,
)
from devflow_worlds import (
    CHECK,
    INSTALLATION,
    REPOSITORY,
    add_file,
    add_issue,
    add_pull_request,
    add_screenshot,
    check_run,
    comment,
    one_repository,
    png,
    repository,
    reviewer_comment,
    status,
    with_setup,
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
    # On a phone the queue is hidden once an item is chosen.
    chosen = page.get_by_role(
        "navigation", name="The queue", include_hidden=True
    ).get_by_role("link", name=re.compile(re.escape(title)), include_hidden=True)
    expect(chosen).to_have_attribute("aria-current", "true")
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

    poll = choose(page, "The poll")
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


# The Projects tab's world: fodr, set up and running, and Personal-platform,
# connected and nothing more. The shape of a setup token, never a real one.
CLAUDE_TOKEN = "sk-ant-oat01-ZTJlLXRlc3RzLW9ubHktbm90LWEtcmVhbC10b2tlbg_AAAAAA"
FODR = 1002


def two_repositories() -> dict:
    """fodr, with a recipe and a card on its own, and Personal-platform, empty,
    on the one installation."""
    world = one_repository(name="fodr")
    fodr = world["installations"][0]["repositories"][0]
    fodr["id"] = FODR
    add_issue(world, 21, "Check the hay analysis units", labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps(
            {"version": 1, "check": "npm test", "start": "", "url": ""}
        ).encode(),
    )
    world["installations"][0]["repositories"].append(repository(one_repository()))
    return world


def fake_run(page: Page, base_url: str, project: str, result: dict, **extra) -> dict:
    """The fake worker takes the project's next job and ends it with `result`."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "result": result, **extra},
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def stage_two_projects(page: Page, base_url: str, devflow_world) -> None:
    """Connects both of two_repositories(). fodr is checked, so registered, by
    the fake worker, which says hello as it does; its card is read as too thin,
    so it needs me; and a feature of two cards is labelled after, so they wait
    for their reading. Personal-platform has no worker."""
    devflow_world(two_repositories())
    page.goto("/devflow/projects")
    link_github(page, base_url)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    ids = {}
    for repository_id in (FODR, REPOSITORY):
        connected = page.request.post(
            "/devflow/api/projects",
            data={"installation_id": INSTALLATION, "repository_id": repository_id},
            headers={"Origin": base_url},
        )
        assert connected.status == 201, connected.text()
        ids[repository_id] = connected.json()["id"]
    fodr = ids[FODR]
    read(page, base_url, fodr)
    checked = fake_run(
        page,
        base_url,
        fodr,
        {
            "outcome": "succeeded",
            "report": None,
            "failure": None,
            "check": {"exit_code": 0, "output": "Tests  1 passed"},
            "auth_method": None,
            "usage": {},
            "duration_ms": 61000,
            "stderr": "",
        },
        for_check=True,
    )
    assert checked["kind"] == "check", checked
    read(page, base_url, fodr)
    reading = fake_run(
        page,
        base_url,
        fodr,
        {
            "outcome": "succeeded",
            "report": {
                "cards": [
                    {
                        "number": 21,
                        "outcome": "too_thin",
                        "questions": [],
                        "missing": ["Which units the analysis is in"],
                    }
                ],
                "touches": "web/src/hay.tsx: the analysis the card checks.",
            },
            "failure": None,
            "check": None,
            "auth_method": "oauth_token",
            "usage": {"num_turns": 4},
            "duration_ms": 41000,
            "stderr": "",
        },
    )
    assert reading["kind"] == "read", reading
    world = page.request.get("/devflow/api/fake/world").json()
    add_issue(
        world, 40, "Export the feeding plan", labels=["devflow"], sub_issues=[41, 42]
    )
    add_issue(world, 41, "Write the plan as a file")
    add_issue(world, 42, "Offer the file on the plan's page")
    devflow_world(world)
    read(page, base_url, fodr)


def test_the_projects_tab_follows_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    page.goto("/devflow/projects")
    connections = page.get_by_role("region", name="Connections")
    github = connections.get_by_role("article", name="GitHub")
    login = connections.get_by_role("article", name="Claude login")
    border = resolve(page, "--border", "border-top-color")
    accent = resolve(page, "--accent-border", "border-top-color")
    surface = resolve(page, "--surface", "background-color")

    # Not linked: the state in words, on a plain item, one line 64px high on
    # desktop; on phone its status and action take a line of their own.
    expect(github.get_by_text("Not linked", exact=True)).to_be_visible()
    assert style(github)["borderColors"] == [border] * 4
    for item in (github, login):
        look = style(item)
        assert look["background"] == surface, look
        assert look["boxShadow"] == "none", look
        if viewport == "desktop":
            assert round(item.bounding_box()["height"]) == 64

    stage_two_projects(page, base_url, devflow_world)
    page.goto("/devflow/projects")

    # Linked, and the login given: each its state in words, on a plain item.
    expect(github.get_by_text("linked as @octo-owner")).to_be_visible()
    expect(login.get_by_text("Given", exact=True)).to_be_visible()
    for item in (github, login):
        assert style(item)["borderColors"] == [border] * 4
        if viewport == "desktop":
            assert round(item.bounding_box()["height"]) == 64

    # Connections, then the heading with Connect a repository, then the cards.
    projects = page.get_by_role("region", name="Projects")
    heading = projects.get_by_role("heading", name="Projects", level=2)
    connect = projects.get_by_role("button", name="Connect a repository")
    fodr = projects.get_by_role("article", name="Adamniels/fodr")
    platform = projects.get_by_role("article", name="Adamniels/Personal-platform")
    expect(fodr).to_be_visible()
    expect(platform).to_be_visible()
    tops = [each.bounding_box()["y"] for each in (connections, heading, fodr, platform)]
    assert tops == sorted(tops), tops
    assert (
        connections.bounding_box()["y"] + connections.bounding_box()["height"]
        <= heading.bounding_box()["y"]
    )
    assert connect.bounding_box()["y"] < fodr.bounding_box()["y"]
    # Connect a repository is the view's one primary: the one amber button.
    fill = resolve(page, "--accent", "background-color")
    amber = [
        button.inner_text()
        for button in page.get_by_role("button").all()
        if button.is_visible() and style(button)["background"] == fill
    ]
    assert amber == ["Connect a repository"], amber

    # fodr needs me: the accent outline, its count in thin numerals, a tick a
    # card with the one that needs me in --accent, and its inset rows.
    assert style(fodr)["borderColors"] == [accent] * 4
    count = fodr.get_by_text("1", exact=True)
    weight = count.evaluate("e => getComputedStyle(e).fontWeight")
    assert weight == "300", weight
    assert count.evaluate("e => getComputedStyle(e).fontSize") == resolve(
        page, "--text-num-l-size", "font-size"
    )
    ticks = fodr.get_by_role("img", name="1 of 3 cards needs you")
    colours = ticks.locator(":scope > *").evaluate_all(
        "ticks => ticks.map(t => getComputedStyle(t).backgroundColor)"
    )
    assert colours == [
        resolve(page, "--accent", "background-color"),
        resolve(page, "--text-2", "background-color"),
        resolve(page, "--text-2", "background-color"),
    ], colours
    assert_inset_rows(page, fodr.get_by_role("listitem"), 3, "fodr's rows")
    expect(fodr.get_by_role("listitem").nth(0)).to_contain_text("Building")
    expect(fodr.get_by_role("listitem").nth(1)).to_contain_text(
        "Export the feeding plan"
    )
    expect(fodr.get_by_role("listitem").nth(2)).to_contain_text("Worker")

    # Personal-platform cannot run yet: a Needs you banner and its one action,
    # in place of the count and the ticks.
    assert style(platform)["borderColors"] == [accent] * 4
    expect(platform.get_by_text("No worker yet, so no card can run.")).to_be_visible()
    expect(platform.get_by_role("img")).to_have_count(0)
    expect(platform.get_by_role("link", name="Set up a worker")).to_be_visible()

    # On phone the cards stack, and each Connections item is full width.
    if viewport == "phone":
        width = connections.bounding_box()["width"]
        for item in (github, login):
            assert abs(item.bounding_box()["width"] - width) < 1
        assert fodr.bounding_box()["x"] == platform.bounding_box()["x"]

    assert_the_look(page, "/devflow/projects with two projects")
    capture(page, "devflow-projects")

    # Expired: the state in words, with the needs you outline and the words
    # "Needs you" for assistive technology, never the colour alone.
    revoked = page.request.post(
        "/devflow/api/fake/revoke-token", headers={"Origin": base_url}
    )
    assert revoked.status == 204, revoked.text()
    assert page.request.get("/devflow/api/github/repositories").status == 409
    page.goto("/devflow/projects")
    expect(github.get_by_text("your link has expired")).to_be_visible()
    expect(github.get_by_text("Needs you")).to_be_attached()
    assert style(github)["borderColors"] == [accent] * 4


def test_a_project_page_follows_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A feature of three cards, five cards on their own, and, once the fake
    # worker has passed the setup's check, a worker.
    world = one_repository()
    add_issue(
        world,
        40,
        "Export the feeding plan",
        labels=["devflow"],
        sub_issues=[41, 42, 43],
    )
    add_issue(world, 41, "Print layout for the plan")
    add_issue(world, 42, "Export the feeding plan as PDF")
    add_issue(world, 43, "Email the plan to the owner")
    for number, title in (
        (50, "Show the plan's date in the header"),
        (51, "Chart colours in dark mode"),
        (52, "Check the hay analysis units"),
        (53, "Translate the plan to English"),
        (54, "Remove the hackathon banner"),
    ):
        add_issue(world, number, title, labels=["devflow"])
    with_setup(world)
    devflow_world(world)
    project = connect_and_read(page, base_url)
    registered(page, base_url, project)
    read(page, base_url, project)

    page.goto(f"/devflow/projects/{project}")
    cards = page.get_by_role("region", name="Cards", exact=True)
    expect(cards.get_by_role("article", name="Feature #40")).to_be_visible()
    own = cards.get_by_role("table", name="Cards on their own")
    expect(own.locator("tbody tr")).to_have_count(5)
    workers = page.get_by_role("article", name="Workers", exact=True)
    expect(
        workers.get_by_role("list", name="Workers").get_by_role("listitem")
    ).to_have_count(1)
    settings = page.get_by_role("article", name="Settings", exact=True)
    expect(settings.get_by_role("term")).to_have_count(7)
    expect(page.get_by_text("8 cards: 8 queued.")).to_be_visible()

    # The cards column first; on desktop the side column beside it, on phone
    # under it, where the table keeps its priority 1 columns and the stat
    # pills give way to the summary line.
    main = cards.bounding_box()
    side = workers.bounding_box()
    changed = own.get_by_role("columnheader", name="Changed")
    pills = page.get_by_role("list", name="Cards by group")
    if viewport == "desktop":
        assert side["x"] >= main["x"] + main["width"], (main, side)
        assert abs(side["y"] - main["y"]) < 1, (main, side)
        expect(changed).to_be_visible()
        expect(pills).to_be_visible()
    else:
        assert side["y"] >= main["y"] + main["height"], (main, side)
        expect(changed).to_be_hidden()
        expect(pills).to_be_hidden()
    assert settings.bounding_box()["y"] > side["y"]

    assert_the_look(page, "a project's page")
    capture(page, "devflow-project")


def setup_result(report: dict) -> dict:
    """A setup job's result, or its review's, as its worker sends one:
    `report`, and no check."""
    return {
        "outcome": "succeeded",
        "report": report,
        "failure": None,
        "check": None,
        "auth_method": "oauth_token",
        "usage": {},
        "duration_ms": 61000,
        "stderr": "",
    }


SETUP_REPORT = {
    "summary": "Wrote a recipe with Postgres, and a check that starts it.",
    "proposed": {
        "preset": "guided",
        "why": "Its migrations are worth a look before they merge.",
        "paths": ["migrations/**", ".github/"],
    },
    "variables": [{"name": "STRIPE_TEST_KEY", "why": "The payment tests call Stripe."}],
    "unsettled": [
        {
            "kind": "containers_in_tests",
            "detail": "The integration tests start Redis with Testcontainers.",
        }
    ],
    "decisions": [],
    "handover": "The check starts Postgres before the tests.",
}

SETUP_VERDICT = {
    "verdict": "approve",
    "summary": "The recipe installs only Postgres.",
    "findings": [],
    "for_owner": [],
}

CLAUDE_TOKEN = "sk-ant-oat01-ZTJlLXRlc3RzLW9ubHktbm90LWEtcmVhbC10b2tlbg_AAAAAA"


def test_a_project_setting_up_follows_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with no .devflow/; what its setup run writes is staged on
    # the setup's branch. Its run proposes guided and lists a value, and is
    # approved, leaving the settings for me; I merge it, so two steps are done
    # and the settings step is open.
    world = one_repository()
    add_file(
        world,
        ".devflow/Dockerfile",
        b"ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nRUN apt-get update\n",
        branch="devflow/setup",
    )
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
        branch="devflow/setup",
    )
    devflow_world(world)
    project = connect_and_read(page, base_url)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    read(page, base_url, project)
    for report, kind in ((SETUP_REPORT, "setup"), (SETUP_VERDICT, "review")):
        ran = page.request.post(
            "/devflow/api/fake/worker/run",
            data={
                "project_id": project,
                "setup": True,
                "settings_wait": True,
                "result": setup_result(report),
            },
            headers={"Origin": base_url},
        )
        assert ran.status == 200, ran.text()
        assert ran.json()["kind"] == kind

    page.goto(f"/devflow/projects/{project}")
    card = page.get_by_role("article", name="Setting it up", exact=True)
    steps = card.get_by_role("list", name="Setup steps").locator(":scope > li")
    expect(steps).to_have_count(6)
    steps.nth(0).get_by_role("button", name="Merge").click()
    expect(steps.nth(2)).to_have_attribute("aria-current", "step")
    expect(
        page.get_by_text(
            "No card runs until setup is done: 2 of 6 steps, and the next one is yours"
        )
    ).to_be_visible()
    expect(steps.nth(2).get_by_role("region", name="The settings")).to_be_visible()

    # The steps card where the cards would be: no cards, and the side column
    # beside it on desktop, under it on phone.
    expect(page.get_by_role("region", name="Cards", exact=True)).to_have_count(0)
    workers = page.get_by_role("article", name="Workers", exact=True)
    main = card.bounding_box()
    side = workers.bounding_box()
    if viewport == "desktop":
        assert side["x"] >= main["x"] + main["width"], (main, side)
        assert abs(side["y"] - main["y"]) < 1, (main, side)
    else:
        assert side["y"] >= main["y"] + main["height"], (main, side)

    # It needs me: the accent border.
    accent_border = resolve(page, "--accent-border", "border-top-color")
    assert style(card)["borderColors"] == [accent_border] * 4

    # Its step bar: one 8px pill per step, 4px apart; the done ones accent,
    # the current one no fill with an accent border, the rest surface-3.
    bar = card.get_by_role("img", name="2 of 6 steps done")
    pills = bar.locator(":scope > span")
    expect(pills).to_have_count(6)
    accent = resolve(page, "--accent", "background-color")
    surface_3 = resolve(page, "--surface-3", "background-color")
    looks = [style(pill) for pill in pills.all()]
    assert [look["background"] for look in looks] == [
        accent,
        accent,
        "rgba(0, 0, 0, 0)",
        surface_3,
        surface_3,
        surface_3,
    ], looks
    assert looks[2]["borderColors"] == [accent_border] * 4
    boxes = [pill.bounding_box() for pill in pills.all()]
    for box in boxes:
        assert abs(box["height"] - 8) < 0.5, box
    for left, right in pairwise(boxes):
        assert abs(right["x"] - (left["x"] + left["width"]) - 4) < 0.5, boxes

    # Each step's 32px circle: ok-soft when done, accent when current,
    # surface-3 to come.
    fills = {
        "done": resolve(page, "--ok-soft", "background-color"),
        "current": accent,
        "to-come": surface_3,
    }
    for at, state in enumerate(
        ["done", "done", "current", "to-come", "to-come", "to-come"]
    ):
        step = steps.nth(at)
        expect(step).to_have_attribute("data-state", state)
        mark = step.locator(":scope > [aria-hidden='true']").first
        box = mark.bounding_box()
        assert abs(box["width"] - 32) < 0.5 and abs(box["height"] - 32) < 0.5, box
        assert style(mark)["background"] == fills[state], (at, state)

    # One primary on the page: the current step's Accept.
    primaries = [
        button
        for button in page.get_by_role("button").all()
        if button.is_visible() and style(button)["background"] == accent
    ]
    assert [button.inner_text() for button in primaries] == ["Accept"]

    assert_the_look(page, "a project setting up")
    capture(page, "devflow-project-setting-up")


def test_the_settings_page_follows_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    project = connect_and_read(page, base_url)
    # Guided, with a second trusted account and a path, and one value.
    headers = {"Origin": base_url}
    current = page.request.get(f"/devflow/api/projects/{project}/settings").json()
    saved = page.request.put(
        f"/devflow/api/projects/{project}/settings",
        data={
            **current,
            "trusted_logins": ["octo-owner", "a-collaborator"],
            "questions": False,
            "waits_for_owner": "sometimes",
            "wait_when_marked": True,
            "wait_when_paths": True,
            "wait_when_decision": True,
            "wait_paths": ["src/calc/**", "migrations/"],
        },
        headers=headers,
    )
    assert saved.status == 200, saved.text()
    given = page.request.put(
        f"/devflow/api/projects/{project}/values/FODR_TEST_SMTP_PASSWORD",
        data={"value": "throwaway"},
        headers=headers,
    )
    assert given.status == 204, given.text()

    page.goto(f"/devflow/projects/{project}/settings")
    form = page.get_by_role("article", name="How much happens alone")
    expect(form.get_by_role("radio", name="Guided")).to_be_checked()
    expect(
        page.get_by_role("list", name="Trusted accounts").get_by_role("listitem")
    ).to_have_count(2)
    expect(
        page.get_by_role("list", name="Values").get_by_text("FODR_TEST_SMTP_PASSWORD")
    ).to_be_visible()
    summary = page.get_by_role("article", name="These settings")
    expect(summary.get_by_text("Guided", exact=True)).to_be_visible()
    # The crumb is the Projects tab's.
    expect(page.locator("platform-nav")).to_have_attribute(
        "crumb", "Dev flow / Projects"
    )

    # The selected preset is inverted, as a selected segment is (hard rule
    # 11), on a track like the Tabs'.
    segment = form.locator("label").filter(has=page.get_by_role("radio", name="Guided"))
    assert style(segment)["background"] == resolve(page, "--text", "background-color")

    # The form first; on desktop the side column beside it with its links to
    # the four sections, on phone under it without them, where the Save bar
    # sticks to the bottom.
    main = form.bounding_box()
    side = summary.bounding_box()
    sections_nav = page.get_by_role("navigation", name="Sections")
    bar = page.get_by_role("region", name="Save the settings")
    if viewport == "desktop":
        assert side["x"] >= main["x"] + main["width"], (main, side)
        assert abs(side["y"] - main["y"]) < 1, (main, side)
        expect(sections_nav).to_be_visible()
    else:
        assert side["y"] >= main["y"] + main["height"], (main, side)
        expect(sections_nav).to_be_hidden()
        assert bar.evaluate("el => getComputedStyle(el).position") == "sticky"

    assert_the_look(page, "a project's settings page")
    capture(page, "devflow-settings-page")


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
    queue = page.get_by_role("navigation", name="The queue")
    expect(queue.get_by_role("link")).to_have_count(4)
    for thumbnail in (
        page.get_by_role("list", name="Screenshots").get_by_role("img").all()
    ):
        page.wait_for_function(
            "img => img.complete && img.naturalWidth > 0",
            arg=thumbnail.element_handle(),
        )
    assert_the_look(page, "/devflow with waiting, stopped and sent back items")
    capture(page, f"devflow-waiting-{theme}")

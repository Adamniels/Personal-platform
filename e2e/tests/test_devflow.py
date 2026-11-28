"""Dev flow, the platform's feature for automating development on connected
repositories, reached through the proxy under /devflow and opened from the
nav bar. See devflow/README.md."""

import base64
import json
import re
import struct
import zlib
from datetime import UTC, date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

import pytest
from playwright.sync_api import Browser, Locator, Page, expect

from conftest import (
    SCREENSHOTS,
    VIEWPORTS,
    account_in_access_cookie,
    capture,
    fake_check,
    link_github,
    register_and_log_in,
    registered,
)
from devflow_worlds import (
    APP_LOGIN,
    CHECK,
    INSTALLATION,
    NAME,
    OWNER,
    REPOSITORY,
    add_activity,
    add_conversation,
    add_file,
    add_issue,
    add_pull_request,
    add_screenshot,
    check_run,
    comment,
    edit,
    label_event,
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
    assert_nothing_active_is_amber,
    assert_nothing_scrolls_sideways,
)

TABS = ["Waiting for you", "Running now", "Projects", "Usage and history", "Machines"]
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
    nav = page.get_by_role("navigation")
    devflow = nav.get_by_role("link", name="Dev flow", exact=True)
    expect(devflow).to_have_attribute("href", "/devflow")

    devflow.click()

    expect(page).to_have_url(f"{base_url}/devflow")
    expect(
        page.get_by_role("navigation").get_by_role("link", name="Dev flow", exact=True)
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
        ("/devflow/running", "Running now", "Nothing is running"),
        ("/devflow", "Waiting for you", "Nothing waits for you"),
        ("/devflow/machines", "Machines", "No machine yet"),
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


NOT_LINKED = "Not linked"


def test_i_link_my_github_account(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    card = page.get_by_role("article", name="GitHub")
    expect(card.get_by_text(NOT_LINKED, exact=True)).to_be_visible()
    capture(page, "devflow-projects-unlinked")

    link_github(page, base_url)

    expect(
        page.get_by_role("article", name="GitHub").get_by_text("linked as @octo-owner")
    ).to_be_visible()
    expect(page.get_by_text(NOT_LINKED, exact=True)).to_have_count(0)
    capture(page, "devflow-projects-linked")
    status = page.request.get("/devflow/api/status")
    assert status.json()["github"] == {"state": "linked", "login": "octo-owner"}


def test_denying_the_link_says_so(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    page.get_by_role("button", name="Link GitHub").click()

    page.get_by_role("button", name="Deny").click()

    expect(page).to_have_url(f"{base_url}/devflow/projects?link=failed")
    expect(page.get_by_role("alert")).to_have_text("GitHub was not linked. Try again.")
    expect(page.get_by_role("button", name="Link GitHub")).to_be_visible()
    assert page.request.get("/devflow/api/status").json()["github"]["state"] == "none"


def test_i_unlink_my_github_account(page: Page, invite_code: str, base_url: str):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    link_github(page, base_url)
    card = page.get_by_role("article", name="GitHub")

    card.get_by_role("button", name="Unlink").click()
    dialog = page.get_by_role("dialog", name="Unlink GitHub?")
    expect(dialog).to_be_visible()
    dialog.get_by_role("button", name="Unlink GitHub").click()

    expect(dialog).to_be_hidden()
    expect(card.get_by_text(NOT_LINKED, exact=True)).to_be_visible()
    expect(page.get_by_text("linked as @octo-owner")).to_have_count(0)
    assert page.request.get("/devflow/api/status").json()["github"] == {
        "state": "none",
        "login": None,
    }
    # And it stays so on a fresh load.
    page.reload()
    expect(page.get_by_role("button", name="Link GitHub")).to_be_visible()


NOT_INSTALLED = "The GitHub App is not installed on any repository you can reach."


def _choose(page: Page) -> None:
    """Opens the list of repositories to connect, under the GitHub link."""
    page.get_by_role("button", name="Connect a repository").click()


def test_i_connect_a_repository(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    page.goto("/devflow/projects")
    expect(page.get_by_text("No project connected yet")).to_be_visible()
    # Connecting is offered only once GitHub is linked.
    expect(page.get_by_role("button", name="Connect a repository")).to_be_disabled()
    link_github(page, base_url)

    _choose(page)

    account = page.get_by_role("region", name="Adamniels")
    expect(account.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    capture(page, "devflow-projects-choose")
    account.get_by_role("button", name="Connect Adamniels/Personal-platform").click()

    projects = page.get_by_role("region", name="Projects")
    card = projects.get_by_role("article", name="Adamniels/Personal-platform")
    expect(card).to_be_visible()
    # Connected but not set up: no worker has said hello, so no card can run.
    expect(card.get_by_text("No worker yet, so no card can run.")).to_be_visible()
    expect(page.get_by_role("region", name="Adamniels")).to_have_count(0)
    expect(page.get_by_text("No project connected yet")).to_have_count(0)
    capture(page, "devflow-projects-connected")
    # It is a project, and stays one on a fresh load.
    listed = page.request.get("/devflow/api/projects").json()["projects"]
    assert [(p["owner"], p["name"]) for p in listed] == [
        ("Adamniels", "Personal-platform")
    ]
    page.reload()
    expect(
        page.get_by_role("region", name="Projects").get_by_text(
            "Adamniels/Personal-platform"
        )
    ).to_be_visible()
    # And it is marked as connected when choosing again.
    _choose(page)
    account = page.get_by_role("region", name="Adamniels")
    expect(account.get_by_text("Connected", exact=True)).to_be_visible()
    expect(
        account.get_by_role("button", name="Connect Adamniels/Personal-platform")
    ).to_have_count(0)


def test_without_an_installation_i_am_told_to_install_the_app(
    page: Page, invite_code: str, base_url: str
):
    # Nothing staged: the app is installed nowhere this account can reach.
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    link_github(page, base_url)

    _choose(page)

    expect(page.get_by_text(NOT_INSTALLED)).to_be_visible()
    install = page.get_by_role("link", name="Install it on GitHub")
    expect(install).to_have_attribute("href", f"{base_url}/devflow/fake-github/install")
    # The fake's address is a harmless page of its own.
    install.click()
    expect(page.get_by_role("heading", name="Fake GitHub", level=1)).to_be_visible()


def test_what_waits_is_read_from_the_code_host(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    head = "4" * 40
    # Approved for its head by the reviewer, handed to the owner, both checks green.
    add_pull_request(
        world,
        304,
        title="The vault",
        labels=["owner-merge"],
        head_commit=head,
        body="## Summary\n\nAdds the vault.\n\nCloses #272\n",
        comments=[reviewer_comment(9001, "approve", head)],
        statuses=[status("review-ok", "success")],
        check_runs=[check_run("ci-ok")],
    )
    # And one still in review, which does not wait for anyone.
    add_pull_request(world, 305, labels=["in-review"])
    devflow_world(world)
    page.goto("/devflow/projects")
    link_github(page, base_url)
    connected = page.request.post(
        "/devflow/api/projects",
        data={"installation_id": INSTALLATION, "repository_id": REPOSITORY},
        headers={"Origin": base_url},
    )
    assert connected.status == 201, connected.text()
    project = connected.json()["id"]

    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )

    assert refreshed.status == 204, refreshed.text()
    waiting = page.request.get("/devflow/api/waiting")
    assert waiting.status == 200
    listed = waiting.json()
    assert listed["count"] == 1
    assert listed["needs_relink"] is False
    (item,) = listed["items"]
    assert item["project_id"] == project
    assert item["project_name"] == "Personal-platform"
    assert (item["number"], item["kind"], item["title"]) == (
        304,
        "waiting",
        "The vault",
    )
    assert (item["slice_number"], item["head_commit"], item["can_merge"]) == (
        272,
        head,
        True,
    )
    assert item["summary"] == "Adds the vault."
    assert item["sent_back"] is False
    (state,) = listed["projects"]
    assert (state["id"], state["stale"], state["failing"]) == (project, False, False)


AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
OWNER_LOGIN = "octo-owner"
SLICE = 272
LATER = "2026-11-15T10:00:00+00:00"


def _connect_and_read(page: Page, base_url: str) -> str:
    """Links GitHub, connects the staged repository and reads it now, as the
    loop would within a minute. Answers the project's id."""
    page.goto("/devflow/projects")
    link_github(page, base_url)
    connected = page.request.post(
        "/devflow/api/projects",
        data={"installation_id": INSTALLATION, "repository_id": REPOSITORY},
        headers={"Origin": base_url},
    )
    assert connected.status == 201, connected.text()
    project = connected.json()["id"]
    _read(page, base_url, project)
    return project


def _read(page: Page, base_url: str, project: str) -> None:
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()


def _approved(world: dict, number: int, title: str, labels: list[str], **extra):
    """A pull request the reviewer approved for its head, with both checks
    green, closing the slice."""
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


DECISION_BODY = (
    "## Summary\n\nSeals every token in the `vault`.\n\nCloses #272\n\n"
    "## Decisions the agent made\n\n"
    "- **owner-decision** The vault seals with AES-GCM. Alternative: libsodium. "
    "Why: no new dependency.\n"
    "- Kept the key in memory. Alternative: a file. Why: simpler.\n"
)


def _decision(world: dict) -> None:
    _approved(
        world,
        304,
        "The vault",
        ["owner-decision"],
        body=DECISION_BODY,
        for_the_owner=["Agrees, the brain does the same."],
    )


def _ready_to_merge(world: dict) -> None:
    _approved(world, 305, "The poll", ["owner-merge"])
    add_conversation(
        world,
        SLICE,
        [
            comment(
                7001,
                "mac-mini-agent",
                "@Adamniels PR #305 is ready for you to read and merge: "
                "https://github.com/Adamniels/Personal-platform/pull/305 "
                "(risk is not low).",
            )
        ],
    )


def _stopped(world: dict) -> None:
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


def _sent_back(world: dict) -> None:
    # The owner commented after the reviewer approved, so the runner has it.
    _approved(
        world,
        307,
        "The board",
        ["owner-merge"],
        comments=[comment(7003, OWNER_LOGIN, "Please rename it.", LATER)],
    )


def _axe_violations(page: Page) -> list[dict]:
    if not page.evaluate("typeof axe !== 'undefined'"):
        page.add_script_tag(path=str(AXE))
    return page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )


def test_the_waiting_list_shows_a_decision(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _decision(world)
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    vault = _item(page, "The vault", "Pull requests waiting")
    expect(vault).to_be_visible()
    expect(vault.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    expect(vault.get_by_text("pull request #304", exact=True)).to_be_visible()
    expect(vault.get_by_text("Waiting for you")).to_be_visible()
    # The summary's backticks are code, and the rest is text.
    expect(vault.locator("code", has_text="vault")).to_be_visible()
    decided = vault.get_by_role("list", name="The agent decided")
    expect(decided.get_by_role("listitem")).to_have_text(
        ["The vault seals with AES-GCM.Instead of libsodium.Because no new dependency."]
    )
    expect(vault.get_by_text("and 1 more it did not need you for")).to_be_visible()
    expect(
        vault.get_by_role("list", name="The reviewer").get_by_role("listitem")
    ).to_have_text(["Agrees, the brain does the same."])
    opened = vault.get_by_role("link", name="Open on GitHub")
    expect(opened).to_have_attribute(
        "href", "https://github.com/Adamniels/Personal-platform/pull/304"
    )
    expect(opened).to_have_attribute("rel", "noreferrer")
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()


def test_the_waiting_list_shows_why_a_pull_request_waits(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    poll = _item(page, "The poll", "Pull requests waiting")
    expect(poll.get_by_text("In the runner's words")).to_be_visible()
    expect(poll.get_by_text("High risk slice.")).to_be_visible()
    expect(poll.get_by_role("list", name="The agent decided")).to_have_count(0)


def test_the_waiting_list_shows_a_stopped_pull_request(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _stopped(world)
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    stopped = _item(page, "Import a photo", "Stopped")
    header = stopped.locator(":scope > header")
    expect(header.get_by_text("Stopped", exact=True)).to_be_visible()
    expect(stopped.get_by_text("Why it stopped")).to_be_visible()
    expect(
        stopped.get_by_text(
            "The slice is Blocked. The tests still fail after three rounds."
        )
    ).to_be_visible()
    expect(page.get_by_role("region", name="Pull requests waiting")).to_have_count(0)


LANDED = "ab12cd34" + "0" * 32


def test_something_landed_on_main_and_i_resume(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    """A push the app made that Dev flow did not: the project stops and waits
    for the owner, who looks and resumes it. See "Watching branches" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    devflow_world(world)
    # The first read only records where main stands.
    project = _connect_and_read(page, base_url)

    staged = page.request.get("/devflow/api/fake/world").json()
    add_activity(staged, 1, after=LANDED, at=LATER)
    devflow_world(staged)
    _read(page, base_url, project)

    page.goto("/devflow")
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    stopped = _item(
        page, "Something landed on main that Dev flow did not put there", "Stopped"
    )
    expect(stopped.get_by_text("Stopped", exact=True)).to_be_visible()
    expect(stopped.get_by_text(f"{OWNER}/{NAME}")).to_be_visible()
    expect(stopped.locator("code")).to_have_text(LANDED[:7])
    expect(stopped.get_by_text(APP_LOGIN)).to_be_visible()
    commit = stopped.get_by_role("link", name="Open the commit on GitHub")
    expect(commit).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/commit/{LANDED}"
    )
    capture(page, "devflow-main-watch")

    stopped.get_by_role("button", name="I have looked, resume").click()
    dialog = page.get_by_role("dialog", name=f"Resume {OWNER}/{NAME}?")
    expect(dialog).to_be_visible()
    expect(dialog.get_by_text("Resuming accepts what landed on main")).to_be_visible()
    expect(dialog.locator("code")).to_have_text(LANDED[:7])
    capture(page, "devflow-main-watch-resume")

    # Cancelling changes nothing.
    dialog.get_by_role("button", name="Cancel").click()
    expect(dialog).to_have_count(0)
    expect(stopped).to_be_visible()

    stopped.get_by_role("button", name="I have looked, resume").click()
    page.get_by_role("dialog", name=f"Resume {OWNER}/{NAME}?").get_by_role(
        "button", name="Resume"
    ).click()

    expect(page.get_by_text(f"Resumed {OWNER}/{NAME}")).to_be_visible()
    expect(stopped).to_have_count(0)
    expect(page.get_by_role("tab", name="Waiting for you", exact=True)).to_be_visible()


FEATURE_BRANCH = "feature-60-the-feature-s-branch"
ON_THE_FEATURE = "fe12cd34" + "0" * 32


def test_a_change_on_a_features_branch_stops_the_project(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    """Another login pushes to a feature's branch: the project stops and waits
    for the owner, as for the default branch, and resuming starts it again.
    See "Watching branches" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(
        world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61, 62]
    )
    add_issue(world, 61, "Make the feature's branch")
    add_issue(world, 62, "Watch it")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    _fake_read(page, base_url, project, [_ready(61), _ready(62)])

    # The feature's first card is handed out, so the app makes its branch,
    # and the next read begins the branch's watch at its creation.
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    _read(page, base_url, project)

    staged = page.request.get("/devflow/api/fake/world").json()
    changes = repository(staged).get("activity", [])
    assert [each["kind"] for each in changes if each["branch"] == FEATURE_BRANCH] == [
        "branch_creation"
    ]
    add_activity(
        staged,
        max(each["id"] for each in changes) + 1,
        after=ON_THE_FEATURE,
        actor="octo-stranger",
        branch=FEATURE_BRANCH,
        at=LATER,
    )
    devflow_world(staged)
    _read(page, base_url, project)

    page.goto("/devflow")
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    stopped = _item(
        page,
        f"Something landed on {FEATURE_BRANCH} that Dev flow did not put there",
        "Stopped",
    )
    expect(stopped.get_by_text("Stopped", exact=True)).to_be_visible()
    expect(stopped.get_by_text(f"{OWNER}/{NAME}")).to_be_visible()
    expect(stopped.locator("code")).to_have_text(ON_THE_FEATURE[:7])
    expect(stopped.get_by_text("octo-stranger")).to_be_visible()
    expect(
        stopped.get_by_role("link", name="Open the commit on GitHub")
    ).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/commit/{ON_THE_FEATURE}"
    )
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-feature-branch-stopped")

    stopped.get_by_role("button", name="I have looked, resume").click()
    dialog = page.get_by_role("dialog", name=f"Resume {OWNER}/{NAME}?")
    expect(
        dialog.get_by_text(f"Resuming accepts what landed on {FEATURE_BRANCH}")
    ).to_be_visible()
    expect(dialog.locator("code")).to_have_text(ON_THE_FEATURE[:7])
    dialog.get_by_role("button", name="Resume").click()

    expect(page.get_by_text(f"Resumed {OWNER}/{NAME}")).to_be_visible()
    expect(stopped).to_have_count(0)
    expect(page.get_by_role("tab", name="Waiting for you", exact=True)).to_be_visible()

    # The project hands out work again: what was accepted is not judged again,
    # and its page holds nothing back.
    _read(page, base_url, project)
    page.goto(f"/devflow/projects/{project}")
    expect(page.get_by_text("No work is handed out")).to_have_count(0)


def test_a_pull_request_that_is_not_my_turn_is_not_listed(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    add_pull_request(world, 308, title="Still in review", labels=["in-review"])
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    expect(page.get_by_role("article", name="The poll")).to_be_visible()
    expect(page.get_by_text("Still in review")).to_have_count(0)
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()


def test_the_waiting_list_passes_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _decision(world)
    _ready_to_merge(world)
    _stopped(world)
    _sent_back(world)
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    expect(_queue(page).get_by_role("link")).to_have_count(4)
    sent_back = page.get_by_role("region", name="Sent back, the runner has it")
    expect(
        sent_back.get_by_role("link", name=re.compile("^The board "))
    ).to_be_visible()
    expect(page.get_by_text("Updated less than a minute ago")).to_be_visible()
    # Three wait for the owner; the one sent back is the runner's.
    expect(page.get_by_role("tab", name="Waiting for you 3")).to_be_visible()
    assert _axe_violations(page) == []
    capture(page, "devflow-waiting-list")


def _queue(page: Page):
    """The inbox's queue, every waiting item a row in it."""
    return page.get_by_role("navigation", name="The queue")


def _row(page: Page, title: str):
    """The queue's row of the item titled `title`."""
    return _queue(page).get_by_role("link", name=re.compile(re.escape(title)))


def _item(page: Page, title: str, group: str | re.Pattern | None = None):
    """Chooses the item titled `title` in the queue, in the group named
    `group` when given, as the owner does, and answers its detail. On a phone
    the detail shows only once an item is chosen."""
    rows = _queue(page)
    if group is not None:
        rows = rows.get_by_role("region", name=group)
    rows.get_by_role("link", name=title).click()
    return page.get_by_role("article", name=title)


def test_the_inbox_opens_the_item_in_its_address(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    page.set_viewport_size(VIEWPORTS["desktop"])
    register_and_log_in(page, invite_code, base_url)
    project, _ = _built_card(page, base_url, devflow_world)
    # A runner's pull request beside the card, after it in its group.
    world = page.request.get("/devflow/api/fake/world").json()
    _approved(world, 305, "The poll", ["owner-merge"])
    devflow_world(world)
    _read(page, base_url, project)

    address = f"{base_url}/devflow?item=card/{project}/{CARD}"
    page.goto(address)
    expect(page.get_by_role("article", name=CARD_TITLE)).to_be_visible()
    expect(page.get_by_role("article")).to_have_count(1)
    expect(_row(page, CARD_TITLE)).to_have_attribute("aria-current", "true")
    capture(page, "devflow-inbox")

    # Choosing another shows it, at an address of its own.
    _row(page, "The poll").click()
    expect(page.get_by_role("article", name="The poll")).to_be_visible()
    expect(page.get_by_role("article", name=CARD_TITLE)).to_have_count(0)
    expect(page).to_have_url(re.compile(r"/devflow\?item=[0-9a-f-]{36}$"))
    expect(_row(page, "The poll")).to_have_attribute("aria-current", "true")

    # Back shows the first again.
    page.go_back()
    expect(page).to_have_url(address)
    expect(page.get_by_role("article", name=CARD_TITLE)).to_be_visible()
    expect(page.get_by_role("article", name="The poll")).to_have_count(0)
    expect(_row(page, CARD_TITLE)).to_have_attribute("aria-current", "true")


def test_the_inbox_on_phone_is_two_screens(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    page.set_viewport_size(VIEWPORTS["phone"])
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _decision(world)
    _ready_to_merge(world)
    _approved(world, 308, "The list", ["owner-merge"])
    _approved(world, 309, "The theme", ["owner-merge"])
    _stopped(world)
    _sent_back(world)
    devflow_world(world)
    _connect_and_read(page, base_url)

    # The queue alone.
    page.goto("/devflow")
    expect(_queue(page).get_by_role("link")).to_have_count(6)
    expect(page.get_by_role("article")).to_have_count(0)
    assert_nothing_scrolls_sideways(page)
    capture(page, "devflow-inbox")

    # Choosing an item shows it alone, with the way back and its position.
    _row(page, "The vault").click()
    expect(page).to_have_url(re.compile(r"/devflow\?item=[0-9a-f-]{36}$"))
    vault = page.get_by_role("article", name="The vault")
    expect(vault).to_be_visible()
    expect(_queue(page)).to_be_hidden()
    back = page.get_by_role("link", name="Waiting for you", exact=True)
    expect(back).to_be_visible()
    expect(page.get_by_text("1 of 6", exact=True)).to_be_visible()

    # Its actions sit in a bar above the tab bar, in view without scrolling.
    merge = vault.get_by_role("button", name="Merge")
    expect(merge).to_be_visible()
    tab_bar = page.evaluate(
        "() => parseFloat(getComputedStyle(document.documentElement)"
        ".getPropertyValue('--tab-bar-h'))"
    )
    box = merge.bounding_box()
    assert box["y"] + box["height"] <= VIEWPORTS["phone"]["height"] - tab_bar, box
    assert_nothing_scrolls_sideways(page)
    capture(page, "devflow-inbox-item")

    # Back shows the queue.
    page.go_back()
    expect(page).to_have_url(f"{base_url}/devflow")
    expect(_queue(page)).to_be_visible()
    expect(page.get_by_role("article")).to_have_count(0)

    # So does the back link, from the second item.
    _row(page, "The poll").click()
    expect(page.get_by_text("2 of 6", exact=True)).to_be_visible()
    back.click()
    expect(page).to_have_url(f"{base_url}/devflow")
    expect(_queue(page)).to_be_visible()


def test_the_waiting_list_when_nothing_waits(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    panel = page.get_by_role("tabpanel")
    expect(panel.get_by_text("Nothing waits for you")).to_be_visible()
    expect(
        panel.get_by_text("Your agents have nothing for you to decide.")
    ).to_be_visible()
    expect(panel.get_by_role("button", name="Refresh")).to_be_visible()
    capture(page, "devflow-waiting-empty")


def test_the_waiting_list_says_when_github_could_not_be_read(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    page.goto("/devflow/projects")
    link_github(page, base_url)
    connected = page.request.post(
        "/devflow/api/projects",
        data={"installation_id": INSTALLATION, "repository_id": REPOSITORY},
        headers={"Origin": base_url},
    )
    assert connected.status == 201, connected.text()
    # GitHub stops showing the repository before the project is ever read.
    devflow_world({"installations": []})
    _read(page, base_url, connected.json()["id"])

    page.goto("/devflow")

    expect(
        page.get_by_text(
            "Adamniels/Personal-platform: GitHub could not be read, "
            "showing what was last seen"
        )
    ).to_be_visible()
    capture(page, "devflow-waiting-stale")


def _writes(page: Page) -> list[dict]:
    """The merges and comments the fake took for this account, with whose
    token made each."""
    answer = page.request.get("/devflow/api/fake/writes")
    assert answer.status == 200, answer.text()
    return answer.json()


def _staged_pull(page: Page, number: int) -> dict:
    """The pull request as the fake's world holds it now."""
    world = page.request.get("/devflow/api/fake/world").json()
    return next(
        pull for pull in repository(world)["pull_requests"] if pull["number"] == number
    )


def test_i_merge_a_waiting_pull_request(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _decision(world)
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    page.goto("/devflow")
    expect(page.get_by_role("tab", name="Waiting for you 2")).to_be_visible()
    vault = _item(page, "The vault")

    vault.get_by_role("button", name="Merge").click()

    dialog = page.get_by_role("dialog", name="Merge #304 into main?")
    expect(dialog).to_be_visible()
    expect(dialog.get_by_text("The vault")).to_be_visible()
    expect(dialog.locator("code")).to_have_text(f"{304:040x}"[:7])
    expect(
        dialog.get_by_text("This confirms 1 decision the agent made.")
    ).to_be_visible()
    capture(page, "devflow-merge-confirm")
    dialog.get_by_role("button", name="Merge").click()

    expect(page.get_by_text("Merged #304")).to_be_visible()
    expect(page.get_by_role("article", name="The vault")).to_have_count(0)
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    assert _writes(page) == [
        {
            "kind": "merge",
            "repository": REPOSITORY,
            "number": 304,
            "commit": f"{304:040x}",
            "method": "merge",
            "body": None,
            "by": OWNER_LOGIN,
        }
    ]
    assert _staged_pull(page, 304)["state"] == "merged"


def test_merging_asks_me_first(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    page.goto("/devflow")

    page.get_by_role("article", name="The poll").get_by_role(
        "button", name="Merge"
    ).click()
    dialog = page.get_by_role("dialog", name="Merge #305 into main?")
    expect(dialog).to_be_visible()
    dialog.get_by_role("button", name="Cancel").click()

    expect(dialog).to_have_count(0)
    expect(page.get_by_role("article", name="The poll")).to_be_visible()
    assert _writes(page) == []
    assert _staged_pull(page, 305)["state"] == "open"


def test_i_send_a_pull_request_back(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    _stopped(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    page.goto("/devflow")
    poll = _item(page, "The poll", "Pull requests waiting")
    text = "Rename the loop to `Poller`.\n\nThen it is fine."

    poll.get_by_role("button", name="Send back").click()
    poll.get_by_role("textbox", name="Comment").fill(text)
    if viewport == "phone":
        # The fixed tab bar would sit over the box's buttons, so they are
        # brought to the middle of the screen first, where the shot shows them.
        poll.get_by_role("button", name="Send", exact=True).evaluate(
            "button => button.scrollIntoView({ block: 'center' })"
        )
    capture(page, "devflow-send-back")
    poll.get_by_role("button", name="Send").click()

    expect(page.get_by_text("Sent back #305")).to_be_visible()
    # Its row moves to the group, behind the item's own screen on a phone.
    sent_back = page.get_by_role(
        "region", name="Sent back, the runner has it", include_hidden=True
    )
    expect(
        sent_back.get_by_role(
            "link", name=re.compile("^The poll "), include_hidden=True
        )
    ).to_have_count(1)
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    capture(page, "devflow-sent-back")
    assert _writes(page) == [
        {
            "kind": "comment",
            "repository": REPOSITORY,
            "number": 305,
            "commit": None,
            "method": None,
            "body": text,
            "by": OWNER_LOGIN,
        }
    ]
    world = page.request.get("/devflow/api/fake/world").json()
    (conversation,) = [
        each for each in repository(world)["conversations"] if each["number"] == 305
    ]
    posted = conversation["comments"][-1]
    assert (posted["author"], posted["body"]) == (OWNER_LOGIN, text)


def test_a_pull_request_that_moved_on_is_not_merged(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    page.goto("/devflow")
    poll = page.get_by_role("article", name="The poll")
    expect(poll).to_be_visible()

    # The agent pushes a new commit after the page loaded, and the reviewer
    # approves it, so it still waits, at a head the page has not seen.
    moved = one_repository()
    newer = "9" * 40
    add_pull_request(
        moved,
        305,
        title="The poll",
        labels=["owner-merge"],
        head_commit=newer,
        body=f"## Summary\n\nAdds the poll.\n\nCloses #{SLICE}\n",
        comments=[reviewer_comment(9305, "approve", newer)],
        statuses=[status("review-ok", "success")],
        check_runs=[check_run("ci-ok")],
    )
    add_conversation(moved, SLICE, repository(world)["conversations"][-1]["comments"])
    devflow_world(moved)
    poll.get_by_role("button", name="Merge").click()
    page.get_by_role("dialog").get_by_role("button", name="Merge").click()

    expect(
        poll.get_by_role("alert").filter(
            has_text="The pull request has changed since this was shown"
        )
    ).to_be_visible()
    assert _writes(page) == []
    assert _staged_pull(page, 305)["state"] == "open"
    # The list was read again: asking once more shows the new commit.
    poll.get_by_role("button", name="Merge").click()
    expect(page.get_by_role("dialog").locator("code")).to_have_text("9999999")


def test_i_cannot_act_when_my_link_has_expired(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    project = _connect_and_read(page, base_url)
    # GitHub stops taking the link, and Dev flow finds out the next time it
    # needs it.
    revoked = page.request.post(
        "/devflow/api/fake/revoke-token", headers={"Origin": base_url}
    )
    assert revoked.status == 204, revoked.text()
    listed = page.request.get("/devflow/api/github/repositories")
    assert listed.status == 409, listed.text()

    page.goto("/devflow")

    poll = page.get_by_role("article", name="The poll")
    expect(
        poll.get_by_text(
            "Your GitHub link has expired. Link it again on the Projects tab "
            "to merge or comment."
        )
    ).to_be_visible()
    expect(poll.get_by_role("button", name="Merge")).to_be_disabled()
    expect(poll.get_by_role("button", name="Send back")).to_be_disabled()
    # Nor does the API act without it.
    (item,) = page.request.get("/devflow/api/waiting").json()["items"]
    merged = page.request.post(
        f"/devflow/api/waiting/{item['id']}/merge",
        data={"head_commit": item["head_commit"]},
        headers={"Origin": base_url},
    )
    assert merged.status == 409
    assert merged.json()["title"] == "Your GitHub link has expired"
    assert _writes(page) == []
    assert item["project_id"] == project


def _devflow_link(page: Page):
    """Dev flow's link in the nav bar, whether or not it has the dot."""
    return page.locator("platform-nav .links").get_by_role(
        "link", name=re.compile("^Dev flow")
    )


def _forget_attention(page: Page) -> None:
    """The pages before the read asked the nav's attention address while
    nothing had been read, and the nav remembers that answer for a minute in
    the tab's sessionStorage. A person sees the dot at most a minute late; a
    test forgets it instead of waiting."""
    page.evaluate("sessionStorage.clear()")


def test_the_nav_shows_a_dot_when_something_waits(
    page: Page, invite_code: str, base_url: str, devflow_world, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    _forget_attention(page)

    page.goto("/")

    link = _devflow_link(page)
    expect(link.locator(".dot")).to_have_count(1)
    expect(link).to_have_accessible_name("Dev flow, needs you")
    # The front page's card says it too; the shot shows both.
    expect(page.get_by_role("heading", name="Dev flow")).to_be_visible()
    capture(page, "nav-attention-dot")

    # Every page of the platform has the same bar, a feature's included.
    page.goto("/review")
    expect(_devflow_link(page).locator(".dot")).to_have_count(1)
    expect(_devflow_link(page)).to_have_accessible_name("Dev flow, needs you")


def test_the_nav_shows_no_dot_when_nothing_waits(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    # The reviewer is still on it, so it is not my turn.
    _approved(world, 305, "The poll", ["in-review"])
    devflow_world(world)
    _connect_and_read(page, base_url)
    _forget_attention(page)

    with page.expect_response(
        lambda r: r.url.endswith("/devflow/api/attention")
    ) as response:
        page.goto("/")

    assert response.value.status == 200
    assert response.value.json() == {"attention": False}
    expect(_devflow_link(page)).to_have_accessible_name("Dev flow")
    expect(page.locator("platform-nav .dot")).to_have_count(0)


def test_the_dot_goes_when_i_merge(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    _ready_to_merge(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    _forget_attention(page)
    page.goto("/devflow")
    expect(_devflow_link(page).locator(".dot")).to_have_count(1)
    navs: list = []
    page.on("request", lambda r: navs.append(r) if r.url.endswith("/api/nav") else None)

    page.get_by_role("article", name="The poll").get_by_role(
        "button", name="Merge"
    ).click()
    page.get_by_role("dialog", name="Merge #305 into main?").get_by_role(
        "button", name="Merge"
    ).click()

    expect(page.get_by_text("Merged #305")).to_be_visible()
    expect(_devflow_link(page).locator(".dot")).to_have_count(0)
    expect(_devflow_link(page)).to_have_accessible_name("Dev flow")
    # The bar asked Dev flow again, and not the core for its links.
    assert navs == []


def test_an_item_shows_its_screenshots(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    lines = [
        add_screenshot(
            world, 305, "devflow-poll-desktop", png(640, 400, (96, 140, 200))
        ),
        add_screenshot(world, 305, "devflow-poll-phone", png(195, 422, (200, 150, 90))),
    ]
    body = (
        f"## Summary\n\nAdds the poll.\n\nCloses #{SLICE}\n\n## Verification\n\n"
        "Screenshots, desktop and 390px phone width:\n\n" + "\n".join(lines) + "\n"
    )
    _approved(world, 305, "The poll", ["owner-merge"], body=body)
    devflow_world(world)
    _connect_and_read(page, base_url)

    page.goto("/devflow")

    poll = _item(page, "The poll")
    shots = poll.get_by_role("list", name="Screenshots")
    expect(shots.get_by_role("listitem")).to_have_count(1)
    expect(shots.get_by_text("devflow-poll", exact=True)).to_be_visible()
    for name, width in (("devflow-poll-desktop", 640), ("devflow-poll-phone", 195)):
        thumbnail = shots.get_by_role("img", name=name)
        expect(thumbnail).to_be_visible()
        # Read from the fake as the PNG staged, and shown at most 160px high.
        page.wait_for_function(
            "img => img.complete && img.naturalWidth > 0",
            arg=thumbnail.element_handle(),
        )
        assert thumbnail.evaluate("img => img.naturalWidth") == width
        assert thumbnail.bounding_box()["height"] <= 160
    capture(page, "devflow-item-screenshots")

    shots.get_by_role("button", name="devflow-poll-desktop").click()

    dialog = page.get_by_role("dialog", name="devflow-poll-desktop")
    expect(dialog).to_be_visible()
    large = dialog.get_by_role("img", name="devflow-poll-desktop")
    expect(large).to_be_visible()
    # At its natural size, larger than its thumbnail.
    box = large.bounding_box()
    assert (box["width"], box["height"]) == (640, 400)
    capture(page, "devflow-screenshot-open")

    dialog.get_by_role("button", name="Close").click()
    expect(page.get_by_role("dialog")).to_have_count(0)


KEY = re.compile(r"^dfw_[A-Za-z0-9_-]{43}$")
SHOWN_ONCE = "Shown once."


def _open_project(
    page: Page, base_url: str, devflow_world, world: dict | None = None
) -> None:
    """Connects the staged repository, the empty Personal-platform unless a
    world is given, and opens its page from its row on the Projects tab."""
    world = world or one_repository()
    devflow_world(world)
    full_name = f"{repository(world)['owner']}/{repository(world)['name']}"
    page.goto("/devflow/projects")
    link_github(page, base_url)
    _choose(page)
    page.get_by_role("button", name=f"Connect {full_name}").click()
    page.get_by_role("region", name="Projects").get_by_role(
        "link", name=f"Open {full_name}"
    ).click()
    expect(page).to_have_url(
        re.compile(rf"{re.escape(base_url)}/devflow/projects/[0-9a-f-]{{36}}$")
    )
    expect(
        page.get_by_role("heading", name=repository(world)["name"], exact=True)
    ).to_be_visible()


def _edit_settings(page: Page) -> Locator:
    """Opens the project's settings page from its page's "Edit settings",
    unless it is open already, and answers the page's content once its form
    is drawn."""
    if not re.search(r"/settings/?$", page.url):
        page.get_by_role("article", name="Settings", exact=True).get_by_role(
            "link", name="Edit settings"
        ).click()
    expect(page).to_have_url(re.compile(r"/devflow/projects/[0-9a-f-]{36}/settings$"))
    content = page.get_by_role("main")
    expect(content.get_by_role("button", name="Save", exact=True)).to_be_visible()
    return content


def _back_to_project(page: Page) -> None:
    """Goes from a project's settings page back to its page, by the header's
    way back."""
    page.locator("main header a").first.click()
    expect(page).to_have_url(re.compile(r"/devflow/projects/[0-9a-f-]{36}$"))
    expect(page.get_by_role("article", name="Workers", exact=True)).to_be_visible()


def _start_a_worker(page: Page) -> Locator:
    """Shows the commands behind the Workers card's "Start a worker", unless
    they show already, as they do once a key is made, and answers them."""
    start = page.get_by_role("article", name="Workers", exact=True).get_by_role(
        "button", name="Start a worker"
    )
    expect(start).to_be_visible()
    if start.get_attribute("aria-expanded") == "false":
        start.click()
    return page.get_by_role("region", name="Start a worker")


def _make_key(page: Page) -> str:
    """Presses "Make a key" in the Workers panel and returns the key shown."""
    workers = page.get_by_role("article", name="Workers")
    workers.get_by_role("button", name="Make a key").click()
    shown = workers.get_by_role("region", name="The new key")
    expect(shown.get_by_text(KEY)).to_be_visible()
    return shown.get_by_text(KEY).inner_text()


def test_i_make_a_worker_key_and_see_it_once(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    page.context.grant_permissions(
        ["clipboard-read", "clipboard-write"], origin=base_url
    )
    _open_project(page, base_url, devflow_world)
    workers = page.get_by_role("article", name="Workers")
    expect(workers.get_by_text("No worker has said hello yet.")).to_be_visible()

    key = _make_key(page)

    shown = workers.get_by_role("region", name="The new key")
    expect(shown).to_contain_text(SHOWN_ONCE)
    capture(page, "devflow-worker-key")
    shown.get_by_role("button", name="Copy the key").click()
    expect(shown.get_by_role("button", name="Copied")).to_be_visible()
    assert page.evaluate("navigator.clipboard.readText()") == key

    # A fresh load shows no key, and offers a new one with a warning first.
    page.reload()
    expect(page.get_by_role("article", name="Workers")).to_be_visible()
    expect(page.get_by_text(KEY)).to_have_count(0)
    expect(page.get_by_text(SHOWN_ONCE)).to_have_count(0)
    page.get_by_role("article", name="Workers").get_by_role(
        "button", name="Make a new key"
    ).click()
    dialog = page.get_by_role("dialog", name="Make a new key?")
    expect(dialog).to_contain_text("The old key stops working at once")
    dialog.get_by_role("button", name="Make a new key").click()
    replaced = page.get_by_role("region", name="The new key").get_by_text(KEY)
    expect(replaced).to_be_visible()
    assert replaced.inner_text() != key


def test_a_worker_that_said_hello_is_listed(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    key = _make_key(page)

    # The worker, as it calls Dev flow: its key in the header, no cookie, no
    # Origin, from outside the browser.
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        response = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": "0190a8f0-0000-7000-8000-0000000000f1",
                "name": "fodr-worker-1",
                "recipe_version": "3",
                "base_image": "platform-hosted/devflow-worker:2026.11.17",
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert response.status == 204, response.text()
    finally:
        worker.dispose()

    page.reload()
    listed = page.get_by_role("list", name="Workers")
    expect(listed.get_by_text("fodr-worker-1")).to_be_visible()
    expect(listed.get_by_text(re.compile(r"^Last seen "))).to_be_visible()
    expect(
        listed.get_by_text("platform-hosted/devflow-worker:2026.11.17")
    ).to_be_visible()
    expect(page.get_by_text("No worker has said hello yet.")).to_have_count(0)
    capture(page, "devflow-project-workers")


def test_i_set_a_projects_settings(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    settings = _edit_settings(page)

    # A new project has the defaults.
    expect(settings.get_by_label("Pick-up label")).to_have_value("devflow")
    expect(settings.get_by_label("Hold label")).to_have_value("devflow-hold")
    expect(settings.get_by_label("Attention label")).to_have_value("devflow-attention")
    expect(settings.get_by_label("Pull requests wait for you")).to_have_value("always")

    # The same name twice is refused under the field, and nothing is saved.
    settings.get_by_label("Hold label").fill("DevFlow")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_label("Hold label")).to_have_attribute(
        "aria-invalid", "true"
    )
    expect(
        settings.get_by_text("must be a different label from the other three")
    ).to_be_visible()

    settings.get_by_label("Pick-up label").fill("agent: go")
    settings.get_by_label("Hold label").fill("agent: wait")
    settings.get_by_label("Pull requests wait for you").select_option("never")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(settings.get_by_label("Hold label")).not_to_have_attribute(
        "aria-invalid", "true"
    )
    capture(page, "devflow-project-settings")

    # Kept: a fresh load reads them back.
    page.reload()
    settings = _edit_settings(page)
    expect(settings.get_by_label("Pick-up label")).to_have_value("agent: go")
    expect(settings.get_by_label("Hold label")).to_have_value("agent: wait")
    expect(settings.get_by_label("Attention label")).to_have_value("devflow-attention")
    expect(settings.get_by_label("Pull requests wait for you")).to_have_value("never")


def test_i_trust_another_account(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A card octo-friend wrote, ignored while only the owner, octo-owner, is
    # trusted.
    world = one_repository()
    add_issue(world, 12, "The friend's card", author="octo-friend", labels=["devflow"])
    with_setup(world)
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    registered(page, base_url, project)

    def read_again():
        refreshed = page.request.post(
            f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
        )
        assert refreshed.status == 204, refreshed.text()
        page.goto(f"/devflow/projects/{project}")

    def card():
        return (
            page.get_by_role("table", name="Cards on their own")
            .locator("tbody tr")
            .filter(has=page.get_by_text("#12", exact=True))
        )

    read_again()
    expect(card()).to_contain_text("Ignored")
    expect(card()).to_contain_text(
        "octo-friend is not a trusted account of this project"
    )

    # My own login is listed and cannot be removed; I add octo-friend.
    settings = _edit_settings(page)
    trusted = settings.get_by_role("list", name="Trusted accounts")
    expect(trusted.get_by_role("listitem")).to_have_count(1)
    expect(trusted.get_by_role("listitem").first).to_contain_text("octo-owner")
    expect(trusted.get_by_role("listitem").first).to_contain_text("you, always on")
    expect(settings.get_by_role("button", name="Remove octo-owner")).to_have_count(0)
    settings.get_by_label("Add a trusted account").fill("octo-friend")
    settings.get_by_role("button", name="Add", exact=True).click()
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(trusted.get_by_role("listitem")).to_have_count(2)
    expect(settings.get_by_role("button", name="Remove octo-friend")).to_be_visible()
    capture(page, "devflow-settings-trusted")

    # The next read queues the card octo-friend wrote.
    read_again()
    expect(card()).to_contain_text("Queued")
    settings = _edit_settings(page)
    expect(
        settings.get_by_role("list", name="Trusted accounts").get_by_role("listitem")
    ).to_have_count(2)


def test_i_change_a_setting_on_its_page(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    project_url = page.url
    summary = page.get_by_role("article", name="Settings", exact=True)
    expect(summary.get_by_text("devflow-hold", exact=True)).to_be_visible()

    # Edit settings opens the page, under the Projects tab.
    summary.get_by_role("link", name="Edit settings").click()
    expect(page).to_have_url(f"{project_url}/settings")
    expect(page.get_by_role("heading", name="Settings", exact=True)).to_be_visible()
    expect(page.get_by_role("tab", name="Projects", exact=True)).to_have_attribute(
        "aria-selected", "true"
    )

    picked = page.get_by_role("article", name="How cards are picked")
    picked.get_by_label("Hold label").fill("agent: wait")
    expect(page.get_by_text("1 unsaved change", exact=True)).to_be_visible()
    page.get_by_role("button", name="Save", exact=True).click()
    expect(page.get_by_text("Saved.", exact=True)).to_be_visible()
    expect(page.get_by_text("No unsaved changes", exact=True)).to_be_visible()

    # Back on the project's page, its summary shows the label as saved.
    page.get_by_role("main").get_by_role(
        "link", name="Personal-platform", exact=True
    ).click()
    expect(page).to_have_url(project_url)
    expect(summary.get_by_text("agent: wait", exact=True)).to_be_visible()

    # Kept: a fresh load of the page reads it back.
    page.goto(f"{project_url}/settings")
    expect(
        page.get_by_role("article", name="How cards are picked").get_by_label(
            "Hold label"
        )
    ).to_have_value("agent: wait")


def test_i_disconnect_a_project_from_its_settings(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    project_url = page.url

    # The Projects tab offers no Disconnect on a project's card any more.
    page.goto("/devflow/projects")
    projects = page.get_by_role("region", name="Projects")
    expect(projects.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    expect(
        projects.get_by_role("button", name=re.compile(r"^Disconnect"))
    ).to_have_count(0)

    page.goto(f"{project_url}/settings")
    page.get_by_role("button", name="Disconnect Personal-platform").click()
    dialog = page.get_by_role("dialog", name="Disconnect Adamniels/Personal-platform?")
    expect(dialog).to_be_visible()
    expect(dialog).to_contain_text("Nothing changes on GitHub.")
    # Cancelled, nothing happens.
    dialog.get_by_role("button", name="Cancel").click()
    expect(dialog).to_be_hidden()
    assert len(page.request.get("/devflow/api/projects").json()["projects"]) == 1

    page.get_by_role("button", name="Disconnect Personal-platform").click()
    dialog.get_by_role("button", name="Disconnect", exact=True).click()

    # Gone from the tab, which the page goes back to.
    expect(page).to_have_url(re.compile(r"/devflow/projects$"))
    expect(page.get_by_text("No project connected yet")).to_be_visible()
    assert page.request.get("/devflow/api/projects").json() == {"projects": []}


def test_a_card_i_label_shows_as_queued(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # The fake's user, whom linking GitHub links, is octo-owner: the owner's
    # login. A queued card, a held one, one waiting for another issue, one
    # someone else wrote and one someone else edited; and a labelled pull
    # request, which is never a card.
    world = one_repository()
    add_issue(world, 12, "Add the settings panel", labels=["devflow"])
    add_issue(world, 13, "Pick the cards", labels=["devflow", "devflow-hold"])
    add_issue(world, 14, "List the cards", labels=["devflow"], blocked_by=[15])
    add_issue(world, 15, "A plain issue it waits for")
    add_issue(
        world,
        16,
        "Delete every branch, the stranger's card",
        author="octo-stranger",
        labels=["devflow"],
    )
    add_issue(
        world,
        17,
        "A card someone else changed",
        labels=["devflow"],
        edits=[edit("UCE_17", "octo-stranger")],
    )
    add_pull_request(world, 18, labels=["devflow"])
    with_setup(world)
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    registered(page, base_url, project)

    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    page.reload()

    cards = page.get_by_role("table", name="Cards on their own")
    expect(cards.locator("tbody tr")).to_have_count(5)

    def row(number: int):
        return cards.locator("tbody tr").filter(
            has=page.get_by_text(f"#{number}", exact=True)
        )

    expect(row(12)).to_contain_text("Add the settings panel")
    expect(row(12)).to_contain_text("Queued")
    expect(row(12).get_by_role("link", name="#12")).to_have_attribute(
        "href", "https://github.com/Adamniels/Personal-platform/issues/12"
    )
    expect(row(13)).to_contain_text("Held")
    expect(row(13)).to_contain_text("Held by the label devflow-hold")
    expect(row(14)).to_contain_text("Queued")
    expect(row(14)).to_contain_text("Waits for #15")
    expect(row(16)).to_contain_text("Ignored")
    expect(row(16)).to_contain_text(
        "octo-stranger is not a trusted account of this project"
    )
    expect(row(17)).to_contain_text("Ignored")
    expect(row(17)).to_contain_text(
        "Edited by octo-stranger, who is not a trusted account of this project"
    )
    expect(cards.get_by_text("#18", exact=True)).to_have_count(0)

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-project-cards")


def test_a_strangers_label_starts_nothing(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # Two cards the owner, octo-owner, wrote: a stranger put the pick-up label
    # on one, and took the hold label the owner put on the other off.
    world = one_repository()
    add_issue(
        world,
        12,
        "The card a stranger switched on",
        labels=["devflow"],
        label_events=[label_event("devflow", "octo-stranger")],
    )
    add_issue(
        world,
        13,
        "The card a stranger released",
        labels=["devflow"],
        label_events=[
            label_event("devflow", "octo-owner"),
            label_event("devflow-hold", "octo-owner"),
            label_event("devflow-hold", "octo-stranger", change="removed"),
        ],
    )
    with_setup(world)
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    registered(page, base_url, project)

    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    page.reload()

    cards = page.get_by_role("table", name="Cards on their own")
    expect(cards.locator("tbody tr")).to_have_count(2)

    def row(number: int):
        return cards.locator("tbody tr").filter(
            has=page.get_by_text(f"#{number}", exact=True)
        )

    expect(row(12)).to_contain_text("Ignored")
    expect(row(12)).to_contain_text(
        "The pick-up label devflow was added by octo-stranger, who is not a"
        " trusted account of this project"
    )
    expect(row(13)).to_contain_text("Held")
    expect(row(13)).to_contain_text(
        "Held by the label devflow-hold, taken off by octo-stranger, who is not a"
        " trusted account of this project"
    )


def test_a_labelled_feature_shows_its_cards_in_order(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A parent with the label and three sub-issues without it, in an order
    # that is not their numbers', one waiting for an issue outside; and a card
    # on its own beside it.
    world = one_repository()
    add_issue(
        world,
        40,
        "Features and their cards",
        labels=["devflow"],
        sub_issues=[43, 41, 42],
    )
    add_issue(world, 41, "Read the sub-issues")
    add_issue(world, 42, "Show each feature on the page", blocked_by=[44])
    add_issue(world, 43, "Store the feature")
    add_issue(world, 44, "A plain issue it waits for")
    add_issue(world, 50, "A card on its own", labels=["devflow"])
    with_setup(world)
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    registered(page, base_url, project)

    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    page.reload()

    features = page.get_by_role("list", name="Features", exact=True)
    expect(features.locator(":scope > li")).to_have_count(1)
    feature = features.locator(":scope > li").first
    expect(feature).to_contain_text("Features and their cards")
    expect(feature.get_by_role("link", name="#40", exact=True)).to_have_attribute(
        "href", "https://github.com/Adamniels/Personal-platform/issues/40"
    )
    # Its state, in its card's header beside its label.
    expect(
        feature.get_by_role("article").locator(":scope > div").first
    ).to_contain_text("Queued")

    # Its three cards in sub-issue order, each with its state.
    cards = feature.get_by_role("list", name="Cards of #40", exact=True)
    rows = cards.get_by_role("listitem")
    expect(rows).to_have_count(3)
    expect(rows).to_contain_text(["#43", "#41", "#42"])
    expect(rows.nth(0)).to_contain_text("Store the feature")
    expect(rows.nth(0)).to_contain_text("Queued")
    expect(rows.nth(2)).to_contain_text("Queued")
    expect(rows.nth(2)).to_contain_text("Waits for #44")

    # Beneath the features, the card on its own, and the parent is not one.
    own = page.get_by_role("table", name="Cards on their own")
    expect(own.locator("tbody tr")).to_have_count(1)
    expect(own).to_contain_text("A card on its own")
    expect(page.get_by_text("#40", exact=True)).to_have_count(1)

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-project-feature")


def test_a_features_cards_are_built_in_order(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A labelled feature of three cards, in an order that is not their
    # numbers', and the project's .devflow/project.json.
    world = one_repository()
    add_issue(
        world,
        60,
        "The feature's branch",
        labels=["devflow"],
        sub_issues=[62, 61, 63],
    )
    add_issue(world, 61, "Build a card from its feature's branch")
    add_issue(world, 62, "Make the feature's branch")
    add_issue(world, 63, "Carry the feature in the brief")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(62), _ready(61), _ready(63)])

    # The fake worker takes the project's next job and keeps it running: the
    # feature's first card, on a branch of its own from the feature's.
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert taken.json()["card"] == 62
    assert taken.json()["branch"] == "devflow/62-make-the-feature-s-branch"
    made = [
        write
        for write in page.request.get("/devflow/api/fake/writes").json()
        if write["kind"] == "branch"
    ]
    assert [write["branch"] for write in made] == ["feature-60-the-feature-s-branch"]
    page.reload()

    features = page.get_by_role("list", name="Features", exact=True)
    feature = features.locator(":scope > li").first
    # Its state, in its card's header beside its label.
    expect(
        feature.get_by_role("article").locator(":scope > div").first
    ).to_contain_text("Building")
    rows = feature.get_by_role("list", name="Cards of #60", exact=True).get_by_role(
        "listitem"
    )
    expect(rows).to_have_count(3)
    expect(rows).to_contain_text(["#62", "#61", "#63"])
    expect(rows.nth(0)).to_contain_text("Building")
    expect(rows.nth(1)).to_contain_text("Queued")
    expect(rows.nth(1)).to_contain_text("Waits for #62, earlier in the feature")
    expect(rows.nth(2)).to_contain_text("Queued")
    expect(rows.nth(2)).to_contain_text("Waits for #61, earlier in the feature")

    # Nothing more for a worker while the first card runs.
    again = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert again.status == 204, again.text()

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-feature-building")


def test_a_feature_is_read_before_it_is_built(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    """Every card is read before anything is built: a labelled feature's cards
    are read together, shown as Reading while a worker reads them and Queued
    once the reading is back, and a card's page shows what it was read as, its
    question with the recommendation it is built on. See "Reading a card" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 60, "The reading job", labels=["devflow"], sub_issues=[61, 62])
    add_issue(world, 61, "Add the reading form")
    add_issue(world, 62, "Show the reading")
    # A valid .devflow/, so saving the settings once it is read does the
    # settings step, as after the setup's pull request.
    add_file(world, ".devflow/Dockerfile", RECIPE)
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    # Questions off, so the card with a question goes on the recommendation;
    # with them on, it waits for my answers, test_i_answer_a_cards_questions.
    _waits_for_me(page, base_url, project, "never", questions=False)

    # A worker on the image released asks for work and is handed the reading
    # of the whole feature, before any build: the parent as its card, and both
    # cards to read.
    key = _make_key(page)
    command = (
        page.get_by_role("region", name="Build and start the worker")
        .locator("code")
        .inner_text()
    )
    image = re.search(r"--build-arg DEVFLOW_BASE=(\S+) ", command).group(1)
    worker_id = "0190a8f0-0000-7000-8000-0000000000f4"
    # The worker is built from the recipe as Dev flow read it.
    version = page.request.get(f"/devflow/api/projects/{project}/workers").json()[
        "recipe_version"
    ]
    assert version is not None
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        hello = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": worker_id,
                "name": "reader-worker-1",
                "recipe_version": version,
                "base_image": image,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert hello.status == 204, hello.text()
        _pass_the_check(worker, key, worker_id)
        asked = worker.post(
            "/devflow/api/worker/jobs/next",
            data={"worker_id": worker_id},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert asked.status == 200, asked.text()
        job = asked.json()
        assert (job["kind"], job["card"]["number"]) == ("read", 60)
        assert [card["number"] for card in job["to_read"]["cards"]] == [61, 62]

        # While it reads, both cards show as Reading, and nothing of the
        # feature is handed out to another worker.
        page.reload()
        rows = page.get_by_role("list", name="Cards of #60", exact=True).get_by_role(
            "listitem"
        )
        expect(rows).to_have_count(2)
        expect(rows.nth(0)).to_contain_text("Reading")
        expect(rows.nth(1)).to_contain_text("Reading")
        again = page.request.post(
            "/devflow/api/fake/worker/run",
            data={"project_id": project, "keep_running": True},
            headers={"Origin": base_url},
        )
        assert again.status == 204, again.text()

        # The reading comes back: 61 ready, 62 with a question.
        question = {
            "number": 62,
            "outcome": "questions",
            "questions": [
                {
                    "question": "Does the reading show on the list or on the card's page?",
                    "recommendation": "On the card's page, with its state on the list",
                    "why": "The list stays one line a card",
                }
            ],
            "missing": [],
        }
        result = worker.post(
            f"/devflow/api/worker/jobs/{job['job_id']}/result",
            data={
                "worker_id": worker_id,
                "outcome": "succeeded",
                "report": _reading([_ready(61), question]),
                "failure": None,
                "check": None,
                "branch": job["branch"],
                "head_commit": None,
                "auth_method": "oauth_token",
                "usage": {"num_turns": 6},
                "duration_ms": 52000,
                "stderr": "",
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert result.status == 204, result.text()
    finally:
        worker.dispose()

    page.reload()
    rows = page.get_by_role("list", name="Cards of #60", exact=True).get_by_role(
        "listitem"
    )
    expect(rows.nth(0)).to_contain_text("Queued")
    expect(rows.nth(1)).to_contain_text("Queued")

    # The card's page shows its reading: built on the recommendation, the
    # question with the answer chosen and why, and the reading among its runs.
    page.goto(f"/devflow/projects/{project}/cards/62")
    reading = page.get_by_role("article", name="The reading")
    expect(reading).to_contain_text("Built on the recommendations")
    questions = reading.get_by_role("list", name="Questions")
    expect(questions.get_by_role("listitem")).to_have_count(1)
    expect(questions).to_contain_text(
        "Does the reading show on the list or on the card's page?"
    )
    expect(questions).to_contain_text(
        "Answer On the card's page, with its state on the list"
    )
    expect(questions).to_contain_text("Because The list stays one line a card")
    entries = _entries(page)
    expect(entries.locator("h3")).to_have_text(["Reading", "Now"])
    expect(entries.first).to_contain_text("Succeeded")
    _look(page, "devflow-card-reading")


RECIPE = b"ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nRUN apt-get update\n"


def test_the_project_page_shows_how_to_start_a_worker(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    page.context.grant_permissions(
        ["clipboard-read", "clipboard-write"], origin=base_url
    )
    _open_project(page, base_url, devflow_world)
    workers = page.get_by_role("article", name="Workers")

    # Before a key, behind "Start a worker", the command that builds and
    # starts a worker, never the one that saves a key.
    _start_a_worker(page)
    start = workers.get_by_role("region", name="Build and start the worker")
    expect(start).to_contain_text("~/.devflow/personal-platform.env")
    expect(workers.get_by_role("region", name="Save the key")).to_have_count(0)

    key = _make_key(page)

    # Then the command that saves it, with the key, first; the start command
    # after it, still without.
    save = workers.get_by_role("region", name="Save the key")
    expect(save).to_contain_text(f"'{key}' > ~/.devflow/personal-platform.env")
    regions = workers.get_by_role(
        "region", name=re.compile("^(Save the key|Build and start the worker)$")
    )
    expect(regions).to_have_count(2)
    assert regions.nth(0).get_attribute("aria-label") == "Save the key"
    command = start.locator("code").inner_text()
    assert "dfw_" not in command
    assert re.search(r"--build-arg DEVFLOW_BASE=\S+ ", command)
    assert re.search(r"-e DEVFLOW_URL=http\S+ devflow-personal-platform", command)
    assert f"{base_url}/devflow/api/worker/recipe" in command
    capture(page, "devflow-start-worker")

    save.get_by_role("button", name="Copy the save command").click()
    expect(save.get_by_role("button", name="Copied")).to_be_visible()
    assert (
        page.evaluate("navigator.clipboard.readText()")
        == save.locator("code").inner_text()
    )
    start.get_by_role("button", name="Copy the start command").click()
    expect(start.get_by_role("button", name="Copied")).to_be_visible()
    assert page.evaluate("navigator.clipboard.readText()") == command


def test_the_page_says_when_a_worker_needs_a_rebuild(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository(name="Fodr")
    add_file(world, ".devflow/Dockerfile", RECIPE)
    _open_project(page, base_url, devflow_world, world)
    key = _make_key(page)
    command = (
        page.get_by_role("region", name="Build and start the worker")
        .locator("code")
        .inner_text()
    )
    image = re.search(r"--build-arg DEVFLOW_BASE=(\S+) ", command).group(1)

    # The worker's build fetches the recipe, which records its version; a
    # worker built from an older one then says hello on the image released.
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        recipe = worker.get(
            "/devflow/api/worker/recipe",
            headers={"Authorization": f"Bearer {key}"},
        )
        assert recipe.status == 200, recipe.text()
        assert recipe.body() == RECIPE
        version = recipe.headers["x-devflow-recipe-version"]
        assert re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", version)
        hello = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": "0190a8f0-0000-7000-8000-0000000000f2",
                "name": "fodr-worker-1",
                "recipe_version": "0" * 40,
                "base_image": image,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert hello.status == 204, hello.text()
        asked = worker.post(
            "/devflow/api/worker/jobs/next",
            data={"worker_id": "0190a8f0-0000-7000-8000-0000000000f2"},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert asked.status == 204, asked.text()
    finally:
        worker.dispose()

    page.reload()
    listed = page.get_by_role("list", name="Workers")
    expect(listed.get_by_text("fodr-worker-1")).to_be_visible()
    expect(
        listed.get_by_text("Fodr's recipe has changed: run the command again")
    ).to_be_visible()
    capture(page, "devflow-worker-rebuild")


# The shape of a setup token from `claude setup-token`, never a real one.
CLAUDE_TOKEN = "sk-ant-oat01-ZTJlLXRlc3RzLW9ubHktbm90LWEtcmVhbC10b2tlbg_AAAAAA"
CLAUDE_PURPOSE = (
    "Agents run on your subscription with this. Make it with claude setup-token."
)


def _claude_login(page: Page):
    return page.get_by_role("article", name="Claude login")


def _day(page: Page, at: str) -> str:
    """A time from the API as the page writes a date, in the browser's own
    zone and language."""
    return page.evaluate(
        "at => new Intl.DateTimeFormat('en-GB', {dateStyle: 'medium'})"
        ".format(new Date(at))",
        at,
    )


def test_i_give_dev_flow_my_claude_login(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    login = _claude_login(page)
    expect(login.get_by_text("Not set", exact=True)).to_be_visible()
    # The field is hidden until Give a token opens it below the line.
    expect(login.get_by_label("Claude token")).to_have_count(0)
    capture(page, "devflow-claude-login-empty")

    login.get_by_role("button", name="Give a token").click()
    expect(login).to_contain_text(CLAUDE_PURPOSE)
    expect(login.get_by_role("button", name="Remove")).to_have_count(0)
    field = login.get_by_label("Claude token")
    expect(field).to_have_attribute("type", "password")
    field.fill(CLAUDE_TOKEN)
    login.get_by_role("button", name="Save").click()

    # The field closes, and the line says when it was given and is due.
    expect(login.get_by_label("Claude token")).to_have_count(0)
    status = page.request.get("/devflow/api/claude-login").json()
    assert status["state"] == "ok"
    said = (
        f"given {_day(page, status['given_at'])} · due {_day(page, status['renew_by'])}"
    )
    expect(login.get_by_text(said)).to_be_visible()
    expect(login.get_by_text("Given", exact=True)).to_be_visible()
    expect(login.get_by_role("button", name="Replace")).to_be_visible()
    capture(page, "devflow-claude-login")

    # Never shown again, not even in part, and still set after a reload.
    page.reload()
    expect(login.get_by_text(said)).to_be_visible()
    login.get_by_role("button", name="Replace").click()
    expect(login.get_by_label("Claude token")).to_have_value("")
    assert CLAUDE_TOKEN[10:] not in page.content()
    assert CLAUDE_TOKEN[-12:] not in page.content()

    # Removing it asks first, and then it is gone.
    login.get_by_role("button", name="Remove").click()
    page.get_by_role("dialog", name="Remove the Claude login?").get_by_role(
        "button", name="Remove the login"
    ).click()
    expect(login.get_by_text("Not set", exact=True)).to_be_visible()
    assert page.request.get("/devflow/api/claude-login").json()["set"] is False


API_KEY = "sk-ant-api03-e2e-only-not-a-real-key-ZmFrZQ_AAAAAAAAAAAAAAAAAAAA"


def test_i_give_an_api_key_and_a_project_runs_on_it(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    key = page.get_by_role("article", name="Anthropic API key")
    expect(key.get_by_text("Not set", exact=True)).to_be_visible()
    expect(key.get_by_text("for runs billed to your Anthropic account")).to_be_visible()

    # Given on the Projects tab, as the Claude login is.
    key.get_by_role("button", name="Give a key").click()
    field = key.get_by_label("API key")
    expect(field).to_have_attribute("type", "password")
    field.fill(API_KEY)
    key.get_by_role("button", name="Save").click()
    expect(key.get_by_label("API key")).to_have_count(0)
    status = page.request.get("/devflow/api/api-key").json()
    assert status["set"] is True
    expect(key.get_by_text(f"given {_day(page, status['given_at'])}")).to_be_visible()
    expect(key.get_by_text("Given", exact=True)).to_be_visible()
    assert API_KEY[10:] not in page.content()
    capture(page, "devflow-connections-api-key")

    # The project is set to run on it, and its settings page shows it saved.
    _open_project(page, base_url, devflow_world)
    settings = _edit_settings(page)
    runs_on = settings.get_by_label("Runs on")
    expect(runs_on).to_have_value("subscription")
    runs_on.select_option(label="The API key only")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    page.reload()
    expect(page.get_by_label("Runs on")).to_have_value("api_key")

    # While the project runs on it, the key cannot be removed.
    removed = page.request.delete("/devflow/api/api-key", headers={"Origin": base_url})
    assert removed.status == 409
    assert "These projects run on it:" in removed.json()["detail"]


def test_dev_flow_tells_me_when_my_claude_login_is_due(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    saved = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert saved.status == 200
    aged = page.request.post(
        "/devflow/api/fake/claude-login-age",
        data={"days": 340},
        headers={"Origin": base_url},
    )
    assert aged.status == 204
    status = page.request.get("/devflow/api/claude-login").json()
    assert status["state"] == "due_soon"

    page.goto("/devflow/projects")
    login = _claude_login(page)
    # In the attention style, the card's outline and "Needs you" for a reader.
    expect(login).to_contain_text("Needs you")
    expect(login.get_by_text("Due", exact=True)).to_be_visible()
    expect(
        login.get_by_text(f"due {_day(page, status['renew_by'])}", exact=False)
    ).to_be_visible()
    capture(page, "devflow-claude-login-due")

    # The front page's card and the nav's dot say it too.
    _forget_attention(page)
    page.goto("/")
    card = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Dev flow", level=2)
    )
    expect(card).to_have_class(re.compile(r"\bneeds-you\b"))
    expect(
        card.get_by_role("link", name="Your Claude login is due for renewal")
    ).to_have_attribute("href", "/devflow/projects")
    expect(_devflow_link(page)).to_have_accessible_name("Dev flow, needs you")


# A card built by the fake worker, as part 2 of the development flow carries one: its pull request,
# opened as the app, waits for the owner on "Waiting for you". See "The pull request Dev flow
# opens" in devflow/README.md.

CARD = 21
CARD_TITLE = "Add the empty state"


def _reading(cards: list[dict]) -> dict:
    """A reading in its form of `cards`, with a note of the code they touch."""
    return {
        "cards": cards,
        "touches": "web/src/list.tsx: the habits list the cards change.",
    }


def _ready(number: int) -> dict:
    """A card read as ready to build."""
    return {"number": number, "outcome": "ready", "questions": [], "missing": []}


def _fake_read(page: Page, base_url: str, project: str, cards: list[dict]) -> dict:
    """The fake worker takes the project's next job, a reading, and ends it
    with `cards` read, as a reader hands them back: before anything is built,
    each card is read. What it answered."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _reading(cards),
                "failure": None,
                "check": None,
                "auth_method": "oauth_token",
                "usage": {"num_turns": 4},
                "duration_ms": 41000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    assert ran.json()["kind"] == "read", ran.text()
    return ran.json()


def _report() -> dict:
    """A report in its form, as an agent hands one back, with a decision for
    the owner beside one that is not."""
    return {
        "outcome": "done",
        "stopped_reason": "",
        "summary": "Added the empty state to the habits list, with `EmptyState`.",
        "tasks": [
            {"text": "Add the empty state", "done": True},
            {"text": "Count the habits", "done": False},
        ],
        "decisions": [
            {
                "chosen": "A plain sentence",
                "alternative": "An illustration",
                "why": "Nothing else on the page is drawn",
                "for_owner": False,
            },
            {
                "chosen": "Show it on every list",
                "alternative": "Only on habits",
                "why": "The lists look the same",
                "for_owner": True,
            },
        ],
        "tests": [{"what": "list.test.tsx", "result": "passed"}],
        "pages": [
            {"path": "/habits", "why": "The empty state shows there", "looked": True}
        ],
        "installed": [],
        "follow_ups": ["Count the habits in the title"],
        "replies": [],
        "handover": "The empty state is in list.tsx.",
    }


def _card_project(page: Page, base_url: str, devflow_world) -> str:
    """Connects a repository with one card the owner labelled and the project's
    .devflow/project.json, gives Dev flow a Claude login, reads the project so
    the card is picked, and has the fake worker read the card as ready, so its
    build is the next job. The project's id."""
    world = one_repository()
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(CARD)])
    return project


def _built_card(
    page: Page,
    base_url: str,
    devflow_world,
    verdict: dict | None = None,
    review: bool = True,
    green: bool = False,
    pictures: dict[str, bytes] | None = None,
    report: dict | None = None,
) -> tuple[str, dict]:
    """Connects a repository with one card the owner labelled and the project's
    .devflow/project.json, gives Dev flow a Claude login, reads the project so
    the card is picked, has the fake worker build it with a passing check, with
    `green` stages CI that passed on the commit it built, and then review its
    pull request with `verdict`, an approve unless given, unless `review` is
    false. With `pictures`, the build's result lists them by name and the fake
    worker puts each on pr-screenshots, as a worker pushes them. With `report`,
    the build hands it back instead of `_report()`. The project's id and what
    the fake worker answered for the build."""
    project = _card_project(page, base_url, devflow_world)
    result = {
        "outcome": "succeeded",
        "report": report or _report(),
        "failure": None,
        "check": {"exit_code": 0, "output": "Tests: 12 passed"},
        "auth_method": "oauth_token",
        "usage": {
            "usage": {"input_tokens": 41, "output_tokens": 6113},
            "total_cost_usd": 1.83,
            "num_turns": 23,
        },
        "duration_ms": 184213,
        "stderr": "",
    }
    run: dict = {"project_id": project, "result": result}
    if pictures is not None:
        result["screenshots"] = {"names": list(pictures), "note": None}
        run["pictures"] = {
            name: base64.b64encode(content).decode()
            for name, content in pictures.items()
        }
    ran = page.request.post(
        "/devflow/api/fake/worker/run", data=run, headers={"Origin": base_url}
    )
    assert ran.status == 200, ran.text()
    if green:
        _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    if review:
        reviewed = _fake_review(page, base_url, project, verdict or _verdict("approve"))
        assert reviewed["head_commit"] == ran.json()["head_commit"]
    return project, ran.json()


def _verdict(verdict: str, findings: list[dict] | None = None) -> dict:
    """A reviewer's verdict in its form, with `findings`, none unless given."""
    return {
        "verdict": verdict,
        "summary": (
            "The empty state is there and tested."
            if verdict == "approve"
            else "The empty state shows on an empty list only after a reload."
        ),
        "findings": findings or [],
        "for_owner": [
            {
                "decision": "Show it on every list",
                "view": "Agreed: the lists share one component.",
            }
        ],
    }


def _fake_review(page: Page, base_url: str, project: str, verdict: dict) -> dict:
    """The fake worker takes the project's next job, the review of the card's
    pull request, and ends it with `verdict`. What it answered."""
    reviewed = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": verdict,
                "failure": None,
                "check": None,
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 12, "output_tokens": 840},
                    "num_turns": 9,
                },
                "duration_ms": 61000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert reviewed.status == 200, reviewed.text()
    assert reviewed.json()["kind"] == "review", reviewed.json()
    return reviewed.json()


def _opened(page: Page) -> dict:
    """The one pull request the app opened in the test's fake world."""
    pulls = [
        pull
        for pull in repository(page.request.get("/devflow/api/fake/world").json())[
            "pull_requests"
        ]
        if pull["author"] == APP_LOGIN
    ]
    assert len(pulls) == 1, pulls
    return pulls[0]


def _waiting_card(page: Page):
    """The card, chosen under Pull requests waiting, in the detail."""
    return _item(page, CARD_TITLE, "Pull requests waiting")


def _waiting_row(page: Page):
    """The card's row under Pull requests waiting."""
    return (
        _queue(page)
        .get_by_role("region", name="Pull requests waiting")
        .get_by_role("link", name=re.compile(f"^{re.escape(CARD_TITLE)} "))
    )


def test_a_built_card_waits_for_me_with_its_pull_request(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    _, ran = _built_card(page, base_url, devflow_world)

    # Its pull request exists in the fake world, by the app, from the card's
    # branch into main, with the body's sections and its one Closes line.
    pull = _opened(page)
    assert pull["head_branch"] == ran["branch"] == "devflow/21-add-the-empty-state"
    assert pull["base_branch"] == "main"
    assert pull["title"] == CARD_TITLE
    assert pull["draft"] is False
    headings = [line for line in pull["body"].splitlines() if line.startswith("#")]
    assert headings == [
        "## Summary",
        "## Tasks",
        "## Decisions the agent made",
        "## Verification",
        "## Follow ups",
    ]
    assert f"Closes #{CARD}" in pull["body"]
    assert "- **For you.** Show it on every list." in pull["body"]

    page.goto("/devflow")
    card = _waiting_card(page)
    expect(card).to_be_visible()
    expect(card.get_by_text("Waiting for you", exact=True)).to_be_visible()
    expect(card).to_contain_text(f"{OWNER}/{NAME}")
    expect(card).to_contain_text(f"#{pull['number']}")
    expect(card).to_contain_text("Added the empty state to the habits list")
    decided = card.get_by_role("list", name="The agent decided")
    expect(decided).to_contain_text("Show it on every list")
    expect(decided).to_contain_text("Only on habits")
    expect(decided).to_contain_text("The lists look the same")
    expect(card).to_contain_text("and 1 more it did not need you for")
    # The check Dev flow ran, as its tile, on the commit in the where line.
    glance = card.get_by_role("list", name="At a glance")
    expect(glance.get_by_role("listitem").first).to_have_text(
        f"The checkPassed{CHECK} · exit 0"
    )
    expect(card).to_contain_text(f"{ran['head_commit'][:7]} → main")
    expect(card.get_by_role("button", name="Merge")).to_be_enabled()
    expect(card.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", pull["url"]
    )
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-inbox-waiting")


def test_i_merge_a_card_from_the_list(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world)
    pull = _opened(page)

    page.goto("/devflow")
    _waiting_card(page).get_by_role("button", name="Merge").click()
    dialog = page.get_by_role("dialog", name=f"Merge #{pull['number']} into main?")
    expect(dialog).to_contain_text(ran["head_commit"][:7])
    expect(dialog).to_contain_text("This confirms 1 decision the agent made.")
    dialog.get_by_role("button", name="Merge").click()

    expect(page.get_by_text(f"Merged #{pull['number']}")).to_be_visible()
    expect(_waiting_row(page)).to_have_count(0)

    # Merged as the owner, naming the commit shown, and never as the app.
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert len(merges) == 1, merges
    assert merges[0]["by"] == "octo-owner"
    assert merges[0]["commit"] == ran["head_commit"]
    assert "purpose" not in merges[0]

    # The card is done.
    page.goto(f"/devflow/projects/{project}")
    row = (
        page.get_by_role("table", name="Cards on their own")
        .locator("tbody tr")
        .filter(has=page.get_by_text(f"#{CARD}", exact=True))
    )
    expect(row).to_contain_text("Done")


def _entries(page: Page):
    """The entries of a card's timeline, How it got here, oldest first, its
    own and none nested in them."""
    return page.get_by_role("list", name="How it got here").locator(":scope > li")


def test_a_card_page_tells_its_story(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    read_once = {
        "blocking": True,
        "file": "web/src/list.tsx",
        "line": 42,
        "what": "The empty state is read once",
        "why": "A list emptied later shows nothing",
    }
    # A build, a review asking for changes, a fix, and a review approving it.
    project, ran = _built_card(
        page, base_url, devflow_world, verdict=_verdict("changes", [read_once])
    )
    fixed = _fake_fix(page, base_url, project, _fix_report())
    _fake_review(page, base_url, project, _verdict("approve"))
    pull = _opened(page)

    # From the project's cards list to the card's page.
    page.goto(f"/devflow/projects/{project}")
    page.get_by_role("table", name="Cards on their own").get_by_role(
        "link", name=CARD_TITLE
    ).click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("tab", name="Projects", selected=True)).to_be_visible()

    # The header: its state, the mono line, the title and its links.
    expect(page.get_by_role("heading", name=CARD_TITLE)).to_be_visible()
    expect(
        page.get_by_text(f"{OWNER}/{NAME} · CARD #{CARD} · NO FEATURE".upper())
    ).to_be_visible()
    expect(page.get_by_role("link", name=f"Issue #{CARD}")).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/issues/{CARD}"
    )
    expect(
        page.get_by_role("link", name=f"Pull request #{pull['number']}")
    ).to_have_attribute("href", pull["url"])

    # What the agent did, the fix's report, before how it got here.
    did = page.get_by_role("article", name="What the agent did")
    expect(did).to_contain_text(
        "Read the empty state on every change, with a test that empties the list."
    )
    expect(did.get_by_role("list", name="Decisions")).to_contain_text("For you")

    # How it got here: the reading, then the build, the review asking for
    # changes, the fix and the approval, oldest first, ending in now.
    entries = _entries(page)
    expect(entries.locator("h3")).to_have_text(
        [
            "Reading",
            "Build",
            f"Review of {ran['head_commit'][:7]}",
            "Fix, round 1 of 3",
            f"Review of {fixed['head_commit'][:7]}",
            "Now",
        ]
    )
    build = entries.nth(1)
    expect(build).to_contain_text("devflow-fake-worker")
    # Tokens by the one definition, 41 + 6,113, with cache reads beside them.
    expect(build).to_contain_text("6,154 tokens · 0 cache reads")
    expect(build).to_contain_text("logged in with oauth_token")
    expect(build).to_contain_text(f"exit 0 on {ran['head_commit'][:7]}")
    expect(build.get_by_text("Tests: 12 passed")).to_be_hidden()
    build.get_by_role("button", name="Show output").click()
    expect(build.get_by_text("Tests: 12 passed")).to_be_visible()
    expect(entries.nth(2).get_by_role("list", name="Findings")).to_contain_text(
        "Blocking web/src/list.tsx:42 The empty state is read once"
    )
    expect(
        entries.nth(3).get_by_role("list", name="The developer's replies")
    ).to_contain_text("Fixed: it is read on every change")
    expect(entries.nth(4)).to_contain_text("Approved")
    expect(entries.nth(5)).to_contain_text("Waiting for you")

    # At a glance: the check and the review of the head, and one round used.
    glance = page.get_by_role("article", name="At a glance")
    expect(glance).to_contain_text("Approved")
    expect(glance.get_by_role("img", name="Fix rounds: 1 of 3 used")).to_be_visible()

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-waiting")

    # The banner's one link opens its item in the inbox.
    page.get_by_role("link", name="Decide in Waiting for you").click()
    expect(page).to_have_url(
        re.compile(rf"/devflow\?item=card(/|%2F){project}(/|%2F){CARD}$")
    )
    expect(page.get_by_role("article", name=CARD_TITLE)).to_be_visible()


def test_a_cards_run_has_its_log(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """After the fake worker's run, the card's page opens that run's log with
    the lines the fake posted, the view of "Running now", not live. See "A
    card's page" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    project, _ = _built_card(page, base_url, devflow_world, review=False)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    build = _entries(page).filter(has=page.locator("h3", has_text="Build"))
    expect(build).to_have_count(1)
    expect(build.get_by_role("log")).to_have_count(0)
    log_button = build.get_by_role("button", name="Log", exact=True)
    expect(log_button).to_have_attribute("aria-expanded", "false")
    log_button.click()

    log = build.get_by_role("log", name="Log of Build")
    expect(log.get_by_text(f"Cloning {OWNER}/{NAME} at main")).to_be_visible()
    expect(log.get_by_text("Reading the card and the code it touches.")).to_be_visible()
    # A tool's name in bold, as on Running now.
    expect(log.locator("strong", has_text="Read")).to_be_visible()
    expect(log.get_by_role("listitem")).to_have_count(3)
    # Three lines are the whole log, so there is nothing more to read.
    expect(build.get_by_role("button", name="Show more")).to_have_count(0)
    expect(build.get_by_role("button", name="Hide log")).to_be_visible()

    _looks_right(page)
    capture(page, "devflow-card-run-log")

    # Hide log closes it again.
    build.get_by_role("button", name="Hide log").click()
    expect(build.get_by_role("log")).to_have_count(0)


def test_the_card_page_shows_which_pages_the_agent_looked_at(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    # The developer looks at its own pages before it reports, and says which in
    # the report's `looked`: the card's page marks the one it did not look at,
    # and so does the pull request's body. See "Screenshots" in
    # devflow/README.md.
    register_and_log_in(page, invite_code, base_url)
    report = _report()
    report["pages"] = [
        {"path": "/habits", "why": "The empty state shows there", "looked": True},
        {"path": "/settings", "why": "The toggle moved", "looked": False},
    ]
    project, _ = _built_card(page, base_url, devflow_world, report=report)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    pages = (
        page.get_by_role("article", name="What the agent did")
        .get_by_role("list", name="Pages")
        .get_by_role("listitem")
    )
    expect(pages).to_have_count(2)
    expect(pages.first).to_contain_text("/habits")
    expect(pages.first).to_contain_text("Looked at")
    expect(pages.first).not_to_contain_text("did not look")
    expect(pages.nth(1)).to_contain_text("/settings")
    expect(pages.nth(1)).to_contain_text("The agent did not look at this page")

    # The pull request's body marks it too.
    body = _opened(page)["body"]
    assert "- `/habits`: The empty state shows there.\n" in body, body
    assert (
        "- `/settings`: The toggle moved. **The agent did not look at this page.**"
        in body
    ), body

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-looked")


def test_a_built_card_is_reviewed(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world, review=False)
    commit = ran["head_commit"]
    pull = _opened(page)

    # Its pull request opened, so the card goes through Reviewing, the reviewer
    # reading it next.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Reviewing", exact=True).first).to_be_visible()
    expect(
        page.get_by_text("Its pull request is open; the reviewer reads it next")
    ).to_be_visible()

    # The fake worker reviews it: changes, with two findings.
    reviewed = _fake_review(
        page,
        base_url,
        project,
        _verdict(
            "changes",
            [
                {
                    "blocking": True,
                    "file": "web/src/list.tsx",
                    "line": 42,
                    "what": "The empty state is read once",
                    "why": "A list emptied later shows nothing",
                },
                {
                    "blocking": False,
                    "file": "web/src/list.css",
                    "line": 3,
                    "what": "A colour by hand",
                    "why": "The tokens hold it",
                },
            ],
        ),
    )
    assert reviewed["head_commit"] == commit

    # The card's page shows the verdict for its commit, the findings and the
    # reviewer's view of the owner's decision; the changes send the card to its
    # first fix round.
    page.reload()
    expect(page.get_by_text("Fixing", exact=True).first).to_be_visible()
    expect(
        page.get_by_text(
            "Round 1 of 3: the reviewer asked for changes; a fix runs next"
        )
    ).to_be_visible()
    reviews = _entries(page).filter(
        has=page.get_by_role("heading", name=f"Review of {commit[:7]}")
    )
    expect(reviews).to_have_count(1)
    expect(reviews.get_by_text("Changes asked for")).to_be_visible()
    expect(reviews).to_contain_text("for the pull request's head")
    expect(reviews).to_contain_text(
        "The empty state shows on an empty list only after a reload."
    )
    findings = reviews.get_by_role("list", name="Findings").get_by_role("listitem")
    expect(findings).to_have_count(2)
    expect(findings.first).to_contain_text(
        "Blocking web/src/list.tsx:42 The empty state is read once"
    )
    expect(findings.first).to_contain_text("A list emptied later shows nothing")
    expect(findings.nth(1)).to_contain_text("Not blocking web/src/list.css:3")
    expect(reviews.get_by_role("list", name="For the owner")).to_contain_text(
        "Agreed: the lists share one component."
    )
    expect(_entries(page).locator("h3")).to_have_text(
        ["Reading", "Build", f"Review of {commit[:7]}", "Now"]
    )

    # The pull request in the fake world has the review's comment, by the app.
    conversation = [
        each
        for each in repository(page.request.get("/devflow/api/fake/world").json())[
            "conversations"
        ]
        if each["number"] == pull["number"]
    ]
    assert len(conversation) == 1, conversation
    comments = conversation[0]["comments"]
    assert len(comments) == 1, comments
    assert comments[0]["author"] == APP_LOGIN
    assert comments[0]["body"].startswith(f"Review of {commit[:7]}: changes\n")
    assert (
        "- **Blocking.** `web/src/list.tsx:42` The empty state is read once."
        in comments[0]["body"]
    )

    # It is not on Waiting for you while it is fixed.
    page.goto("/devflow")
    expect(_waiting_row(page)).to_have_count(0)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(_entries(page).nth(2)).to_contain_text("Changes asked for")
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-review")


def test_a_cards_pictures_are_on_its_page_and_the_list(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    sizes = {"habits-desktop": (640, 400), "habits-phone": (195, 422)}
    project, ran = _built_card(
        page,
        base_url,
        devflow_world,
        pictures={
            "habits-desktop": png(640, 400, (96, 140, 200)),
            "habits-phone": png(195, 422, (200, 150, 90)),
        },
    )
    head = ran["head_commit"]

    # The pull request's body embeds each at its address on pr-screenshots.
    body = _opened(page)["body"]
    for name in sizes:
        assert (
            f"![{name}](https://github.com/{OWNER}/{NAME}/blob/pr-screenshots/"
            f"devflow/{CARD}/{head[:7]}/{name}.png?raw=true)"
        ) in body

    def shown(shots) -> None:
        """The one pair, under its page's name, each thumbnail read from
        Dev flow as the PNG the worker took, at most 160px high."""
        expect(shots.get_by_role("listitem")).to_have_count(1)
        expect(shots.get_by_text("habits", exact=True)).to_be_visible()
        for name, (width, _) in sizes.items():
            thumbnail = shots.get_by_role("img", name=name)
            expect(thumbnail).to_be_visible()
            page.wait_for_function(
                "img => img.complete && img.naturalWidth > 0",
                arg=thumbnail.element_handle(),
            )
            assert thumbnail.evaluate("img => img.naturalWidth") == width
            assert thumbnail.bounding_box()["height"] <= 160

    # The card's page: its Screenshots, of the head commit, paired desktop and
    # phone.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    panel = page.get_by_role("article", name="Screenshots")
    expect(panel).to_contain_text(f"Taken by the worker of {head[:7]}")
    shown(panel.get_by_role("list", name="Screenshots"))
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-screenshots")

    # One opens large, at its natural size.
    panel.get_by_role("button", name="habits-desktop").click()
    dialog = page.get_by_role("dialog", name="habits-desktop")
    large = dialog.get_by_role("img", name="habits-desktop")
    expect(large).to_be_visible()
    box = large.bounding_box()
    assert (box["width"], box["height"]) == sizes["habits-desktop"]
    dialog.get_by_role("button", name="Close").click()
    expect(page.get_by_role("dialog")).to_have_count(0)

    # And its item on Waiting for you, the same pair.
    page.goto("/devflow")
    card = _waiting_card(page)
    shown(card.get_by_role("list", name="Screenshots"))
    assert _axe_violations(page) == []
    capture(page, "devflow-card-waiting-screenshots")


def _fix_report() -> dict:
    """A fix's report in its form, with a reply to the review's finding."""
    report = _report()
    report["summary"] = (
        "Read the empty state on every change, with a test that empties the list."
    )
    report["replies"] = [
        {
            "finding": "The empty state is read once",
            "answer": "Fixed: it is read on every change, and list.test.tsx empties it.",
        }
    ]
    return report


def _fake_fix(page: Page, base_url: str, project: str, report: dict) -> dict:
    """The fake worker takes the project's next job, a fix of the card on its
    branch, and ends it with `report` and a passing check. What it answered."""
    fixed = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": report,
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 13 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 30, "output_tokens": 2104},
                    "num_turns": 14,
                },
                "duration_ms": 98000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert fixed.status == 200, fixed.text()
    assert fixed.json()["kind"] == "fix", fixed.json()
    return fixed.json()


def test_i_send_a_card_back_with_a_comment(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world)
    pull = _opened(page)

    # From Waiting for you, Send back with a comment.
    page.goto("/devflow")
    card = _waiting_card(page)
    card.get_by_role("button", name="Send back").click()
    said = "Name it EmptyList, as the other lists are named."
    card.get_by_role("textbox", name="What should the developer change?").fill(said)
    card.get_by_role("button", name="Send", exact=True).click()
    expect(page.get_by_text(f"Sent back #{pull['number']}")).to_be_visible()
    expect(_waiting_row(page)).to_have_count(0)

    # Posted as the owner on the card's pull request, never as the app.
    mine = [
        write
        for write in _writes(page)
        if write["kind"] == "comment" and write["by"] == "octo-owner"
    ]
    assert len(mine) == 1, mine
    assert mine[0]["number"] == pull["number"]
    assert mine[0]["body"] == said
    assert "purpose" not in mine[0]

    # The read that followed took it: the card is being fixed for his comment,
    # which is not a review round.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Fixing", exact=True).first).to_be_visible()
    expect(
        page.get_by_text("You commented on its pull request; a fix runs next")
    ).to_be_visible()

    # Its fix is the next job, on the card's branch, and leaves a new commit
    # that the reviewer reads next.
    fixed = _fake_fix(page, base_url, project, _fix_report())
    assert fixed["branch"] == ran["branch"]
    assert fixed["head_commit"] != ran["head_commit"]
    assert _staged_pull(page, pull["number"])["head_commit"] == fixed["head_commit"]
    page.reload()
    expect(page.get_by_text("Reviewing", exact=True).first).to_be_visible()


def test_a_card_with_changes_is_fixed_and_reviewed_again(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    read_once = {
        "blocking": True,
        "file": "web/src/list.tsx",
        "line": 42,
        "what": "The empty state is read once",
        "why": "A list emptied later shows nothing",
    }
    project, ran = _built_card(
        page, base_url, devflow_world, verdict=_verdict("changes", [read_once])
    )

    # Round 1's fix, and a review of its commit that asks for changes again.
    first = _fake_fix(page, base_url, project, _fix_report())
    assert first["branch"] == ran["branch"]
    no_test = {
        "blocking": True,
        "file": "",
        "line": 0,
        "what": "No test empties the list after it was shown",
        "why": "The bug comes back unseen",
    }
    again = _fake_review(page, base_url, project, _verdict("changes", [no_test]))
    assert again["head_commit"] == first["head_commit"]

    # In round 2, the card's page shows round 1: the review that sent it back,
    # the developer's reply, and the review of the commit it left.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Fixing", exact=True).first).to_be_visible()
    expect(
        page.get_by_text(
            "Round 2 of 3: the reviewer asked for changes; a fix runs next"
        )
    ).to_be_visible()
    one = page.get_by_role("listitem", name="Fix, round 1 of 3")
    expect(one).to_contain_text(
        f"The review of {ran['head_commit'][:7]} asked for changes."
    )
    replies = one.get_by_role("list", name="The developer's replies")
    expect(replies).to_contain_text("The empty state is read once")
    expect(replies).to_contain_text("Fixed: it is read on every change")
    expect(one).to_contain_text(
        f"It left {first['head_commit'][:7]}, and the review of it asked for changes."
    )
    # Oldest first: the reading, the build, its review, round 1, its review.
    expect(_entries(page).locator("h3")).to_have_text(
        [
            "Reading",
            "Build",
            f"Review of {ran['head_commit'][:7]}",
            "Fix, round 1 of 3",
            f"Review of {first['head_commit'][:7]}",
            "Now",
        ]
    )
    expect(_entries(page).nth(2)).to_contain_text(
        "for an older commit, so it does not count"
    )

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-fixing")

    # Round 2's fix, and a review that approves it: the card waits for the
    # owner, and its page shows both rounds.
    second = _fake_fix(page, base_url, project, _fix_report())
    _fake_review(page, base_url, project, _verdict("approve"))
    page.reload()
    expect(page.get_by_text("Waiting for you", exact=True).first).to_be_visible()
    expect(
        _entries(page).filter(
            has=page.get_by_role("heading", name=re.compile("^Fix, round"))
        )
    ).to_have_count(2)
    expect(page.get_by_role("listitem", name="Fix, round 2 of 3")).to_contain_text(
        f"It left {second['head_commit'][:7]}, and the review of it approved it."
    )

    # On Waiting for you, the approve on the commit the second fix left.
    page.goto("/devflow")
    card = _waiting_card(page)
    expect(card).to_contain_text("Approved")
    expect(card).to_contain_text(f"on {second['head_commit'][:7]}")


def test_a_card_page_of_no_project_is_not_found(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto(f"/devflow/projects/{'0' * 8}-0000-7000-8000-{'0' * 12}/cards/1")
    expect(page.get_by_text("There is no such card")).to_be_visible()


# Stops: a card that cannot be made to work stops, is marked on its issue and shows under Stopped
# with why, and the owner's comment runs it again; a project whose runs keep failing outside their
# cards says it cannot run them. See "Stops" in devflow/README.md.

TURNS = "it reached the most turns, 200 turns"
TWICE = f"Failed twice in a row with no new commit: The run failed: {TURNS}"
LOGGED_OUT = "Claude Code is not logged in with the token"


def _failure(in_card: bool, reason: str) -> dict:
    """A run that failed, in the card or outside it, as a worker sends one."""
    return {
        "outcome": "failed",
        "report": None,
        "failure": {"reason": reason, "in_card": in_card},
        "check": None,
        "auth_method": "oauth_token",
        "usage": {},
        "duration_ms": 1000,
        "stderr": "",
    }


def _fake_run(
    page: Page, base_url: str, project: str, result: dict, leave_no_commit=False
) -> dict:
    """The fake worker takes the project's next job and ends it with `result`;
    with `leave_no_commit`, nothing is pushed. What it answered."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": result,
            "leave_no_commit": leave_no_commit,
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def _stopped_region(page: Page):
    return page.get_by_role("region", name=re.compile("^Stopped"))


def test_a_stopped_card_tells_me_why_and_i_run_it_again(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project = _card_project(page, base_url, devflow_world)
    # The fake worker fails twice in the card, with nothing pushed between,
    # Claude Code's error output the last lines of each.
    failed = _failure(True, TURNS)
    failed["stderr"] = "Starting Claude Code\nError: Reached max turns (200)"
    for _ in range(2):
        _fake_run(page, base_url, project, failed, leave_no_commit=True)

    # The app marked it on its issue: the attention label and one comment.
    world = repository(page.request.get("/devflow/api/fake/world").json())
    issue = next(each for each in world["issues"] if each["number"] == CARD)
    assert "devflow-attention" in issue["labels"]
    said = [
        comment
        for conversation in world["conversations"]
        if conversation["number"] == CARD
        for comment in conversation["comments"]
        if comment["author"] == APP_LOGIN
    ]
    assert len(said) == 1, said
    assert said[0]["body"].startswith(f"Dev flow stopped this card: {TWICE}.")
    assert "Comment here to run it again." in said[0]["body"]

    # "Waiting for you" shows it under Stopped, with why, and no Merge.
    page.goto("/devflow")
    stopped = _item(page, CARD_TITLE, re.compile("^Stopped"))
    expect(stopped).to_be_visible()
    expect(stopped.get_by_text("Stopped", exact=True)).to_be_visible()
    expect(stopped).to_contain_text(f"{OWNER}/{NAME}")
    expect(stopped).to_contain_text(TWICE)
    expect(stopped.get_by_role("button", name="Merge")).to_have_count(0)
    expect(stopped.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/issues/{CARD}"
    )
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-inbox-stopped")

    # The card's page says why, and links back to its item.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("link", name="Decide in Waiting for you")).to_be_visible()
    expect(page.locator("[data-banner]")).to_contain_text("Needs you")
    expect(page.get_by_text(TWICE).first).to_be_visible()
    error = page.get_by_role("button", name="Show Claude Code's error output").last
    error.click()
    expect(page.get_by_text("Error: Reached max turns (200)").last).to_be_visible()
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-stopped")

    # Back to its item through the banner's link, which opens it on phone too.
    page.get_by_role("link", name="Decide in Waiting for you").click()
    expect(stopped).to_be_visible()

    # "Comment to run again" posts his comment on the card's issue, which queues it.
    stopped.get_by_role("button", name="Comment to run again").click()
    stopped.get_by_label("A comment runs it again.").fill(
        "Leave the sorting out and try again."
    )
    stopped.get_by_role("button", name="Send").click()
    expect(page.get_by_text(f"Commented on #{CARD}; it runs again")).to_be_visible()
    expect(_stopped_region(page)).to_have_count(0)
    cards = page.request.get(f"/devflow/api/projects/{project}/cards").json()["cards"]
    assert [card["state"] for card in cards if card["number"] == CARD] == ["queued"]
    world = repository(page.request.get("/devflow/api/fake/world").json())
    assert any(
        comment["author"] == OWNER_LOGIN
        and comment["body"] == "Leave the sorting out and try again."
        for conversation in world["conversations"]
        if conversation["number"] == CARD
        for comment in conversation["comments"]
    )


# The fake worker's id, the same in every project, which holds a job it keeps
# running; FakeWorkerEndpoints.WorkerId.
FAKE_WORKER = "0190a8f0-0000-7000-8000-00000000fa4e"
ON_ITS_OWN = 70
ON_ITS_OWN_TITLE = "Sort the list by name"


def _stopped_result() -> dict:
    """What a worker sends for a run the owner stopped: nothing judged, no
    report, failure or check."""
    return {
        "outcome": "stopped",
        "report": None,
        "failure": None,
        "check": None,
        "auth_method": "oauth_token",
        "usage": {"usage": {"input_tokens": 12, "output_tokens": 34}},
        "duration_ms": 5000,
        "stderr": "",
    }


def _looks_right(page: Page) -> None:
    """The look checks every page is held to, and no accessibility
    violation, once the fonts are in."""
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []


def test_i_stop_and_run_again(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    """A run I stop leaves its card "Stopped by you" under Stopped, beside a
    feature whose review I stopped, with Run again and Drop in place of a
    comment; Run again queues the card where its run started, and releases
    the feature. See "Stops" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    # A feature whose whole review I stopped: its worker ended it stopped.
    project, _ = _published_feature(page, base_url, devflow_world)
    held = _fake_run(page, base_url, project, _stopped_result())
    assert (held["kind"], held["card"]) == ("feature_review", 60), held

    # A card on its own beside it, read as ready, whose build the fake worker
    # takes and keeps running.
    staged = page.request.get("/devflow/api/fake/world").json()
    add_issue(staged, ON_ITS_OWN, ON_ITS_OWN_TITLE, labels=["devflow"])
    devflow_world(staged)
    _read(page, base_url, project)
    _fake_read(page, base_url, project, [_ready(ON_ITS_OWN)])
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert (taken.json()["kind"], taken.json()["card"]) == ("build", ON_ITS_OWN)
    run = taken.json()["job_id"]

    # I stop it through the route; the worker's next post to the run's log is
    # answered stop, and it ends the run stopped.
    stop = page.request.post(
        f"/devflow/api/projects/{project}/runs/{run}/stop",
        headers={"Origin": base_url},
    )
    assert stop.status == 202, stop.text()
    made = page.request.post(
        f"/devflow/api/projects/{project}/worker-key", headers={"Origin": base_url}
    )
    assert made.status == 200, made.text()
    key = made.json()["key"]
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        posted = worker.post(
            f"/devflow/api/worker/jobs/{run}/log",
            data={"worker_id": FAKE_WORKER, "lines": []},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert posted.status == 200, posted.text()
        assert posted.json() == {"stop": True}
        ended = worker.post(
            f"/devflow/api/worker/jobs/{run}/result",
            data={
                **_stopped_result(),
                "worker_id": FAKE_WORKER,
                "branch": taken.json()["branch"],
                "head_commit": None,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert ended.status == 204, ended.text()
    finally:
        worker.dispose()

    # Both wait under Stopped, said to be mine, and nothing is marked on the
    # card's issue.
    page.goto("/devflow")
    expect(page.get_by_role("tab", name="Waiting for you 2")).to_be_visible()
    stopped = _stopped_region(page)
    expect(
        stopped.get_by_role("link", name=re.compile("stopped by you"))
    ).to_have_count(2)
    if viewport == "phone":
        # On a phone the queue is a screen of its own, and it lists both.
        _looks_right(page)
        capture(page, "devflow-stopped-by-you")
    feature = _item(page, "The feature's branch", re.compile("^Stopped"))
    expect(feature.get_by_text("Stopped by you", exact=True)).to_be_visible()
    expect(feature.get_by_text("You stopped its review")).to_be_visible()
    expect(feature.get_by_role("button", name="Run again")).to_be_visible()
    expect(feature.get_by_role("button", name="Drop")).to_have_count(0)
    page.goto("/devflow")
    card = _item(page, ON_ITS_OWN_TITLE, re.compile("^Stopped"))
    expect(card.get_by_text("Stopped by you", exact=True)).to_be_visible()
    expect(card.get_by_role("button", name="Run again")).to_be_visible()
    expect(card.get_by_role("button", name="Drop")).to_be_enabled()
    expect(card.get_by_role("button", name="Comment to run again")).to_have_count(0)
    expect(card.get_by_role("button", name="Merge")).to_have_count(0)
    world = repository(page.request.get("/devflow/api/fake/world").json())
    issue = next(each for each in world["issues"] if each["number"] == ON_ITS_OWN)
    assert "devflow-attention" not in issue["labels"]
    if viewport == "desktop":
        # The queue with both, and the card I stopped open beside it.
        _looks_right(page)
        capture(page, "devflow-stopped-by-you")
    else:
        _looks_right(page)

    # Drop asks first; cancelled, nothing is sent.
    card.get_by_role("button", name="Drop").click()
    dialog = page.get_by_role("dialog", name=f"Drop #{ON_ITS_OWN}?")
    expect(
        dialog.get_by_text("Dev flow takes its label off and leaves its branch.")
    ).to_be_visible()
    dialog.get_by_role("button", name="Cancel").click()
    expect(dialog).to_have_count(0)

    # Run again queues the card where its build started.
    card.get_by_role("button", name="Run again").click()
    expect(page.get_by_text(f"Running #{ON_ITS_OWN} again")).to_be_visible()
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    cards = page.request.get(f"/devflow/api/projects/{project}/cards").json()["cards"]
    assert [each["state"] for each in cards if each["number"] == ON_ITS_OWN] == [
        "queued"
    ]

    # And the feature, from its page, says so and is released too.
    page.goto(f"/devflow/projects/{project}/features/60")
    expect(
        page.get_by_text(
            "Stopped by you. None of its jobs is handed out until you run it again."
        )
    ).to_be_visible()
    page.get_by_role("button", name="Run again").click()
    expect(page.get_by_text("Running #60 again")).to_be_visible()
    expect(page.get_by_text("Stopped by you.")).to_have_count(0)


SHARING = 80
SHARING_TITLE = "Share a plan by link"
DECKS = 81
DECKS_TITLE = "Decks and cards, the pages"


def _running_panel(page: Page, number: int, title: str) -> Locator:
    return page.get_by_role("article", name=f"#{number} {title}")


def test_running_now_shows_the_log(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    """Two busy workers are two panels on Running now, each with its log as
    it arrives; Stop now asks, the panel says it is stopping, and once the
    worker ends the run its card is "Stopped by you". See "The pages" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, SHARING, SHARING_TITLE, labels=["devflow"])
    add_issue(world, DECKS, DECKS_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    # Each card on its own is read by a reading of its own, the older first.
    assert _fake_read(page, base_url, project, [_ready(SHARING)])["card"] == SHARING
    assert _fake_read(page, base_url, project, [_ready(DECKS)])["card"] == DECKS

    # The fake worker takes the first card and keeps it running, its first
    # lines posted.
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert (taken.json()["kind"], taken.json()["card"]) == ("build", SHARING)
    run = taken.json()["job_id"]

    # A second worker, on the fake's recipe and image, takes the other card
    # through the worker API, and posts a line and its tokens so far.
    key = _make_key(page)
    fake = next(
        each
        for each in page.request.get(f"/devflow/api/projects/{project}/workers").json()[
            "workers"
        ]
        if each["name"] == "devflow-fake-worker"
    )
    second = "0190a8f0-0000-7000-8000-0000000000f7"
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        hello = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": second,
                "name": "platform-2",
                "recipe_version": fake["recipe_version"],
                "base_image": fake["base_image"],
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert hello.status == 204, hello.text()
        asked = worker.post(
            "/devflow/api/worker/jobs/next",
            data={"worker_id": second},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert asked.status == 200, asked.text()
        assert asked.json()["card"]["number"] == DECKS
        other = asked.json()["job_id"]

        def log(job: str, worker_id: str, lines: list[dict], usage=None) -> dict:
            body = {"worker_id": worker_id, "lines": lines}
            if usage is not None:
                body["usage"] = usage
            posted = worker.post(
                f"/devflow/api/worker/jobs/{job}/log",
                data=body,
                headers={"Authorization": f"Bearer {key}"},
            )
            assert posted.status == 200, posted.text()
            return posted.json()

        def line(seq: int, kind: str, text: str) -> dict:
            return {
                "seq": seq,
                "at": "2026-11-27T08:00:00Z",
                "kind": kind,
                "text": text,
            }

        log(
            other,
            second,
            [
                line(1, "system", "Cloning Adamniels/Personal-platform at main"),
                line(2, "text", "Reading the card and the diff, 14 files."),
                line(3, "tool", "Bash npm test"),
                line(4, "check", "Tests: 12 passed, 1 failed"),
            ],
            usage={
                "input_tokens": 1_000,
                "cache_creation_input_tokens": 20_000,
                "cache_read_input_tokens": 2_100_000,
                "output_tokens": 64_000,
            },
        )

        page.goto("/devflow/running")
        expect(page.get_by_role("tab", name="Running now 2")).to_have_attribute(
            "aria-selected", "true"
        )
        sharing = _running_panel(page, SHARING, SHARING_TITLE)
        decks = _running_panel(page, DECKS, DECKS_TITLE)
        expect(sharing.get_by_text("Building")).to_be_visible()
        expect(sharing.get_by_text("devflow-fake-worker")).to_be_visible()
        sharing_log = sharing.get_by_role("log", name=f"Log of #{SHARING}")
        expect(
            sharing_log.get_by_text("Reading the card and the code it touches.")
        ).to_be_visible()
        expect(decks.get_by_text("platform-2")).to_be_visible()
        expect(decks.get_by_text("85 k tokens · 2.1 M cache reads")).to_be_visible()
        expect(
            decks.get_by_role("log", name=f"Log of #{DECKS}").get_by_text(
                "Tests: 12 passed, 1 failed"
            )
        ).to_be_visible()

        # A line the fake's run posts now arrives while the page is open.
        log(run, FAKE_WORKER, [line(4, "text", "Writing the failing test first.")])
        expect(
            sharing_log.get_by_text("Writing the failing test first.")
        ).to_be_visible()

        _looks_right(page)
        capture(page, "devflow-running-now")

        # Stop now asks first; then the panel says it is stopping.
        sharing.get_by_role("button", name="Stop now").click()
        dialog = page.get_by_role("dialog", name="Stop this run?")
        expect(
            dialog.get_by_text(
                "What it did is kept on its branch, and it waits for you."
            )
        ).to_be_visible()
        dialog.get_by_role("button", name="Stop now").click()
        expect(sharing.get_by_text("Stopping…")).to_be_visible()

        # The worker's next post is answered stop, and it ends the run stopped.
        assert log(run, FAKE_WORKER, []) == {"stop": True}
        ended = worker.post(
            f"/devflow/api/worker/jobs/{run}/result",
            data={
                **_stopped_result(),
                "worker_id": FAKE_WORKER,
                "branch": taken.json()["branch"],
                "head_commit": None,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert ended.status == 204, ended.text()
    finally:
        worker.dispose()

    # Its panel goes, the other stays, and the card waits, stopped by me.
    expect(sharing).to_have_count(0)
    expect(decks).to_be_visible()
    expect(page.get_by_role("tab", name="Running now 1")).to_be_visible()
    page.goto("/devflow")
    card = _item(page, SHARING_TITLE, re.compile("^Stopped"))
    expect(card.get_by_text("Stopped by you", exact=True)).to_be_visible()


BLOCKED = 90
BLOCKED_TITLE = "Revoke a shared link"
BUILDING = 91
BUILDING_TITLE = "Share a plan by link"
BUSY = 92
BUSY_TITLE = "Scheduling"
FODR = 1002
TICK = 1003


def _another_repository(
    world: dict, repository_id: int, name: str, number: int, title: str
) -> dict:
    """Adds a repository of the owner's to the world's installation, holding
    project.json and one card on its own, `number`, labelled. Returns it."""
    added = {
        "id": repository_id,
        "owner": OWNER,
        "name": name,
        "pull_requests": [],
        "conversations": [],
        "commits": [],
        "files": [
            {
                "path": ".devflow/project.json",
                "branch": "main",
                "content": base64.b64encode(
                    json.dumps(
                        {"version": 1, "check": CHECK, "start": "", "url": ""}
                    ).encode()
                ).decode("ascii"),
            }
        ],
        "issues": [
            {
                "number": number,
                "title": title,
                "author": "octo-owner",
                "labels": ["devflow"],
                "body": f"The card {title}.",
                "url": f"https://github.com/{OWNER}/{name}/issues/{number}",
                "created_at": "2026-11-15T09:30:00+00:00",
                "open": True,
                "blocked_by": [],
                "edits": [],
            }
        ],
    }
    world["installations"][0]["repositories"].append(added)
    return added


def _connect(page: Page, base_url: str, repository_id: int) -> str:
    """Connects a staged repository and reads it now, as the loop would.
    Answers the project's id."""
    connected = page.request.post(
        "/devflow/api/projects",
        data={"installation_id": INSTALLATION, "repository_id": repository_id},
        headers={"Origin": base_url},
    )
    assert connected.status == 201, connected.text()
    project = connected.json()["id"]
    _read(page, base_url, project)
    return project


def test_next_up_says_why(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """Under the panels of Running now, the cards that come next, each with
    why it is not running yet, linked to where that is acted on: a card
    blocked by the one being built, one next for the busy worker, one of a
    stopped project and one of a project with no worker. See "The pages" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, BLOCKED, BLOCKED_TITLE, labels=["devflow"], blocked_by=[BUILDING])
    add_issue(world, BUILDING, BUILDING_TITLE, labels=["devflow"])
    add_issue(world, BUSY, BUSY_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _another_repository(world, FODR, "fodr", 12, "Dark mode")
    _another_repository(world, TICK, "tick", 9, "Sync conflict on wake")
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    fodr = _connect(page, base_url, FODR)
    stopped = _connect(page, base_url, TICK)

    # tick stops: a push on its main that Dev flow did not make.
    staged = page.request.get("/devflow/api/fake/world").json()
    tick = next(
        each
        for each in staged["installations"][0]["repositories"]
        if each["id"] == TICK
    )
    tick["activity"] = [
        {
            "id": 1,
            "branch": "main",
            "before": "0" * 40,
            "after": LANDED,
            "at": LATER,
            "kind": "push",
            "actor": "mallory",
        }
    ]
    devflow_world(staged)
    _read(page, base_url, stopped)

    # Personal-platform's free cards are read, and the fake worker takes the
    # first and keeps it running, so the other waits for it.
    assert _fake_read(page, base_url, project, [_ready(BUILDING)])["card"] == BUILDING
    assert _fake_read(page, base_url, project, [_ready(BUSY)])["card"] == BUSY
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert (taken.json()["kind"], taken.json()["card"]) == ("build", BUILDING)

    page.goto("/devflow/running")
    expect(_running_panel(page, BUILDING, BUILDING_TITLE)).to_be_visible()
    next_up = page.get_by_role("article", name=re.compile("^Next up"))
    expect(next_up.get_by_role("heading", name="Next up 4")).to_be_visible()
    table = next_up.get_by_role("table", name="Next up")
    expect(table.get_by_role("link", name=re.compile(r"^#\d+ "))).to_have_count(4)

    # A card blocked by another says so, linked to the card it waits for.
    waits = table.get_by_role(
        "link", name=f"Waits for #{BUILDING}, which it depends on"
    )
    expect(waits).to_have_attribute(
        "href", f"/devflow/projects/{project}/cards/{BUILDING}"
    )
    expect(
        table.get_by_text("Next for devflow-fake-worker, after its current run").filter(
            visible=True
        )
    ).to_be_visible()
    expect(
        table.get_by_role("link", name="The project is stopped: resume it")
    ).to_have_attribute("href", f"/devflow/projects/{stopped}")
    # One of a project with no worker says so, with a link to its page.
    no_worker = table.get_by_role("link", name="No worker is running for fodr")
    expect(no_worker).to_have_attribute("href", f"/devflow/projects/{fodr}")
    if viewport == "desktop":
        expect(table.get_by_role("columnheader")).to_have_text(
            ["Card", "Project", "State", "Why it is not running"]
        )
    else:
        # On a phone a list: the card, its project and state, then why.
        expect(table.get_by_role("columnheader")).to_have_text(["Card"])
        expect(table.get_by_text("fodr · To read").filter(visible=True)).to_be_visible()

    _looks_right(page)
    capture(page, "devflow-next-up")

    no_worker.click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{fodr}")
    expect(page.get_by_role("heading", name="fodr", exact=True)).to_be_visible()


def _thin(number: int, missing: list[str]) -> dict:
    """A card read as too thin to build, lacking `missing`."""
    return {
        "number": number,
        "outcome": "too_thin",
        "questions": [],
        "missing": missing,
    }


THIN = ["Which page shows the list", "What an empty list says"]


def test_a_thin_card_is_refused_and_read_again(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """A card read as too thin is refused: marked on its issue, listed under
    Refused on "Waiting for you" with what is missing, and read again once I
    edit it. See "Reading a card" under "Jobs" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    _fake_read(page, base_url, project, [_thin(CARD, THIN)])

    # The app marked it on its issue: the attention label and one comment
    # listing what is missing.
    world = repository(page.request.get("/devflow/api/fake/world").json())
    issue = next(each for each in world["issues"] if each["number"] == CARD)
    assert "devflow-attention" in issue["labels"]
    said = [
        comment
        for conversation in world["conversations"]
        if conversation["number"] == CARD
        for comment in conversation["comments"]
        if comment["author"] == APP_LOGIN
    ]
    assert len(said) == 1, said
    assert said[0]["body"].startswith(
        "Dev flow read this card and will not build it as it is written"
    )
    assert "- Which page shows the list\n- What an empty list says\n" in said[0]["body"]

    # "Waiting for you" lists it under Refused, with what is missing and its
    # issue, and counts it.
    page.goto("/devflow")
    refused = _item(page, CARD_TITLE, re.compile("^Refused"))
    expect(refused).to_be_visible()
    expect(refused).to_contain_text(f"{OWNER}/{NAME}")
    missing = refused.get_by_role("list", name="What is missing")
    expect(missing.get_by_role("listitem")).to_have_text(THIN)
    expect(refused.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/issues/{CARD}"
    )
    expect(refused.get_by_role("button")).to_have_count(0)
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    _look(page, "devflow-waiting-refused")

    # My edit of it on GitHub reads it again; read as ready, it is Queued, its
    # label comes off and it no longer waits for me.
    staged = page.request.get("/devflow/api/fake/world").json()
    issue = next(
        each for each in repository(staged)["issues"] if each["number"] == CARD
    )
    issue["body"] = 'Show "No habits yet" on the habits list when it is empty.'
    issue["edits"].append(edit("edit-1", OWNER_LOGIN, at=LATER))
    devflow_world(staged)
    _read(page, base_url, project)
    ran = _fake_read(page, base_url, project, [_ready(CARD)])
    assert ran["card"] == CARD
    cards = page.request.get(f"/devflow/api/projects/{project}/cards").json()["cards"]
    assert [card["state"] for card in cards if card["number"] == CARD] == ["queued"]
    world = repository(page.request.get("/devflow/api/fake/world").json())
    issue = next(each for each in world["issues"] if each["number"] == CARD)
    assert "devflow-attention" not in issue["labels"]
    page.goto("/devflow")
    expect(page.get_by_role("region", name=re.compile("^Refused"))).to_have_count(0)
    page.goto(f"/devflow/projects/{project}")
    expect(
        page.get_by_role("table", name="Cards on their own")
        .locator("tbody tr")
        .filter(has_text=CARD_TITLE)
    ).to_contain_text("Queued")


def test_an_edited_card_is_read_again(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    """A card I edit after it was read, before it starts, is read again,
    alone, as it is now: it shows Reading while a worker reads it, then Queued,
    and its page shows both readings. See "Reading a card" under "Jobs" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    _fake_read(page, base_url, project, [_ready(CARD)])

    # I edit it on GitHub before anything is built from it.
    edited = 'Show "No habits yet" on the habits list when it is empty.'
    staged = page.request.get("/devflow/api/fake/world").json()
    issue = next(
        each for each in repository(staged)["issues"] if each["number"] == CARD
    )
    issue["body"] = edited
    issue["edits"].append(edit("edit-1", OWNER_LOGIN, at=LATER))
    devflow_world(staged)
    _read(page, base_url, project)

    # A worker asking for work is handed its reading again, of it alone, as it
    # is now, not its build.
    key = _make_key(page)
    command = (
        page.get_by_role("region", name="Build and start the worker")
        .locator("code")
        .inner_text()
    )
    image = re.search(r"--build-arg DEVFLOW_BASE=(\S+) ", command).group(1)
    worker_id = "0190a8f0-0000-7000-8000-0000000000f5"
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        hello = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": worker_id,
                "name": "reader-worker-2",
                "recipe_version": "1",
                "base_image": image,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert hello.status == 204, hello.text()
        _pass_the_check(worker, key, worker_id)
        asked = worker.post(
            "/devflow/api/worker/jobs/next",
            data={"worker_id": worker_id},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert asked.status == 200, asked.text()
        job = asked.json()
        assert (job["kind"], job["card"]["number"]) == ("read", CARD)
        assert [card["body"] for card in job["to_read"]["cards"]] == [edited]
        assert "## Read again" in job["brief"]

        # While it is read, the card shows Reading.
        page.reload()
        row = (
            page.get_by_role("table", name="Cards on their own")
            .locator("tbody tr")
            .filter(has_text=CARD_TITLE)
        )
        expect(row).to_contain_text("Reading")

        result = worker.post(
            f"/devflow/api/worker/jobs/{job['job_id']}/result",
            data={
                "worker_id": worker_id,
                "outcome": "succeeded",
                "report": _reading([_ready(CARD)]),
                "failure": None,
                "check": None,
                "branch": job["branch"],
                "head_commit": None,
                "auth_method": "oauth_token",
                "usage": {"num_turns": 3},
                "duration_ms": 30000,
                "stderr": "",
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert result.status == 204, result.text()
    finally:
        worker.dispose()

    # Read again as ready, it is Queued.
    page.reload()
    expect(
        page.get_by_role("table", name="Cards on their own")
        .locator("tbody tr")
        .filter(has_text=CARD_TITLE)
    ).to_contain_text("Queued")

    # Its page shows both readings: the newest, and the one before it under
    # Earlier readings, and two readings among its runs.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    reading = page.get_by_role("article", name="The reading")
    expect(reading).to_contain_text("Ready to build")
    reading.get_by_role("button", name="Show the earlier reading").click()
    earlier = reading.get_by_role("list", name="Earlier readings")
    expect(earlier.get_by_role("listitem", name=re.compile("^Read on "))).to_have_count(
        1
    )
    expect(earlier).to_contain_text("Ready")
    expect(_entries(page).locator("h3")).to_have_text(["Reading", "Reading", "Now"])
    _look(page, "devflow-card-read-again")


def test_a_project_that_cannot_run_its_cards_says_so(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project = _card_project(page, base_url, devflow_world)
    # Six runs in a row fail outside the card, each once the project's wait is over.
    for failure in range(6):
        if failure > 0:
            ended = page.request.post(
                "/devflow/api/fake/end-wait",
                data={"project_id": project},
                headers={"Origin": base_url},
            )
            assert ended.status == 204, ended.text()
        _fake_run(page, base_url, project, _failure(False, LOGGED_OUT))
    message = f"Dev flow cannot run {NAME}'s cards: {LOGGED_OUT}"

    page.goto("/devflow")
    failing = _item(page, message, re.compile("^Stopped"))
    expect(failing).to_be_visible()
    expect(failing.get_by_text("Cannot run", exact=True)).to_be_visible()
    expect(failing).to_contain_text("6 runs in a row failed outside their cards")
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-project-failing")

    # The project's page says it too.
    page.goto(f"/devflow/projects/{project}")
    workers = page.get_by_role("article", name="Workers")
    expect(workers.get_by_text(message)).to_be_visible()
    assert_nothing_scrolls_sideways(page)
    capture(page, "devflow-project-cannot-run")


# The merge check: an approved card merges as the app when everything holds, or
# waits for the owner with why. See "The merge check" in devflow/README.md.


def _stage_ci(page: Page, devflow_world, commit: str, check_runs: list[dict]) -> None:
    """Stages `check_runs` on the commit in the fake world, as CI posting them
    would leave GitHub, keeping everything else the world holds."""
    world = page.request.get("/devflow/api/fake/world").json()
    repository(world)["commits"] = [
        each for each in repository(world)["commits"] if each["id"] != commit
    ] + [{"id": commit, "statuses": [], "check_runs": check_runs}]
    devflow_world(world)


QUESTIONS = [
    {
        "question": "Is the list sorted by name or by date?",
        "recommendation": "By date, newest first",
        "why": "The habits page beside it sorts so",
    },
    {
        "question": "Does an empty list show a sentence or nothing?",
        "recommendation": "A sentence saying there are no habits yet",
        "why": "An empty page looks broken",
    },
    {
        "question": "Is the count shown beside the heading?",
        "recommendation": "Yes, in the mono font",
        "why": "Every group on the platform shows its count so",
    },
]


def test_i_answer_a_cards_questions(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """With questions on, as a new project has them, a card read with
    questions waits for me under "Questions before starting" on "Waiting for
    you". I accept two recommendations and write the third answer myself, and
    send them: they are posted on the card's issue as my comment, and the card
    is Queued. See "Reading a card" under "Jobs" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, project)
    _fake_read(
        page,
        base_url,
        project,
        [
            {
                "number": CARD,
                "outcome": "questions",
                "questions": QUESTIONS,
                "missing": [],
            }
        ],
    )

    # Nothing is posted on the card while it waits for me.
    world = repository(page.request.get("/devflow/api/fake/world").json())
    assert not [
        conversation
        for conversation in world["conversations"]
        if conversation["number"] == CARD and conversation["comments"]
    ]

    # "Waiting for you" lists it first, under "Questions before starting",
    # each question with the recommendation and why, and counts it.
    page.goto("/devflow")
    group = page.get_by_role("region", name=re.compile("^Questions before starting"))
    asked = _item(page, CARD_TITLE, re.compile("^Questions before starting"))
    expect(asked).to_be_visible()
    expect(asked).to_contain_text(f"{OWNER}/{NAME}")
    questions = asked.get_by_role("group")
    expect(questions).to_have_count(3)
    for at, question in enumerate(QUESTIONS):
        expect(questions.nth(at)).to_contain_text(question["question"])
        expect(questions.nth(at)).to_contain_text(question["recommendation"])
        expect(questions.nth(at)).to_contain_text(question["why"])
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()
    send = asked.get_by_role("button", name="Send answers")
    expect(send).to_be_disabled()

    # One answer written; the look of the three questions with it.
    questions.nth(1).get_by_role("button", name="Write another answer").click()
    questions.nth(1).get_by_label("Another answer").fill(
        "Nothing at all, the page hides the list"
    )
    _look(page, "devflow-waiting-questions")

    # The other two accepted, and sent.
    questions.nth(0).get_by_role("button", name="Accept").click()
    questions.nth(2).get_by_role("button", name="Accept").click()
    expect(send).to_be_enabled()
    send.click()
    expect(page.get_by_text(f"Answers sent; #{CARD} is queued")).to_be_visible()
    expect(group).to_have_count(0)

    # The card is Queued, and my answers are on its issue as my comment.
    page.goto(f"/devflow/projects/{project}")
    row = (
        page.get_by_role("table", name="Cards on their own")
        .locator("tbody tr")
        .filter(has=page.get_by_text(f"#{CARD}", exact=True))
    )
    expect(row).to_contain_text("Queued")
    world = repository(page.request.get("/devflow/api/fake/world").json())
    mine = [
        comment
        for conversation in world["conversations"]
        if conversation["number"] == CARD
        for comment in conversation["comments"]
        if comment["author"] == OWNER_LOGIN
    ]
    assert len(mine) == 1, mine
    body = mine[0]["body"]
    assert (
        "1. Is the list sorted by name or by date?\n"
        "   - Answer: By date, newest first (recommended)\n"
    ) in body
    assert (
        "2. Does an empty list show a sentence or nothing?\n"
        "   - Answer: Nothing at all, the page hides the list\n"
    ) in body
    assert "   - Answer: Yes, in the mono font (recommended)\n" in body


def _merges_by_itself(page: Page, base_url: str, project: str) -> None:
    """Sets the project to merge an approved card by itself, never waiting for
    me, which the tests of what follows a review are about. A new project's
    own is "Only I merge", test_i_choose_a_preset."""
    _waits_for_me(page, base_url, project, "never")


def _waits_for_me(
    page: Page, base_url: str, project: str, waits: str, **changes
) -> None:
    """Sets the project's Pull requests wait for you, and any other setting
    named in `changes`, keeping every other as it is."""
    current = page.request.get(f"/devflow/api/projects/{project}/settings")
    assert current.status == 200, current.text()
    settings = current.json()
    saved = page.request.put(
        f"/devflow/api/projects/{project}/settings",
        data={
            **settings,
            "trusted_logins": [entry["login"] for entry in settings["trusted_logins"]],
            "waits_for_owner": waits,
            **changes,
        },
        headers={"Origin": base_url},
    )
    assert saved.status == 200, saved.text()


def test_an_approved_card_merges_by_itself(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world, review=False, green=True)
    pull = _opened(page)

    # Reviewing, then the reviewer approves, and with the project on never and
    # CI green the card is Done without the owner.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Reviewing", exact=True).first).to_be_visible()
    _fake_review(page, base_url, project, _verdict("approve"))
    page.reload()
    expect(page.get_by_text("Done", exact=True).first).to_be_visible()
    merge = page.get_by_role("article", name="At a glance")
    expect(merge.get_by_role("heading", name="The merge")).to_be_visible()
    expect(merge).to_contain_text("Into main, by Dev flow as the app.")
    expect(merge).to_contain_text(f"approved {ran['head_commit'][:7]}")

    # The fake world shows the merge, by the app with a key for merging,
    # naming the approved commit, and never as the owner.
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert len(merges) == 1, merges
    assert merges[0]["by"] == APP_LOGIN
    assert merges[0]["purpose"] == "merge"
    assert merges[0]["commit"] == ran["head_commit"]
    assert _staged_pull(page, pull["number"])["state"] == "merged"

    # Nothing waits for the owner on its account.
    page.goto("/devflow")
    expect(_waiting_row(page)).to_have_count(0)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("heading", name="The merge")).to_be_visible()
    expect(page.get_by_role("link", name="Decide in Waiting for you")).to_have_count(0)
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-card-merged")


def test_i_choose_a_preset(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world, review=False, green=True)
    _opened(page)
    # Back to what a new project starts as, questions on and always.
    _waits_for_me(page, base_url, project, "always", questions=True)

    page.goto(f"/devflow/projects/{project}")
    header = page.locator("header").filter(has=page.get_by_role("heading", level=2))
    expect(header).to_contain_text("Only I merge")
    settings = _edit_settings(page)
    alone = settings.get_by_role("article", name="How much happens alone")
    matched = settings.get_by_role("article", name="These settings").locator("p").first
    expect(matched).to_have_text("Only I merge")
    expect(alone.get_by_role("radio", name="Only I merge")).to_be_checked()

    # Guided fills in its settings in the form, and nothing is saved yet.
    alone.get_by_role("radio", name="Guided").check()
    expect(matched).to_have_text("Guided")
    expect(alone.get_by_label("Pull requests wait for you")).to_have_value("sometimes")
    expect(
        alone.get_by_role("switch", name="Questions before starting")
    ).not_to_be_checked()
    conditions = alone.get_by_role("group", name="Waits for you when")
    for condition in (
        "The card or its feature is marked for your review",
        "The change touches a path on the list",
        "The agent made a decision for you",
    ):
        expect(conditions.get_by_label(condition)).to_be_checked()
    saved = page.request.get(f"/devflow/api/projects/{project}/settings").json()
    assert saved["preset"] == "only_i_merge"
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-settings")

    # One covered setting changed by hand is Custom, with no preset chosen.
    conditions.get_by_label("The agent made a decision for you").uncheck()
    expect(matched).to_have_text("Custom")
    expect(alone.get_by_role("radio", checked=True)).to_have_count(0)

    # Fully automatic, saved: the project's heading shows it once back, and so
    # does a fresh load.
    alone.get_by_role("radio", name="Fully automatic").check()
    expect(matched).to_have_text("Fully automatic")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    _back_to_project(page)
    expect(header).to_contain_text("Fully automatic")
    page.reload()
    expect(header).to_contain_text("Fully automatic")
    expect(
        _edit_settings(page).get_by_label("Pull requests wait for you")
    ).to_have_value("never")
    _back_to_project(page)
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-project-preset")

    # The card merges without waiting for me once the reviewer approves.
    _fake_review(page, base_url, project, _verdict("approve"))
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Done", exact=True).first).to_be_visible()
    expect(page.get_by_role("article", name="At a glance")).to_contain_text(
        "Into main, by Dev flow as the app."
    )
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert [merge["commit"] for merge in merges] == [ran["head_commit"]]
    page.goto("/devflow")
    expect(_waiting_row(page)).to_have_count(0)


def test_with_always_an_approved_card_waits_for_me(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, _ = _built_card(page, base_url, devflow_world, review=False, green=True)
    _waits_for_me(page, base_url, project, "always")
    _fake_review(page, base_url, project, _verdict("approve"))

    # It waits on Waiting for you, saying why, with Merge offered as his own.
    page.goto("/devflow")
    card = _waiting_card(page)
    expect(card).to_be_visible()
    expect(card).to_contain_text("This project waits for you on every pull request")
    expect(card.get_by_role("button", name="Merge")).to_be_enabled()
    assert [write for write in _writes(page) if write["kind"] == "merge"] == []

    # Its page says the same.
    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_text("Waiting for you", exact=True).first).to_be_visible()
    expect(
        page.get_by_text("This project waits for you on every pull request")
    ).to_be_visible()


# "Sometimes": a card's pull request waits for the owner only when a condition
# he ticked holds. See "The merge check" in devflow/README.md.

OTHER_CARD = 22


def _build(page: Page, base_url: str, project: str, decisions: list[dict]) -> dict:
    """The fake worker takes the project's next job, a build, and ends it with
    a passing check and a report holding `decisions`. What it answered."""
    built = _fake_run(
        page,
        base_url,
        project,
        {
            "outcome": "succeeded",
            "report": {**_report(), "decisions": decisions},
            "failure": None,
            "check": {"exit_code": 0, "output": "Tests: 12 passed"},
            "auth_method": "oauth_token",
            "usage": {"usage": {"input_tokens": 41, "output_tokens": 6113}},
            "duration_ms": 184213,
            "stderr": "",
        },
    )
    assert built["kind"] == "build", built
    return built


def test_a_card_marked_for_my_review_waits(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    # The marked card is the older, so it is built first.
    add_issue(
        world,
        CARD,
        CARD_TITLE,
        labels=["devflow", "devflow-review"],
        created_at="2026-11-15T09:00:00+00:00",
    )
    add_issue(world, OTHER_CARD, "Count the habits", labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _waits_for_me(page, base_url, project, "sometimes")
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    # Each card on its own is read alone, the older first.
    _fake_read(page, base_url, project, [_ready(CARD)])
    _fake_read(page, base_url, project, [_ready(OTHER_CARD)])

    # Both built with nothing marked for the owner, CI green, and approved.
    plain = [decision | {"for_owner": False} for decision in _report()["decisions"]]
    marked = _build(page, base_url, project, plain)
    _stage_ci(page, devflow_world, marked["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    other = _build(page, base_url, project, plain)
    assert other["head_commit"] != marked["head_commit"]
    _stage_ci(page, devflow_world, other["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))

    # The card with the review label waits on Waiting for you, saying why,
    # with Merge offered as his own.
    page.goto("/devflow")
    card = _waiting_card(page)
    expect(card).to_have_count(1)
    expect(card).to_contain_text(CARD_TITLE)
    expect(card).to_contain_text("This card is marked for your review")
    expect(card.get_by_role("button", name="Merge")).to_be_enabled()

    # The one without it merged by itself, as the app, naming its commit.
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert len(merges) == 1, merges
    assert merges[0]["by"] == APP_LOGIN
    assert merges[0]["commit"] == other["head_commit"]
    page.goto(f"/devflow/projects/{project}/cards/{OTHER_CARD}")
    expect(page.get_by_text("Done", exact=True).first).to_be_visible()


def test_i_set_the_models_and_limits(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    register_and_log_in(page, invite_code, base_url)
    _card_project(page, base_url, devflow_world)
    settings = _edit_settings(page)
    runs = settings.get_by_role("article", name="Runs", exact=True)

    # A new project's: opus for each job, 90 minutes, 200 turns, 25 USD, and 3
    # fix rounds a card and 3 a feature.
    for label, value in (
        ("Reading model", "opus"),
        ("Building model", "opus"),
        ("Reviewing model", "opus"),
        ("Minutes a run may take", "90"),
        ("Turns a run may take", "200"),
        ("Spend per run, USD", "25"),
        ("Fix rounds a card gets", "3"),
        ("Fix rounds a feature gets", "3"),
    ):
        expect(runs.get_by_label(label)).to_have_value(value)

    # A time Dev flow refuses is named under its field, and nothing is saved.
    runs.get_by_label("Minutes a run may take").fill("5")
    settings.get_by_role("button", name="Save").click()
    expect(runs.get_by_label("Minutes a run may take")).to_have_attribute(
        "aria-invalid", "true"
    )
    expect(runs.get_by_text("must be from 10 to 240")).to_be_visible()

    runs.get_by_label("Building model").select_option("sonnet")
    runs.get_by_label("Reviewing model").select_option("haiku")
    runs.get_by_label("Minutes a run may take").fill("45")
    runs.get_by_label("Turns a run may take").fill("300")
    runs.get_by_label("Spend per run, USD").fill("40")
    runs.get_by_label("Fix rounds a card gets").fill("1")
    runs.get_by_label("Fix rounds a feature gets").fill("0")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(runs.get_by_label("Minutes a run may take")).not_to_have_attribute(
        "aria-invalid", "true"
    )
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
        check(page)
    assert _axe_violations(page) == []
    runs.scroll_into_view_if_needed()
    capture(page, "devflow-settings-runs")
    if viewport == "phone":
        # The full page at phone width puts the sticky Save bar and the nav
        # over the card's last fields, so the spend field and its help as
        # they are seen, scrolled clear of them.
        runs.get_by_label("Spend per run, USD").evaluate(
            "field => field.scrollIntoView({block: 'center'})"
        )
        page.screenshot(
            path=str(SCREENSHOTS / "devflow-settings-runs-detail-phone.png")
        )

    # Kept: a fresh load reads them back.
    page.reload()
    runs = _edit_settings(page).get_by_role("article", name="Runs", exact=True)
    for label, value in (
        ("Reading model", "opus"),
        ("Building model", "sonnet"),
        ("Reviewing model", "haiku"),
        ("Minutes a run may take", "45"),
        ("Turns a run may take", "300"),
        ("Spend per run, USD", "40"),
        ("Fix rounds a card gets", "1"),
        ("Fix rounds a feature gets", "0"),
    ):
        expect(runs.get_by_label(label)).to_have_value(value)

    # The next job carries them: a worker on the image released asks for work
    # and is handed the card's build on the building model, with the time, the
    # turns and the spend set.
    _back_to_project(page)
    key = _make_key(page)
    command = (
        page.get_by_role("region", name="Build and start the worker")
        .locator("code")
        .inner_text()
    )
    image = re.search(r"--build-arg DEVFLOW_BASE=(\S+) ", command).group(1)
    worker = playwright.request.new_context(base_url=base_url, ignore_https_errors=True)
    try:
        worker_id = "0190a8f0-0000-7000-8000-0000000000f3"
        hello = worker.post(
            "/devflow/api/worker/hello",
            data={
                "worker_id": worker_id,
                "name": "platform-worker-1",
                "recipe_version": "1",
                "base_image": image,
            },
            headers={"Authorization": f"Bearer {key}"},
        )
        assert hello.status == 204, hello.text()
        _pass_the_check(worker, key, worker_id)
        asked = worker.post(
            "/devflow/api/worker/jobs/next",
            data={"worker_id": worker_id},
            headers={"Authorization": f"Bearer {key}"},
        )
        assert asked.status == 200, asked.text()
        job = asked.json()
    finally:
        worker.dispose()
    assert (job["kind"], job["card"]["number"]) == ("build", CARD)
    assert job["model"] == "sonnet"
    assert job["limits"] == {"turns": 300, "budget_usd": 40, "minutes": 45}


def test_i_set_sometimes(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    settings = _edit_settings(page)

    # Sometimes shows its conditions, all three ticked, and an empty list of
    # paths; the review label it reads is with the other labels.
    expect(settings.get_by_label("Paths")).to_have_count(0)
    settings.get_by_label("Pull requests wait for you").select_option("sometimes")
    conditions = settings.get_by_role("group", name="Waits for you when")
    for condition in (
        "The card or its feature is marked for your review",
        "The change touches a path on the list",
        "The agent made a decision for you",
    ):
        expect(conditions.get_by_label(condition)).to_be_checked()
    expect(settings.get_by_label("Review label")).to_have_value("devflow-review")
    expect(settings.get_by_label("Paths")).to_have_value("")

    # A pattern Dev flow refuses is named under the list, and nothing is saved.
    settings.get_by_label("Paths").fill("!src/**")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_label("Paths")).to_have_attribute("aria-invalid", "true")

    conditions.get_by_label("The agent made a decision for you").uncheck()
    settings.get_by_label("Paths").fill("src/auth/**\ninfra/hosted/compose.yaml")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(settings.get_by_label("Paths")).not_to_have_attribute("aria-invalid", "true")
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
        check(page)
    assert _axe_violations(page) == []
    settings.scroll_into_view_if_needed()
    capture(page, "devflow-settings-sometimes")

    # Kept: a fresh load reads them back.
    page.reload()
    settings = _edit_settings(page)
    expect(settings.get_by_label("Pull requests wait for you")).to_have_value(
        "sometimes"
    )
    conditions = settings.get_by_role("group", name="Waits for you when")
    expect(
        conditions.get_by_label("The agent made a decision for you")
    ).not_to_be_checked()
    expect(settings.get_by_label("Paths")).to_have_value(
        "src/auth/**\ninfra/hosted/compose.yaml"
    )


def test_a_decision_for_me_becomes_an_issue(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, _ = _built_card(page, base_url, devflow_world, green=True)
    pull = _opened(page)

    # The card merged, and the decision the agent marked for the owner became
    # an issue by the app, with the attention label.
    issues = [
        issue
        for issue in repository(page.request.get("/devflow/api/fake/world").json())[
            "issues"
        ]
        if issue["author"] == APP_LOGIN
    ]
    assert len(issues) == 1, issues
    issue = issues[0]
    assert issue["title"] == f"Decision in #{pull['number']}: Show it on every list"
    assert issue["labels"] == ["devflow-attention"]

    # Under Decisions to look at on Waiting for you, with what was decided,
    # the alternative, why and the reviewer's view.
    page.goto("/devflow")
    decisions = page.get_by_role("region", name=re.compile("Decisions to look at"))
    decision = _item(page, "Show it on every list", re.compile("Decisions to look at"))
    expect(decision).to_be_visible()
    expect(decision).to_contain_text("Only on habits")
    expect(decision).to_contain_text("The lists look the same")
    expect(decision).to_contain_text("Agreed: the lists share one component.")
    expect(decision).to_contain_text(f"{OWNER}/{NAME}")
    expect(
        decision.get_by_role("link", name="Open the issue on GitHub")
    ).to_have_attribute("href", issue["url"])
    expect(page.get_by_role("tab", name="Waiting for you 1")).to_be_visible()

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-attention-issue")

    # Closed on GitHub, it is gone at the next read.
    world = page.request.get("/devflow/api/fake/world").json()
    for each in repository(world)["issues"]:
        if each["number"] == issue["number"]:
            each["open"] = False
    devflow_world(world)
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    page.reload()
    expect(decisions.get_by_role("link")).to_have_count(0)


def test_a_features_card_lands_in_its_branch(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A labelled feature of two cards, and the project's .devflow/project.json.
    world = one_repository()
    add_issue(
        world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61, 62]
    )
    add_issue(world, 61, "Make the feature's branch")
    add_issue(world, 62, "Close a landed card's issue")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61), _ready(62)])

    # The fake worker builds the first card with a passing check, on a branch
    # of its own from the feature's, and its pull request goes into the
    # feature's branch. The project is read, so the feature's branch is watched.
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 41, "output_tokens": 6113},
                    "num_turns": 23,
                },
                "duration_ms": 184213,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    assert ran.json()["card"] == 61
    pull = _opened(page)
    assert pull["base_branch"] == FEATURE_BRANCH
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # CI green and the reviewer's approve: with the project on never, Dev flow
    # merges it into the feature's branch as the app.
    _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert len(merges) == 1, merges
    assert (merges[0]["by"], merges[0]["purpose"]) == (APP_LOGIN, "merge")
    assert merges[0]["commit"] == ran.json()["head_commit"]

    # Its issue is closed in the fake world, after the app's one comment
    # saying where it landed.
    staged = repository(page.request.get("/devflow/api/fake/world").json())
    card = next(issue for issue in staged["issues"] if issue["number"] == 61)
    assert card["open"] is False
    said = [
        comment["body"]
        for conversation in staged["conversations"]
        if conversation["number"] == 61
        for comment in conversation["comments"]
        if comment["author"] == APP_LOGIN
    ]
    landed = (
        f"Built and merged into the feature's branch `{FEATURE_BRANCH}` in "
        f"#{pull['number']}. It reaches `main` with its feature, #60."
    )
    assert said == [landed]

    # The second card is handed out next, from the feature's branch.
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert taken.json()["card"] == 62

    # The project's page shows the first card Done and the second Building.
    page.goto(f"/devflow/projects/{project}")
    feature = page.get_by_role("list", name="Features", exact=True).locator(
        ":scope > li"
    )
    rows = feature.get_by_role("list", name="Cards of #60", exact=True).get_by_role(
        "listitem"
    )
    expect(rows).to_have_count(2)
    expect(rows.nth(0)).to_contain_text("#61")
    expect(rows.nth(0)).to_contain_text("Done")
    expect(rows.nth(1)).to_contain_text("#62")
    expect(rows.nth(1)).to_contain_text("Building")

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, "devflow-feature-card-landed")


def _landed_feature(
    page: Page, base_url: str, devflow_world, next_running: bool = True
) -> str:
    """Connects a repository with a labelled feature of three cards, 61, 62 and
    63 under 60, and the project's .devflow/project.json; the fake worker
    builds 61 with `_report()`, which marks a decision for the owner, and two
    pictures of one page, CI passes on it and the reviewer approves, so Dev
    flow merges it into the feature's branch; then 62 is handed out and kept
    running, unless `next_running` is false, when it stays queued. The
    project's id."""
    world = one_repository()
    add_issue(
        world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61, 62, 63]
    )
    add_issue(world, 61, "Read the sub-issues")
    add_issue(world, 62, "Show each feature")
    add_issue(world, 63, "The feature's page")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61), _ready(62), _ready(63)])

    pictures = {
        "plan-desktop": png(640, 400, (96, 140, 200)),
        "plan-phone": png(195, 422, (200, 150, 90)),
    }
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 41, "output_tokens": 6113},
                    "num_turns": 23,
                },
                "duration_ms": 184213,
                "stderr": "",
                "screenshots": {"names": list(pictures), "note": None},
            },
            "pictures": {
                name: base64.b64encode(content).decode()
                for name, content in pictures.items()
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    assert ran.json()["card"] == 61
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    assert len([write for write in _writes(page) if write["kind"] == "merge"]) == 1
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    if not next_running:
        return project

    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert taken.json()["card"] == 62
    return project


def _feature_header(page: Page) -> Locator:
    """A feature's page's header: its state with its caption, its title, its
    reason and its links. A card's label is no header, so it is the one."""
    return page.locator("header").filter(has=page.locator("h2"))


def _every_review_and_round(page: Page) -> Locator:
    """Every review and round of a feature, opened in place in its side
    column, newest first."""
    panel = page.get_by_role("article", name="Reviews and rounds")
    panel.get_by_role("button", name="Read every review and round").click()
    return panel.get_by_role("list", name="Every review and round")


def _look(page: Page, name: str) -> None:
    """The look checks every page is held to, the accessibility check, and its
    screenshot."""
    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    capture(page, name)


def test_a_features_page_shows_its_cards_and_decisions(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    page.context.grant_permissions(
        ["clipboard-read", "clipboard-write"], origin=base_url
    )
    project = _landed_feature(page, base_url, devflow_world)

    # From the project's page to the feature's, by its title.
    page.goto(f"/devflow/projects/{project}")
    page.get_by_role("list", name="Features", exact=True).get_by_role(
        "link", name="The feature's branch"
    ).click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{project}/features/60")
    expect(page.get_by_role("heading", name="The feature's branch")).to_be_visible()
    expect(page.get_by_role("tab", name="Projects", selected=True)).to_be_visible()
    feature = _feature_header(page)
    expect(feature).to_contain_text("Building")

    # The decision the first card's report marked for me, with the
    # reviewer's view, and the one not marked left out.
    wants = page.get_by_role("article", name="What the agents want you to know")
    decisions = wants.get_by_role("list", name="Decisions for you").get_by_role(
        "listitem"
    )
    expect(decisions).to_have_count(1)
    expect(decisions.first).to_contain_text("#61 · Read the sub-issues")
    expect(decisions.first).to_contain_text("Show it on every list")
    expect(decisions.first).to_contain_text("Instead of Only on habits")
    expect(decisions.first).to_contain_text("Because The lists look the same")
    expect(decisions.first).to_contain_text(
        "Reviewer: Agreed: the lists share one component."
    )
    expect(wants).not_to_contain_text("A plain sentence")

    # The branch and the command to check it out, copied with one tap.
    command = f"git fetch origin {FEATURE_BRANCH} && git switch {FEATURE_BRANCH}"
    checkout = page.get_by_role("region", name="Check out the branch")
    expect(checkout).to_contain_text(command)
    checkout.get_by_role("button", name="Copy the command").click()
    expect(checkout.get_by_role("button", name="Copied")).to_be_visible()
    assert page.evaluate("navigator.clipboard.readText()") == command

    # The first card's screenshots, read from Dev flow through the card's
    # route.
    shots = page.get_by_role("region", name="Screenshots of #61")
    for name in ("plan-desktop", "plan-phone"):
        thumbnail = shots.get_by_role("img", name=name)
        expect(thumbnail).to_be_visible()
        assert thumbnail.get_attribute("src") == (
            f"/devflow/api/projects/{project}/cards/61/screenshots/{name}"
        )
        page.wait_for_function(
            "img => img.complete && img.naturalWidth > 0",
            arg=thumbnail.element_handle(),
        )

    # The cards in order, each with its state and a link to its page.
    cards = page.get_by_role("article", name="Cards")
    expect(cards).to_contain_text("1 of 3 landed")
    rows = cards.get_by_role("list", name="Cards of the feature").get_by_role(
        "listitem"
    )
    expect(rows).to_have_count(3)
    expect(rows.nth(0)).to_contain_text("#61Read the sub-issues")
    expect(rows.nth(0)).to_contain_text("Landed")
    expect(rows.nth(1)).to_contain_text("#62Show each feature")
    expect(rows.nth(1)).to_contain_text("Building")
    expect(rows.nth(2)).to_contain_text("#63The feature's page")
    expect(rows.nth(2)).to_contain_text("Queued")
    _look(page, "devflow-feature-page")

    # A card's page links back to its feature.
    rows.nth(0).get_by_role("link", name="Read the sub-issues", exact=True).click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{project}/cards/61")
    page.get_by_role("link", name="Feature #60: The feature's branch").click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{project}/features/60")
    expect(page.get_by_role("heading", name="The feature's branch")).to_be_visible()


def test_i_answer_a_decision_fine_as_it_is(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project = _landed_feature(page, base_url, devflow_world)
    world_before = page.request.get("/devflow/api/fake/world").json()
    writes_before = _writes(page)

    page.goto(f"/devflow/projects/{project}/features/60")
    wants = page.get_by_role("article", name="What the agents want you to know")
    expect(wants).to_contain_text("Show it on every list")
    wants.get_by_role("button", name="Fine as it is").click()

    # Answered in place: no button left, and it says when.
    expect(wants.get_by_role("button")).to_have_count(0)
    expect(wants.get_by_text("Fine as it is", exact=True)).to_be_visible()
    expect(wants).to_contain_text("answered")

    # Kept: a reload shows it answered still.
    page.reload()
    wants = page.get_by_role("article", name="What the agents want you to know")
    expect(wants.get_by_text("Fine as it is", exact=True)).to_be_visible()
    expect(wants.get_by_role("button")).to_have_count(0)

    # Nothing of it reached the code host.
    assert page.request.get("/devflow/api/fake/world").json() == world_before
    assert _writes(page) == writes_before
    _look(page, "devflow-feature-decision-answered")


def test_i_send_a_card_back_from_the_features_page(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project = _landed_feature(page, base_url, devflow_world, next_running=False)

    # From the feature's page, on the card that landed, with a comment.
    page.goto(f"/devflow/projects/{project}/features/60")
    cards = page.get_by_role("article", name="Cards")
    expect(cards.get_by_role("button", name="Send back #62", exact=True)).to_have_count(
        0
    )
    cards.get_by_role("button", name="Send back #61", exact=True).click()
    said = "Read the sub-issues a hundred at a time, not all at once."
    cards.get_by_role("textbox", name="What should the developer change in #61?").fill(
        said
    )
    cards.get_by_role("button", name="Send", exact=True).click()
    expect(page.get_by_text("Sent back #61")).to_be_visible()

    # Posted as me on the card's issue, never as the app.
    mine = [
        write
        for write in _writes(page)
        if write["kind"] == "comment" and write["by"] == "octo-owner"
    ]
    assert len(mine) == 1, mine
    assert (mine[0]["number"], mine[0]["body"]) == (61, said)

    # The read that followed took it: the card is queued with my words, and
    # the feature builds again.
    rows = cards.get_by_role("list", name="Cards of the feature").get_by_role(
        "listitem"
    )
    expect(rows.nth(0)).to_contain_text("Queued")
    expect(_feature_header(page)).to_contain_text("Building")
    expect(rows.nth(0)).to_contain_text("You sent it back once, from pull request #")
    expect(rows.nth(0)).to_contain_text(said)
    _look(page, "devflow-feature-card-sent-back")

    # The fake worker takes it next, on a new branch from the feature's, and
    # it is building.
    taken = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert taken.status == 200, taken.text()
    assert (taken.json()["card"], taken.json()["branch"]) == (
        61,
        "devflow/61-read-the-sub-issues-2",
    )
    page.reload()
    rows = (
        page.get_by_role("article", name="Cards")
        .get_by_role("list", name="Cards of the feature")
        .get_by_role("listitem")
    )
    expect(rows.nth(0)).to_contain_text("Building")
    expect(_feature_header(page)).to_contain_text("Building")


def test_a_finished_feature_gets_its_pull_request(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A labelled feature of one card, and the project's .devflow/project.json.
    world = one_repository()
    add_issue(world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61])
    add_issue(world, 61, "Bring main in")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61)])

    # The fake worker builds the one card into its own branch from the
    # feature's, with a passing check.
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 41, "output_tokens": 6113},
                    "num_turns": 23,
                },
                "duration_ms": 184213,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    assert ran.json()["card"] == 61

    # Meanwhile I push to main myself, and the project is read, so the watch
    # judges my push fine and the feature's branch is watched.
    pushed = page.request.post(
        "/devflow/api/fake/push",
        data={"repository": REPOSITORY, "branch": "main", "message": "Main moves on"},
        headers={"Origin": base_url},
    )
    assert pushed.status == 200, pushed.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # CI green and the reviewer's approve: the card lands in the feature's
    # branch, which ends the feature.
    _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))

    # The fake world holds main brought into the feature's branch by the app,
    # a merge commit, and then the feature's pull request into main.
    writes = _writes(page)
    brought = [write for write in writes if write["kind"] == "branch_merge"]
    assert len(brought) == 1, brought
    assert (brought[0]["by"], brought[0]["purpose"], brought[0]["method"]) == (
        APP_LOGIN,
        "merge",
        "merge",
    )
    assert (brought[0]["head"], brought[0]["base"]) == ("main", FEATURE_BRANCH)
    assert brought[0]["body"] == f"Bring main into {FEATURE_BRANCH}"
    opened = [
        write
        for write in writes
        if write["kind"] == "pull_request" and write.get("base") == "main"
    ]
    assert len(opened) == 1, opened
    assert writes.index(brought[0]) < writes.index(opened[0])
    pull = next(
        pull
        for pull in repository(page.request.get("/devflow/api/fake/world").json())[
            "pull_requests"
        ]
        if pull["number"] == opened[0]["number"]
    )
    assert (pull["author"], pull["state"], pull["draft"]) == (APP_LOGIN, "open", False)
    assert (pull["head_branch"], pull["base_branch"]) == (FEATURE_BRANCH, "main")
    assert pull["title"] == "The feature's branch"
    assert "\nCloses #60\n" in pull["body"]
    assert re.search(
        r"^- #61 Bring main in, landed in #\d+$", pull["body"], re.MULTILINE
    )

    # Read again, the watch finds both merges on the feature's branch fine.
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    workers = page.request.get(f"/devflow/api/projects/{project}/workers")
    assert workers.status == 200, workers.text()
    assert workers.json()["held"] is None

    # The feature's page links to its pull request and says it is reviewed as a
    # whole next.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Reviewing")
    link = feature.get_by_role("link", name=f"Pull request #{pull['number']}")
    expect(link).to_have_attribute("href", pull["url"])
    _look(page, "devflow-feature-pull-request")


def test_a_conflict_is_resolved_by_an_agent(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A labelled feature of one card, the project's .devflow/project.json, and
    # main conflicting with the feature's branch when it is brought in.
    world = one_repository()
    add_issue(world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61])
    add_issue(world, 61, "Bring main in")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    repository(world)["conflicts"] = [FEATURE_BRANCH]
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61)])

    # The fake worker builds the one card; meanwhile I push to main, and the
    # project is read, so the watch judges my push fine.
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 41, "output_tokens": 6113},
                    "num_turns": 23,
                },
                "duration_ms": 184213,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    pushed = page.request.post(
        "/devflow/api/fake/push",
        data={"repository": REPOSITORY, "branch": "main", "message": "Main moves on"},
        headers={"Origin": base_url},
    )
    assert pushed.status == 200, pushed.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # CI green and the reviewer's approve: the card lands, which ends the
    # feature, and bringing main in conflicts. Nothing is brought in, and the
    # feature is resolving, an agent's job.
    _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    assert [write for write in _writes(page) if write["kind"] == "branch_merge"] == []
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Resolving a conflict")
    expect(feature).to_contain_text(
        "main conflicts with the feature's branch; an agent resolves it"
    )

    # The project's next read reads the feature's branch, so the conflict job
    # starts from its head. The fake worker takes it and answers a resolved
    # merge, a merge commit of main on the feature's branch, with its remerge
    # diff.
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    resolved = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 13 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 12, "output_tokens": 3400},
                    "num_turns": 9,
                },
                "duration_ms": 720000,
                "stderr": "",
                "remerge_diff": "diff --git a/README.md b/README.md\n",
            },
        },
        headers={"Origin": base_url},
    )
    assert resolved.status == 200, resolved.text()
    assert (resolved.json()["kind"], resolved.json()["card"]) == ("conflict", 60)
    assert resolved.json()["branch"] == FEATURE_BRANCH

    # The feature goes on as after a clean bringing in: its pull request into
    # main is opened, and it is reviewed as a whole next; the watch finds the
    # push fine.
    opened = [
        write
        for write in _writes(page)
        if write["kind"] == "pull_request" and write.get("base") == "main"
    ]
    assert len(opened) == 1, opened
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    workers = page.request.get(f"/devflow/api/projects/{project}/workers")
    assert workers.status == 200, workers.text()
    assert workers.json()["held"] is None

    # Its page shows the run that resolved it, newest first, after the reading
    # of its cards.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Reviewing")
    runs = page.get_by_role("article", name="Its own runs")
    expect(runs.locator("dt")).to_have_text(["Conflict", "Reading"])
    expect(runs.locator("dd").first).to_contain_text("Succeeded · ")
    _look(page, "devflow-feature-conflict-resolved")


def _published_feature(page: Page, base_url: str, devflow_world) -> tuple[str, int]:
    """A feature of one card, 61 under the parent 60, built, reviewed and
    landed by the fake worker while main moves on, so its end brings main in
    and opens its pull request; then read again, so its branch is watched at
    the merge and its whole review is the next job. The project's id and the
    feature's pull request."""
    # A labelled feature of one card, and the project's .devflow/project.json.
    world = one_repository()
    add_issue(world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61])
    add_issue(world, 61, "Review the whole feature")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61)])

    # The fake worker builds the one card; meanwhile I push to main, and the
    # project is read, so the watch judges my push fine.
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 41, "output_tokens": 6113},
                    "num_turns": 23,
                },
                "duration_ms": 184213,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    pushed = page.request.post(
        "/devflow/api/fake/push",
        data={"repository": REPOSITORY, "branch": "main", "message": "Main moves on"},
        headers={"Origin": base_url},
    )
    assert pushed.status == 200, pushed.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # CI green and the reviewer's approve: the card lands, which ends the
    # feature: main is brought in, and the feature's pull request opened.
    _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    opened = [
        write
        for write in _writes(page)
        if write["kind"] == "pull_request" and write.get("base") == "main"
    ]
    assert len(opened) == 1, opened
    pull = opened[0]["number"]

    # Read again, so the feature's branch is watched at the merge, and the
    # whole feature's review is the next job.
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    return project, pull


def _fake_job(
    page: Page, base_url: str, project: str, report: dict | None, check: dict | None
) -> dict:
    """The fake worker takes the project's next job and ends it with
    `report`, and `check` when a check ran; its answer."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": report,
                "failure": None,
                "check": check,
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 30, "output_tokens": 1200},
                    "num_turns": 11,
                },
                "duration_ms": 95000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def test_a_reviewed_feature_is_ready_for_me(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, pull = _published_feature(page, base_url, devflow_world)

    # The fake worker takes the next job, the whole feature's review, and
    # approves it.
    whole = _verdict(
        "approve",
        [
            {
                "blocking": False,
                "file": "devflow/src/DevFlow/Features/FeatureReady.cs",
                "line": 12,
                "what": "The card and the end both read the branch's head",
                "why": "One reader would do once the next card lands",
            }
        ],
    )
    whole["summary"] = (
        "The feature does what its parent asks, and its cards fit together."
    )
    reviewed = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": whole,
                "failure": None,
                "check": None,
                "auth_method": "oauth_token",
                "usage": {
                    "usage": {"input_tokens": 30, "output_tokens": 1200},
                    "num_turns": 11,
                },
                "duration_ms": 95000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert reviewed.status == 200, reviewed.text()
    assert (reviewed.json()["kind"], reviewed.json()["card"]) == ("feature_review", 60)
    assert reviewed.json()["branch"] == FEATURE_BRANCH
    head = reviewed.json()["head_commit"]

    # Its copy is posted on the feature's pull request, by the app.
    posted = [
        write
        for write in _writes(page)
        if write["kind"] == "comment" and write["number"] == pull
    ]
    assert len(posted) == 1, posted
    assert posted[0]["by"] == APP_LOGIN
    assert posted[0]["body"].startswith(
        f"Review of the feature at {head[:7]}: approve\n"
    )

    # CI passes on the head; the next minute finds it ready, here as the fake
    # worker asks for work, which there is none of.
    _stage_ci(page, devflow_world, head, [check_run("ci")])
    asked = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert asked.status == 204, asked.text()

    # It shows under "Features ready for your review", with the review's
    # summary and the one decision the card marked for me.
    page.goto("/devflow")
    group = page.get_by_role("region", name="Features ready for your review")
    expect(group).to_be_visible()
    item = _item(page, "The feature's branch", "Features ready for your review")
    expect(item).to_contain_text("Ready for you")
    expect(item).to_contain_text(
        "The feature does what its parent asks, and its cards fit together."
    )
    decisions = item.get_by_role("list", name="At a glance").get_by_role("listitem")
    expect(decisions.nth(2)).to_have_text("Decisions1 for younot answered yet")
    expect(item.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", re.compile(rf"/pull/{pull}$")
    )
    _look(page, "devflow-feature-ready")

    # Its page shows the review, for the pull request's head.
    item.get_by_role("link", name="Open the feature").click()
    expect(page).to_have_url(re.compile(rf"/devflow/projects/{project}/features/60$"))
    feature = _feature_header(page)
    expect(feature).to_contain_text("Ready for you")
    reviews = _every_review_and_round(page)
    latest = reviews.locator(":scope > li").first
    expect(latest).to_contain_text(head[:7])
    expect(latest).to_contain_text("Approved")
    expect(latest).to_contain_text("For the pull request's head")
    expect(latest).to_contain_text("The card and the end both read the branch's head")
    _look(page, "devflow-feature-review")


def test_a_feature_is_fixed_after_its_review(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, _ = _published_feature(page, base_url, devflow_world)

    # The whole feature's review asks for changes: round 1 starts.
    changes = _verdict(
        "changes",
        [
            {
                "blocking": True,
                "file": "devflow/src/DevFlow/Features/FeatureReady.cs",
                "line": 12,
                "what": "The card and the end both read the branch's head",
                "why": "Two readers will drift apart",
            }
        ],
    )
    reviewed = _fake_job(page, base_url, project, changes, None)
    assert (reviewed["kind"], reviewed["card"]) == ("feature_review", 60)
    reviewed_head = reviewed["head_commit"]

    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Fixing")
    expect(feature).to_contain_text(
        "Round 1 of 3: the review of the whole feature asked for changes; a fix runs next"
    )
    latest = _every_review_and_round(page).locator(":scope > li").first
    expect(latest).to_contain_text("The card and the end both read the branch's head")
    _look(page, "devflow-feature-fixing")

    # The fix is the next job, on the feature's branch, and leaves a new commit
    # there that Dev flow recorded as its own.
    fix = _fix_report()
    fix["replies"] = [
        {
            "finding": "The card and the end both read the branch's head",
            "answer": "Fixed: one reader, in FeatureReady.",
        }
    ]
    fixed = _fake_job(
        page, base_url, project, fix, {"exit_code": 0, "output": "Tests: 13 passed"}
    )
    assert (fixed["kind"], fixed["card"], fixed["branch"]) == (
        "feature_fix",
        60,
        FEATURE_BRANCH,
    )
    assert fixed["head_commit"] != reviewed_head

    # Read again, so the watch finds the push its own; the new review reads the
    # fix's commit and approves it, and with CI green the feature is ready.
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    again = _fake_job(page, base_url, project, _verdict("approve"), None)
    assert (again["kind"], again["head_commit"]) == (
        "feature_review",
        fixed["head_commit"],
    )
    _stage_ci(page, devflow_world, again["head_commit"], [check_run("ci")])
    asked = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert asked.status == 204, asked.text()

    # Its page shows the round: the review that started it, the reply, the
    # commit it left and the review of it.
    page.reload()
    expect(_feature_header(page)).to_contain_text("Ready for you")
    expect(
        page.get_by_role("article", name="Reviews and rounds").get_by_role(
            "img", name="Fix rounds: 1 of 3 used"
        )
    ).to_be_visible()
    round_one = _every_review_and_round(page).get_by_role(
        "listitem", name="Round 1 of 3"
    )
    expect(round_one).to_contain_text(reviewed_head[:7])
    expect(round_one).to_contain_text("Fixed: one reader, in FeatureReady.")
    expect(round_one).to_contain_text(fixed["head_commit"][:7])
    expect(round_one).to_contain_text("the review of it approved it")


def test_i_send_the_whole_feature_back(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, pull = _published_feature(page, base_url, devflow_world)
    _fake_job(page, base_url, project, _verdict("approve"), None)

    # From the feature's page, with a comment.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Waiting for you")
    merging = page.get_by_role("article", name="Merging into main")
    merging.get_by_role("button", name="Send the whole feature back").click()
    said = "Read the branch's head in one place for the card and the end."
    merging.get_by_role(
        "textbox", name="What should the developer change in the whole feature?"
    ).fill(said)
    _look(page, "devflow-feature-send-back")
    merging.get_by_role("button", name="Send", exact=True).click()
    expect(page.get_by_text("Sent back feature #60")).to_be_visible()

    # Posted as me on the feature's pull request, never as the app.
    mine = [
        write
        for write in _writes(page)
        if write["kind"] == "comment" and write["by"] == "octo-owner"
    ]
    assert len(mine) == 1, mine
    assert (mine[0]["number"], mine[0]["body"]) == (pull, said)

    # The read that followed took it: the feature is being fixed, and its fix
    # is the next job, with my comment in its brief.
    expect(feature).to_contain_text("Fixing")
    expect(feature).to_contain_text(
        "You commented on its pull request; a fix runs next"
    )
    fixed = _fake_job(
        page,
        base_url,
        project,
        _fix_report(),
        {"exit_code": 0, "output": "Tests: 13 passed"},
    )
    assert (fixed["kind"], fixed["card"]) == ("feature_fix", 60)


# Merging a feature into main, as the owner, from its item under "Features
# ready for your review" or from its page. See "Merging a feature" in
# devflow/README.md.


def _reviewed_feature(
    page: Page,
    base_url: str,
    devflow_world,
    whole: dict,
    titles: tuple[str, ...] = ("Merge the feature",),
) -> tuple[str, int, str]:
    """A labelled feature of a card for each of `titles`, 61 onwards, one
    unless given, carried to its end: each card built and landed in turn,
    only the first marking a decision for me, main brought in and the
    feature's pull request opened, its whole review answered `whole`, and CI
    green on its head, decided on the next minute. A verdict other than
    approve comes after every fix round is used, each a review asking for
    changes and its fix, so the feature waits for me, the one way one its
    review did not approve does. The project, the feature's pull request and
    its head."""
    numbers = [61 + at for at in range(len(titles))]
    world = one_repository()
    add_issue(world, 60, "The feature's branch", labels=["devflow"], sub_issues=numbers)
    for number, title in zip(numbers, titles, strict=True):
        add_issue(world, number, title)
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(number) for number in numbers])

    # The fake worker builds each card in turn, and each lands in the
    # feature's branch on CI green and the reviewer's approve; only the first
    # marks a decision for me. Before the last lands I push to main, and the
    # project is read, so the watch judges my push fine.
    for number in numbers:
        report = _report()
        if number != numbers[0]:
            report["decisions"] = [
                decision
                for decision in report["decisions"]
                if not decision["for_owner"]
            ]
        ran = page.request.post(
            "/devflow/api/fake/worker/run",
            data={
                "project_id": project,
                "result": {
                    "outcome": "succeeded",
                    "report": report,
                    "failure": None,
                    "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                    "auth_method": "oauth_token",
                    "usage": {
                        "usage": {"input_tokens": 41, "output_tokens": 6113},
                        "num_turns": 23,
                    },
                    "duration_ms": 184213,
                    "stderr": "",
                },
            },
            headers={"Origin": base_url},
        )
        assert ran.status == 200, ran.text()
        assert ran.json()["card"] == number, ran.json()
        if number == numbers[-1]:
            pushed = page.request.post(
                "/devflow/api/fake/push",
                data={
                    "repository": REPOSITORY,
                    "branch": "main",
                    "message": "Main moves on",
                },
                headers={"Origin": base_url},
            )
            assert pushed.status == 200, pushed.text()
        refreshed = page.request.post(
            f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
        )
        assert refreshed.status == 204, refreshed.text()
        _stage_ci(page, devflow_world, ran.json()["head_commit"], [check_run("test")])
        _fake_review(page, base_url, project, _verdict("approve"))
        if number != numbers[-1]:
            refreshed = page.request.post(
                f"/devflow/api/projects/{project}/refresh",
                headers={"Origin": base_url},
            )
            assert refreshed.status == 204, refreshed.text()

    # The last card landing ends the feature: main is brought in, and the
    # feature's pull request opened.
    opened = [
        write
        for write in _writes(page)
        if write["kind"] == "pull_request" and write.get("base") == "main"
    ]
    assert len(opened) == 1, opened
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # Short of an approve, the three fix rounds are used first: each review
    # asks for changes, its fix runs, and the project is read again so the
    # watch finds the fix's push its own.
    if whole["verdict"] != "approve":
        asked = _verdict(
            "changes",
            [
                {
                    "blocking": True,
                    "file": "devflow/src/DevFlow/Features/FeatureEnd.cs",
                    "line": 12,
                    "what": "The end reads the branch twice",
                    "why": "One read would do",
                }
            ],
        )
        for _ in range(3):
            asked_changes = _fake_job(page, base_url, project, asked, None)
            assert asked_changes["kind"] == "feature_review", asked_changes
            fixed = _fake_job(
                page,
                base_url,
                project,
                _fix_report(),
                {"exit_code": 0, "output": "Tests: 12 passed"},
            )
            assert fixed["kind"] == "feature_fix", fixed
            refreshed = page.request.post(
                f"/devflow/api/projects/{project}/refresh",
                headers={"Origin": base_url},
            )
            assert refreshed.status == 204, refreshed.text()

    # The fake worker takes the whole feature's review and answers `whole`.
    reviewed = _fake_job(page, base_url, project, whole, None)
    assert reviewed["kind"] == "feature_review", reviewed
    head = reviewed["head_commit"]

    # CI passes on the head; the next minute decides, here as the fake worker
    # asks for work, which there is none of.
    _stage_ci(page, devflow_world, head, [check_run("ci")])
    asked = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert asked.status == 204, asked.text()
    return project, opened[0]["number"], head


def test_i_merge_a_feature_into_main(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, pull, head = _reviewed_feature(
        page, base_url, devflow_world, _verdict("approve")
    )

    # From "Features ready for your review", Merge into main asks first,
    # naming the pull request and its commit.
    page.goto("/devflow")
    group = page.get_by_role("region", name="Features ready for your review")
    item = _item(page, "The feature's branch", "Features ready for your review")
    expect(item).to_contain_text("Ready for you")
    item.get_by_role("button", name="Merge into main").click()
    dialog = page.get_by_role("dialog", name="Merge feature #60 into main?")
    expect(dialog).to_be_visible()
    expect(dialog).to_contain_text(f"Pull request #{pull}")
    expect(dialog.locator("code")).to_have_text(head[:7])
    expect(dialog).to_contain_text("The review approved this commit.")
    expect(dialog).to_contain_text("Merging confirms 1 decision you have not answered.")
    dialog.get_by_role("button", name="Merge").click()

    # Merged as me: the fake world holds my merge of its pull request, a merge
    # commit naming the head that was shown, and the item is gone.
    expect(page.get_by_text("Merged feature #60")).to_be_visible()
    expect(
        group.get_by_role("link", name=re.compile("^The feature's branch "))
    ).to_have_count(0)
    # A person's merges, with no key's purpose: the app's merge of the card into
    # the feature's branch is not among them.
    merges = [
        write
        for write in _writes(page)
        if write["kind"] == "merge" and "purpose" not in write
    ]
    assert merges == [
        {
            "kind": "merge",
            "repository": REPOSITORY,
            "number": pull,
            "commit": head,
            "method": "merge",
            "body": None,
            "by": OWNER_LOGIN,
        }
    ], merges
    assert _staged_pull(page, pull)["state"] == "merged"

    # Its page says it is merged, and offers the merge no more.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Merged")
    expect(page.get_by_role("button", name="Merge into main")).to_have_count(0)
    _look(page, "devflow-feature-merged")


ELEVEN_CARDS = (
    "Features and their cards",
    "The feature's branch, made once",
    "Watching a feature's branch",
    "A card lands in its feature",
    "The feature's page",
    "Bringing main in",
    "Resolving a conflict",
    "Reviewing the whole feature",
    "Fixing a feature",
    "Merging a feature into main",
    "Sending a card back",
)


def test_a_feature_page_puts_merging_first(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, pull, head = _reviewed_feature(
        page, base_url, devflow_world, _verdict("approve"), titles=ELEVEN_CARDS
    )

    # The header: its state with the mono caption, the title, its links.
    page.goto(f"/devflow/projects/{project}/features/60")
    expect(
        page.get_by_role("heading", name="The feature's branch", exact=True)
    ).to_be_visible()
    expect(
        page.get_by_text(f"{OWNER}/{NAME} · FEATURE #60 · 11 CARDS".upper())
    ).to_be_visible()
    expect(page.get_by_role("link", name=f"Pull request #{pull}")).to_be_visible()

    # The merge card, before the decisions and the cards: CI, the review and
    # the decisions as tiles, with the head it merges.
    merging = page.get_by_role("article", name="Merging into main")
    wants = page.get_by_role("article", name="What the agents want you to know")
    cards = page.get_by_role("article", name="Cards")
    expect(merging).to_contain_text(f"head {head[:7]}")
    tiles = merging.get_by_role("list", name="At a glance").get_by_role("listitem")
    expect(tiles).to_have_count(3)
    expect(tiles.nth(0)).to_contain_text("Passed")
    expect(tiles.nth(1)).to_contain_text("Approved")
    expect(tiles.nth(2)).to_contain_text("0 of 1 answered")
    expect(merging).to_contain_text(
        "Merging confirms 1 decision you have not answered."
    )
    tops = [box.bounding_box()["y"] for box in (merging, wants, cards)]
    assert tops == sorted(tops), tops

    # Exactly one primary on the page: Merge into main.
    primaries = [
        button.inner_text()
        for button in page.get_by_role("button").all()
        if button.is_visible()
        and re.search(r"(^|\s)_?primary", button.get_attribute("class") or "")
    ]
    assert primaries == ["Merge into main"], primaries

    # The one open decision, with its two answers.
    decision = wants.get_by_role("list", name="Decisions for you").get_by_role(
        "listitem"
    )
    expect(decision).to_have_count(1)
    expect(decision).to_contain_text("#61 · Features and their cards")
    expect(decision).to_contain_text("Show it on every list")
    expect(
        decision.get_by_role("button", name=re.compile("^Fine as it is"))
    ).to_be_visible()
    expect(
        decision.get_by_role("button", name=re.compile("^Send back #61"))
    ).to_be_visible()

    # The eleven cards in order, all landed, each with Send back: beside its
    # title on desktop, under it on phone.
    expect(cards).to_contain_text("11 of 11 landed")
    expect(cards.get_by_role("img", name="Cards: 11 of 11 landed")).to_be_visible()
    rows = cards.get_by_role("list", name="Cards of the feature").get_by_role(
        "listitem"
    )
    expect(rows).to_have_count(len(ELEVEN_CARDS))
    for at, title in enumerate(ELEVEN_CARDS):
        row = rows.nth(at)
        expect(row).to_contain_text(f"#{61 + at}")
        expect(row).to_contain_text("Landed")
        named = row.get_by_role("link", name=title, exact=True).bounding_box()
        back = row.get_by_role(
            "button", name=f"Send back #{61 + at}", exact=True
        ).bounding_box()
        if viewport == "phone":
            assert back["y"] >= named["y"] + named["height"] - 1, (title, named, back)
        else:
            assert back["y"] < named["y"] + named["height"], (title, named, back)
            assert back["x"] > named["x"] + named["width"], (title, named, back)

    # The side column: the branch with its command, and the review.
    expect(page.get_by_role("article", name="The branch")).to_contain_text(
        FEATURE_BRANCH
    )
    expect(
        page.get_by_role("article", name="Reviews and rounds").get_by_role(
            "img", name="Fix rounds: 0 of 3 used"
        )
    ).to_be_visible()
    _look(page, "devflow-feature")


def test_merging_warns_when_the_review_did_not_approve(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    findings = [
        {
            "blocking": True,
            "file": "devflow/src/DevFlow/Features/FeatureEnd.cs",
            "line": 40,
            "what": "The end reads the cards twice",
            "why": "A card added between the reads is missed",
        },
        {
            "blocking": True,
            "file": "devflow/web/src/feature.tsx",
            "line": 120,
            "what": "The branch line says main for every project",
            "why": "A project whose default branch is trunk reads wrong",
        },
        {
            "blocking": False,
            "file": "devflow/README.md",
            "line": 3000,
            "what": "A sentence could be shorter",
            "why": "It reads twice",
        },
    ]
    project, pull, head = _reviewed_feature(
        page, base_url, devflow_world, _verdict("changes", findings)
    )

    # It waits for me, since the review asked for changes with every round
    # used, and CI passed, so Merge into main is offered on its page.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = _feature_header(page)
    expect(feature).to_contain_text("Waiting for you")
    expect(feature).to_contain_text("Review rounds used up")
    page.get_by_role("article", name="Merging into main").get_by_role(
        "button", name="Merge into main"
    ).click()

    # The confirmation says the review asked for changes and lists the open
    # findings, and counts the decision I have not answered.
    dialog = page.get_by_role("dialog", name="Merge feature #60 into main?")
    expect(dialog).to_contain_text(f"Pull request #{pull}")
    expect(dialog.get_by_text(head[:7], exact=True)).to_be_visible()
    expect(dialog).to_contain_text("The review asked for changes: 2 open findings")
    listed = dialog.get_by_role("list", name="Open findings")
    expect(listed.get_by_role("listitem")).to_have_count(2)
    expect(listed).to_contain_text("The end reads the cards twice")
    expect(listed).to_contain_text("devflow/web/src/feature.tsx:120")
    expect(dialog).not_to_contain_text("A sentence could be shorter")
    expect(dialog).to_contain_text("Merging confirms 1 decision you have not answered.")
    _look(page, "devflow-feature-merge-dialog")

    # Cancelled, nothing is merged by me.
    dialog.get_by_role("button", name="Cancel").click()
    expect(dialog).to_have_count(0)
    assert [
        write
        for write in _writes(page)
        if write["kind"] == "merge" and "purpose" not in write
    ] == []
    assert _staged_pull(page, pull)["state"] == "open"


def _png() -> bytes:
    """A real one by one PNG, so a page showing it shows a picture."""

    def chunk(kind: bytes, data: bytes) -> bytes:
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)
    pixels = zlib.compress(b"\x00\x2b\x6c\xb0")
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", pixels)
        + chunk(b"IEND", b"")
    )


def _pass_the_check(worker, key: str, worker_id: str) -> None:
    """A worker on a version no check has passed is handed that check before
    anything else: it takes it and ends it passed, as a working recipe's
    worker does, so its next job is the one the test is about."""
    asked = worker.post(
        "/devflow/api/worker/jobs/next",
        data={"worker_id": worker_id},
        headers={"Authorization": f"Bearer {key}"},
    )
    assert asked.status == 200, asked.text()
    job = asked.json()
    assert (job["kind"], job["card"]) == ("check", None), job["kind"]
    result = worker.post(
        f"/devflow/api/worker/jobs/{job['job_id']}/result",
        data={
            "worker_id": worker_id,
            "outcome": "succeeded",
            "report": None,
            "failure": None,
            "check": {"exit_code": 0, "output": "Tests  1 passed"},
            "branch": job["branch"],
            "head_commit": None,
            "auth_method": None,
            "usage": {},
            "duration_ms": 61000,
            "stderr": "",
        },
        headers={"Authorization": f"Bearer {key}"},
    )
    assert result.status == 204, result.text()


def test_a_project_is_checked_before_its_first_card(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with its recipe and project.json on its default branch, and
    # one card the owner labelled.
    world = one_repository(name="Fodr")
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(world, ".devflow/Dockerfile", RECIPE)
    add_file(
        world,
        ".devflow/project.json",
        json.dumps(
            {
                "version": 1,
                "check": CHECK,
                "start": "npm run dev",
                "url": "http://localhost:5173",
            }
        ).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # Setting up: its page shows the steps of setting it up in place of its
    # cards, the recipe read and nothing else done yet.
    page.reload()
    setup = page.get_by_role("article", name="Setting it up", exact=True)
    steps = setup.get_by_role("list", name="Setup steps").locator(":scope > li")
    expect(steps).to_have_count(6)
    expect(steps.nth(0)).to_have_attribute("data-state", "done")
    expect(steps.nth(0)).to_contain_text("The setup run")
    expect(steps.nth(1)).to_have_attribute("data-state", "done")
    expect(steps.nth(1)).to_contain_text("The recipe on the default branch")
    expect(steps.nth(2)).to_have_attribute("data-state", "current")
    # No setup run listed a value, so the values step is done.
    expect(steps.nth(3)).to_have_attribute("data-state", "done")
    expect(steps.nth(3)).to_contain_text("The values its tests need")
    expect(steps.nth(4)).to_have_attribute("data-state", "to-come")
    expect(steps.nth(5)).to_have_attribute("data-state", "to-come")
    expect(page.get_by_role("region", name="Cards", exact=True)).to_have_count(0)
    assert (
        page.request.get(f"/devflow/api/projects/{project}/setup").json()["state"]
        == "setting_up"
    )

    # Its worker's first job is the check, which fails: nothing else is handed
    # out, and the page shows the output and Check again. The fake worker takes
    # the settings as accepted, as the owner saving them would; their step is
    # test_i_accept_the_proposed_settings'.
    output = "\n".join(
        ["> fodr@1.0.0 test", "> vitest run", ""]
        + [f" ✓ src/habit-{n}.test.ts (3 tests)" for n in range(1, 9)]
        + [" × src/list.test.ts > shows the empty state", "Tests  1 failed | 24 passed"]
    )
    failed = fake_check(page, base_url, project, 1, output)
    assert failed["kind"] == "check"
    assert failed["card"] is None
    nothing = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert nothing.status == 204, nothing.text()
    page.reload()
    check = setup.get_by_role("region", name="The newest check")
    expect(check.get_by_text("Failed", exact=True)).to_be_visible()
    expect(check.get_by_text("exited with 1", exact=False)).to_be_visible()
    expect(check.locator("pre")).to_contain_text("Tests  1 failed | 24 passed")
    expect(steps.nth(2)).to_have_attribute("data-state", "done")
    expect(steps.nth(3)).to_have_attribute("data-state", "done")
    expect(steps.nth(4)).to_have_attribute("data-state", "done")
    expect(steps.nth(5)).to_have_attribute("data-state", "current")
    expect(steps.nth(5).get_by_role("button", name="Check again")).to_be_visible()
    assert _axe_violations(page) == []
    capture(page, "devflow-project-first-check-failed")

    # Waiting for you lists it under Setup.
    waiting = page.request.get("/devflow/api/waiting").json()
    (listed,) = waiting["setup_projects"]
    assert listed["project_id"] == project
    assert listed["state"] == "setting_up"

    # Check again: the next check passes, and takes the project's address.
    setup.get_by_role("button", name="Check again").click()
    expect(
        setup.get_by_text(
            "Another check was asked for: the next worker on this version runs it."
        )
    ).to_be_visible()
    picture = _png()
    passed = fake_check(
        page,
        base_url,
        project,
        0,
        "Tests  25 passed",
        {"home-desktop": picture, "home-phone": picture},
    )
    assert passed["kind"] == "check"
    answered = page.request.get(f"/devflow/api/projects/{project}/setup").json()
    assert answered["state"] == "registered"
    assert [step["done"] for step in answered["steps"]] == [True] * 6
    shown = page.request.get(
        f"/devflow/api/projects/{project}/setup/checks/{answered['check']['id']}"
        "/pictures/home-desktop"
    )
    assert shown.status == 200
    assert shown.headers["content-type"] == "image/png"
    assert shown.body() == picture
    assert page.request.get("/devflow/api/waiting").json()["setup_projects"] == []

    # Registered: the labelled card is read next, and the page shows its cards.
    read = _fake_read(page, base_url, project, [_ready(CARD)])
    assert read["kind"] == "read"
    assert read["card"] == CARD
    page.reload()
    expect(page.get_by_role("region", name="Cards", exact=True)).to_be_visible()
    expect(page.get_by_role("article", name="Setting it up", exact=True)).to_have_count(
        0
    )


def _setup_report() -> dict:
    """A setup run's report in its form, with two things it could not
    settle."""
    return {
        "summary": "Wrote a recipe with Postgres, and a check that starts it.",
        "proposed": {
            "preset": "guided",
            "why": "Its migrations are worth a look before they merge.",
            "paths": ["migrations/**"],
        },
        "variables": [
            {"name": "STRIPE_TEST_KEY", "why": "The payment tests call Stripe."}
        ],
        "unsettled": [
            {
                "kind": "real_secret",
                "detail": "The export tests upload to the real bucket.",
            },
            {
                "kind": "containers_in_tests",
                "detail": "The integration tests start Redis with Testcontainers.",
            },
        ],
        "decisions": [],
        "handover": "The check starts Postgres before the tests.",
    }


def _fake_setup(page: Page, base_url: str, project: str, report: dict) -> dict:
    """The fake worker, started from the base image with no recipe, takes
    the project's next setup job and ends it with `report`. What it
    answered."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "setup": True,
            "result": {
                "outcome": "succeeded",
                "report": report,
                "failure": None,
                "check": None,
                "auth_method": "oauth_token",
                "usage": {},
                "duration_ms": 61000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def test_a_project_is_set_up_by_an_agent(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with no .devflow/ on its default branch; what its setup
    # run writes is staged on the setup's branch.
    world = one_repository(name="Fodr")
    add_file(world, ".devflow/Dockerfile", RECIPE, branch="devflow/setup")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
        branch="devflow/setup",
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # Its step shows why a setup run is due and the command that starts its
    # worker from the base image.
    page.reload()
    setup = page.get_by_role("article", name="Setting it up", exact=True)
    steps = setup.get_by_role("list", name="Setup steps").locator(":scope > li")
    expect(steps).to_have_count(6)
    expect(steps.nth(0)).to_have_attribute("data-state", "current")
    expect(steps.nth(0)).to_contain_text("The setup run")
    run = setup.get_by_role("region", name="The setup run")
    expect(run.get_by_text(".devflow/Dockerfile is missing.")).to_be_visible()
    expect(
        run.get_by_text("--name devflow-fodr-setup --memory 2g", exact=False)
    ).to_be_visible()

    # The fake worker's setup opens a pull request, which is reviewed, and
    # waits for me with what it could not settle.
    ran = _fake_setup(page, base_url, project, _setup_report())
    assert ran["kind"] == "setup"
    assert ran["branch"] == "devflow/setup"
    reviewed = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "setup": True,
            "result": {
                "outcome": "succeeded",
                "report": _verdict("approve"),
                "failure": None,
                "check": None,
                "auth_method": "oauth_token",
                "usage": {},
                "duration_ms": 61000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert reviewed.status == 200, reviewed.text()
    assert reviewed.json()["kind"] == "review"
    page.reload()
    expect(run.get_by_text("Waits for you", exact=True)).to_be_visible()
    expect(
        run.get_by_role("link", name=re.compile(r"Pull request #\d+"))
    ).to_be_visible()
    unsettled = run.get_by_role("list", name="What it could not settle")
    expect(unsettled.get_by_role("listitem")).to_have_count(2)
    expect(unsettled.get_by_text("Needs a real secret")).to_be_visible()
    expect(unsettled.get_by_text("Its tests start containers")).to_be_visible()
    assert _axe_violations(page) == []
    capture(page, "devflow-project-setup-run")
    waiting = page.request.get("/devflow/api/waiting").json()
    (listed,) = waiting["setup_runs"]
    assert listed["project_id"] == project
    assert listed["state"] == "waiting_for_owner"

    # I merge it, and the next step is the value its tests need, the
    # settings taken as accepted by the fake worker; then a worker on the
    # recipe.
    run.get_by_role("button", name="Merge").click()
    expect(steps.nth(0)).to_have_attribute("data-state", "done")
    expect(steps.nth(1)).to_have_attribute("data-state", "done")
    expect(steps.nth(2)).to_have_attribute("data-state", "done")
    expect(steps.nth(3)).to_have_attribute("data-state", "current")
    expect(steps.nth(3)).to_contain_text("The values its tests need")
    expect(steps.nth(4)).to_have_attribute("data-state", "to-come")
    expect(steps.nth(4)).to_contain_text("A worker on the recipe")
    assert page.request.get("/devflow/api/waiting").json()["setup_runs"] == []


def test_a_stopped_setup_runs_again_on_my_comment(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    world = one_repository(name="Fodr")
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # The setup reaches its time limit with nothing pushed: it stops at once,
    # before it had a pull request, so there is nothing on GitHub to comment on.
    stopped = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "setup": True,
            "leave_no_commit": True,
            "result": {
                "outcome": "failed",
                "report": None,
                "failure": {
                    "reason": "The run reached its time limit, 90 minutes",
                    "in_card": True,
                    "timed_out": True,
                },
                "check": None,
                "auth_method": "oauth_token",
                "usage": {},
                "duration_ms": 5400000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert stopped.status == 200, stopped.text()
    assert stopped.json()["kind"] == "setup"
    (listed,) = page.request.get("/devflow/api/waiting").json()["setup_runs"]
    assert listed["state"] == "stopped"

    # Its step says why, and my comment from it runs it again.
    page.reload()
    run = page.get_by_role("article", name="Setting it up", exact=True).get_by_role(
        "region", name="The setup run"
    )
    expect(run.get_by_text("Stopped", exact=True)).to_be_visible()
    expect(run.get_by_text("reached its time limit", exact=False)).to_be_visible()
    expect(run.get_by_role("link", name=re.compile(r"Pull request"))).to_have_count(0)
    run.get_by_role("button", name="Comment to run again").click()
    run.get_by_label("A comment runs it again.").fill(
        "Give it more time: the test suite alone takes an hour."
    )
    assert _axe_violations(page) == []
    capture(page, "devflow-project-setup-stopped")
    run.get_by_role("button", name="Send").click()
    expect(run.get_by_text("Queued", exact=True)).to_be_visible()
    expect(run.get_by_text("You commented on it; it runs again next.")).to_be_visible()
    assert page.request.get("/devflow/api/waiting").json()["setup_runs"] == []


def _setup_result(report: dict) -> dict:
    """A setup job's result as its worker sends one: `report`, no check."""
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


def test_i_accept_the_proposed_settings(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with no .devflow/; what its setup run writes is staged on
    # the setup's branch.
    world = one_repository(name="Fodr")
    add_file(world, ".devflow/Dockerfile", RECIPE, branch="devflow/setup")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
        branch="devflow/setup",
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # The setup run proposes guided and its migrations, and is reviewed; the
    # fake worker leaves the settings for me in both runs.
    for result, kind in (
        (_setup_result(_setup_report()), "setup"),
        (_setup_result(_verdict("approve")), "review"),
    ):
        ran = page.request.post(
            "/devflow/api/fake/worker/run",
            data={
                "project_id": project,
                "setup": True,
                "settings_wait": True,
                "result": result,
            },
            headers={"Origin": base_url},
        )
        assert ran.status == 200, ran.text()
        assert ran.json()["kind"] == kind

    # While its pull request waits for me, the settings step is not offered:
    # it comes after the setup's pull request.
    page.reload()
    setup = page.get_by_role("article", name="Setting it up", exact=True)
    steps = setup.get_by_role("list", name="Setup steps").locator(":scope > li")
    expect(steps.nth(2)).to_contain_text("The settings")
    expect(steps.nth(2)).to_have_attribute("data-state", "to-come")
    run = setup.get_by_role("region", name="The setup run")
    expect(run.get_by_text("Waits for you", exact=True)).to_be_visible()
    expect(setup.get_by_role("region", name="The settings")).to_have_count(0)

    # I merge it, and the settings step waits with the proposal.
    run.get_by_role("button", name="Merge").click()
    expect(steps.nth(0)).to_have_attribute("data-state", "done")
    expect(steps.nth(2)).to_have_attribute("data-state", "current")
    step = setup.get_by_role("region", name="The settings")
    expect(step.get_by_text("Guided", exact=True)).to_be_visible()
    expect(
        step.get_by_text("Its migrations are worth a look before they merge.")
    ).to_be_visible()
    expect(
        step.get_by_role("list", name="Paths that wait for you").get_by_text(
            "migrations/**"
        )
    ).to_be_visible()
    assert _axe_violations(page) == []
    capture(page, "devflow-project-setup-settings")

    # I accept: the step is done, and the settings page shows the preset and
    # the path.
    step.get_by_role("button", name="Accept").click()
    expect(steps.nth(2)).to_have_attribute("data-state", "done")
    expect(setup.get_by_role("region", name="The settings")).to_have_count(0)
    settings = _edit_settings(page)
    expect(
        settings.get_by_role("article", name="These settings").get_by_text(
            "Guided", exact=True
        )
    ).to_be_visible()
    expect(settings.get_by_label("Paths", exact=True)).to_have_value("migrations/**")
    saved = page.request.get(f"/devflow/api/projects/{project}/settings").json()
    assert saved["preset"] == "guided"
    assert saved["wait_paths"] == ["migrations/**"]


def test_i_give_a_projects_values(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with no .devflow/; what its setup run writes is staged on
    # the setup's branch.
    world = one_repository(name="Fodr")
    add_file(world, ".devflow/Dockerfile", RECIPE, branch="devflow/setup")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
        branch="devflow/setup",
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # The setup run lists one variable its tests need, and is reviewed; the
    # fake worker takes the settings as accepted.
    for result, kind in (
        (_setup_result(_setup_report()), "setup"),
        (_setup_result(_verdict("approve")), "review"),
    ):
        ran = page.request.post(
            "/devflow/api/fake/worker/run",
            data={"project_id": project, "setup": True, "result": result},
            headers={"Origin": base_url},
        )
        assert ran.status == 200, ran.text()
        assert ran.json()["kind"] == kind
    page.reload()
    setup = page.get_by_role("article", name="Setting it up", exact=True)
    setup.get_by_role("region", name="The setup run").get_by_role(
        "button", name="Merge"
    ).click()

    # The values step lists the one variable, with why, missing.
    steps = setup.get_by_role("list", name="Setup steps").locator(":scope > li")
    expect(steps.nth(3)).to_contain_text("The values its tests need")
    expect(steps.nth(3)).to_have_attribute("data-state", "current")
    values = setup.get_by_role("region", name="The values")
    rows = values.get_by_role("list", name="Values").get_by_role("listitem")
    expect(rows).to_have_count(1)
    expect(rows.nth(0)).to_contain_text("STRIPE_TEST_KEY")
    expect(rows.nth(0)).to_contain_text("The payment tests call Stripe.")
    expect(rows.nth(0).get_by_text("Missing", exact=True)).to_be_visible()

    # A value it did not list, which I add, does not do the step.
    values.get_by_role("button", name="Add a value").click()
    adding = values.get_by_role("form", name="Add a value")
    adding.get_by_label("Its name").fill("MAPS_TEST_KEY")
    adding.get_by_label("The value", exact=True).fill(MAPS_VALUE)
    adding.get_by_role("button", name="Give").click()
    expect(rows).to_have_count(2)
    expect(rows.nth(1)).to_contain_text("MAPS_TEST_KEY")
    expect(rows.nth(1).get_by_text("Given", exact=True)).to_be_visible()
    expect(steps.nth(3)).to_have_attribute("data-state", "current")
    assert _axe_violations(page) == []
    capture(page, "devflow-project-values")

    # I give the one it listed: the step is done.
    field = rows.nth(0).get_by_label("The value of STRIPE_TEST_KEY")
    expect(field).to_have_attribute("type", "password")
    field.fill(STRIPE_VALUE)
    rows.nth(0).get_by_role("button", name="Give").click()
    # The step is done, and closes to its one line: the worker is next.
    expect(steps.nth(3)).to_have_attribute("data-state", "done")
    expect(values).to_have_count(0)
    expect(steps.nth(4)).to_have_attribute("data-state", "current")

    # The page never shows a value again, nor does any route it reads.
    page.reload()
    expect(steps.nth(3)).to_have_attribute("data-state", "done")
    for value in (STRIPE_VALUE, MAPS_VALUE):
        assert value not in page.content()
    listed = page.request.get(f"/devflow/api/projects/{project}/values")
    assert listed.status == 200, listed.text()
    assert [(each["name"], each["asked"]) for each in listed.json()["values"]] == [
        ("STRIPE_TEST_KEY", True),
        ("MAPS_TEST_KEY", False),
    ]
    for path in (
        f"/devflow/api/projects/{project}/values",
        f"/devflow/api/projects/{project}/setup",
        "/devflow/api/projects",
        "/devflow/api/waiting",
    ):
        text = page.request.get(path).text()
        for value in (STRIPE_VALUE, MAPS_VALUE):
            assert value not in text, path


STRIPE_VALUE = "sk_test_throwaway-4f9a1c7e2b"
MAPS_VALUE = "maps-key-made-for-the-tests-71"


def test_i_send_a_failed_check_back(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A repository with its recipe and project.json on its default branch;
    # what the setup run sent back writes is staged on its branch.
    world = one_repository(name="Fodr")
    add_file(world, ".devflow/Dockerfile", RECIPE)
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    add_file(
        world,
        ".devflow/Dockerfile",
        RECIPE + b"RUN apt-get install -y redis-server\n",
        branch="devflow/setup",
    )
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
        branch="devflow/setup",
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()

    # Its check fails.
    output = (
        "> fodr@1.0.0 test\n"
        "> vitest run\n"
        " × src/cache.test.ts > connects to Redis\n"
        "Tests  1 failed | 24 passed"
    )
    failed = fake_check(page, base_url, project, 1, output)
    assert failed["kind"] == "check"

    # I send it back with a comment from the check.
    page.reload()
    setup = page.get_by_role("article", name="Setting it up", exact=True)
    check = setup.get_by_role("region", name="The newest check")
    expect(check.get_by_text("Failed", exact=True)).to_be_visible()
    check.get_by_role("button", name="Send back with a comment").click()
    comment = "The cache tests need Redis: install it and start it in the check."
    check.get_by_label("What should the setup do differently?").fill(comment)
    assert _axe_violations(page) == []
    capture(page, "devflow-project-check-failed")
    check.get_by_role("button", name="Send").click()
    run = setup.get_by_role("region", name="The setup run")
    expect(run.get_by_text("Queued", exact=True)).to_be_visible()
    expect(
        run.get_by_text("You sent the failed check back", exact=False)
    ).to_have_count(2)
    expect(check.get_by_role("button", name="Send back with a comment")).to_have_count(
        0
    )

    # The setup worker's run starts from the default branch with the output
    # and my comment in its brief, and its pull request opens.
    ran = _fake_setup(page, base_url, project, _setup_report())
    assert ran["kind"] == "setup"
    assert ran["branch"] == "devflow/setup"
    assert "## Sent back" in ran["brief"]
    assert "Tests  1 failed | 24 passed" in ran["brief"]
    assert comment in ran["brief"]
    page.reload()
    expect(
        run.get_by_role("link", name=re.compile(r"Pull request #\d+"))
    ).to_be_visible()
    expect(run.get_by_text("devflow/setup", exact=True)).to_be_visible()
    answered = page.request.get(f"/devflow/api/projects/{project}/setup").json()
    assert answered["setup"]["sent_back"] is True
    assert answered["setup"]["state"] == "reviewing"


# An item's detail, by kind: every kind in the same frame. See "What waits" in
# devflow/README.md.


def _claude_login_given(page: Page, base_url: str) -> None:
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()


def _read_card(page: Page, base_url: str, devflow_world, card: dict) -> str:
    """Connects a repository with one labelled card and its project.json,
    gives Dev flow a Claude login, and has the fake worker read the card as
    `card` says. The project's id."""
    world = one_repository()
    add_issue(world, CARD, CARD_TITLE, labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _claude_login_given(page, base_url)
    _read(page, base_url, project)
    _fake_read(page, base_url, project, [card])
    return project


def _a_waiting_card(page: Page, base_url: str, devflow_world) -> str:
    _built_card(page, base_url, devflow_world)
    return CARD_TITLE


def _a_runner_item(page: Page, base_url: str, devflow_world) -> str:
    world = one_repository()
    _decision(world)
    devflow_world(world)
    _connect_and_read(page, base_url)
    return "The vault"


def _a_feature(page: Page, base_url: str, devflow_world) -> str:
    """A feature whose review approved its head, with CI passed there, so
    it is ready and its merge is offered."""
    project, _ = _published_feature(page, base_url, devflow_world)
    reviewed = _fake_job(page, base_url, project, _verdict("approve"), None)
    assert reviewed["kind"] == "feature_review", reviewed
    _stage_ci(page, devflow_world, reviewed["head_commit"], [check_run("ci")])
    asked = page.request.post(
        "/devflow/api/fake/worker/run",
        data={"project_id": project, "keep_running": True},
        headers={"Origin": base_url},
    )
    assert asked.status == 204, asked.text()
    return "The feature's branch"


def _a_question_card(page: Page, base_url: str, devflow_world) -> str:
    _read_card(
        page,
        base_url,
        devflow_world,
        {"number": CARD, "outcome": "questions", "questions": QUESTIONS, "missing": []},
    )
    return CARD_TITLE


def _a_stopped_card(page: Page, base_url: str, devflow_world) -> str:
    project = _card_project(page, base_url, devflow_world)
    for _ in range(2):
        _fake_run(page, base_url, project, _failure(True, TURNS), leave_no_commit=True)
    return CARD_TITLE


def _a_card_stopped_by_me(page: Page, base_url: str, devflow_world) -> str:
    project = _card_project(page, base_url, devflow_world)
    _fake_run(page, base_url, project, _stopped_result())
    return CARD_TITLE


def _a_feature_stopped_by_me(page: Page, base_url: str, devflow_world) -> str:
    project, _ = _published_feature(page, base_url, devflow_world)
    held = _fake_run(page, base_url, project, _stopped_result())
    assert held["kind"] == "feature_review", held
    return "The feature's branch"


def _a_stopped_project(page: Page, base_url: str, devflow_world) -> str:
    devflow_world(one_repository())
    project = _connect_and_read(page, base_url)
    staged = page.request.get("/devflow/api/fake/world").json()
    add_activity(staged, 1, after=LANDED, at=LATER)
    devflow_world(staged)
    _read(page, base_url, project)
    return "Something landed on main that Dev flow did not put there"


def _a_decision(page: Page, base_url: str, devflow_world) -> str:
    _built_card(page, base_url, devflow_world, green=True)
    return "Show it on every list"


def _a_refused_card(page: Page, base_url: str, devflow_world) -> str:
    _read_card(page, base_url, devflow_world, _thin(CARD, THIN))
    return CARD_TITLE


def _a_failing_project(page: Page, base_url: str, devflow_world) -> str:
    project = _card_project(page, base_url, devflow_world)
    for failure in range(6):
        if failure > 0:
            ended = page.request.post(
                "/devflow/api/fake/end-wait",
                data={"project_id": project},
                headers={"Origin": base_url},
            )
            assert ended.status == 204, ended.text()
        _fake_run(page, base_url, project, _failure(False, LOGGED_OUT))
    return f"Dev flow cannot run {NAME}'s cards: {LOGGED_OUT}"


def _a_setup_item(page: Page, base_url: str, devflow_world) -> str:
    """A setup run that stopped before it had a pull request."""
    _open_project(page, base_url, devflow_world, one_repository())
    project = page.url.rsplit("/", 1)[1]
    _claude_login_given(page, base_url)
    _read(page, base_url, project)
    stopped = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "setup": True,
            "leave_no_commit": True,
            "result": {
                **_failure(True, "The run reached its time limit, 90 minutes"),
                "failure": {
                    "reason": "The run reached its time limit, 90 minutes",
                    "in_card": True,
                    "timed_out": True,
                },
                "duration_ms": 5400000,
            },
        },
        headers={"Origin": base_url},
    )
    assert stopped.status == 200, stopped.text()
    return f"{NAME}'s setup stopped"


# Each kind: how it is staged, how many primary buttons its detail has, and
# the screenshot it is captured as at desktop and phone width, if any.
DETAIL_KINDS = {
    "waiting-card": (_a_waiting_card, 1, "devflow-item-card"),
    "runner-item": (_a_runner_item, 1, None),
    "feature": (_a_feature, 1, "devflow-item-feature"),
    "question-card": (_a_question_card, 1, "devflow-item-questions"),
    "stopped-card": (_a_stopped_card, 1, "devflow-item-stopped"),
    "card-stopped-by-me": (_a_card_stopped_by_me, 1, None),
    "feature-stopped-by-me": (_a_feature_stopped_by_me, 1, None),
    "stopped-project": (_a_stopped_project, 1, None),
    "decision": (_a_decision, 0, None),
    "refused-card": (_a_refused_card, 0, None),
    "failing-project": (_a_failing_project, 0, None),
    "setup-item": (_a_setup_item, 0, None),
}

DETAIL_STYLE = """element => {
    const style = getComputedStyle(element);
    const family = value => value.split(",")[0].trim().replace(/^["']|["']$/g, "");
    const probe = document.createElement("div");
    document.body.append(probe);
    const token = (name, property) => {
        probe.style.setProperty(property, `var(${name})`);
        return getComputedStyle(probe).getPropertyValue(property);
    };
    const resolved = {
        heading: family(token("--font-heading", "font-family")),
        mono: family(token("--font-mono", "font-family")),
        hero: token("--text-hero-size", "font-size"),
    };
    probe.remove();
    return {
        family: family(style.fontFamily),
        size: style.fontSize,
        transform: style.textTransform,
        weight: style.fontWeight,
        resolved,
    };
}"""


@pytest.mark.parametrize("kind", list(DETAIL_KINDS))
def test_every_item_kind_follows_the_detail_rule(
    page: Page, invite_code: str, base_url: str, devflow_world, kind: str
):
    page.set_viewport_size(VIEWPORTS["desktop"])
    register_and_log_in(page, invite_code, base_url)
    stage, primaries, shot = DETAIL_KINDS[kind]
    title = stage(page, base_url, devflow_world)

    for viewport in ("desktop", "phone") if shot else ("desktop",):
        page.set_viewport_size(VIEWPORTS[viewport])
        page.goto("/devflow")
        item = _item(page, title)
        expect(item).to_be_visible()

        # The where line in the mono font, naming the repository, above the
        # title, which is the hero label: the heading font at the hero size.
        where = item.locator(":scope > header > p").first
        expect(where).to_contain_text(f"{OWNER}/{NAME}")
        hero = item.get_by_role("heading", level=2, name=title)
        expect(hero).to_be_visible()
        look = where.evaluate(DETAIL_STYLE)
        assert look["family"] == look["resolved"]["mono"], (kind, look)
        look = hero.evaluate(DETAIL_STYLE)
        assert look["family"] == look["resolved"]["heading"], (kind, look)
        assert look["size"] == look["resolved"]["hero"], (kind, look)
        assert (look["transform"], look["weight"]) == ("uppercase", "600"), kind
        assert where.bounding_box()["y"] < hero.bounding_box()["y"], kind

        # Exactly one primary button where the kind acts, none where it
        # does not: a button of the primary kind, which has the accent fill
        # while it can be pressed.
        accent = page.evaluate(
            """() => {
                const probe = document.createElement("div");
                probe.style.backgroundColor = "var(--accent)";
                document.body.append(probe);
                const value = getComputedStyle(probe).backgroundColor;
                probe.remove();
                return value;
            }"""
        )
        filled = [
            button
            for button in item.get_by_role("button").all()
            if button.is_visible()
            and re.search(r"(^|\s)_?primary", button.get_attribute("class") or "")
        ]
        for button in filled:
            if button.is_enabled():
                assert (
                    button.evaluate("b => getComputedStyle(b).backgroundColor")
                    == accent
                ), (kind, button.inner_text())
        assert len(filled) == primaries, (kind, viewport, filled)

        page.evaluate("document.fonts.ready.then(() => null)")
        for check in (assert_headings_are_capitals, assert_nothing_scrolls_sideways):
            check(page)
        if shot:
            capture(page, shot)
        if shot and viewport == "phone":
            # The full page at phone width puts the sticky actions over the
            # tiles, so the detail from its where line down, as it is seen.
            item.locator(":scope > header").evaluate(
                "header => header.scrollIntoView({block: 'start'})"
            )
            page.screenshot(path=str(SCREENSHOTS / f"{shot}-detail-phone.png"))


def _fake_build(
    page: Page, base_url: str, project: str, usage: dict, cost: float | None = None
) -> dict:
    """The fake worker takes the project's next job, a card's build, and ends
    it succeeded with Claude's `usage` figures, and with `cost`, Claude
    Code's estimate in US dollars, when given. What it answered."""
    ran = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "succeeded",
                "report": _report(),
                "failure": None,
                "check": {"exit_code": 0, "output": "Tests: 12 passed"},
                "auth_method": "oauth_token",
                "usage": {"usage": usage, "num_turns": 23}
                | ({} if cost is None else {"total_cost_usd": cost}),
                "duration_ms": 184213,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert ran.status == 200, ran.text()
    return ran.json()


def _used(page: Page, base_url: str, project: str, days: dict[int, int]) -> None:
    """Gives the project a day of use for each entry of `days`, so many days
    before today and its tokens, through the fake, which copies the project's
    last finished run onto that day."""
    staged = page.request.post(
        "/devflow/api/fake/usage-history",
        data={
            "project_id": project,
            "days": [
                {"days_ago": ago, "tokens": tokens} for ago, tokens in days.items()
            ],
        },
        headers={"Origin": base_url},
    )
    assert staged.status == 204, staged.text()


def _stockholm_date(days_ago: int) -> date:
    """The day so many days before today in Dev flow's zone."""
    return datetime.now(ZoneInfo("Europe/Stockholm")).date() - timedelta(days=days_ago)


def _chart_day(days_ago: int) -> str:
    """`_stockholm_date` as the chart says it: "27 Nov"."""
    day = _stockholm_date(days_ago)
    return f"{day.day} {day.strftime('%b')}"


def test_usage_and_history(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A labelled feature of two cards, and the project's .devflow/project.json.
    world = one_repository()
    add_issue(
        world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61, 62]
    )
    add_issue(world, 61, "Make the feature's branch")
    add_issue(world, 62, "Close a landed card's issue")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    _merges_by_itself(page, base_url, project)
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61), _ready(62)])

    def build(usage: dict) -> dict:
        return _fake_build(page, base_url, project, usage)

    # The fake worker builds the first card, with Claude's usage cache figures
    # and all: 41 + 18,204 + 6,113 = 24,358 tokens, 512,870 cache reads. CI
    # passes and the reviewer approves, 12 + 840 = 852 tokens, so it lands in
    # the feature's branch and the second card is built, 100 + 900 tokens.
    first = build(
        {
            "input_tokens": 41,
            "cache_creation_input_tokens": 18204,
            "cache_read_input_tokens": 512870,
            "output_tokens": 6113,
        }
    )
    assert first["card"] == 61
    _stage_ci(page, devflow_world, first["head_commit"], [check_run("test")])
    _fake_review(page, base_url, project, _verdict("approve"))
    second = build({"input_tokens": 100, "output_tokens": 900})
    assert second["card"] == 62

    # The tab, selected at its own address, over the last 30 days. Where it
    # sits among the tabs is TABS's to check.
    page.goto("/devflow/usage")
    expect(page.get_by_role("tab", selected=True)).to_have_text("Usage and history")
    expect(page.get_by_label("Period")).to_have_value("30d")

    # The project's table, closed, then open with its feature, which opens into
    # its cards and its own runs, the reading among them.
    table = page.get_by_role("region", name=re.compile(r"^Usage of "))
    expect(table).to_have_count(1)
    rows = table.get_by_role("row")
    expect(rows).to_have_count(2)
    table.locator("tbody tr").first.get_by_role("button").click()
    table.get_by_role("button", name="Open #60 The feature's branch").click()
    expect(rows.nth(2)).to_contain_text("#60 The feature's branch")
    expect(rows.nth(3)).to_contain_text("#61 Make the feature's branch")
    expect(rows.nth(4)).to_contain_text("#62 Close a landed card's issue")
    expect(rows.nth(5)).to_contain_text("The feature's own runs")

    # Tokens by the one definition, cache reads beside them: the feature its
    # cards' and its own together, each card its own runs, the build and its
    # review for the first.
    expect(rows.nth(2)).to_contain_text("26,210")
    expect(rows.nth(2)).to_contain_text("512,870")
    expect(rows.nth(3)).to_contain_text("25,210")
    expect(rows.nth(3)).to_contain_text("512,870")
    expect(rows.nth(4)).to_contain_text("1,000")
    if viewport == "desktop":
        # Runs, then agent time, on desktop each in its own column.
        cells = rows.nth(3).get_by_role("cell")
        expect(cells.nth(1)).to_have_text("2")
        expect(cells.nth(4)).to_have_text("25,210")
    else:
        # On a phone, each row's numbers fold under its name.
        expect(rows.nth(3)).to_contain_text("2 runs")
        expect(rows.nth(3)).to_contain_text("25,210 tokens")
    expect(
        table.get_by_role("link", name="Make the feature's branch", exact=True)
    ).to_have_attribute("href", f"/devflow/projects/{project}/cards/61")
    expect(
        table.get_by_role("link", name="The feature's branch", exact=True)
    ).to_have_attribute("href", f"/devflow/projects/{project}/features/60")

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []
    # The pointer off the rows, on the page's title, so no row shows hovered.
    page.get_by_role("heading", name="Dev flow", exact=True).hover()
    capture(page, "devflow-usage")

    # The period is in the address, and the last 7 days still has them.
    page.get_by_label("Period").select_option("7d")
    expect(page).to_have_url(re.compile(r"/devflow/usage\?period=7d$"))
    expect(page.get_by_role("region", name=re.compile(r"^Usage of "))).to_have_count(1)


def test_tokens_per_day(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """Under each project's table on Usage and history, its tokens per day
    over the last 30 days: two projects with a month of use, each day's
    tokens shown on hover and from the keyboard. See "Usage and history" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 61, "Make the feature's branch", labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _another_repository(world, FODR, "fodr", 12, "Dark mode")
    _open_project(page, base_url, devflow_world, world)
    platform = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    _read(page, base_url, platform)
    fodr = _connect(page, base_url, FODR)

    # Each project's card is read and built today: 41 + 18,204 + 6,113 =
    # 24,358 tokens for Personal-platform's, 1,200 + 800 for fodr's.
    _fake_read(page, base_url, platform, [_ready(61)])
    assert (
        _fake_build(
            page,
            base_url,
            platform,
            {
                "input_tokens": 41,
                "cache_creation_input_tokens": 18204,
                "cache_read_input_tokens": 512870,
                "output_tokens": 6113,
            },
        )["card"]
        == 61
    )
    _fake_read(page, base_url, fodr, [_ready(12)])
    assert (
        _fake_build(page, base_url, fodr, {"input_tokens": 1200, "output_tokens": 800})[
            "card"
        ]
        == 12
    )

    # A month of use before today: Personal-platform most days, with days of
    # none between, its most 3 days ago; fodr now and then, its most 9 days
    # ago. The 99,999 of 30 days ago is a day before the 30, so never shown.
    _used(
        page,
        base_url,
        platform,
        {
            ago: tokens
            for ago, tokens in zip(
                range(1, 31),
                [
                    31_200,
                    18_400,
                    52_000,
                    0,
                    0,
                    27_900,
                    12_300,
                    40_100,
                    22_800,
                    9_600,
                    0,
                    0,
                    35_400,
                    28_100,
                    16_700,
                    44_900,
                    19_200,
                    0,
                    0,
                    30_300,
                    11_800,
                    25_600,
                    38_700,
                    14_400,
                    0,
                    0,
                    21_100,
                    33_300,
                    8_900,
                    99_999,
                ],
                strict=True,
            )
            if tokens
        },
    )
    _used(page, base_url, fodr, {2: 6_400, 9: 12_800, 16: 3_200, 23: 9_600, 29: 4_100})

    page.goto("/devflow/usage")
    platform_table = page.get_by_role("region", name="Usage of Personal-platform")
    expect(platform_table).to_be_visible()

    # A chart under each project's table, named for screen readers with its
    # highest day, 30 bars each, the first and last dates under them.
    chart = page.get_by_role(
        "img",
        name=f"Personal-platform: tokens per day, the last 30 days, most on {_chart_day(3)}, 52,000",
    )
    expect(chart).to_be_visible()
    other = page.get_by_role(
        "img",
        name=f"fodr: tokens per day, the last 30 days, most on {_chart_day(9)}, 12,800",
    )
    expect(other).to_be_visible()
    expect(chart.locator("[data-day]")).to_have_count(30)
    expect(other.locator("[data-day]")).to_have_count(30)
    table_box = platform_table.bounding_box()
    chart_box = chart.bounding_box()
    assert chart_box["y"] >= table_box["y"] + table_box["height"], (
        table_box,
        chart_box,
    )
    figure = page.locator("figure").filter(has=chart)
    expect(figure).to_contain_text("52,000")
    expect(figure).to_contain_text(_chart_day(29))
    expect(figure).to_contain_text(_chart_day(0))

    # Pointed at, the bar of 3 days ago shows its day and tokens.
    day = figure.get_by_role("slider", name="A day of Personal-platform's tokens")
    box = day.bounding_box()
    page.mouse.move(box["x"] + box["width"] * 26.5 / 30, box["y"] + box["height"] / 2)
    expect(figure).to_contain_text(f"{_chart_day(3)}: 52,000 tokens")
    expect(chart.locator("[data-active='true']")).to_have_attribute(
        "data-day", _stockholm_date(3).isoformat()
    )

    # From the keyboard: today's at focus, then the arrows move along the days.
    page.get_by_role("heading", name="Dev flow", exact=True).hover()
    day.focus()
    expect(figure).to_contain_text(f"{_chart_day(0)}: 24,358 tokens")
    for _ in range(3):
        page.keyboard.press("ArrowLeft")
    expect(figure).to_contain_text(f"{_chart_day(3)}: 52,000 tokens")
    expect(day).to_have_attribute("aria-valuetext", f"{_chart_day(3)}: 52,000 tokens")
    page.keyboard.press("Home")
    expect(figure).to_contain_text(f"{_chart_day(29)}: 8,900 tokens")

    page.evaluate("document.fonts.ready.then(() => null)")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    assert _axe_violations(page) == []

    # Shown with the day of most use pointed at.
    page.keyboard.press("End")
    for _ in range(3):
        page.keyboard.press("ArrowLeft")
    capture(page, "devflow-usage-per-day")


PAUSED_LINE = "The run under way finishes; nothing new starts."
EVERYTHING_PAUSED = "Everything is paused. Nothing new starts in any project."


def _project_card(page: Page, full_name: str) -> Locator:
    """The project's card on the Projects tab."""
    return page.get_by_role("region", name="Projects").get_by_role(
        "article", name=full_name
    )


def test_i_pause_a_project_and_continue_it(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """On a project's page I pause it: Pause turns to Continue, the badge
    Paused and its line show, and its card on the Projects tab says Paused;
    Continue clears both. See "The pages" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 7, "Show the plan's date in the header", labels=["devflow"])
    _open_project(page, base_url, devflow_world, world)
    project_page = page.url
    _read(page, base_url, project_page.rsplit("/", 1)[1])
    page.reload()
    full_name = f"{OWNER}/Personal-platform"
    header = page.locator("main header").filter(
        has=page.get_by_role("heading", name="Personal-platform", exact=True)
    )

    header.get_by_role("button", name="Pause", exact=True).click()
    expect(header.get_by_role("button", name="Continue", exact=True)).to_be_visible()
    expect(header.get_by_text("Paused", exact=True)).to_be_visible()
    expect(header.get_by_text(PAUSED_LINE, exact=False)).to_be_visible()
    workers = page.get_by_role("article", name="Workers", exact=True)
    expect(workers.get_by_text("you paused it", exact=False)).to_be_visible()
    _looks_right(page)
    capture(page, "devflow-project-paused")

    # Its card says so, as a badge, not a banner: I paused it myself.
    page.goto("/devflow/projects")
    card = _project_card(page, full_name)
    expect(card.get_by_text("Paused", exact=True)).to_be_visible()
    expect(card.get_by_text("you paused it", exact=False)).to_have_count(0)

    # Continue clears both.
    page.goto(project_page)
    header.get_by_role("button", name="Continue", exact=True).click()
    expect(header.get_by_role("button", name="Pause", exact=True)).to_be_visible()
    expect(header.get_by_text("Paused", exact=True)).to_have_count(0)
    expect(header.get_by_text(PAUSED_LINE, exact=False)).to_have_count(0)
    page.goto("/devflow/projects")
    expect(_project_card(page, full_name)).to_be_visible()
    expect(
        _project_card(page, full_name).get_by_text("Paused", exact=True)
    ).to_have_count(0)


def test_i_pause_everything(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """The switch on Running now pauses every project of mine and shows the
    strip under the heading on every tab, Waiting for you among them;
    Continue everything clears it. See "The pages" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 7, "Show the plan's date in the header", labels=["devflow"])
    _another_repository(world, FODR, "fodr", 12, "Dark mode")
    _open_project(page, base_url, devflow_world, world)
    _connect(page, base_url, FODR)

    page.goto("/devflow/running")
    switch = page.get_by_role("switch", name="Pause everything")
    expect(switch).not_to_be_checked()
    switch.click()
    expect(switch).to_be_checked()
    strip = page.locator("main > header").get_by_text(EVERYTHING_PAUSED)
    expect(strip).to_be_visible()
    # Next up says why fodr's card does not run. Its row alone: whether
    # Personal-platform's card is listed too depends on how far its read has
    # got, and it would say the same.
    next_up = page.get_by_role("table", name="Next up")
    fodr_row = next_up.get_by_role("row").filter(has_text="Dark mode")
    expect(
        fodr_row.get_by_text("Everything is paused: continue it").filter(visible=True)
    ).to_be_visible()

    # Every project of mine is held, both of them.
    listed = page.request.get("/devflow/api/projects").json()["projects"]
    assert [each["summary"]["workers"]["held"] for each in listed] == [
        "all_paused",
        "all_paused",
    ]
    _looks_right(page)
    capture(page, "devflow-everything-paused")

    # The strip is on Waiting for you too, with Continue everything.
    page.goto("/devflow")
    expect(strip).to_be_visible()
    page.get_by_role("button", name="Continue everything").click()
    expect(strip).to_have_count(0)
    page.goto("/devflow/running")
    expect(page.get_by_role("switch", name="Pause everything")).not_to_be_checked()
    expect(strip).to_have_count(0)


def test_i_give_a_project_a_schedule(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """On the settings page I choose "By schedule" and add "Night", Monday to
    Friday 22:00 to 06:00, and "Weekend", which I switch off; the project's
    page shows Night on, with when it runs next, and switching it off there
    says nothing runs. See "The pages" and "Schedules" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    _open_project(page, base_url, devflow_world)
    content = _edit_settings(page)
    card = content.get_by_role("article", name="When it runs", exact=True)
    expect(card.get_by_role("radio", name="Always")).to_be_checked()
    expect(card.get_by_text("Times are in Europe/Stockholm")).to_be_visible()

    # By schedule, saved at once.
    card.get_by_role("radio", name="By schedule").check()
    expect(card.get_by_role("radio", name="By schedule")).to_be_checked()
    expect(card.get_by_text("Saved.", exact=True)).to_be_visible()

    def add(name: str, days: list[str], start: str, end: str) -> None:
        card.get_by_role("button", name="Add a schedule").click()
        editor = card.get_by_role("form", name="A new schedule")
        editor.get_by_label("Name").fill(name)
        for day in days:
            editor.get_by_role("checkbox", name=day, exact=True).check()
        editor.get_by_label("From").fill(start)
        editor.get_by_label("To").fill(end)
        editor.get_by_role("button", name="Add", exact=True).click()
        expect(card.get_by_role("switch", name=name, exact=True)).to_be_checked()

    add("Night", ["Mon", "Tue", "Wed", "Thu", "Fri"], "22:00", "06:00")
    expect(card.get_by_text("Mon–Fri · 22:00–06:00")).to_be_visible()
    add("Weekend", ["Sat", "Sun"], "10:00", "14:00")
    weekend = card.get_by_role("switch", name="Weekend", exact=True)
    weekend.uncheck()
    expect(weekend).not_to_be_checked()
    # Disabled while it saves, so enabled again once Dev flow has it.
    expect(weekend).to_be_enabled()
    expect(card.get_by_text("Sat, Sun · 10:00–14:00")).to_be_visible()

    # Saved as Dev flow holds it, without the page's Save.
    project_id = page.url.split("/")[-2]
    saved = page.request.get(f"/devflow/api/projects/{project_id}/schedules").json()
    assert saved["runs_by"] == "schedule"
    assert [(each["name"], each["active"]) for each in saved["schedules"]] == [
        ("Night", True),
        ("Weekend", False),
    ]
    card.scroll_into_view_if_needed()
    _looks_right(page)
    capture(page, "devflow-settings-schedules")

    # The project's page: Night on, and when it runs next, or until when.
    _back_to_project(page)
    when = page.get_by_role("article", name="When it runs", exact=True)
    expect(when.get_by_role("switch", name="Night", exact=True)).to_be_checked()
    expect(when.get_by_role("switch", name="Weekend", exact=True)).not_to_be_checked()
    expect(
        when.get_by_text(
            re.compile(
                r"^(Runs next (Mon|Tue|Wed|Thu|Fri) 22:00|Running now, until 06:00)$"
            )
        )
    ).to_be_visible()
    expect(when.get_by_role("link", name="Edit schedules")).to_have_attribute(
        "href", f"/devflow/projects/{project_id}/settings#settings-schedules"
    )
    _looks_right(page)
    capture(page, "devflow-project-when-it-runs")

    # Switched off there, nothing runs.
    when.get_by_role("switch", name="Night", exact=True).uncheck()
    expect(when.get_by_text("No schedule is on, so nothing runs")).to_be_visible()


def test_i_run_a_feature_now_anyway(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """Outside the project's schedule, I press "Run now anyway" on a feature's
    page, and Running now's queue no longer says its cards are outside the
    schedule. See "Schedules" and "A feature's page" in devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 60, "Night work", labels=["devflow"], sub_issues=[61, 62])
    add_issue(world, 61, "Plan the night")
    add_issue(world, 62, "Run the night")
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61), _ready(62)])

    # By schedule with no window on: outside it whatever the time.
    scheduled = page.request.put(
        f"/devflow/api/projects/{project}/schedules",
        data={"runs_by": "schedule", "schedules": []},
        headers={"Origin": base_url},
    )
    assert scheduled.status == 200, scheduled.text()

    # Running now's queue says the feature's first card is outside it.
    page.goto(f"{base_url}/devflow/running")
    next_up = page.get_by_role("article", name=re.compile("^Next up"))
    expect(next_up).to_contain_text("No schedule is on")

    # The feature's page offers to run it anyway.
    page.goto(f"{base_url}/devflow/projects/{project}/features/60")
    expect(
        page.get_by_text(
            "Runs this outside the schedule until it has nothing left to run."
        )
    ).to_be_visible()
    run_now = page.get_by_role("button", name="Run now anyway")
    expect(run_now).to_be_visible()
    _looks_right(page)
    capture(page, "devflow-feature-run-now")

    run_now.click()
    expect(
        page.get_by_text(
            "Running outside the schedule, until it has nothing left to run"
        )
    ).to_be_visible()
    expect(page.get_by_role("button", name="Back to the schedule")).to_be_visible()
    expect(page.get_by_role("button", name="Run now anyway")).to_have_count(0)
    _looks_right(page)
    capture(page, "devflow-feature-run-now-on")

    # Running now's queue no longer says the feature's cards are outside it.
    page.goto(f"{base_url}/devflow/running")
    next_up = page.get_by_role("article", name=re.compile("^Next up"))
    expect(
        next_up.get_by_role("link", name=re.compile("Plan the night")).first
    ).to_be_visible()
    expect(next_up).not_to_contain_text("No schedule is on")


def test_a_project_reaches_its_budget(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """A card's build used 1.2 million tokens today; on the settings page I
    switch the daily budget on at 1 million, and "Waiting for you" lists the
    project under Stopped, until midnight; "Raise the budget" opens the
    settings page at the field, and raising it to 2 million clears the item.
    See "Settings" under "Cards", "Waiting for you" and "Stops" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 61, "Make the feature's branch", labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    given = page.request.put(
        "/devflow/api/claude-login",
        data={"token": CLAUDE_TOKEN},
        headers={"Origin": base_url},
    )
    assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61)])

    # The build used 1,000,000 + 150,000 + 50,000 tokens, its cache reads
    # apart.
    built = _fake_build(
        page,
        base_url,
        project,
        {
            "input_tokens": 1_000_000,
            "cache_creation_input_tokens": 150_000,
            "cache_read_input_tokens": 4_000_000,
            "output_tokens": 50_000,
        },
    )
    assert built["card"] == 61

    # On the settings page the budget is off; switched on, its field takes
    # millions, and it is saved with the rest.
    page.reload()
    content = _edit_settings(page)
    usage = content.get_by_role("article", name="Usage", exact=True)
    daily = usage.get_by_role("switch", name="Daily budget")
    expect(daily).not_to_be_checked()
    expect(usage.get_by_label("Million tokens a day")).to_have_count(0)
    daily.check()
    millions = usage.get_by_label("Million tokens a day")
    millions.fill("1")
    expect(
        usage.get_by_text(
            "Counts input, cache writes and output. When reached, nothing new starts until midnight."
        )
    ).to_be_visible()
    content.get_by_role("button", name="Save", exact=True).click()
    expect(content.get_by_text("Saved.", exact=True)).to_be_visible()
    saved = page.request.get(f"/devflow/api/projects/{project}/settings").json()
    assert saved["daily_token_budget"] == 1_000_000
    usage.scroll_into_view_if_needed()
    _looks_right(page)
    capture(page, "devflow-settings-budget")

    # "Waiting for you" lists it under Stopped, with what it used of what,
    # and until when nothing new starts, midnight in Stockholm.
    page.goto("/devflow")
    stopped = page.get_by_role("region", name="Stopped")
    title = f"{OWNER}/{NAME} reached its daily budget"
    row = stopped.get_by_role("link", name=title)
    expect(row).to_be_visible()
    expect(row).to_contain_text("1.2 M of 1 M tokens")
    row.click()
    item = page.get_by_role("article", name=title)
    midnight = datetime.now(ZoneInfo("Europe/Stockholm")).date() + timedelta(days=1)
    expect(
        item.get_by_text(
            f"Nothing new starts until {midnight.strftime('%a')} 00:00, midnight in "
            "Europe/Stockholm. The run that went over finished."
        )
    ).to_be_visible()
    _looks_right(page)
    capture(page, "devflow-budget-reached")

    # Raise the budget opens the settings page at the field.
    item.get_by_role("link", name="Raise the budget").click()
    expect(page).to_have_url(
        re.compile(rf"/devflow/projects/{project}/settings#usage$")
    )
    millions = page.get_by_role("article", name="Usage", exact=True).get_by_label(
        "Million tokens a day"
    )
    expect(millions).to_have_value("1")
    expect(millions).to_be_in_viewport()

    # Raised to 2 million, the item is gone.
    millions.fill("2")
    page.get_by_role("button", name="Save", exact=True).click()
    expect(page.get_by_text("Saved.", exact=True)).to_be_visible()
    page.goto("/devflow")
    expect(page.get_by_role("tab", name="Waiting for you")).to_be_visible()
    expect(page.get_by_role("link", name=title)).to_have_count(0)


def test_a_project_continues_on_the_key_at_the_limit(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    """A project on "then the API key" whose build finds Claude's usage limit
    goes on on the key: its page says "Running on the API key until <time>",
    with what the key cost today of its cap, and the run on the key shows its
    spend on Usage and history. See "The API key" and "Stops" in
    devflow/README.md."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    add_issue(world, 61, "Make the feature's branch", labels=["devflow"])
    add_issue(world, 62, "Name the feature's branch", labels=["devflow"])
    add_file(
        world,
        ".devflow/project.json",
        json.dumps({"version": 1, "check": CHECK, "start": "", "url": ""}).encode(),
    )
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]
    for path, body in (
        ("/devflow/api/claude-login", {"token": CLAUDE_TOKEN}),
        ("/devflow/api/api-key", {"key": API_KEY}),
    ):
        given = page.request.put(path, data=body, headers={"Origin": base_url})
        assert given.status == 200, given.text()
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    _fake_read(page, base_url, project, [_ready(61), _ready(62)])

    # On the settings page, "Runs on" offers the three choices; continuing on
    # the key asks for its daily cap, proposing 20.
    content = _edit_settings(page)
    usage = content.get_by_role("article", name="Usage", exact=True)
    runs_on = usage.get_by_label("Runs on")
    expect(runs_on.locator("option")).to_have_text(
        [
            "Your subscription, waiting at its limit",
            "Your subscription, then the API key at its limit",
            "The API key only",
        ]
    )
    expect(usage.get_by_label("API key spend a day, USD")).to_have_count(0)
    runs_on.select_option(label="Your subscription, then the API key at its limit")
    cap = usage.get_by_label("API key spend a day, USD")
    expect(cap).to_have_value("20")
    expect(
        usage.get_by_text(
            "1 to 1000. Counted from Claude Code's estimate of each run. When "
            "reached, nothing new starts on the key until midnight."
        )
    ).to_be_visible()
    content.get_by_role("button", name="Save", exact=True).click()
    expect(content.get_by_text("Saved.", exact=True)).to_be_visible()
    saved = page.request.get(f"/devflow/api/projects/{project}/settings").json()
    assert (saved["runs_on"], saved["api_daily_usd"]) == (
        "subscription_then_api_key",
        20,
    )
    usage.scroll_into_view_if_needed()
    _looks_right(page)
    capture(page, "devflow-settings-usage")

    # The first build carries the subscription and finds the usage limit,
    # which resets in two hours.
    reset = datetime.now(UTC).replace(microsecond=0) + timedelta(hours=2)
    failed = page.request.post(
        "/devflow/api/fake/worker/run",
        data={
            "project_id": project,
            "result": {
                "outcome": "failed",
                "report": None,
                "failure": {
                    "reason": "Claude's usage limit was reached",
                    "in_card": False,
                    "resets_at": reset.isoformat(),
                },
                "check": None,
                "auth_method": None,
                "usage": {},
                "duration_ms": 1000,
                "stderr": "",
            },
        },
        headers={"Origin": base_url},
    )
    assert failed.status == 200, failed.text()

    # Its next build goes at once, on the key, and costs 3.25 USD.
    built = _fake_build(
        page,
        base_url,
        project,
        {"input_tokens": 1_000, "output_tokens": 500},
        cost=3.25,
    )
    assert built["card"] in (61, 62)

    # The project's page says it runs on the key until the reset, as wall
    # clock in Stockholm, with what the key cost today of its cap.
    page.goto(f"/devflow/projects/{project}")
    until = reset.astimezone(ZoneInfo("Europe/Stockholm")).strftime("%a %H:%M")
    expect(page.get_by_text(f"Running on the API key until {until}")).to_be_visible()
    expect(page.get_by_text("3.25 of 20 USD today")).to_be_visible()
    expect(
        page.get_by_text(
            "Your subscription, then the API key at its limit, 20 USD a day on the key"
        )
    ).to_be_visible()

    # Usage and history shows the day's spend on the key.
    page.goto("/devflow/usage")
    spend = page.get_by_role("list", name=f"{NAME}: API key spend per day")
    expect(spend).to_contain_text("API key: 3.25 USD")
    spend.scroll_into_view_if_needed()
    _looks_right(page)
    capture(page, "devflow-usage-api-spend")


CODE = re.compile(r"'(dfe_[A-Za-z0-9_-]{22})'")


def _enroll(playwright, base_url: str, code: str, name: str, os: str, arch: str):
    """The machine program enrolling with the code and saying hello, as it
    calls Dev flow: no cookie and no Origin, from outside the browser, its key
    in the header once it has one. Answers the machine's id."""
    machine = playwright.request.new_context(
        base_url=base_url, ignore_https_errors=True
    )
    try:
        enrolled = machine.post(
            "/devflow/api/machine/enroll",
            data={"code": code, "name": name, "os": os, "arch": arch},
        )
        assert enrolled.status == 200, enrolled.text()
        answer = enrolled.json()
        assert re.fullmatch(r"dfm_[A-Za-z0-9_-]{43}", answer["key"])
        hello = machine.post(
            "/devflow/api/machine/hello",
            data={
                "name": name,
                "os": os,
                "arch": arch,
                "program_version": "4f2a9c1b7e30",
                "docker": "running",
            },
            headers={"Authorization": f"Bearer {answer['key']}"},
        )
        assert hello.status == 200, hello.text()
        assert set(hello.json()) == {"release"}
        # The code is spent.
        again = machine.post(
            "/devflow/api/machine/enroll",
            data={"code": code, "name": name, "os": os, "arch": arch},
        )
        assert again.status == 401
        return answer["machine_id"]
    finally:
        machine.dispose()


def _add_a_machine(page: Page) -> Locator:
    """Presses "Add a machine" and answers its dialog, once both commands
    show."""
    page.get_by_role("button", name="Add a machine").click()
    dialog = page.get_by_role("dialog", name="Add a machine")
    expect(dialog.get_by_role("region", name="On any machine")).to_be_visible()
    expect(
        dialog.get_by_role("region", name="On the machine the platform runs on")
    ).to_be_visible()
    return dialog


def _new_machine(page: Page, playwright, base_url: str, name: str, os: str, arch: str):
    """A machine enrolled with a new code from "Add a machine" on the Machines
    tab, as the machine program enrols one. Answers its id."""
    page.goto("/devflow/machines")
    code = CODE.findall(
        _add_a_machine(page)
        .get_by_role("region", name="On any machine")
        .locator("code")
        .inner_text()
    )[-1]
    page.get_by_role("dialog", name="Add a machine").get_by_role(
        "button", name="Close"
    ).click()
    return _enroll(playwright, base_url, code, name, os, arch)


def test_i_place_workers_on_a_machine(
    page: Page,
    invite_code: str,
    base_url: str,
    viewport: str,
    devflow_world,
    playwright,
):
    register_and_log_in(page, invite_code, base_url)
    _new_machine(page, playwright, base_url, "mac-mini", "macos", "arm64")
    box = _new_machine(page, playwright, base_url, "build-box", "linux", "x64")
    aged = page.request.post(
        "/devflow/api/fake/machine-age",
        data={"machine_id": box, "minutes": 10},
        headers={"Origin": base_url},
    )
    assert aged.status == 204, aged.text()

    _open_project(page, base_url, devflow_world, one_repository(name="Fodr"))
    where = page.get_by_role("article", name="Workers").get_by_role(
        "region", name="Where its workers run"
    )
    # Placed nowhere yet, it says so, and lists both machines, online or not.
    expect(where.get_by_text("Choose where its workers run.")).to_be_visible()
    rows = where.get_by_role("list", name="Machines").get_by_role("listitem")
    expect(rows).to_have_count(2)
    mini = rows.filter(has_text="mac-mini")
    expect(mini.get_by_text("Online", exact=True)).to_be_visible()
    expect(
        rows.filter(has_text="build-box").get_by_text("Offline", exact=True)
    ).to_be_visible()
    expect(where.get_by_label("Memory per worker, GB")).to_have_value("2")

    # Two workers on the Mac mini: the card shows them wanted there, after a
    # reload too.
    where.get_by_label("Workers on mac-mini").fill("2")
    where.get_by_role("button", name="Save").click()
    expect(mini.get_by_text("2 workers wanted")).to_be_visible()
    expect(where.get_by_text("Choose where its workers run.")).to_have_count(0)
    page.reload()
    expect(mini.get_by_text("2 workers wanted")).to_be_visible()
    expect(where.get_by_label("Workers on mac-mini")).to_have_value("2")

    # One more on the offline box, and 4 GB each, for the picture.
    where.get_by_label("Workers on build-box").fill("1")
    where.get_by_label("Memory per worker, GB").fill("4")
    where.get_by_role("button", name="Save").click()
    expect(
        rows.filter(has_text="build-box").get_by_text("1 worker wanted")
    ).to_be_visible()
    page.reload()
    expect(where.get_by_label("Memory per worker, GB")).to_have_value("4")
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-workers-placed")


def test_i_add_a_machine(
    page: Page, invite_code: str, base_url: str, viewport: str, playwright
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/machines")
    expect(page.get_by_text("No machine yet")).to_be_visible()

    # Both commands, holding the one code and their two addresses.
    dialog = _add_a_machine(page)
    command = dialog.get_by_role("region", name="On any machine").locator("code")
    local = dialog.get_by_role(
        "region", name="On the machine the platform runs on"
    ).locator("code")
    code = CODE.findall(command.inner_text())[-1]
    assert command.inner_text() == (
        f"curl -fsSL '{base_url}/devflow/machine/install.sh'"
        f" | sh -s -- '{base_url}' '{code}'"
    )
    assert re.fullmatch(
        rf"curl -fsSL '(http://[^']+)/devflow/machine/install\.sh' \| sh -s -- '\1' '{code}'",
        local.inner_text(),
    ), local.inner_text()
    expect(dialog.get_by_text(re.compile(r"^The code expires at"))).to_be_visible()
    expect(
        dialog.get_by_role("button", name="Copy the command", exact=True)
    ).to_be_visible()
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-add-machine")
    dialog.get_by_role("button", name="Close").click()
    expect(dialog).to_be_hidden()

    # A machine enrolled with the code shows as online, with its version.
    _enroll(playwright, base_url, code, "mac-mini", "macos", "arm64")
    machines = page.get_by_role("list", name="Machines")
    mini = machines.get_by_role("listitem").filter(has_text="mac-mini")
    expect(mini.get_by_text("Online", exact=True)).to_be_visible(timeout=15_000)
    expect(mini).to_contain_text("macOS · arm64 · Program 4f2a9c1b7e30")
    expect(mini.get_by_text("Docker running")).to_be_visible()

    # A second, quiet for ten minutes, shows since when it was not seen.
    second = CODE.findall(
        _add_a_machine(page)
        .get_by_role("region", name="On any machine")
        .locator("code")
        .inner_text()
    )[-1]
    page.get_by_role("dialog", name="Add a machine").get_by_role(
        "button", name="Close"
    ).click()
    box = _enroll(playwright, base_url, second, "build-box", "linux", "x64")
    aged = page.request.post(
        "/devflow/api/fake/machine-age",
        data={"machine_id": box, "minutes": 10},
        headers={"Origin": base_url},
    )
    assert aged.status == 204, aged.text()
    page.reload()
    rows = page.get_by_role("list", name="Machines").get_by_role("listitem")
    expect(rows).to_have_count(2)
    quiet = rows.filter(has_text="build-box")
    expect(quiet.get_by_text("Offline", exact=True)).to_be_visible()
    expect(quiet.get_by_text(re.compile(r"^Not seen since"))).to_be_visible()
    expect(rows.filter(has_text="mac-mini").get_by_text("Online")).to_be_visible()
    expect(page.get_by_role("article", name="Machines")).to_contain_text(
        "1 of 2 online"
    )
    for check in (
        assert_headings_are_capitals,
        assert_nothing_scrolls_sideways,
        assert_active_is_inverted,
        assert_nothing_active_is_amber,
    ):
        check(page)
    capture(page, "devflow-machines")

    # Removed, after asking, it is gone.
    quiet.get_by_role("button", name="Remove build-box").click()
    asking = page.get_by_role("dialog", name="Remove build-box?")
    expect(asking).to_contain_text("Its key stops working at once.")
    asking.get_by_role("button", name="Remove").click()
    expect(rows).to_have_count(1)

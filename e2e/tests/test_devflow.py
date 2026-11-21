"""Dev flow, the platform's feature for automating development on connected
repositories, reached through the proxy under /devflow and opened from the
nav bar. See devflow/README.md."""

import base64
import json
import re
from pathlib import Path

from playwright.sync_api import Browser, Page, expect

from conftest import (
    account_in_access_cookie,
    capture,
    link_github,
    register_and_log_in,
)
from devflow_worlds import (
    APP_LOGIN,
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
    one_repository,
    png,
    repository,
    reviewer_comment,
    status,
)
from look import (
    assert_active_is_inverted,
    assert_headings_are_capitals,
    assert_nothing_active_is_amber,
    assert_nothing_scrolls_sideways,
)

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


LINK_INVITE = (
    "Link your GitHub account to connect repositories and to merge and comment "
    "as yourself."
)


def test_i_link_my_github_account(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/devflow/projects")
    card = page.get_by_role("article", name="GitHub")
    expect(card.get_by_text(LINK_INVITE)).to_be_visible()
    capture(page, "devflow-projects-unlinked")

    link_github(page, base_url)

    expect(
        page.get_by_role("article", name="GitHub").get_by_text("Linked as @octo-owner")
    ).to_be_visible()
    expect(page.get_by_text(LINK_INVITE)).to_have_count(0)
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
    expect(card.get_by_text(LINK_INVITE)).to_be_visible()
    expect(page.get_by_text("Linked as @octo-owner")).to_have_count(0)
    assert page.request.get("/devflow/api/status").json()["github"] == {
        "state": "none",
        "login": None,
    }
    # And it stays so on a fresh load.
    page.reload()
    expect(page.get_by_role("button", name="Link GitHub")).to_be_visible()


RUNNER_LINE = "Read the way this repository's runner writes."
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
    expect(projects.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    expect(projects.get_by_text(RUNNER_LINE)).to_be_visible()
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


def test_i_disconnect_a_project(
    page: Page, invite_code: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    devflow_world(one_repository())
    page.goto("/devflow/projects")
    link_github(page, base_url)
    _choose(page)
    page.get_by_role("button", name="Connect Adamniels/Personal-platform").click()
    projects = page.get_by_role("region", name="Projects")
    expect(projects.get_by_text("Adamniels/Personal-platform")).to_be_visible()

    projects.get_by_role(
        "button", name="Disconnect Adamniels/Personal-platform"
    ).click()
    dialog = page.get_by_role("dialog", name="Disconnect Adamniels/Personal-platform?")
    expect(dialog).to_be_visible()
    expect(dialog).to_contain_text("Nothing changes on GitHub.")
    dialog.get_by_role("button", name="Disconnect", exact=True).click()

    expect(dialog).to_be_hidden()
    expect(page.get_by_text("No project connected yet")).to_be_visible()
    expect(page.get_by_role("region", name="Projects")).to_have_count(0)
    assert page.request.get("/devflow/api/projects").json() == {"projects": []}
    page.reload()
    expect(page.get_by_text("No project connected yet")).to_be_visible()


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

    group = page.get_by_role("region", name="Pull requests waiting")
    vault = group.get_by_role("article", name="The vault")
    expect(vault).to_be_visible()
    expect(vault.get_by_text("Adamniels/Personal-platform")).to_be_visible()
    expect(vault.get_by_text("#304")).to_be_visible()
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

    poll = page.get_by_role("region", name="Pull requests waiting").get_by_role(
        "article", name="The poll"
    )
    expect(poll.get_by_text("Why it waits")).to_be_visible()
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

    stopped = page.get_by_role("region", name="Stopped").get_by_role(
        "article", name="Import a photo"
    )
    expect(stopped.get_by_text("Stopped", exact=True)).to_be_visible()
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
    stopped = page.get_by_role("region", name="Stopped").get_by_role(
        "article", name="Something landed on main that Dev flow did not put there"
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
    stopped = page.get_by_role("region", name="Stopped").get_by_role(
        "article",
        name=f"Something landed on {FEATURE_BRANCH} that Dev flow did not put there",
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

    expect(page.get_by_role("article")).to_have_count(4)
    sent_back = page.get_by_role("region", name="Sent back, the runner has it")
    expect(sent_back.get_by_role("article", name="The board")).to_be_visible()
    expect(page.get_by_text("Updated less than a minute ago")).to_be_visible()
    # Three wait for the owner; the one sent back is the runner's.
    expect(page.get_by_role("tab", name="Waiting for you 3")).to_be_visible()
    assert _axe_violations(page) == []
    capture(page, "devflow-waiting-list")


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
    vault = page.get_by_role("article", name="The vault")

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
    poll = page.get_by_role("region", name="Pull requests waiting").get_by_role(
        "article", name="The poll"
    )
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
    sent_back = page.get_by_role("region", name="Sent back, the runner has it")
    expect(sent_back.get_by_role("article", name="The poll")).to_be_visible()
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

    poll = page.get_by_role("article", name="The poll")
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
        "link", name=full_name
    ).click()
    expect(page).to_have_url(
        re.compile(rf"{re.escape(base_url)}/devflow/projects/[0-9a-f-]{{36}}$")
    )
    expect(page.get_by_role("heading", name=full_name)).to_be_visible()


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
    settings = page.get_by_role("article", name="Settings")

    # A new project has the defaults.
    expect(settings.get_by_label("Pick-up label")).to_have_value("devflow")
    expect(settings.get_by_label("Hold label")).to_have_value("devflow-hold")
    expect(settings.get_by_label("Attention label")).to_have_value("devflow-attention")
    expect(settings.get_by_label("Pull requests wait for you")).to_have_value("never")

    # The same name twice is refused under the field, and nothing is saved.
    settings.get_by_label("Hold label").fill("DevFlow")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_label("Hold label")).to_have_attribute(
        "aria-invalid", "true"
    )
    expect(
        settings.get_by_text("must be a different label from the other two")
    ).to_be_visible()

    settings.get_by_label("Pick-up label").fill("agent: go")
    settings.get_by_label("Hold label").fill("agent: wait")
    settings.get_by_label("Pull requests wait for you").select_option("always")
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(settings.get_by_label("Hold label")).not_to_have_attribute(
        "aria-invalid", "true"
    )
    capture(page, "devflow-project-settings")

    # Kept: a fresh load reads them back.
    page.reload()
    settings = page.get_by_role("article", name="Settings")
    expect(settings.get_by_label("Pick-up label")).to_have_value("agent: go")
    expect(settings.get_by_label("Hold label")).to_have_value("agent: wait")
    expect(settings.get_by_label("Attention label")).to_have_value("devflow-attention")
    expect(settings.get_by_label("Pull requests wait for you")).to_have_value("always")


def test_i_trust_another_account(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    # A card octo-friend wrote, ignored while only the owner, octo-owner, is
    # trusted.
    world = one_repository()
    add_issue(world, 12, "The friend's card", author="octo-friend", labels=["devflow"])
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]

    def read_again():
        refreshed = page.request.post(
            f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
        )
        assert refreshed.status == 204, refreshed.text()
        page.reload()

    def card():
        return (
            page.get_by_role("list", name="Cards")
            .get_by_role("listitem")
            .filter(has=page.get_by_text("#12", exact=True))
        )

    read_again()
    expect(card()).to_contain_text("Ignored")
    expect(card()).to_contain_text(
        "octo-friend is not a trusted account of this project"
    )

    # My own login is listed and cannot be removed; I add octo-friend.
    settings = page.get_by_role("article", name="Settings")
    trusted = settings.get_by_role("list", name="Trusted accounts")
    expect(trusted.get_by_role("listitem")).to_have_count(1)
    expect(trusted.get_by_role("listitem").first).to_contain_text("octo-owner")
    expect(trusted.get_by_role("listitem").first).to_contain_text("You")
    expect(settings.get_by_role("button", name="Remove octo-owner")).to_have_count(0)
    settings.get_by_label("Add a trusted account").fill("octo-friend")
    settings.get_by_role("button", name="Add").click()
    settings.get_by_role("button", name="Save").click()
    expect(settings.get_by_text("Saved.")).to_be_visible()
    expect(trusted.get_by_role("listitem")).to_have_count(2)
    expect(settings.get_by_role("button", name="Remove octo-friend")).to_be_visible()
    capture(page, "devflow-settings-trusted")

    # The next read queues the card octo-friend wrote.
    read_again()
    expect(card()).to_contain_text("Queued")
    settings = page.get_by_role("article", name="Settings")
    expect(
        settings.get_by_role("list", name="Trusted accounts").get_by_role("listitem")
    ).to_have_count(2)


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
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]

    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
    page.reload()

    cards = page.get_by_role("list", name="Cards")
    expect(cards.get_by_role("listitem")).to_have_count(5)

    def row(number: int):
        return cards.get_by_role("listitem").filter(
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
    _open_project(page, base_url, devflow_world, world)
    project = page.url.rsplit("/", 1)[1]

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
    expect(feature.locator(":scope > div").first).to_contain_text("Queued")

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
    own = page.get_by_role("list", name="Cards", exact=True)
    expect(own.get_by_role("listitem")).to_have_count(1)
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
    expect(feature.locator(":scope > div").first).to_contain_text("Building")
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

    # Before a key, the command that builds and starts a worker, never the
    # one that saves a key.
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
    expect(login).to_contain_text(CLAUDE_PURPOSE)
    expect(login.get_by_text("Not set yet.")).to_be_visible()
    expect(login.get_by_role("button", name="Remove")).to_have_count(0)
    capture(page, "devflow-claude-login-empty")

    field = login.get_by_label("Claude token")
    expect(field).to_have_attribute("type", "password")
    field.fill(CLAUDE_TOKEN)
    login.get_by_role("button", name="Save").click()

    # The field empties, and the section says when it was given and is due.
    expect(field).to_have_value("")
    status = page.request.get("/devflow/api/claude-login").json()
    assert status["state"] == "ok"
    said = (
        f"Set on {_day(page, status['given_at'])}, "
        f"renew by {_day(page, status['renew_by'])}."
    )
    expect(login.get_by_text(said)).to_be_visible()
    expect(login.get_by_role("button", name="Replace")).to_be_visible()
    expect(login.get_by_role("button", name="Remove")).to_be_visible()
    capture(page, "devflow-claude-login")

    # Never shown again, not even in part, and still set after a reload.
    page.reload()
    expect(login.get_by_text(said)).to_be_visible()
    expect(login.get_by_label("Claude token")).to_have_value("")
    assert CLAUDE_TOKEN[10:] not in page.content()
    assert CLAUDE_TOKEN[-12:] not in page.content()

    # Removing it asks first, and then it is gone.
    login.get_by_role("button", name="Remove").click()
    page.get_by_role("dialog", name="Remove the Claude login?").get_by_role(
        "button", name="Remove the login"
    ).click()
    expect(login.get_by_text("Not set yet.")).to_be_visible()
    assert page.request.get("/devflow/api/claude-login").json()["set"] is False


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
    expect(
        login.get_by_text(
            f"Renew it by {_day(page, status['renew_by'])}: "
            "agents cannot run once it runs out."
        )
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
CHECK = "npm ci && npm test"


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
    .devflow/project.json, gives Dev flow a Claude login, and reads the project
    so the card is picked. The project's id."""
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
    refreshed = page.request.post(
        f"/devflow/api/projects/{project}/refresh", headers={"Origin": base_url}
    )
    assert refreshed.status == 204, refreshed.text()
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
    return page.get_by_role(
        "region", name=re.compile("Pull requests waiting")
    ).get_by_role("article", name=CARD_TITLE)


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
    expect(card).to_contain_text(
        f"Dev flow ran {CHECK} on {ran['head_commit'][:7]}: exit 0."
    )
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
    capture(page, "devflow-card-waiting")


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
    expect(_waiting_card(page)).to_have_count(0)

    # Merged as the owner, naming the commit shown, and never as the app.
    merges = [write for write in _writes(page) if write["kind"] == "merge"]
    assert len(merges) == 1, merges
    assert merges[0]["by"] == "octo-owner"
    assert merges[0]["commit"] == ran["head_commit"]
    assert "purpose" not in merges[0]

    # The card is done.
    page.goto(f"/devflow/projects/{project}")
    row = (
        page.get_by_role("list", name="Cards")
        .get_by_role("listitem")
        .filter(has=page.get_by_text(f"#{CARD}", exact=True))
    )
    expect(row).to_contain_text("Done")


def test_a_cards_page_tells_its_story(
    page: Page, invite_code: str, base_url: str, viewport: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)
    project, ran = _built_card(page, base_url, devflow_world)
    pull = _opened(page)

    # From the project's cards list to the card's page.
    page.goto(f"/devflow/projects/{project}")
    page.get_by_role("list", name="Cards").get_by_role("link", name=CARD_TITLE).click()
    expect(page).to_have_url(f"{base_url}/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("heading", name=CARD_TITLE)).to_be_visible()
    expect(page.get_by_role("tab", name="Projects", selected=True)).to_be_visible()

    # Its state with its reason, and links to the issue and the pull request:
    # approved, and with nothing from CI on its commit yet the merge check
    # waits for it.
    expect(page.get_by_text("Waiting for you", exact=True).first).to_be_visible()
    expect(page.get_by_text("Waiting for CI to report on its commit")).to_be_visible()
    expect(page.get_by_role("link", name=f"Issue #{CARD}")).to_have_attribute(
        "href", f"https://github.com/{OWNER}/{NAME}/issues/{CARD}"
    )
    expect(
        page.get_by_role("link", name=f"Pull request #{pull['number']}")
    ).to_have_attribute("href", pull["url"])

    # Its runs, newest first: the review, then the build with its kind,
    # worker, outcome, auth method, tokens and the check, with the output's
    # tail behind Show.
    runs = page.get_by_role("list", name="Runs")
    expect(runs.get_by_role("listitem")).to_have_count(2)
    expect(runs.get_by_role("listitem").first).to_contain_text("Review")
    run = runs.get_by_role("listitem").nth(1)
    expect(run).to_contain_text("Build")
    expect(run).to_contain_text("devflow-fake-worker")
    expect(run).to_contain_text("Succeeded")
    expect(run).to_contain_text("oauth_token")
    expect(run).to_contain_text("41 in, 6,113 out")
    expect(run).to_contain_text(f"exit 0 on {ran['head_commit'][:7]}")
    expect(run.get_by_text("Tests: 12 passed")).to_be_hidden()
    run.get_by_role("button", name="Show").click()
    expect(run.get_by_text("Tests: 12 passed")).to_be_visible()

    # The latest report: summary, tasks, decisions, tests, pages and follow ups.
    report = page.get_by_role("article", name="The latest report")
    expect(report).to_contain_text("Added the empty state to the habits list")
    expect(report.get_by_role("list", name="Tasks")).to_contain_text("Count the habits")
    decisions = report.get_by_role("list", name="Decisions")
    expect(decisions).to_contain_text("Show it on every list")
    expect(decisions).to_contain_text("For you")
    expect(report.get_by_role("list", name="Tests")).to_contain_text("list.test.tsx")
    expect(report.get_by_role("list", name="Pages")).to_contain_text("/habits")
    expect(report.get_by_role("list", name="Follow ups")).to_contain_text(
        "Count the habits in the title"
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
    capture(page, "devflow-card-page")


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
        page.get_by_role("article", name="The latest report")
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
    reviews = page.get_by_role("article", name="Reviews")
    expect(reviews).to_contain_text(f"Review of {commit[:7]}")
    expect(reviews.get_by_text("Changes asked for")).to_be_visible()
    expect(reviews).to_contain_text("For the pull request's head")
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
    runs = page.get_by_role("list", name="Runs").get_by_role("listitem")
    expect(runs.first).to_contain_text("Review")
    expect(runs.first).to_contain_text("Succeeded")

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
    expect(_waiting_card(page)).to_have_count(0)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("article", name="Reviews")).to_be_visible()
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
    expect(_waiting_card(page)).to_have_count(0)

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
    rounds = page.get_by_role("list", name="Rounds")
    one = rounds.get_by_role("listitem", name="Round 1 of 3")
    expect(one).to_contain_text(
        f"The review of {ran['head_commit'][:7]} asked for changes."
    )
    expect(one.get_by_role("list", name="Replies")).to_contain_text(
        "The empty state is read once"
    )
    expect(one.get_by_role("list", name="Replies")).to_contain_text(
        "Fixed: it is read on every change"
    )
    expect(one).to_contain_text(
        f"It left {first['head_commit'][:7]}, and the review of it asked for changes."
    )
    reviews = page.get_by_role("article", name="Reviews")
    expect(reviews).to_contain_text(f"Review of {first['head_commit'][:7]}")
    expect(reviews).to_contain_text(f"Review of {ran['head_commit'][:7]}")
    expect(reviews).to_contain_text("For an older commit, so it does not count")
    runs = page.get_by_role("list", name="Runs").get_by_role("listitem")
    expect(runs).to_have_count(4)
    expect(runs.nth(1)).to_contain_text("Fix")

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
    expect(rounds.get_by_role("listitem", name=re.compile("^Round"))).to_have_count(2)
    expect(rounds.get_by_role("listitem", name="Round 2 of 3")).to_contain_text(
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
    # The fake worker fails twice in the card, with nothing pushed between.
    for _ in range(2):
        _fake_run(page, base_url, project, _failure(True, TURNS), leave_no_commit=True)

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
    stopped = _stopped_region(page).get_by_role("article", name=CARD_TITLE)
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
    capture(page, "devflow-card-stopped")

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
    failing = _stopped_region(page).get_by_role("article", name=message)
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


def _waits_for_me(page: Page, base_url: str, project: str, waits: str) -> None:
    """Sets the project's Pull requests wait for you, its labels the defaults
    and its trusted accounts as they are."""
    current = page.request.get(f"/devflow/api/projects/{project}/settings")
    assert current.status == 200, current.text()
    saved = page.request.put(
        f"/devflow/api/projects/{project}/settings",
        data={
            "pickup_label": "devflow",
            "hold_label": "devflow-hold",
            "attention_label": "devflow-attention",
            "waits_for_owner": waits,
            "trusted_logins": [
                entry["login"] for entry in current.json()["trusted_logins"]
            ],
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
    merge = page.get_by_role("article", name="The merge")
    expect(merge).to_contain_text(f"Dev flow merged #{pull['number']} into main")
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
    expect(_waiting_card(page)).to_have_count(0)

    page.goto(f"/devflow/projects/{project}/cards/{CARD}")
    expect(page.get_by_role("article", name="The merge")).to_be_visible()
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
    decision = decisions.get_by_role("article", name="Show it on every list")
    expect(decision).to_be_visible()
    expect(decision).to_contain_text("Only on habits")
    expect(decision).to_contain_text("The lists look the same")
    expect(decision).to_contain_text("Agreed: the lists share one component.")
    expect(decision).to_contain_text(f"{OWNER}/{NAME}")
    expect(decision.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", issue["url"]
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
    expect(decisions.get_by_role("article")).to_have_count(0)


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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Building")
    expect(feature).to_contain_text("1 of 3 cards done")

    # The decision the first card's report marked for me, with the
    # reviewer's view, and the one not marked left out.
    wants = page.get_by_role("article", name="What the agents want you to know")
    decisions = wants.get_by_role("list", name="Decisions for you").get_by_role(
        "listitem"
    )
    expect(decisions).to_have_count(1)
    expect(decisions.first).to_contain_text("#61: Show it on every list")
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
    rows = page.get_by_role("table", name="Cards of the feature").get_by_role("row")
    expect(rows).to_have_count(4)
    expect(rows.nth(1)).to_contain_text("#61 Read the sub-issues")
    expect(rows.nth(1)).to_contain_text("Done")
    expect(rows.nth(2)).to_contain_text("#62 Show each feature")
    expect(rows.nth(2)).to_contain_text("Building")
    expect(rows.nth(3)).to_contain_text("#63 The feature's page")
    expect(rows.nth(3)).to_contain_text("Queued")
    _look(page, "devflow-feature-page")

    # A card's page links back to its feature.
    rows.nth(1).get_by_role("link", name="#61 Read the sub-issues").click()
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
    expect(cards.get_by_role("button", name="Send back #62")).to_have_count(0)
    cards.get_by_role("button", name="Send back #61").click()
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
    rows = page.get_by_role("table", name="Cards of the feature").get_by_role("row")
    expect(rows.nth(1)).to_contain_text("Queued")
    expect(page.get_by_role("article", name="The feature")).to_contain_text("Building")
    sent = cards.get_by_role("list", name="Cards you sent back")
    expect(sent).to_contain_text("#61 Read the sub-issues")
    expect(sent).to_contain_text("Sent back from pull request #")
    expect(sent).to_contain_text(said)
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
    rows = page.get_by_role("table", name="Cards of the feature").get_by_role("row")
    expect(rows.nth(1)).to_contain_text("Building")
    expect(page.get_by_role("article", name="The feature")).to_contain_text("Building")


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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Reviewing")
    expect(feature).to_contain_text("1 of 1 cards done")
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
    feature = page.get_by_role("article", name="The feature")
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

    # Its page shows the run that resolved it.
    page.goto(f"/devflow/projects/{project}/features/60")
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Reviewing")
    runs = page.get_by_role("article", name="Runs").get_by_role(
        "list", name="Runs of the feature"
    )
    run = runs.get_by_role("listitem")
    expect(run).to_have_count(1)
    expect(run).to_contain_text("Conflict")
    expect(run).to_contain_text("Succeeded")
    expect(run).to_contain_text(f"on {resolved.json()['head_commit'][:7]}")
    expect(run).to_contain_text("Tokens 12 in, 3,400 out")
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
    item = group.get_by_role("article", name="The feature's branch")
    expect(item).to_contain_text("Ready for you")
    expect(item).to_contain_text(
        "The feature does what its parent asks, and its cards fit together."
    )
    expect(item).to_contain_text(
        "1 decision its cards marked for you is not answered yet."
    )
    expect(item.get_by_role("link", name="Open on GitHub")).to_have_attribute(
        "href", re.compile(rf"/pull/{pull}$")
    )
    _look(page, "devflow-feature-ready")

    # Its page shows the review, for the pull request's head.
    item.get_by_role("link", name="Open the feature").click()
    expect(page).to_have_url(re.compile(rf"/devflow/projects/{project}/features/60$"))
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Ready for you")
    reviews = page.get_by_role("list", name="Reviews of the feature")
    latest = reviews.get_by_role("listitem").first
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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Fixing")
    expect(feature).to_contain_text(
        "Round 1 of 3: the review of the whole feature asked for changes; a fix runs next"
    )
    latest = (
        page.get_by_role("list", name="Reviews of the feature")
        .get_by_role("listitem")
        .first
    )
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
    expect(page.get_by_role("article", name="The feature")).to_contain_text(
        "Ready for you"
    )
    expect(page.get_by_text("Fix rounds, 1 of 3 used")).to_be_visible()
    round_one = page.get_by_role("list", name="Fix rounds of the feature").get_by_role(
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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Waiting for you")
    feature.get_by_role("button", name="Send the whole feature back").click()
    said = "Read the branch's head in one place for the card and the end."
    feature.get_by_role(
        "textbox", name="What should the developer change in the whole feature?"
    ).fill(said)
    _look(page, "devflow-feature-send-back")
    feature.get_by_role("button", name="Send", exact=True).click()
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
    page: Page, base_url: str, devflow_world, whole: dict
) -> tuple[str, int, str]:
    """A labelled feature of one card carried to its end: the card built and
    landed, main brought in and the feature's pull request opened, its whole
    review answered `whole`, and CI green on its head, decided on the next
    minute. A verdict other than approve comes after every fix round is used,
    each a review asking for changes and its fix, so the feature waits for me,
    the one way one its review did not approve does. The project, the
    feature's pull request and its head."""
    world = one_repository()
    add_issue(world, 60, "The feature's branch", labels=["devflow"], sub_issues=[61])
    add_issue(world, 61, "Merge the feature")
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
    item = group.get_by_role("article", name="The feature's branch")
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
    expect(group.get_by_role("article", name="The feature's branch")).to_have_count(0)
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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Merged")
    expect(page.get_by_role("button", name="Merge into main")).to_have_count(0)
    _look(page, "devflow-feature-merged")


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
    feature = page.get_by_role("article", name="The feature")
    expect(feature).to_contain_text("Waiting for you")
    expect(feature).to_contain_text("Review rounds used up")
    feature.get_by_role("button", name="Merge into main").click()

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

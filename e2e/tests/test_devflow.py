"""Dev flow, the platform's feature for automating development on connected
repositories, reached through the proxy under /devflow and opened from the
nav bar. See devflow/README.md."""

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
    INSTALLATION,
    REPOSITORY,
    add_conversation,
    add_file,
    add_pull_request,
    add_screenshot,
    check_run,
    comment,
    one_repository,
    png,
    repository,
    reviewer_comment,
    status,
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

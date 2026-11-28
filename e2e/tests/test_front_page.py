"""The front page, composed by the core from every feature's display data.
Check 4 of the feature: canary's card, rendered by the core from what canary
sent with the user's token. See "What a feature exposes for the front page"
in Core/docs/core-architecture.md."""

import re
from collections.abc import Callable
from datetime import UTC, datetime, timedelta

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import capture, link_github, register_and_log_in
from devflow_worlds import (
    INSTALLATION,
    REPOSITORY,
    add_pull_request,
    one_repository,
    reviewer_comment,
)


def test_front_page_shows_canarys_card(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)

    # Canary's own API, asked with the page's cookies, is the reference for
    # who this is, so the test does not trust the card to say it.
    whoami = page.request.get("/canary/api/whoami")
    assert whoami.status == 200
    account_id = whoami.json()["account_id"]

    expect(page.get_by_role("heading", name="Platform", level=1)).to_be_visible()
    card = page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Canary", level=2)
    )
    expect(card).to_have_count(1)
    expect(card.get_by_role("link", name="Open Canary", exact=True)).to_have_attribute(
        "href", "/canary"
    )
    expect(card.get_by_text(account_id)).to_be_visible()
    expect(card.get_by_text("Verified your token in Python")).to_be_visible()
    expect(page.get_by_text("could not be reached")).to_have_count(0)

    # The cards never make the page scroll sideways, at phone width included.
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "front-signed-in")


def review(page: Page, method: str, path: str, body: dict) -> dict:
    """Calls review's API from the page, as its own page does, so the
    session's cookie and the platform's origin go with it."""
    status, answer = page.evaluate(
        """async ([method, path, body]) => {
            const response = await fetch(`/review/api${path}`, {
                method,
                credentials: "same-origin",
                headers: {"content-type": "application/json"},
                body: JSON.stringify(body),
            });
            return [response.status, await response.json()];
        }""",
        [method, path, body],
    )
    assert status in (200, 201), answer
    return answer


def add_card(page: Page, deck: str, front: str) -> str:
    topics = [{"slug": "home", "name": "Home"}]
    card = {"deck_id": deck, "front": front, "back": "Back", "topics": topics}
    return review(page, "POST", "/cards", card)["id"]


def add_deck(page: Page, name: str) -> str:
    return review(page, "POST", "/decks", {"name": name})["id"]


def review_card(page: Page) -> Locator:
    return page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Review", level=2)
    )


def test_the_home_page_shows_what_is_due_in_review(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    """Review comes to the owner: the home page, the first thing seen after
    logging in, says how many cards are due and in which decks."""
    register_and_log_in(page, invite_code, base_url)
    compilers = add_deck(page, "Compilers")
    databases = add_deck(page, "Databases")
    # A new card is due at once.
    add_card(page, compilers, "What does an LL(1) parser look ahead at?")
    add_card(page, compilers, "What does an LR parser build?")
    add_card(page, databases, "What does MVCC keep per row?")

    page.goto("/")

    card = review_card(page)
    expect(card).to_have_count(1)
    expect(card.locator(".key-fact")).to_have_text("3 due now")
    decks = card.get_by_role("listitem")
    expect(decks).to_have_count(2)
    expect(decks.nth(0)).to_have_text(re.compile(r"Compilers\s*2"))
    expect(decks.nth(1)).to_have_text(re.compile(r"Databases\s*1"))
    expect(page.get_by_text("could not be reached")).to_have_count(0)
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "home-review-due")

    card.get_by_role("link", name="Start review").click()
    expect(page).to_have_url(re.compile(r"/review$"))


def test_the_home_page_says_when_nothing_is_due_in_review(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)
    page.goto("/")

    # With no cards at all, the card offers to add some.
    card = review_card(page)
    expect(card.locator(".key-fact")).to_have_text("Nothing due")
    expect(card.get_by_text("No cards yet")).to_be_visible()
    expect(card.get_by_role("link", name="Add cards")).to_have_attribute(
        "href", "/review/cards"
    )

    # A card graded Good comes back in minutes, so nothing is due until then.
    deck = add_deck(page, "Databases")
    graded = add_card(page, deck, "What does MVCC keep per row?")
    review(page, "POST", f"/cards/{graded}/grades", {"rating": 3, "version": 1})
    page.goto("/")

    card = review_card(page)
    expect(card.locator(".key-fact")).to_have_text("Nothing due")
    expect(card.get_by_text(re.compile(r"^Next in \d+ minutes?$"))).to_be_visible()
    expect(card.get_by_role("link", name="Start review")).to_have_count(0)
    capture(page, "home-review-nothing")


def devflow_card(page: Page) -> Locator:
    return page.get_by_role("article").filter(
        has=page.get_by_role("heading", name="Dev flow", level=2)
    )


def connect_and_read(page: Page, base_url: str, world: dict, devflow_world) -> None:
    """Stages `world` in Dev flow's fake, links GitHub, connects the world's
    repository and reads it now, as the Projects tab and its refresh do,
    through the page's own session."""
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


def test_the_home_page_shows_what_waits_in_dev_flow(
    page: Page, invite_code: str, viewport: str, base_url: str, devflow_world
):
    """Dev flow comes to the owner: the home page says how many pull requests
    wait for him, the moment he opens the platform."""
    register_and_log_in(page, invite_code, base_url)
    world = one_repository()
    head = "4" * 40
    # Approved for its head and handed to the owner: it waits.
    add_pull_request(
        world,
        304,
        title="The vault",
        labels=["owner-merge"],
        head_commit=head,
        comments=[reviewer_comment(9001, "approve", head)],
    )
    # The runner gave up on this one: it stopped.
    add_pull_request(world, 305, title="The sealed key", labels=["owner-blocked"])
    connect_and_read(page, base_url, world, devflow_world)

    page.goto("/")

    card = devflow_card(page)
    expect(card).to_have_count(1)
    expect(card.locator(".key-fact")).to_have_text("2 waiting for you")
    expect(card.get_by_text("1 stopped", exact=True)).to_be_visible()
    expect(card).to_have_class(re.compile(r"\bneeds-you\b"))
    expect(
        card.get_by_role("link", name="Open Dev flow", exact=True)
    ).to_have_attribute("href", "/devflow")
    expect(page.get_by_text("could not be reached")).to_have_count(0)
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "home-devflow-waiting")

    card.get_by_role("link", name="Open what waits").click()
    expect(page).to_have_url(f"{base_url}/devflow")


def test_the_home_page_says_when_nothing_waits_in_dev_flow(
    page: Page, invite_code: str, viewport: str, base_url: str, devflow_world
):
    register_and_log_in(page, invite_code, base_url)

    # With no project, the card says so and where to connect one.
    card = devflow_card(page)
    expect(card.locator(".key-fact")).to_have_text("No project connected yet")
    expect(card.get_by_role("link", name="Connect a repository")).to_have_attribute(
        "href", "/devflow/projects"
    )

    # Connected and read, with a pull request still in review: nothing is his
    # turn.
    world = one_repository()
    add_pull_request(world, 306, labels=["in-review"])
    connect_and_read(page, base_url, world, devflow_world)
    page.goto("/")

    card = devflow_card(page)
    expect(card.locator(".key-fact")).to_have_text("Nothing waits for you")
    expect(card).not_to_have_class(re.compile(r"\bneeds-you\b"))
    expect(card.locator(".count")).to_have_count(0)
    expect(card.get_by_text("Not updated for a while")).to_have_count(0)
    expect(card.get_by_role("link", name="Connect a repository")).to_have_count(0)
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]
    capture(page, "home-devflow-nothing")


# The backup line. On the dev stack CORE_BACKUP_STATUS_FILE is unset, as on
# the laptop and in CI; the hosted copy sets it and mounts the backup job's
# status. See "The backup line on the front page" in
# Core/docs/core-architecture.md.

UTC_TITLE = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC$")


def status_dot(line: Locator) -> dict:
    """The width and colour of the line's ::before, the status dot."""
    return line.evaluate(
        """element => {
            const dot = getComputedStyle(element, "::before");
            return {content: dot.content, width: dot.width, colour: dot.backgroundColor};
        }"""
    )


@pytest.mark.dev_stack_only(
    reason="the hosted copy sets CORE_BACKUP_STATUS_FILE, so its home page has the line"
)
def test_the_home_page_says_nothing_about_backups_without_the_setting(
    page: Page, invite_code: str, base_url: str
):
    register_and_log_in(page, invite_code, base_url)

    expect(page.locator(".backup")).to_have_count(0)
    expect(page.get_by_text(re.compile("backup", re.IGNORECASE))).to_have_count(0)


@pytest.mark.hosted_only
# Both read the one backup status file, which the second replaces.
@pytest.mark.serial
def test_the_home_page_shows_the_last_backup(
    page: Page, invite_code: str, viewport: str, base_url: str
):
    """check.sh runs `backup now` before the suite, so the real status file
    holds a success from minutes ago."""
    register_and_log_in(page, invite_code, base_url)

    line = page.locator("main p.backup")
    expect(line).to_have_count(1)
    expect(line).to_have_text(
        re.compile(r"^Last backup (just now|\d+ (minute|hour)s? ago)$")
    )
    expect(line).not_to_have_class(re.compile(r"\battention\b"))
    expect(line.locator("time")).to_have_attribute("title", UTC_TITLE)
    assert status_dot(line)["content"] == "none"
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "home-backup-fine")


@pytest.mark.hosted_only
# Both read the one backup status file, which the second replaces.
@pytest.mark.serial
def test_the_home_page_warns_when_the_backup_is_stale(
    page: Page,
    invite_code: str,
    viewport: str,
    base_url: str,
    backup_status: Callable[[dict], None],
):
    two_days_ago = (datetime.now(UTC) - timedelta(days=2)).replace(microsecond=0)
    stamp = two_days_ago.strftime("%Y-%m-%dT%H:%M:%SZ")
    backup_status(
        {
            "last_attempt_at": stamp,
            "last_attempt_ok": True,
            "last_success_at": stamp,
            "last_error": None,
        }
    )
    register_and_log_in(page, invite_code, base_url)

    line = page.locator("main p.backup")
    expect(line).to_have_class(re.compile(r"\battention\b"))
    expect(line).to_have_text("Last backup 2 days ago, none since")
    expect(line.locator("time")).to_have_attribute(
        "title", two_days_ago.strftime("%Y-%m-%d %H:%M:%S UTC")
    )
    # A dot and words, never colour alone: the 6px dot in the line's own
    # amber.
    dot = status_dot(line)
    assert dot["width"] == "6px", dot
    assert dot["colour"] == line.evaluate("element => getComputedStyle(element).color")
    scroll_width = page.evaluate("document.documentElement.scrollWidth")
    assert scroll_width <= page.viewport_size["width"]

    capture(page, "home-backup-stale")

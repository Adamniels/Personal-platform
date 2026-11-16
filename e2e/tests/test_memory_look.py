"""The memory centre's frame and every section in the platform's look: the
crumb in the top bar, the page header, the panel, the inset rows, the card
padding, the type of the sub headings, the card row and the tables of
design/DESIGN.md, and the three look checks of look.py. See "The memory
centre" in Brain/README.md.

Colours and sizes are compared with the token as the page resolves it, as
test_style.py does, so no value is written here but the row's padding, which
DESIGN.md gives in pixels."""

import re
import uuid
from datetime import UTC, datetime, timedelta
from itertools import pairwise

import pytest
from playwright.sync_api import Locator, Page, expect

from conftest import open_section, register_and_log_in, sections
from look import (
    assert_active_is_inverted,
    assert_headings_are_capitals,
    assert_nothing_scrolls_sideways,
)

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
        fontFamily: style.fontFamily,
        fontWeight: style.fontWeight,
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        textTransform: style.textTransform,
        whiteSpace: style.whiteSpace,
        overflowX: style.overflowX,
        overflowY: style.overflowY,
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


def test_the_page_header_follows_the_layout(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    lede = page.get_by_text(
        "What the platform knows about you. Correct anything that is wrong."
    )
    look = style(lede)
    assert look["fontSize"] == resolve(page, "--text-body-lg-size", "font-size")
    assert look["lineHeight"] == resolve(page, "--text-body-lg-line", "line-height")
    assert look["color"] == resolve(page, "--text-2", "color")

    header = page.locator("main > header")
    header_box = header.bounding_box()
    tabs_box = sections(page, LABEL).bounding_box()
    gap = tabs_box["y"] - (header_box["y"] + header_box["height"])
    assert abs(gap - px(page, "--space-4")) < 0.5, gap


def test_a_panel_has_the_card_padding(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    stage_rules(page)
    stage_timeline(page)

    def check(surface: Locator, where: str) -> None:
        look = style(surface)
        fill = resolve(page, "--surface", "background-color")
        radius = resolve(page, "--radius-lg", "border-top-left-radius")
        assert look["background"] == fill, where
        assert look["borderWidths"] == ["1px"] * 4, where
        assert look["borderStyles"] == ["solid"] * 4, where
        assert look["borderColors"] == [resolve(page, "--border", "color")] * 4, where
        assert look["radii"] == [radius] * 4, where
        card_pad = resolve(page, "--card-pad", "padding-top")
        assert look["padding"] == [card_pad] * 4, where

    page.goto("/brain/rules")
    rules = entry_list(panel(page, "Rules"))
    expect(rows(rules)).to_have_count(2)
    check(rules, "Rules")

    # A day's list on the Timeline is a panel as well.
    page.goto("/brain/timeline")
    day = entry_list(panel(page, "Timeline"))
    expect(rows(day).first).to_be_visible()
    check(day, "Timeline")


@pytest.mark.parametrize("theme", THEMES)
def test_entries_are_inset_rows(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_memory(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    stage_profile(page)
    stage_rules(page)

    def check(scope: Locator, count: int, where: str) -> None:
        entries = rows(entry_list(scope))
        expect(entries).to_have_count(count)
        assert_inset_rows(page, entries, where)

    page.goto("/brain")
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    values = panel(page, "Profile").get_by_role("article", name="Values")
    check(values, 2, "Profile, Values")

    page.goto("/brain/rules")
    check(panel(page, "Rules"), 2, "Rules")


def test_profile_cards_are_a_wrapping_row(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    cards = panel(page, "Profile").get_by_role("article")
    expect(cards).to_have_count(6)
    assert_a_wrapping_card_row(page, cards, viewport, "Profile")


def assert_inset_rows(page: Page, entries: Locator, where: str) -> None:
    """Each row is an inset row, "Inset list row" in section 4 of
    design/DESIGN.md: the --surface-2 fill, --radius-md, padding 8px 12px,
    min height --row-h, no line, and --inset-gap to the next."""
    fill = resolve(page, "--surface-2", "background-color")
    radius = resolve(page, "--radius-md", "border-top-left-radius")
    row_h = resolve(page, "--row-h", "min-height")
    found = entries.all()
    assert found, f"{where}: no rows"
    for row in found:
        look = style(row)
        assert look["background"] == fill, where
        assert look["radii"] == [radius] * 4, where
        assert look["padding"] == ["8px", "12px", "8px", "12px"], where
        assert look["minHeight"] == row_h, where
        assert look["borderWidths"] == ["0px"] * 4, where
    gap = px(page, "--inset-gap")
    boxes = [row.bounding_box() for row in found]
    for above, below in pairwise(boxes):
        between = below["y"] - (above["y"] + above["height"])
        assert abs(between - gap) < 0.5, f"{where}: {between}px between rows"


def assert_a_wrapping_card_row(
    page: Page, cards: Locator, viewport: str, where: str
) -> None:
    """The cards sit in a row of cards, section 2 of design/DESIGN.md: a
    wrapping flex row, gap --space-4, each card flex 1 1 340px with no
    minimum width, more than one on a line on desktop and one column on
    phone."""
    row = cards.first.locator("xpath=..")
    look = style(row)
    assert look["display"] == "flex", where
    assert look["flexWrap"] == "wrap", where
    gap = resolve(page, "--space-4", "row-gap")
    assert look["rowGap"] == look["columnGap"] == gap, where
    for card in cards.all():
        card_look = style(card)
        assert card_look["flex"] == ["1", "1", "340px"], where
        assert card_look["minWidth"] == "0px", where

    boxes = [card.bounding_box() for card in cards.all()]
    if viewport == "phone":
        # One column: every card at the same left edge and the full width.
        assert len({round(box["x"]) for box in boxes}) == 1, where
        assert len({round(box["width"]) for box in boxes}) == 1, where
    else:
        # More than one card on the first line.
        assert round(boxes[1]["y"]) == round(boxes[0]["y"]), where


def assert_the_look(page: Page, where: str) -> None:
    # The bar has drawn with the account, and the toasts have left.
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
def test_the_frame_and_first_sections_follow_the_look(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_memory(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    stage_profile(page)
    stage_rules(page)
    stage_now(page)
    stage_timeline(page)

    for name, path in [
        ("Profile", "/brain"),
        ("Rules", "/brain/rules"),
        ("Now", "/brain/now"),
        ("Timeline", "/brain/timeline"),
    ]:
        page.goto(path)
        expect(page.locator("html")).to_have_attribute("data-theme", theme)
        expect(panel(page, name).get_by_role("listitem").first).to_be_visible()
        assert_the_look(page, path)


# The other four sections: Knowledge, History, Everything and Ask, and their
# sub pages, staged through the API the sections use.

DISAGREE = "The brain holds things that disagree"
# A word too long for a phone's width, so the raw answer has to wrap it.
LONG_WORD = "https://example.com/" + "a-path-without-any-space-in-it/" * 4


def add(page: Page, body: dict) -> dict:
    return call(page, "POST", "/items", body)


def topic(page: Page, name: str) -> dict:
    return call(page, "POST", "/topics", {"name": name})


def under(page: Page, narrower: dict, broader: dict) -> None:
    call(
        page, "POST", f"/topics/{narrower['id']}/broader", {"broader_id": broader["id"]}
    )


def same_as(page: Page, alias: dict, canonical: dict) -> None:
    call(page, "POST", f"/topics/{alias['id']}/same-as", {"target_id": canonical["id"]})


def provisional(page: Page, name: str) -> None:
    """A topic that arrives on its own, with an event a feature sends."""
    call(
        page,
        "POST",
        "/events",
        {
            "verb": "opened",
            "origin": "e2e",
            "evidence": f"Opened a page about {name}",
            "topic": name,
            "payload": {},
            "payload_version": 1,
            "idempotency_key": str(uuid.uuid4()),
        },
    )


def stage_knowledge(page: Page) -> None:
    """Postgres with two other names, two broader topics, two narrower ones
    and two misconceptions, a level and focus on each canonical topic, and
    two provisional topics."""
    databases = topic(page, "Databases")
    software = topic(page, "Open source software")
    postgres = topic(page, "Postgres")
    indexing = topic(page, "Postgres indexing")
    planning = topic(page, "Query planning")
    under(page, postgres, databases)
    under(page, postgres, software)
    under(page, indexing, postgres)
    under(page, planning, postgres)
    for name in ["PostgreSQL", "Postgres SQL"]:
        same_as(page, topic(page, name), postgres)
    for slug, level, focus in [
        ("databases", "solid", "steady"),
        ("postgres", "working", "learning"),
        ("postgres-indexing", "shallow", "not_pursuing"),
    ]:
        add(page, {"kind": "topic_level", "topic": slug, "value": level})
        add(page, {"kind": "topic_focus", "topic": slug, "value": focus})
    for believed, correct in [
        (
            "An index is always used when it exists",
            "The planner skips an index when a scan is cheaper",
        ),
        ("VACUUM locks the table", "Only VACUUM FULL takes the exclusive lock"),
    ]:
        add(
            page,
            {
                "kind": "history",
                "topic": "postgres",
                "details": {
                    "misconception": True,
                    "believed": believed,
                    "correct": correct,
                },
            },
        )
    add(
        page,
        {
            "kind": "history",
            "content": "Read about B-tree indexes",
            "occurred_at": "2026-02-01T00:00:00Z",
            "occurred_precision": "month",
            "topics": ["postgres-indexing"],
        },
    )
    provisional(page, "kubernetes")
    provisional(page, "terraform")


def stage_decisions(page: Page) -> dict:
    """Three decisions about Postgres, the first marked as contradicting the
    other two, so an answer holds two conflicts. Postgres must exist. Returns
    the first."""
    first = add(
        page,
        {
            "kind": "decision",
            "content": "Use Postgres for every service",
            "details": {
                "reasoning": "One database to run.",
                "rejected": ["A database per language", "SQLite everywhere"],
            },
            "topics": ["postgres"],
        },
    )
    for content, reasoning in [
        ("Use SQLite inside Operation Rollout", "The game ships offline."),
        ("Keep the review cards in Redis", "They are read on every request."),
    ]:
        other = add(
            page,
            {
                "kind": "decision",
                "content": content,
                "details": {"reasoning": reasoning},
                "topics": ["postgres"],
            },
        )
        call(
            page,
            "POST",
            f"/items/{first['id']}/contradicts",
            {"contradicts": other["id"]},
        )
    return first


def stage_identity(page: Page) -> None:
    add(
        page,
        {"kind": "profile", "category": "identity", "content": "A student in Uppsala"},
    )
    add(
        page,
        {"kind": "profile", "category": "identity", "content": f"Writes {LONG_WORD}"},
    )


def question(page: Page, name: str) -> Locator:
    """Chooses one of Ask's questions, and returns its panel."""
    panel(page, "Ask").get_by_role("tab", name=name, exact=True).click()
    chosen = page.get_by_role("tabpanel", name=name)
    expect(chosen).to_be_visible()
    return chosen


def press_ask(scope: Locator) -> None:
    scope.get_by_role("button", name="Ask", exact=True).click()
    expect(scope.get_by_text("Nothing asked yet")).to_have_count(0)


def ask_about(page: Page, name: str, label: str | None, option: str | None) -> Locator:
    """Opens Ask, asks a question, about a topic or a subject when given,
    and returns its panel once the answer is in."""
    page.goto("/brain/ask")
    asked = question(page, name)
    if label is not None:
        asked.get_by_label(label, exact=True).select_option(label=option)
    press_ask(asked)
    return asked


def open_topic_page(page: Page, slug: str, name: str) -> None:
    page.goto(f"/brain/knowledge/{slug}")
    expect(
        panel(page, "Knowledge").get_by_role("heading", name=name, level=2)
    ).to_be_visible()
    expect(panel(page, "Knowledge").get_by_text("Loading…")).to_have_count(0)


def open_decision_page(page: Page, decision: dict) -> None:
    page.goto(f"/brain/history/{decision['id']}")
    expect(
        panel(page, "History").get_by_role("heading", name=decision["content"], level=2)
    ).to_be_visible()


def open_item_page(page: Page, item: dict) -> None:
    page.goto(f"/brain/everything/{item['id']}")
    expect(
        panel(page, "Everything").get_by_role("heading", name=item["content"], level=2)
    ).to_be_visible()


def card(scope: Locator, title: str) -> Locator:
    return scope.get_by_role("article", name=title, exact=True)


def card_rows(scope: Locator) -> Locator:
    """The rows of a card's first list."""
    return scope.get_by_role("list").first.locator(":scope > li")


def test_rows_in_knowledge_cards_are_inset_rows(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    stage_knowledge(page)

    open_topic_page(page, "postgres", "Postgres")
    knowledge = panel(page, "Knowledge")
    for title in ["Also called", "Under", "Includes", "Misconceptions"]:
        found = card_rows(card(knowledge, title))
        expect(found).to_have_count(2)
        assert_inset_rows(page, found, title)

    # The provisional topics' card uses the same rows.
    page.goto("/brain/knowledge")
    found = card_rows(
        card(panel(page, "Knowledge"), "Topics that arrived on their own")
    )
    expect(found).to_have_count(2)
    assert_inset_rows(page, found, "Topics that arrived on their own")


def test_conflicts_are_inset_rows_without_lines(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    topic(page, "Postgres")
    stage_decisions(page)

    asked = ask_about(page, "What is the history?", "Subject", "Postgres")
    conflicts = card_rows(card(asked, DISAGREE))
    expect(conflicts).to_have_count(2)
    assert_inset_rows(page, conflicts, "the conflicts")


def test_answer_items_and_the_ask_form_have_the_card_padding(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    stage_identity(page)

    asked = ask_about(page, "Who is this person?", None, None)
    card_pad = resolve(page, "--card-pad", "padding-top")
    radius = resolve(page, "--radius-lg", "border-top-left-radius")
    for where, surface in [
        ("the item", card(asked, "A student in Uppsala")),
        ("the form", asked.get_by_role("form", name="Question")),
    ]:
        look = style(surface)
        assert look["padding"] == [card_pad] * 4, where
        assert look["radii"] == [radius] * 4, where


def test_sub_headings_follow_the_type_rules(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    stage_knowledge(page)
    decision = stage_decisions(page)

    # A part of an item's page is a section heading.
    open_item_page(page, decision)
    part = panel(page, "Everything").get_by_role(
        "heading", name="Why the brain holds this", level=3
    )
    look = style(part)
    assert look["fontFamily"] == resolve(page, "--font-heading", "font-family")
    assert look["fontSize"] == resolve(page, "--text-h2-size", "font-size")
    assert look["lineHeight"] == resolve(page, "--text-h2-line", "line-height")
    assert look["fontWeight"] == "600"
    assert look["textTransform"] == "uppercase"

    # A broad topic's group is a section heading; a narrower one under it
    # is a label.
    asked = ask_about(page, "What do they know?", "Topic", "Databases")
    top = style(asked.get_by_role("heading", level=2).first)
    assert top["fontFamily"] == resolve(page, "--font-heading", "font-family")
    assert top["textTransform"] == "uppercase"
    nested = asked.get_by_role("heading", level=3)
    expect(nested.first).to_be_visible()
    for heading in nested.all():
        look = style(heading)
        assert look["fontFamily"] == resolve(page, "--font-ui", "font-family")
        assert look["fontSize"] == resolve(page, "--text-label-size", "font-size")
        assert look["lineHeight"] == resolve(page, "--text-label-line", "line-height")
        assert look["fontWeight"] == "500"
        assert look["color"] == resolve(page, "--text-2", "color")


def test_the_raw_answer_wraps(page: Page, invite_code: str, base_url: str):
    page.set_viewport_size({"width": 390, "height": 844})
    open_memory(page, invite_code, base_url)
    stage_identity(page)

    asked = ask_about(page, "Who is this person?", None, None)
    asked.get_by_role("button", name="Show what a feature receives").click()
    raw = asked.get_by_label("What a feature receives", exact=True)
    expect(raw).to_contain_text(LONG_WORD)

    frame = style(raw)
    assert frame["whiteSpace"] == "pre-wrap"
    assert frame["overflowX"] == "hidden"
    assert frame["overflowY"] == "auto"
    assert style(raw.locator("pre"))["whiteSpace"] == "pre-wrap"
    width, client = raw.evaluate("e => [e.scrollWidth, e.clientWidth]")
    assert width <= client, f"{width}px in {client}px"


def test_topic_and_decision_cards_are_a_wrapping_row(
    page: Page, invite_code: str, base_url: str, viewport: str
):
    open_memory(page, invite_code, base_url)
    stage_knowledge(page)
    decision = stage_decisions(page)

    open_topic_page(page, "postgres", "Postgres")
    cards = panel(page, "Knowledge").get_by_role("article")
    expect(cards).to_have_count(5)
    assert_a_wrapping_card_row(page, cards, viewport, "a topic's page")

    open_decision_page(page, decision)
    cards = panel(page, "History").get_by_role("article")
    expect(cards).to_have_count(2)
    assert_a_wrapping_card_row(page, cards, viewport, "a decision's page")

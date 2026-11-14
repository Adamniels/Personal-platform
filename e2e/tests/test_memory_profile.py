"""The memory centre's Profile and Rules sections: adding, correcting and
retiring entries by hand, and their earlier versions. See "The memory centre"
in Brain/README.md and "Editing changes provenance, and every edit records a
judgment" in Brain/docs/trust-model.md."""

import re
from pathlib import Path

import pytest
from playwright.sync_api import Browser, Locator, Page, expect

from conftest import capture, register_and_log_in

THEMES = ["light", "dark"]
AXE = Path(__file__).parents[2] / "design/ui/node_modules/axe-core/axe.min.js"
STORAGE_KEY = "platform-theme"
RULES_LINE = (
    "How the platform and its agents should work with you. "
    "A rule applies everywhere, at once."
)
# "13 Nov 2026, 09:41", the form a version's date is shown in.
DATE = re.compile(r"^\d{1,2} [A-Z][a-z]{2,3} \d{4}, \d{2}:\d{2}$")


def open_memory(
    page: Page, invite_code: str, base_url: str, path: str = "/brain"
) -> None:
    register_and_log_in(page, invite_code, base_url)
    page.goto(path)
    expect(page.get_by_role("tablist", name="Memory sections")).to_be_visible()


def panel(page: Page, name: str) -> Locator:
    return page.get_by_role("tabpanel", name=name)


def card(page: Page, name: str) -> Locator:
    return panel(page, "Profile").get_by_role("article", name=name)


def entry(scope: Locator, text: str) -> Locator:
    return scope.get_by_role("listitem").filter(has_text=text)


def toast(page: Page, text: str) -> Locator:
    return page.get_by_role("status").filter(has_text=text)


def add_profile_entry(page: Page, category: str, text: str) -> None:
    """Adds an entry through the section's primary button, which asks for the
    category first."""
    profile = panel(page, "Profile")
    profile.get_by_role("button", name="Add entry", exact=True).click()
    form = profile.get_by_role("form", name="Add entry")
    form.get_by_label("Category").select_option(label=category)
    form.get_by_label("New entry").fill(text)
    form.get_by_role("button", name="Save", exact=True).click()
    expect(entry(card(page, category), text)).to_be_visible()


def add_rule(page: Page, text: str) -> None:
    rules = panel(page, "Rules")
    rules.get_by_role("button", name="Add rule", exact=True).click()
    rules.get_by_label("New rule").fill(text)
    rules.get_by_role("button", name="Save", exact=True).click()
    expect(entry(rules, text)).to_be_visible()


def correct(scope: Locator, old: str, new: str, label: str) -> None:
    entry(scope, old).get_by_role("button", name="Edit", exact=True).click()
    field = scope.get_by_label(label, exact=True)
    expect(field).to_have_value(old)
    field.fill(new)
    scope.get_by_role("button", name="Save", exact=True).click()
    expect(entry(scope, new)).to_be_visible()


def retire_and_undo(page: Page, scope: Locator, text: str) -> None:
    entry(scope, text).get_by_role("button", name="Retire", exact=True).click()
    dialog = page.get_by_role("dialog", name="Retire this entry?")
    expect(dialog).to_be_visible()
    expect(
        dialog.get_by_text(
            "It stops being used. It stays in Everything, where you can bring it back."
        )
    ).to_be_visible()
    dialog.get_by_role("button", name="Retire entry", exact=True).click()

    expect(dialog).to_be_hidden()
    expect(entry(scope, text)).to_have_count(0)
    retired = toast(page, "Entry retired")
    expect(retired).to_be_visible()

    retired.get_by_role("button", name="Undo", exact=True).click()
    expect(entry(scope, text)).to_be_visible()


def test_i_can_add_a_profile_entry_and_it_stays(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)

    add_profile_entry(page, "Values", "Honesty over comfort")

    expect(toast(page, "Entry saved")).to_be_visible()
    # Only under Values.
    expect(entry(panel(page, "Profile"), "Honesty over comfort")).to_have_count(1)
    page.reload()
    expect(entry(card(page, "Values"), "Honesty over comfort")).to_be_visible()


def test_i_can_correct_an_entry_and_see_its_earlier_version(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)
    add_profile_entry(page, "Values", "Honesty over comfort")
    values = card(page, "Values")
    # An entry never edited has no earlier versions to show.
    expect(values.get_by_role("button", name="Earlier versions")).to_have_count(0)

    correct(values, "Honesty over comfort", "Honesty, said kindly", "Entry")

    expect(entry(values, "Honesty over comfort")).to_have_count(0)
    current = entry(values, "Honesty, said kindly")
    current.get_by_role("button", name="Earlier versions", exact=True).click()
    versions = current.get_by_role("list", name="Earlier versions")
    expect(versions.get_by_role("listitem")).to_have_count(1)
    old = versions.get_by_role("listitem").first
    expect(old).to_contain_text("Honesty over comfort")
    date = old.locator("time")
    expect(date).to_have_text(DATE)
    expect(date).to_have_css("font-family", re.compile("JetBrains Mono"))

    page.reload()
    expect(entry(card(page, "Values"), "Honesty, said kindly")).to_be_visible()


def test_i_can_retire_an_entry_and_bring_it_back(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url)
    add_profile_entry(page, "Goals", "Finish the degree")

    retire_and_undo(page, card(page, "Goals"), "Finish the degree")

    page.reload()
    expect(entry(card(page, "Goals"), "Finish the degree")).to_be_visible()


def test_i_can_add_correct_and_retire_a_rule(
    page: Page, invite_code: str, base_url: str
):
    open_memory(page, invite_code, base_url, "/brain/rules")
    rules = panel(page, "Rules")
    expect(rules.get_by_text(RULES_LINE, exact=True)).to_be_visible()

    add_rule(page, "Answer in English")
    expect(toast(page, "Entry saved")).to_be_visible()
    add_rule(page, "Keep answers short")
    # In the order they were made, oldest first.
    expect(rules.get_by_role("listitem")).to_have_text(
        [re.compile("Answer in English"), re.compile("Keep answers short")]
    )

    correct(rules, "Answer in English", "Answer in English, always", "Rule")
    # A correction keeps its place.
    expect(rules.get_by_role("listitem")).to_have_text(
        [re.compile("Answer in English, always"), re.compile("Keep answers short")]
    )

    retire_and_undo(page, rules, "Keep answers short")

    page.reload()
    expect(panel(page, "Rules").get_by_role("listitem")).to_have_text(
        [re.compile("Answer in English, always"), re.compile("Keep answers short")]
    )


def test_another_account_sees_none_of_it(
    page: Page,
    browser: Browser,
    base_url: str,
    invite_code: str,
    another_invite_code: str,
):
    open_memory(page, invite_code, base_url)
    add_profile_entry(page, "Identity", "A student in Uppsala")
    page.get_by_role("tab", name="Rules", exact=True).click()
    add_rule(page, "Answer in English")

    other = browser.new_context(ignore_https_errors=True, base_url=base_url)
    try:
        stranger = other.new_page()
        open_memory(stranger, another_invite_code, base_url)
        for name in ["Identity", "Goals", "Values"]:
            expect(card(stranger, name)).to_be_visible()
        expect(panel(stranger, "Profile").get_by_role("listitem")).to_have_count(0)
        expect(
            card(stranger, "Identity").get_by_text("Nothing under Identity yet.")
        ).to_be_visible()

        stranger.get_by_role("tab", name="Rules", exact=True).click()
        expect(
            panel(stranger, "Rules").get_by_text("No rules yet", exact=True)
        ).to_be_visible()
        expect(panel(stranger, "Rules").get_by_role("listitem")).to_have_count(0)
    finally:
        other.close()


def violations(page: Page) -> list[dict]:
    page.evaluate("document.fonts.ready.then(() => null)")
    if not page.evaluate("typeof axe !== 'undefined'"):
        page.add_script_tag(path=str(AXE))
    return page.evaluate(
        """() => axe.run().then(result => result.violations.map(v => ({
            id: v.id,
            targets: v.nodes.map(node => node.target.join(" ")),
        })))"""
    )


@pytest.mark.parametrize("theme", THEMES)
def test_profile_and_rules_pass_the_accessibility_check(
    page: Page, invite_code: str, base_url: str, viewport: str, theme: str
):
    open_memory(page, invite_code, base_url)
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', '{theme}')")
    page.reload()
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    add_profile_entry(page, "Values", "Honesty over comfort")
    add_profile_entry(
        page,
        "Working style",
        "Long focused blocks in the morning, meetings after lunch",
    )
    correct(
        card(page, "Values"), "Honesty over comfort", "Honesty, said kindly", "Entry"
    )
    entry(card(page, "Values"), "Honesty, said kindly").get_by_role(
        "button", name="Earlier versions", exact=True
    ).click()
    # The bar has drawn once more with the account, so it is checked whole.
    expect(page.get_by_role("button", name=re.compile("@"))).to_be_visible()
    # The saved toasts have left, so the page is captured as it rests.
    expect(page.get_by_role("status").filter(has_text="Entry saved")).to_have_count(
        0, timeout=10_000
    )

    assert violations(page) == []
    capture(page, f"memory-profile-{theme}")

    goals = card(page, "Goals")
    goals.get_by_role("button", name="Add", exact=True).click()
    entry(card(page, "Working style"), "Long focused").get_by_role(
        "button", name="Edit", exact=True
    ).click()
    assert violations(page) == []
    capture(page, f"memory-profile-editing-{theme}")

    page.get_by_role("tab", name="Rules", exact=True).click()
    add_rule(page, "Answer in English")
    add_rule(page, "When I ask for a review, list the problems before the praise")
    expect(page.get_by_role("status").filter(has_text="Entry saved")).to_have_count(
        0, timeout=10_000
    )
    assert violations(page) == []
    capture(page, f"memory-rules-{theme}")

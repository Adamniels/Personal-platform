"""The look checks: three rules of design/DESIGN.md that hold on every page,
as functions a test calls on an open page once it has drawn. Each fails
naming the elements that break the rule, so a failure says where to look.
See "The look checks" in README.md.

They read every visible element of the page, those inside an open shadow
root included, so the nav bar's items are checked as a person sees them.
Colours are compared with the token as the page resolves it, so no value is
written here."""

from playwright.sync_api import Page

# Every visible element of the document and of each open shadow root in it,
# with a short name for a failure message: its tag, id, classes and the
# start of its text.
ELEMENTS = """
const all = [];
const walk = root => {
    for (const element of root.querySelectorAll("*")) {
        all.push(element);
        if (element.shadowRoot) walk(element.shadowRoot);
    }
};
walk(document);
const visible = all.filter(element => {
    if (element.getClientRects().length === 0) return false;
    return getComputedStyle(element).visibility !== "hidden";
});
const describe = element => {
    let name = element.tagName.toLowerCase();
    if (element.id) name += `#${element.id}`;
    for (const cls of element.classList) name += `.${cls}`;
    const text = (element.textContent || "").trim().replace(/\\s+/g, " ");
    return text ? `${name} "${text.slice(0, 40)}"` : name;
};
const resolve = (token, property) => {
    const probe = document.createElement("div");
    probe.style.setProperty(property, `var(${token})`);
    document.body.append(probe);
    const value = getComputedStyle(probe).getPropertyValue(property);
    probe.remove();
    return value;
};
"""

HEADINGS = (
    """() => {"""
    + ELEMENTS
    + """
    const family = value => value.split(",")[0].trim().replace(/^["']|["']$/g, "");
    const heading = family(resolve("--font-heading", "font-family"));
    return visible
        .filter(element => family(getComputedStyle(element).fontFamily) === heading)
        .filter(element => {
            const style = getComputedStyle(element);
            return style.textTransform !== "uppercase" || style.fontWeight !== "600";
        })
        .map(element => {
            const style = getComputedStyle(element);
            return `${describe(element)}: ${style.textTransform}, weight ${style.fontWeight}`;
        });
}"""
)

SIDEWAYS = (
    """() => {"""
    + ELEMENTS
    + """
    const offenders = [];
    const root = document.documentElement;
    if (root.scrollWidth > root.clientWidth) {
        offenders.push(`the document: ${root.scrollWidth}px in ${root.clientWidth}px`);
    }
    for (const element of visible) {
        const overflow = getComputedStyle(element).overflowX;
        if (overflow !== "auto" && overflow !== "scroll") continue;
        if (element.scrollWidth > element.clientWidth) {
            offenders.push(
                `${describe(element)}: ${element.scrollWidth}px in ${element.clientWidth}px`
            );
        }
    }
    return offenders;
}"""
)

# A selected table row and a selected option keep their own rule,
# --info-soft, so they are left out. A breadcrumb's last item says where the
# page is with aria-current="page" but is no nav item, tab or segment, so it
# is held only to the first half: never marked with amber.
ACTIVE = (
    """() => {"""
    + ELEMENTS
    + """
    const accent = resolve("--accent-border", "color");
    const fill = resolve("--text", "background-color");
    const sides = ["Top", "Right", "Bottom", "Left"];
    const offenders = [];
    for (const element of visible) {
        const current = element.getAttribute("aria-current") === "page";
        const selected = element.getAttribute("aria-selected") === "true";
        if (!current && !selected) continue;
        const role = element.getAttribute("role") || "";
        const tag = element.tagName.toLowerCase();
        if (tag === "tr" || tag === "option" || role === "row" || role === "option") continue;
        const style = getComputedStyle(element);
        const outlined = style.outlineStyle !== "none"
            && parseFloat(style.outlineWidth) > 0
            && style.outlineColor === accent;
        const bordered = sides.some(side =>
            style[`border${side}Style`] !== "none"
            && parseFloat(style[`border${side}Width`]) > 0
            && style[`border${side}Color`] === accent);
        if (outlined || bordered) {
            offenders.push(`${describe(element)}: marked in --accent-border`);
        }
        const crumb = element.closest('nav[aria-label="Breadcrumb" i]') !== null;
        const tabOrNavItem = (current && !crumb) || role === "tab";
        if (tabOrNavItem && style.backgroundColor !== fill) {
            offenders.push(
                `${describe(element)}: fill ${style.backgroundColor}, not --text ${fill}`
            );
        }
    }
    return offenders;
}"""
)


def _check(page: Page, script: str, rule: str) -> None:
    offenders = page.evaluate(script)
    assert not offenders, f"{rule}, broken by:\n" + "\n".join(offenders)


def assert_headings_are_capitals(page: Page) -> None:
    """Hard rule 12: every visible element in the heading font is in
    capitals at weight 600."""
    _check(page, HEADINGS, "the heading font is weight 600 in capitals")


def assert_nothing_scrolls_sideways(page: Page) -> None:
    """Hard rule 13: the document does not scroll sideways, and no visible
    element that scrolls sideways has more to scroll than it shows."""
    _check(page, SIDEWAYS, "nothing scrolls sideways")


def assert_active_is_inverted(page: Page) -> None:
    """Hard rule 11: what is active is never marked with amber, and an active
    tab or nav item has the --text fill."""
    _check(page, ACTIVE, "active means inverted")

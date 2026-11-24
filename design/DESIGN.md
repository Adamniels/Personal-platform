# Personal Platform design system

This file tells an implementing agent how the Personal Platform UI must look. Read it fully before building or changing any UI. Values live in `tokens.css` in this folder; this file gives the rules for using them. Reference images of designed screens are in `reference/`.

The product is a private web application used for hours every day. The look is quiet and confident: near black or pale grey grounds, rounded white surfaces with thin 1px outlines, condensed capitals for titles, thin numerals for the numbers that matter, one amber accent, small coloured status words, and pixel art characters for agents. It is a tool, not a marketing site.

## 0. What this decides, and what it does not

**This is a style, not a specification of any page.** It was designed without knowing what the platform does. Read it with that line in mind:

* **Binding:** `tokens.css`, the hard rules in section 1, the layout rules in section 2, the navigation in section 3, how each component in section 4 looks, how an agent avatar is drawn, and the fonts. An agent building any UI follows these exactly.
* **Illustrative only:** everything a reference screen contains. Which tabs, columns, filters, links, cards, statuses, labels and numbers appear, what they are called, and how a page is laid out, were invented so there was something to draw. They are never the truth about a feature.

**What a page contains and how it is laid out comes from its slice, and only from its slice.** Where a reference screen and a slice differ, the slice is right and the screen is not consulted. A slice that needs something no screen shows, a new page, a new component, a new state, builds it from the same tokens and the same component rules, taking the reference screens as the direction for how it should feel: how round, how quiet, how a status reads, how much amber. It does not invent a colour, a radius or a type size to do it.

Sections 3, 5 and 6 mix the two, and each says where its line runs.

## 1. Hard rules

1. Use tokens only. No raw hex values and no sizes outside the scales in `tokens.css`.
2. Dark mode and the phone layout swap token values on the root element. Components contain no theme conditions.
3. Surfaces are separated by a 1px `--border`, not by shadow. Shadow is for floating layers only: `--shadow-1` for menus and toasts, `--shadow-2` for dialogs and sheets. Rows inside a card that hold more than a label and a value sit on `--surface-2` insets rather than between lines.
4. At most one primary button per view. Amber is reserved for the primary button, for progress, and for anything that needs the user. Nothing else is amber.
5. Status is always a dot plus a word. Colour is never the only signal.
6. Text colour is `--text`, `--text-2` or `--text-3`. Nothing fainter. All three pass 4.5:1 on `--bg` and on every surface, in both themes.
7. Minimum text size is 12px. Inputs on phone are 16px (`--input-font-size`).
8. Times, dates, ids and counts use the mono font with tabular figures.
9. Every interactive element is a real `button`, `a` or form control with a visible focus ring (`outline: var(--focus-ring); outline-offset: var(--focus-offset)` on `:focus-visible`). Never remove the ring. Icon only buttons need an `aria-label`.
10. Touch targets are at least 44px on phone. `--control-h` and `--row-h` already grow below the breakpoint.
11. Active means inverted. The selected nav item, tab or segment has a `--text` fill with its label in `--bg`. It is never marked with amber.
12. The heading font is always weight 600 in capitals, and is kept for the page title, one hero label per card, and section headings. The number a card is about uses thin numerals, the UI font at weight 300, never bold.
13. Nothing scrolls sideways, ever. On phone, content folds into a meta line, wraps, hides when it repeats something, or moves into a picker. A page is planned so no table needs to scroll, and a table's frame scrolls before the page does.

## 2. Layout

* One breakpoint: 640px. Below it is the phone layout. Design must work at 390px wide.
* Page shell: on desktop the whole window, padded `--page-pad` on every side. The nav rail is `--rail-w` wide, `--page-pad` from the window's left edge at every width, and stays put as the page scrolls. Right of it, a `--page-pad` gap away, the top bar runs to the window's right edge less `--page-pad`, the account pill at its end, and under the bar the content column, up to `--content-max` wide and centred in the room right of the rail; where that room is narrower, the column fills it less `--page-pad` either side. On a very wide window the rail and the pill sit far from the content, and that is the look chosen. On phone the shell is a column with no padding: the top bar, the content with `--page-pad` at its sides and `--space-6` under it, and the bottom tab bar.
* Content column: the top bar, then the page's sections, `--space-4` apart.
* Page header: an optional mono caption line, the page title, and an optional one line summary in body large, `--text-2`. Actions for the whole page sit on the right of the header, aligned to its bottom, and wrap under it on phone.
* Rows of cards: a wrapping flex row with a `flex-basis` per card and gap `--space-4`, so cards take unequal widths on desktop and stack on phone without a breakpoint. A card that should be wider takes a larger `flex-grow` than its neighbours.
* Main plus side panel pages (session, agents): a wrapping flex row. Main column `flex: 999 1 560px`, side column `flex: 1 1 280px` (320px for the agent list), both `min-width: 0`, so the side column stacks under the main one on phone.
* Reading text (notes, lessons, briefings) uses body large (16/24) and `max-width: var(--reading-max)`.
* Spacing use: inside a control `--space-2`; between inset rows in a card `--inset-gap`; between the parts of a card and between cards `--space-4`; card padding `--card-pad`.

## 3. Navigation

Binding here: the rail, the top bar and the bottom tab bar, their sizes, fills and borders, how an item and the active item look, the dot that marks something needing the user, and what the top bar is made of. Illustrative: which links exist, their names, icons and order. Those follow what the platform actually has, as its slices say. The reference screens also draw a search field and a notifications button in the top bar; the bar holds neither until the platform has something behind them.

The bar is `nav.js` in this folder, `<platform-nav>`, with `theme.js` and `page.css` beside it; what it holds and how a page includes it is "Navigation" in `High-level/docs/platform-architecture.md`.

* Rail, desktop: `--rail-w` wide, as tall as the window less the shell's padding, `--surface` fill, 1px `--border`, `--radius-lg`, its items in a centred column with `--space-2` between them and `--space-4` above and below. At the top the mark: a 44px square, `--radius-sm`, `--accent` fill, its icon in `--on-accent`, linking to the front page. Under it a 32px wide, 1px `--border` line, then one item per link. At the bottom the theme toggle.
* Rail item: a 44px circle with a 20px stroke icon and no visible text, its name as `aria-label`. `--surface-2` fill and `--text-2` icon. The active item is inverted, `--text` fill and `--bg` icon, and carries `aria-current="page"`. A link without an icon of its own gets the fallback icon.
* An item that has something needing the user shows an 8px `--accent` dot on its top right corner, ringed 2px in the fill behind it, with the words "needs you" for assistive technology.
* Top bar: `--top-bar-h` high, no fill and no border, so it sits on the ground. Left: the breadcrumb, in the mono font, capitals, `--text-3`, cut with an ellipsis rather than wrapped. A page names its own crumb; without one it is the name of what `current` points at. Right: the account pill, 44px high, `--radius-full`, `--surface` fill, 1px `--border`, holding a 34px round `--surface-3` avatar with the first letter of the account's email in capitals and, on desktop, the email, cut at 24 characters with an ellipsis, since the platform has no display name. It opens the account menu: "Change password" and "Log out".
* Phone: the rail is not shown. The top bar gains the mark on its left, 40px square, and the account pill is the avatar alone. The theme toggle moves into the account menu, as an item saying "Light mode" or "Dark mode". The links move to a bottom tab bar that sticks to the bottom: `--tab-bar-h` high, `--surface` fill, 1px top border, at most five equal tabs, and past five the first four show and More holds the rest. Each tab is a 20px stroke icon over a 12px label, weight 500, in `--text-2`, at least 56px high with `--radius-md` corners. The active tab is inverted, as on the rail.
* Menus, the account menu and More: `--surface` fill, 1px `--border`, `--radius-md`, `--shadow-1`, each item `--row-h` high.

## 4. Components

The components are built as React in `ui/`, the `@platform/ui` package, each when a page first needs it, and each built one is shown in every state in its gallery at `/design/gallery/`; how to use and add one is in `ui/README.md`.

### Button
Height `--control-h`, padding 0 18px (ghost 0 14px), `--radius-full`, 14px label.

* Primary: `--accent` fill, `--on-accent` text, 1px `--accent-border`, weight 600. One per view.
* Secondary: transparent fill, `--text`, 1px `--border-strong`, weight 500.
* Ghost: transparent fill and border, `--text-2`, weight 500.
* Destructive: `--danger-soft` fill, `--danger` text and 1px `--danger` border, weight 600.
* Hover on secondary and ghost: `--surface-2` fill. Disabled: no fill, `--text-3`, 1px `--border`.

### Icon button and arrow link
* Icon button: a 44px circle with an 18px or 20px stroke icon and an `aria-label`. For a tool: `--surface` fill, 1px `--border`, icon in `--text-2`. Inverted, `--text` fill and `--bg` icon, for the main media action, as for the active nav item.
* Arrow link: a 36px `--surface-2` circle with an arrow pointing up and right, in a card's header on the right. It opens the page the card is a summary of, and its `aria-label` says which.

### Text field, textarea, select
Label above the field in caption style (12/16, weight 500, `--text-2`), gap `--space-1`. Field: height `--control-h`, padding 0 16px, `--radius-full`, 1px `--border-strong`, `--surface` fill, `--text`, font size `--input-font-size`. Placeholder `--text-3`. Help text below in caption style, `--text-3`. Error: 2px `--danger` border, `aria-invalid="true"`, and a message in `--danger`, weight 500, that starts with the word "Error". Select uses the same rule and the native menu. Textarea has `--radius-md` corners, padding 12px 16px and body large for notes.

### Checkbox
Native checkbox, 18px box, `accent-color: var(--info)`. The whole label row is the target: `--row-h` tall, gap 10px between box and text. Completed to do items get `--text-3` and a line through.

### Switch
A 44 by 26px track with `--radius-full` and an 18px round thumb, 3px from the edge. On: `--ok` fill and border, thumb in `--surface`, at the right. Off: no fill, 1px `--border-strong`, thumb in `--border-strong`, at the left. It is a real `button` with `role="switch"` and `aria-checked`, and the whole label row is the target, as for the checkbox.

### Badge and status label
Both: height 26px, padding 0 10px, `--radius-full`, caption style.

* Badge (categories and counts): `--surface-3` fill, `--text-2`, no dot.
* Status label: a 6px round dot in the text colour plus a word, soft fill with its matching text token. Exactly five statuses:

| Status | Fill | Text | Example words |
|---|---|---|---|
| Running | `--info-soft` | `--info` | Working, Running, In progress, Corrected, Editing |
| Needs you | `--accent-soft` | `--accent-text` | Needs you, Needs review, overdue counts |
| Done | `--ok-soft` | `--ok` | Done, Confirmed, Now |
| Failed | `--danger-soft` | `--danger` | Failed |
| Idle | `--surface-3` | `--text-2` | Idle |

* Tool chip (for example the agent's tool name): height 22px, padding 0 10px, `--radius-full`, 1px `--border-strong`, no fill, caption style, `--text-2`.

### Stat pill
Height 36px, padding 0 14px, `--radius-full`, `--surface` fill, 1px `--border`: a 6px dot, a label in `--text-3`, and a value in the mono font, `--text`. A row of them wraps. It is not shown on phone, where the page's summary line carries the same counts.

### Tabs
A segmented track: `--surface` fill, 1px `--border`, `--radius-full`, 4px padding. Segment: height `--control-h`, padding 0 16px, `--radius-full`, no border, `--text-2`, weight 500. Selected: inverted, `--text` fill, label in `--bg`, weight 600, `aria-selected="true"`. On desktop the track is as wide as its segments and holds as many as there are; when they do not fit on one line it wraps onto further lines, its corners become `--radius-lg`, and it never scrolls. On phone it is the full width with equal segments, their labels 13px with 4px side padding, cut with an ellipsis if one does not fit. On phone more than five segments become a select, styled by the field rule as a pill at the full width, holding one option per segment with its count in brackets; `Tabs` does this itself, with CSS alone. A tab's count follows its label as a badge. An agent tab is a segment of its own, 1px `--border` when not selected, with a 1x avatar before the name and a 6px status dot after it; agent tabs wrap onto a second line rather than scroll.

### Card
`--surface` fill, 1px `--border`, `--radius-lg`, padding `--card-pad`, inner gap `--space-4`, no shadow. Header: the card's label on the left (15/22, weight 500, `--text-2`), which is its heading, and a status label, a caption or an arrow link on the right. Body: the one thing the card is about, as a key number in thin numerals with a two line caption to its right, or as a hero label in the heading font, then at most three inset rows. An action, when the card has one, is last, pushed to the bottom (`margin-top: auto`) so cards in a row align. A card whose content needs the user gets a 1px `--accent-border` in place of its border.

Thin numerals: the UI font at weight 300, `letter-spacing: var(--tracking-num)`, tabular figures, in `--text`. `--text-num-xl-size` for the one number a card is about, `--text-num-l-size` for clocks, counts and timers, and for a card that holds two numbers.

### Inset list row
A row inside a card: `--surface-2` fill, `--radius-md` (`--radius-sm` for a row of one line), padding 8px 12px, min height `--row-h`, with `--inset-gap` between rows and no divider lines. Left to right: a time in the mono font or a sprite tile, the title at weight 500 with an optional mono line under it, and a status label on the right. In a list where one row is current, such as the day's events, only the current row has the fill and the others are plain. A short list of a label and a value alone, with no status and nothing to open, is plain rows 36px high with a 1px `--border` under each, the label in `--text-2` and the value in the mono font.

### Needs you banner
`--accent-soft` fill, 1px `--accent-border`, `--radius-md`, padding 14px 16px: a warning icon in `--accent-text`, the words "Needs you" in caption style, `--accent-text`, weight 600, then the question at weight 600, an optional line of consequence in `--text-2`, and at most three actions, the recommended one primary. One per view. The card that holds it also takes the `--accent-border` outline.

### Table
In a card of its own: `--surface` fill, 1px `--border`, `--radius-lg`, no inner padding; or with `--radius-md` inside another card. It is planned never to scroll sideways: columns carry a priority, and the table is checked at 390px and 1280px. The frame keeps `overflow-x: auto` as a safety net, so a table that is still too wide scrolls inside its frame and neither clips nor makes the page scroll. Header cells: no fill, caption style, `--text-3`; the sorted column is `--text`, weight 600, with an arrow and `aria-sort`. Body cells: padding 6px 12px, height `--row-h`, 1px `--border` between rows. Numbers are right aligned in the mono font. Hover row: `--surface-2`. Selected or editing row: `--info-soft`.

* Inline edit replaces the cell text with a field in place, with Save (secondary) and Cancel (ghost) directly under it.
* Column priority: every column has priority 1, 2 or 3. Below 640px only priority 1 columns show, and priority 2 values fold into one mono meta line under the first cell (for example "Topic · 3 / 5 · 12 Nov"). Priority 3 columns are hidden on phone.
* Filters sit in a wrapping row above the table: a search field (`flex: 1 1 220px`), selects, removable filter pills (32px high, `--radius-full`, `--surface-3` fill, no border, 13px, `--text`, 14px padding on the left and 4px on the right, the remove button a 26px round ghost button inside it), and a mono result count on the right.
* Paging sits under the table: "Rows 1 to 7 of 214" on the left in `--text-2`, Previous and Next as secondary buttons on the right.

### Dialog
Max width 480px, `--surface` fill, 1px `--border`, `--radius-xl`, `--shadow-2`, padding `--space-6`. Backdrop: `--bg` at 70% opacity. The title is an h2 in the heading font that states the action as a question. Actions are right aligned, cancel first; the confirming verb repeats the title ("Delete entries"). A dialog that only shows something, such as an image, asks nothing: its title names what it shows, and its one action is a secondary Close. On phone it becomes a full width sheet fixed to the bottom, with `--radius-xl` on its top corners only.

### Toast
`--surface` fill, 1px `--border-strong`, `--radius-full`, `--shadow-1`, padding 6px 6px 6px 16px, an 8px status dot, its text and one text action in `--info`, weight 600. Bottom right on desktop, above the tab bar on phone, max width 420px. Success leaves after 5 seconds. Errors stay until dismissed and start with the word "Error".

### Empty state
1px dashed `--border-strong`, `--radius-lg`, padding `--space-7` `--space-4`, centred. One line in the heading font (`--text-h2-size`) that says what is empty, one small line (13/18, `--text-2`) that says what happens next, at most one secondary button. No illustration.

### Kanban column and card
Column: 300px wide, `--surface-2` fill, `--radius-lg`, padding `--space-2`, gap `--space-2`, a header with the column name (caption, weight 600, `--text-2`) and a mono count. Card: `--surface` fill, 1px `--border`, `--radius-md`, padding 10px 12px, title weight 500, one badge and one mono date; an overdue date is `--danger`, weight 500, and says "overdue". Dragging: `--shadow-1` plus a 2px `--focus` outline. On phone one column shows at a time, picked with tabs above it, each with its count.

### Calendar event block
The inset list row, with a category. `--radius-md`, padding 8px 12px, soft fill with its matching text token. Title 13/18, weight 600. Time and category on a second line in the mono font, and the category is always written out. Categories: study uses `--info-soft` and `--info`; work uses `--accent-soft` and `--accent-text`; own time uses `--ok-soft` and `--ok`; a placeholder block uses no fill, `--text-2` and a 1px dashed `--border-strong`. Events under 30 minutes show one line, title only.

### Timeline entry
A 10px wide rail with an 8px ring (2px `--border-strong`, `--surface` fill) and a 1px `--border` line down to the next entry; no line after the last entry. Content: a mono timestamp in `--text-3`, one body line, an optional small detail line in `--text-2`. Gap between rail and content `--space-3`, 14px between entries. An entry that needs the user has a filled `--accent` dot with an `--accent-border` ring.

### Step list
Steps in their order, `--space-4` apart, each a 32px round circle and its content beside it, `--space-3` away, the title's line centred on the circle. Done: `--ok-soft` with a check in `--ok`, the title at weight 600 and one line of what happened in `--text-2`. Current: `--accent` with its number in `--on-accent`, the title at weight 600 with a status label beside it when the step needs the user, and its content open. To come: `--surface-3` with its number in `--text-2`, the title in `--text-2` and one line in `--text-3` of what comes there. Numbers are in the mono font. A step that is not current holds no button. The state is said in words to assistive technology, never by the circle's colour alone.

### Progress bar and meter
* Bar: track 6px high, `--radius-full`, `--surface-3` fill, no border. Fill: `--accent` for progress and for context used; `--info` for budget used.
* Tick bar: one tick per item in a row 44px high, each `--radius-xs`, equal widths, 4px apart; `--accent` for the items that need the user and `--text-2` for the rest.
* Steps: one 8px high pill per step, 4px apart; done steps `--accent`, the current step no fill with a 1px `--accent-border`, the rest `--surface-3`.
* A meter row has a mono label (28px wide) on the left and a mono value on the right.

Give each `role="img"` and an `aria-label` that says the number.

## 5. Agents

### Avatars
Agents are shown as pixel art characters. The six sprites are in `sprites/` as 34 by 44 pixel PNGs with transparent backgrounds: `builder`, `engineer`, `scribe`, `tester`, `reviewer`, `deployer`. One character per agent role.

* Render only at 1x (34 by 44) or 2x (68 by 88) with `image-rendering: pixelated`. Never smooth, crop, tint or stretch them.
* 1x in tabs and in list rows; 2x in agent cards and roster tiles.
* A sprite sits on a tile: `--surface-2` fill, or `--surface-3` where the tile is itself on `--surface-2`, with `--radius-sm` at 1x and `--radius-md` at 2x. In a tab it has no tile.
* When the name is next to the sprite, use `alt=""`; when the sprite stands alone, use the agent name as `alt`.
* New characters must match the set: same canvas size, a 1px dark outline (`#1B1428`), three tones per material, a front facing pose with one prop.

### Agent card
The card rule with padding `--space-4` and a `--space-4` gap between the sprite tile and the content. Content, top to bottom: name (weight 600) with a status label on the right; the task in the mono font, `--text-3`; a tool chip plus the model in the mono font; two meters, CTX (context used) and BUD (budget used). The selected card gets a 2px `--accent-border` outline (1px border plus `box-shadow: 0 0 0 1px var(--accent-border)`).

### Agents page
Illustrative, apart from how each piece looks. It shows one way the avatars, the agent card, the status labels and the meters come together. What the page for running agents contains is decided when that feature is planned.

* A roster strip: one card per status (Needs you, Working, Done, Idle) with the status as a dot and a word, its count in thin numerals on the right, and one roster tile per agent, a 2x sprite on its tile. The Needs you card has the `--accent-border` outline.
* Below it, the agent list (cards) on the left and the detail panel on the right.
* Detail panel: a card holding the agent tabs; a mono line with name, path and branch; a log on a `--surface-2` inset in the mono font, 13/20, where the user's prompts start with ">" and use `--accent-text`; a needs you banner with the question, one line of consequence and the choices as buttons, the recommended one primary; a message field with a Send button; a mono status line above the card's bottom edge, over a 1px `--border`.

## 6. Reference screens

Illustrative, all of it. These are in `reference/`, each in light and dark, on desktop and on phone, with two boards that show the tokens and every component. They exist so an agent can see the style applied to something whole. The descriptions below say what was drawn, not what any feature has: the Memory screen, for one, shows tabs, columns, statuses and a way of writing levels that the brain does not have, and every screen's top bar shows a search field and a notifications button that the platform does not have.

* `foundations-tokens-and-rules.png`, `components-light.png`, `components-dark.png`, `navigation-bar.png`: the tokens and the components. These match sections 1 to 4 and can be trusted for how a component looks, with two exceptions where this file is right: a board names a token the way the drawing tool did, where `tokens.css` has the name to use, and a few corners are drawn at 16, 18 or 22px, where the nearest step of the radius scale is the rule.
* `home-*`, `memory-browser-*`, `workbench-session-*`, `agents-*`: whole screens, as examples of the style.

What was drawn:

* Home: a row of stat pills, a date line in mono, the page title, a one line summary of what needs the user, and the time in thin numerals. Then one card per feature: Workbench with a hero label, a progress bar, the day's events as inset rows and a needs you banner; Review with its count in thin numerals, a tick bar and the primary button; Agents with its count and one inset row per agent; Memory; Learning with steps; and News.
* Memory browser: a caption line, the page title and its summary, two counts in thin numerals with an "Add entry" primary button, view tabs (Profile, Rules, Topics, Timeline, Browse), the filter row, the table with inline editing, and paging.
* Workbench session: a breadcrumb of links, a status label with a project badge and a mono time range, the page title, Pause and End session buttons, the notes card (reading text with section headings, a code block on `--surface-2`, a to do checklist, an add note field), and a side column with a session card holding its timer in thin numerals, linked kanban cards and a "Sent to memory" timeline.
* Agents: described above.

Every page, drawn here or not, is built from the same tokens and components, with its content and layout taken from its slice. Do not invent new colours, radii or type sizes for any of them.

## 7. Fonts

Inter (300, 400, 500, 600), Barlow Condensed (600) and JetBrains Mono (400, 500). They are hosted here, in `fonts/`, as woff2 in the latin and latin-ext subsets, and declared in `fonts.css`, which a page loads from `/design/fonts.css` beside `/design/tokens.css`. Never load a font or a stylesheet from an outside host, Google Fonts included. Fallbacks are in the `--font-*` tokens. Where the files came from, and how the proxy serves them, is in `fonts.css` and in "The design folder" in `infra/README.md`.

Each face has one job. Inter is the interface: every label, line and control, and at weight 300 the thin numerals. Barlow Condensed is the heading font, used only at weight 600 in capitals, as hard rule 12 says: the page title (`--text-display-size`, with `letter-spacing: var(--tracking-display)`), a card's hero label (`--text-hero-size`) and section headings (`--text-h2-size`). JetBrains Mono is for what hard rule 8 names.

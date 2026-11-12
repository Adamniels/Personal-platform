# Personal Platform design system, v1

This file tells an implementing agent how the Personal Platform UI must look. Read it fully before building or changing any UI. Values live in `tokens.css` in this folder; this file gives the rules for using them. Reference images of designed screens are in `reference/`.

The product is a private web application used for hours every day. The look is calm, dense and readable: ink navy grounds, thin 1px outlines, one amber accent, small coloured status words, and pixel art characters for agents. It is a tool, not a marketing site.

## 0. What this decides, and what it does not

**This is a style, not a specification of any page.** It was designed without knowing what the platform does. Read it with that line in mind:

* **Binding:** `tokens.css`, the hard rules in section 1, the layout rules in section 2, how each component in section 4 looks, how an agent avatar is drawn, and the fonts. An agent building any UI follows these exactly.
* **Illustrative only:** everything a reference screen contains. Which tabs, columns, filters, links, cards, statuses, labels and numbers appear, what they are called, and how a page is laid out, were invented so there was something to draw. They are never the truth about a feature.

**What a page contains and how it is laid out comes from its slice, and only from its slice.** Where a reference screen and a slice differ, the slice is right and the screen is not consulted. A slice that needs something no screen shows, a new page, a new component, a new state, builds it from the same tokens and the same component rules, taking the reference screens as the direction for how it should feel: how dense, how quiet, how a status reads, how much amber. It does not invent a colour, a radius or a type size to do it.

Sections 3, 5 and 6 mix the two, and each says where its line runs.

## 1. Hard rules

1. Use tokens only. No raw hex values and no sizes outside the scales in `tokens.css`.
2. Dark mode swaps token values on the root element. Components contain no theme conditions.
3. Surfaces are separated by a 1px `--border`, not by shadow. Shadow is for floating layers only: `--shadow-1` for menus and toasts, `--shadow-2` for dialogs.
4. At most one primary button per view. Amber is reserved for the primary button, for progress, and for the outline of the selected tab, the selected panel and anything that needs the user.
5. Status is always a dot plus a word. Colour is never the only signal.
6. Text colour is `--text`, `--text-2` or `--text-3`. Nothing fainter. All three pass 4.5:1 on `--bg`, `--surface` and `--surface-2`.
7. Minimum text size is 12px. Inputs on phone are 16px (`--input-font-size`).
8. Times, dates, ids and numbers in tables use the mono font with tabular figures.
9. Every interactive element is a real `button`, `a` or form control with a visible focus ring (`outline: var(--focus-ring); outline-offset: var(--focus-offset)` on `:focus-visible`). Never remove the ring. Icon only buttons need an `aria-label`.
10. Touch targets are at least 44px on phone. `--control-h` and `--row-h` already grow to 44px below the breakpoint.

## 2. Layout

* One breakpoint: 640px. Below it is the phone layout. Design must work at 390px wide.
* Page container: `max-width: var(--content-max)`, centred, side padding `var(--page-pad)`, top padding `--space-5`, gap between page sections `--space-4` to `--space-5`.
* Page shell: a column with the top nav bar, the page container, and on phone the bottom tab bar.
* Card grids: `grid-template-columns: repeat(auto-fit, minmax(min(340px, 100%), 1fr))`, gap `--space-4`.
* Main plus side panel pages (session, agents): a wrapping flex row. Main column `flex: 999 1 560px`, side column `flex: 1 1 280px` (320px for the agent list), both `min-width: 0`, so the side column stacks under the main one on phone.
* Reading text (notes, lessons, briefings) uses body large (16/24) and `max-width: var(--reading-max)`.
* Spacing use: inside a control `--space-2`; between rows in a card `--space-3`; card padding and grid gap `--space-4`; between page sections `--space-5`.

## 3. Navigation

Binding here: the bar's height, fill and border, how a link and the active link look, the dot that marks something needing the user, and how the bar becomes a bottom tab bar on phone. Illustrative: which links exist, their names and order, and whether the bar holds a search button, a theme toggle or an avatar. Those follow what the platform actually has, as its slices say.

The bar is `nav.js` in this folder, `<platform-nav>`, with `theme.js` and `page.css` beside it; what it holds and how a page includes it is "Navigation" in `High-level/docs/platform-architecture.md`.

* Desktop: a top bar, height `--nav-h`, `--surface` fill, 1px bottom border. Left: a small amber square mark and the wordmark in the heading font, 16px, weight 600. Then the links: Home, Memory, Review, Workbench, Agents, Learning, News. Right: a search button showing "Search" and the shortcut, a theme toggle, and a round avatar link to settings.
* Nav link: height 36px, padding 0 12px, `--radius-md`, 14px, weight 500, colour `--text-2`, transparent border. The active link has `--text` colour, `--surface-2` fill and a 1px `--accent-border` outline, and carries `aria-current="page"`.
* The Agents link shows a 6px `--accent-text` dot after its label when any agent needs the user.
* Phone: the top bar keeps only the mark, the wordmark, a search icon button and the avatar. The links move to a bottom tab bar fixed to the bottom, height `--nav-h`, `--surface` fill, 1px top border, five equal tabs: Home, Memory, Workbench, Agents, More. Each tab is a 20px stroke icon over a 12px label. The active tab has `--text` colour and a 2px `--accent` line on its top edge. Review, Learning and News live under More.

## 4. Components

The components are built as React in `ui/`, the `@platform/ui` package, and each is shown in every state in its gallery at `/design/gallery/`; how to use and add one is in `ui/README.md`.

### Button
Height `--control-h`, padding 0 14px (ghost 0 10px), `--radius-md`, 14px label.

* Primary: `--accent` fill, `--on-accent` text, 1px `--accent-border`, weight 600. One per view.
* Secondary: `--surface` fill, `--text`, 1px `--border-strong`, weight 500.
* Ghost: transparent fill and border, `--text-2`, weight 500.
* Destructive: `--danger-soft` fill, `--danger` text and 1px `--danger` border, weight 600.
* Hover on secondary and ghost: `--surface-2` fill. Disabled: `--surface-2` fill, `--text-3`, 1px `--border`.

### Text field, textarea, select
Label above the field in caption style (12/16, weight 500, `--text-2`), gap `--space-1`. Field: height `--control-h`, padding 0 10px, `--radius-md`, 1px `--border-strong`, `--surface` fill, `--text`, font size `--input-font-size`. Placeholder `--text-3`. Help text below in caption style, `--text-3`. Error: 2px `--danger` border, `aria-invalid="true"`, and a message in `--danger`, weight 500, that starts with the word "Error". Select uses the same rule and the native menu. Textarea uses padding 8px 10px and body large for notes.

### Checkbox
Native checkbox, 16px box, `accent-color: var(--info)`. The whole label row is the target: `--row-h` tall, gap 10px between box and text. Completed to do items get `--text-3` and a line through.

### Badge and status label
Both: height 22px, padding 0 8px, `--radius-sm`, caption style.

* Badge (categories and counts): `--surface-2` fill, `--text-2`, no dot.
* Status label: a 6px round dot in the text colour plus a word, soft fill with its matching text token. Exactly five statuses:

| Status | Fill | Text | Example words |
|---|---|---|---|
| Running | `--info-soft` | `--info` | Working, Running, In progress, Corrected, Editing |
| Needs you | `--accent-soft` | `--accent-text` | Needs you, Needs review, overdue counts |
| Done | `--ok-soft` | `--ok` | Done, Confirmed |
| Failed | `--danger-soft` | `--danger` | Failed |
| Idle | `--surface-2` | `--text-2` | Idle |

* Tool chip (for example the agent's tool name): height 20px, padding 0 6px, `--radius-sm`, 1px `--border-strong`, no fill, caption style, `--text-2`.

### Tabs
A row with a 1px bottom border and 8px bottom padding, gap `--space-1`. Tab: height `--control-h`, padding 0 12px, no border, `--text-2`, weight 500. Selected: `--surface-2` fill, 1px `--accent-border` outline, `--radius-md`, `--text`, weight 600, `aria-selected="true"`. On phone the row scrolls sideways; it never wraps. An agent tab adds a 1x avatar before the name and a 6px status dot after it, and is 44px tall.

### Card
`--surface` fill, 1px `--border`, `--radius-lg`, padding `--space-4`, inner gap `--space-3`, no shadow. Header: an h2 (17/24, heading font, 600) on the left, a caption or status label on the right. Body: one key fact, then at most three rows. Footer: one action, pushed to the bottom (`margin-top: auto`) so cards in a row align. A card whose content needs the user gets a 2px `--accent-border` outline (1px border plus `box-shadow: 0 0 0 1px var(--accent-border)`).

### Table
Wrapper: 1px `--border`, `--radius-lg`, `overflow-x: auto`, `--surface` fill. Header cells: `--surface-2` fill, caption style, `--text-3`; the sorted column is `--text`, weight 600, with an arrow and `aria-sort`. Body cells: padding 6px 12px, height `--row-h`, 1px `--border` between rows. Numbers are right aligned in the mono font. Hover row: `--surface-2`. Selected or editing row: `--info-soft`.

* Inline edit replaces the cell text with a field in place, with Save (secondary) and Cancel (ghost) directly under it.
* Column priority: every column has priority 1, 2 or 3. Below 640px only priority 1 columns show, and priority 2 values fold into one mono meta line under the first cell (for example "Topic · 3 / 5 · 12 Nov"). Priority 3 columns are hidden on phone.
* Filters sit in a wrapping row above the table: a search field (`flex: 1 1 220px`), selects, removable filter pills (28px high, `--radius-full`, 1px `--border-strong`), and a mono result count on the right.

### Dialog
Max width 480px, `--surface` fill, 1px `--border`, `--radius-lg`, `--shadow-2`, padding 20px. Backdrop: `--bg` at 70% opacity. The title is an h2 that states the action as a question. Actions are right aligned, cancel first; the confirming verb repeats the title ("Delete entries"). On phone it becomes a full width sheet fixed to the bottom.

### Toast
`--surface` fill, 1px `--border-strong`, `--radius-md`, `--shadow-1`, padding 8px 12px, an 8px status dot, one line of text and one text action in `--info`, weight 600. Bottom right on desktop, above the tab bar on phone, max width 400px. Success leaves after 5 seconds. Errors stay until dismissed and start with the word "Error".

### Empty state
1px dashed `--border-strong`, `--radius-lg`, padding `--space-6` `--space-4`, centred. One weight 600 line that says what is empty, one small line (13/18, `--text-2`) that says what happens next, at most one secondary button. No illustration.

### Kanban column and card
Column: 280px wide, `--bg` fill, 1px `--border`, `--radius-lg`, padding `--space-2`, gap `--space-2`, a header with the column name (caption, weight 600, `--text-2`) and a mono count. Card: `--surface` fill, 1px `--border`, `--radius-md`, padding 10px 12px, title weight 500, one badge and one mono date; an overdue date is `--danger`, weight 500, and says "overdue". Dragging: `--shadow-1` plus a 2px `--focus` outline. On phone, columns scroll sideways one at a time.

### Calendar event block
`--radius-md`, padding 8px 10px, soft fill with its matching text token. Title 13/18, weight 600. Time and category on a second line in the mono font, and the category is always written out. Categories: study uses `--info-soft` and `--info`; work uses `--accent-soft` and `--accent-text`; own time uses `--ok-soft` and `--ok`; a placeholder block uses `--surface-2`, `--text-2` and a 1px dashed `--border-strong`. Events under 30 minutes show one line, title only.

### Timeline entry
A 10px wide rail with an 8px ring (2px `--border-strong`, `--surface` fill) and a 1px `--border` line down to the next entry; no line after the last entry. Content: a mono timestamp in `--text-3`, one body line, an optional small detail line in `--text-2`. Gap between rail and content `--space-3`, 14px between entries. An entry that needs the user has a filled `--accent` dot with an `--accent-border` ring.

### Progress bar and meter
Track: 6px high, `--radius-full`, `--surface-2` fill, 1px `--border`. Fill: `--accent` for progress and for context used; `--info` for budget used. A meter row has a mono label (28px wide) on the left and a mono value on the right. Give the bar `role="img"` and an `aria-label` with the value.

## 5. Agents

### Avatars
Agents are shown as pixel art characters. The six sprites are in `sprites/` as 34 by 44 pixel PNGs with transparent backgrounds: `builder`, `engineer`, `scribe`, `tester`, `reviewer`, `deployer`. One character per agent role.

* Render only at 1x (34 by 44) or 2x (68 by 88) with `image-rendering: pixelated`. Never smooth, crop, tint or stretch them.
* 1x in tabs and in list rows; 2x in agent cards and roster tiles.
* When the name is next to the sprite, use `alt=""`; when the sprite stands alone, use the agent name as `alt`.
* New characters must match the set: same canvas size, a 1px dark outline (`#1B1428`), three tones per material, a front facing pose with one prop.

### Agent card
The card rule with padding `--space-3` and a 12px gap between sprite and content. Content, top to bottom: name (weight 600) with a status label on the right; the task in the mono font, `--text-3`; a tool chip plus the model in the mono font; two meters, CTX (context used) and BUD (budget used). The selected card gets the 2px `--accent-border` outline.

### Agents page
Illustrative, apart from how each piece looks. It shows one way the avatars, the agent card, the status labels and the meters come together. What the page for running agents contains is decided when that feature is planned.

* A roster strip: one panel per status (Needs you, Working, Done, Idle) with a coloured caption, a mono count and one roster tile per agent. Roster tile: `--surface-2` fill, 1px `--border`, `--radius-md`, a 2x sprite. The Needs you panel has the 2px accent outline.
* Below it, the agent list (cards) on the left and the detail panel on the right.
* Detail panel: agent tabs on a `--surface-2` strip; a mono line with name, path and branch; a log on `--bg` in the mono font, 13/20, where the user's prompts start with ">" and use `--accent-text`; a "Needs you" decision box (`--accent-soft` fill, 1px `--accent-border`, `--radius-lg`) with the question, one line of consequence and the choices as buttons, the recommended one primary; a message field with a Send button; a mono status bar on `--surface-2`.

## 6. Reference screens

Illustrative, all of it. These are in `reference/`, each in light and dark, on desktop and on phone, with two boards that show the tokens and every component. They exist so an agent can see the style applied to something whole. The descriptions below say what was drawn, not what any feature has: the Memory screen, for one, shows tabs, columns, statuses and a way of writing levels that the brain does not have.

* `foundations-tokens-and-rules.png`, `components-light.png`, `components-dark.png`, `navigation-bar.png`: the tokens and the components. These match sections 1 to 4 and can be trusted for how a component looks.
* `home-*`, `memory-browser-*`, `workbench-session-*`, `agents-*`: whole screens, as examples of the style.

What was drawn:

* Home: date line in mono, a display title, a one line summary of what needs the user, then one card per feature (Workbench, Review, Agents, Memory, Learning, News).
* Memory browser: title, an "Add entry" primary button, view tabs (Profile, Rules, Topics, Timeline, Browse), the filter row, the table with inline editing, and pagination.
* Workbench session: breadcrumb, title with status label, project badge and mono time range, Pause and End session buttons, the notes article (reading text, code block on `--surface-2`, to do checklist, add note field), and a side column with session details, linked kanban cards and a "Sent to memory" timeline.
* Agents: described above.

Every page, drawn here or not, is built from the same tokens and components, with its content and layout taken from its slice. Do not invent new colours, radii or type sizes for any of them.

## 7. Fonts

Inter (400, 500, 600), Space Grotesk (500, 600) and JetBrains Mono (400, 500). They are hosted here, in `fonts/`, as variable woff2 in the latin and latin-ext subsets, and declared in `fonts.css`, which a page loads from `/design/fonts.css` beside `/design/tokens.css`. Never load a font or a stylesheet from an outside host, Google Fonts included. Fallbacks are in the `--font-*` tokens. Where the files came from, and how the proxy serves them, is in `fonts.css` and in "The design folder" in `infra/README.md`.

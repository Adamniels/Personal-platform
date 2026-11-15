# @platform/ui

The platform's React components, built from the values in `../tokens.css` and the rules in
`../DESIGN.md`, and the gallery where each one can be seen in every state, at
`https://platform.test/design/gallery/`. It is the second thing shared on purpose, after `libs/`;
why that is an exception to "no service imports from another service's directory" is in "One
repository" in `High-level/docs/platform-architecture.md`.

**Only a component more than one feature needs goes in.** A component one feature needs stays in
that feature's frontend until a second one needs it. This package holds the components of
section 4 of `DESIGN.md` and nothing feature specific, which is what keeps it from becoming the
shared place everything drifts into.

## How a frontend uses it

A frontend lives in a `web` folder one level under its service, and depends on the package by
path, adjusted to where it sits:

```json
"dependencies": { "@platform/ui": "file:../../design/ui" }
```

There is no build step here and no root level workspace. `exports` points at `src/index.ts`, and
the frontend's own Vite compiles the source, CSS Modules included. React is a peer dependency, so
the frontend brings its own; give its Vite config `resolve.dedupe: ["react", "react-dom"]`, since
the linked package also has React in its own `node_modules` for its tests, and two copies of React
on one page break hooks.

The page, not the component, loads the values and fonts, from the proxy:

```html
<link rel="stylesheet" href="/design/tokens.css">
<link rel="stylesheet" href="/design/fonts.css">
```

## The components

Each one is the native element, so everything that element takes passes through to it, the ref
included, and each follows its entry in section 4 of `DESIGN.md`.

- `Button`: `kind`, one of `primary`, `secondary` (the default), `ghost` and `destructive`.
  `type` defaults to `button`, so it never submits a form unasked.
- `TextField`, `TextArea` and `Select`: `label`, required and always shown above the field, as
  its accessible name; `help`, a line under it read as its description; and `error`, what is
  wrong, as text. With `error` set, the field shows it as "Error: <error>", without doubling a
  word the caller already wrote, sets `aria-invalid="true"` and ties the message to the control
  with `aria-describedby`, read before the help. An `id` or `aria-describedby` the caller passes
  is kept. `Select` is the native select and menu, its options passed as `option` children.
  There is no field without a visible label: an icon only control is a `Button` with an
  `aria-label`.
- `Checkbox`: `label`, the text of a row that is the box's label, so the whole row is one target;
  and `done`, the completed to do look. `type` is always `checkbox`.
- `Status`: `tone`, one of `running`, `needs-you`, `done`, `failed` and `idle`, and its word as
  children, a required string. The tone decides the colours and the caller decides the word, since
  a feature has its own words, so a status is always a dot plus a word and the dot is hidden from
  assistive technology. There are exactly five tones; another is a type error.
- `Badge`: its text as children, a category or a count. It has no tone and no dot: it is always
  the neutral fill, which is what tells it apart from a status.
- `Chip`: the outlined tool chip, its text as children.
- `Progress`: `value`, from 0 to 100, clamped, and 0 when it is not a number; `label`, what the
  bar measures; and `kind`, `progress` (the default, amber) or `budget` (info). It is
  `role="img"`, named "<label>: <value>%" with the value rounded, while the fill is drawn exact.
- `Meter`: a row of `label`, a short mono label such as "CTX", a `Progress` and the value in mono.
  `value` and `kind` are the bar's, and `description`, such as "Context used", is what assistive
  technology hears, the short label when it is left out. The two texts are hidden from assistive
  technology, so the meter is read once, as its bar.

- `Tabs`, with `Tab` and `TabPanel`: `Tabs` takes `label`, the tablist's accessible name;
  `value`, the selected tab's value; and `onChange`, called with a tab's value when the user picks
  it. The caller owns the selection, so a page can keep it in the address, and nothing changes
  until the caller passes the new value back. Each `Tab` takes a `value`, its label as children,
  and an optional `count`, shown after the label as a `Badge`; each `TabPanel` takes the `value`
  of its tab. Both are direct children of `Tabs`, which puts the tabs in the row in their order
  and the panels after it, ties each panel to its tab, and hides the panels of the others. It
  follows the keyboard pattern for tabs: the selected tab alone is in the tab order (the first,
  when the value names none), Left and Right move and wrap, Home and End go to the ends, and
  activation follows focus. The tabs are a segmented track: on desktop as wide as its tabs,
  wrapping onto further lines with the large corners when they do not fit, and never scrolling;
  on phone the full width, with equal tabs. With more than five tabs, `Tabs` also renders a native
  select named by `label`, one option per tab, its label with its count in brackets, as
  "Decisions (2)"; below 640px CSS shows the select and hides the track, and from 640px the other
  way round, so nothing reads the window's width. Choosing an option calls `onChange`, as a click
  does. An option holds text alone, so it shows the text of the tab's label and drops any markup
  in it. Give every `Tab` its `TabPanel`, since each tab names its panel.
- `Card`: `title`, the card's label, still the h2 that names the card, in the UI font and never in
  capitals; `aside`, an optional caption or status label on the right of it; `footer`, an optional
  action, pushed to the bottom so cards in a row of the card grid align; `attention`, the one
  accent border, with a visually hidden "Needs you" so the mark is not colour alone; and its body
  as children. It is an `article`. The design's one key fact and at most three rows are the page's
  to keep to; the card does not count them.
- `EmptyState`: `title`, the line that says what is empty; `detail`, the line that says what
  happens next; and an optional `action`, one element, normally a secondary `Button`. It is a
  `div`, with no illustration.
- `Timeline`, with `TimelineEntry`: `Timeline` is an `ol`, so give it an `aria-label` where no
  heading names it, and its entries are its direct children, so it knows the last one, which draws
  no line. Each `TimelineEntry` takes `time`, already formatted and shown in the mono font; its
  line as children; an optional `detail`; and `attention`, the filled accent dot, with a visually
  hidden "Needs you" as on the card. The rail is hidden from assistive technology.
- `Table`: `label`, the table's accessible name; `columns`; `rows`, shown in the order given; and
  `rowKey`, a stable key for a row. **The caller owns the data**: it sorts, filters and pages the
  rows, and holds what an edit changes, while the table renders what it is given and reports what
  was asked for. A column has a `key`, a `header`, a `cell` function from a row to what to show,
  `priority` (1, 2 or 3, default 1), `numeric` and `sortable`, and two optional functions, `fold`
  and `edit`, below. `numeric` sets the column right aligned in the mono font with tabular
  figures, so it is for every value hard rule 8 puts in mono: numbers, times, dates and ids.
  Sorting is `sort`, `{ key, direction }` with `direction` `ascending` or `descending`, shown on
  the header by its arrow and `aria-sort`, and `onSort`, called when a sortable header, a button,
  is pressed, with that column and the direction it asks for: the other way when it is already
  sorted, ascending otherwise. `rowState` gives a row's state, `selected` or `editing`; both are
  drawn in `--info-soft` and set `aria-selected`. `rowAction` is the one action at the end of a
  row, such as an Edit button.

  For an editing row the table calls each column's `edit` in place of its cell, the cell where a
  column has none, and puts Save (secondary) and Cancel (ghost) directly under the first cell,
  reporting `onSave` and `onCancel` with the row; Escape cancels. Focus goes to the first field
  when a row starts editing, and back to the row's action when it stops, unless the user had moved
  it out of the row. A row that is editing when the table first renders takes no focus. The row's
  action is not shown while it edits.

  **Column priority** is the design's rule for phone. Below 640px only priority 1 columns are
  columns. A priority 2 column gives a short text through `fold`, or its cell when that is text or
  a number, and those texts are joined with " · " in one mono line under the first priority 1
  cell, the line a column without text is left out of. Priority 3 columns are not shown on phone.
  Since a column that is not shown cannot be edited, a column that must be editable on phone is
  priority 1.

  The frame is a `section` named by `label`, with the surface, a 1px border and the large radius,
  and a header row with no fill. **A table is planned never to scroll**: a page gives its columns
  priorities so the table fits at 390px and at 1280px. The frame still scrolls sideways when the
  table is wider than it, as a safety net, so a table that is too wide scrolls inside its frame
  and the page never does; that is why the frame is in the tab order, so the keyboard can scroll
  it.
- `FilterRow`: the wrapping row above a table. It takes `search`, normally a `TextField`, which
  grows to fill the row; the other filters as children, selects and pills in their order; and
  `count`, the result count, in mono on the right and announced politely when it changes.
- `FilterPill`: `label`, the filter in words, and `onRemove`, called by its remove button, which
  is named "Remove filter <label>". It is a 32px pill filled with `--surface-3`, with no border,
  and its remove button is a 26px round ghost button inside it whose target reaches 44px.
- `Paging`: `from`, `to` and `total`, shown as "Rows 1 to 7 of 214"; `hasPrevious` and `hasNext`,
  without which Previous or Next is disabled; and `onPage`, called with `previous` or `next`. It
  is a `nav` named `label`, "Pages" unless a page has more than one.

- `Dialog`: the native `dialog` element, opened with `showModal`, which gives the top layer, the
  backdrop, Escape and an inert page behind it. It takes `open`, which **the caller owns**;
  `title`, the h2 that labels it and states the action as a question, which the caller writes;
  its body as children; `confirmLabel`, the confirming button, which repeats the title's verb;
  `kind`, that button's kind, `primary` (the default) or `destructive`; `onConfirm`; and
  `onCancel`. Cancel comes first and the confirming button last, right aligned. `onCancel` is
  called on Cancel, Escape, a click on the backdrop and a close the browser makes by itself, and
  the dialog stays open until the caller passes `open={false}`. `onConfirm` may return a promise,
  and while it is pending the confirming button is disabled, so a second press does nothing; a
  rejection is the caller's to handle. Tab wraps at the dialog's ends, since a browser would
  otherwise let it leave for its own interface, and focus returns to what had it when the dialog
  opened. Below 640px it is a sheet at the bottom, the full width of the screen.
- `ToastProvider` and `useToast`: **a page wraps itself in `ToastProvider` once**, and anything
  inside it calls `useToast()`, which throws without one. It gives `success(text, action?)` and
  `error(text, action?)`, where an action is `{ label, onAction }`, one text action such as Undo
  or Retry, which reports and dismisses the toast. A success is a `role="status"` and leaves after
  five seconds. An error is a `role="alert"`, is shown as "Error: <text>" the way a field's error
  is, and stays until its action is pressed, or Dismiss, which an error without one gets. At most
  three show, the newest lowest, and a fourth drops the oldest. They sit bottom right on desktop
  and above the bottom tab bar on phone. The provider's `clock`, read once, is where the five
  seconds come from: the window's timers unless a test passes its own.

The dialog and the toast are the floating layers, and the only components with a shadow, as hard
rule 3 of `DESIGN.md` says. `Dialog` takes everything a `dialog` takes apart from what it sets
itself: `open`, `aria-labelledby`, and the `onCancel`, `onClose`, `onClick`, `onMouseDown` and
`onKeyDown` it handles. `ToastProvider` renders no element of its own beside the stack.

`Status`, `Badge`, `Chip`, `Progress`, `Meter`, `Card`, `EmptyState`, `Timeline`,
`TimelineEntry`, `TabPanel`, `Table`, `FilterRow`, `FilterPill` and `Paging` render a `span`, a
`div`, an `article`, an `ol`, an `li`, a `section` or a `nav`, the table's being its wrapper, and
take everything that element takes, apart from the props they set themselves, such as a panel's `id`,
`role`, `hidden` and `aria-labelledby`. `Tabs` takes a `div`'s, and `Tab` a `button`'s, without
the ones the pattern decides: its `id`, `role`, `type`, `tabIndex`, `aria-selected` and
`aria-controls`.

A field fills the width of what holds it, so a form sizes its fields by its layout. Form state and
validation are the feature's own.

## Adding a component

A component is a folder under `src/`: the component, its `*.module.css`, and its test.

- It follows its entry in section 4 of `DESIGN.md` exactly, and is a real `button`, `a` or form
  control where it is interactive.
- Its styles use `var(--…)` for every colour and for every size that has a token. It never imports
  `tokens.css` and never holds a theme condition: dark mode is the values changing.
  `src/styles.test.ts` fails on a hex colour, `rgb(` or `hsl(` in any `*.module.css`.
- Text meant for assistive technology alone, such as the "Needs you" of a card, uses the one
  shared rule in `src/visually-hidden.module.css`.
- It is exported from `src/index.ts`.
- It gets a section in the gallery, `gallery/sections/<name>-section.tsx`, added to
  `gallery/gallery.tsx`, showing every kind and state. Its look is tested in
  `e2e/tests/test_gallery.py`, against the styles the browser computed, compared with the token
  as the same page resolves it.

`src/tokens.test.ts` checks `tokens.css` itself: the two dark blocks hold the same values, and every
text colour reaches 4.5 to 1 on every surface, in both themes.

## The gate

Node 26 (`.node-version`) and npm. From this folder, the same in CI (`.github/workflows/web.yml`)
and on the Mac mini:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

`npm run build` builds the gallery into `gallery/dist`, git ignored, which the proxy serves at
`/design/gallery/`; until it is built, that path answers 404. Every frontend has a `build` script,
so CI and the agents build any frontend the same way.

## Libraries

Pinned exactly, since `npm ci` installs from the lock: React 19.3.1 (peer and dev), `lucide-react`
1.72.0 for icons, TypeScript 7.0.2 in strict mode with `noUncheckedIndexedAccess`, Vite 8.3.8 with
`@vitejs/plugin-react` 6.3.0, Vitest 5.1.0 with `@testing-library/react` 16.3.3 and jsdom 31.0.0,
Biome 2.6.7 with its recommended rules, and `axe-core` 4.14.0, which the accessibility check in
`e2e/` injects into the gallery.

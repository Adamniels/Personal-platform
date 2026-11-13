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
  activation follows focus. The row scrolls sideways when the tabs are wider than it, and never
  wraps. Give every `Tab` its `TabPanel`, since each tab names its panel.
- `Card`: `title`, the h2 that names the card; `aside`, an optional caption or status label on the
  right of it; `footer`, an optional action, pushed to the bottom so cards in a row of the card
  grid align; `attention`, the needs you outline, with a visually hidden "Needs you" so the mark
  is not colour alone; and its body as children. It is an `article`. The design's one key fact
  and at most three rows are the page's to keep to; the card does not count them.
- `EmptyState`: `title`, the line that says what is empty; `detail`, the line that says what
  happens next; and an optional `action`, one element, normally a secondary `Button`. It is a
  `div`, with no illustration.
- `Timeline`, with `TimelineEntry`: `Timeline` is an `ol`, so give it an `aria-label` where no
  heading names it, and its entries are its direct children, so it knows the last one, which draws
  no line. Each `TimelineEntry` takes `time`, already formatted and shown in the mono font; its
  line as children; an optional `detail`; and `attention`, the filled accent dot, with a visually
  hidden "Needs you" as on the card. The rail is hidden from assistive technology.

`Status`, `Badge`, `Chip`, `Progress`, `Meter`, `Card`, `EmptyState`, `Timeline`,
`TimelineEntry` and `TabPanel` render a `span`, a `div`, an `article`, an `ol` or an `li`, and take
everything that element takes, apart from the props they set themselves, such as a panel's `id`,
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

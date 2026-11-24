// <platform-nav>, the platform's shared nav bar, served by the proxy at
// /design/nav.js. A page includes it with the design files and the element,
// a direct child of body, where page.css puts the page shell around it:
//
//   <link rel="stylesheet" href="/design/tokens.css">
//   <link rel="stylesheet" href="/design/fonts.css">
//   <link rel="stylesheet" href="/design/page.css">
//   <script src="/design/theme.js"></script>
//   <script src="/design/nav.js" defer></script>
//   ...
//   <platform-nav current="brain" crumb="Memory / Rules"></platform-nav>
//
// `current` is `home`, `brain` or a feature's prefix without the slash; the
// matching link gets aria-current="page". `crumb`, optional, names the page
// in the top bar; without it the crumb is the name of the link `current`
// names. Changing either draws the bar again. The bar fetches the core's
// /api/nav and renders, on desktop, a rail fixed to the window with the
// mark, Home, Memory (the brain's memory centre, which is not a feature and
// so is fixed here), one item per feature in the order the endpoint gives,
// and the theme toggle at its bottom; the element itself is the top bar,
// with the crumb and the account pill, whose menu holds "Change password"
// and "Log out". On phone the rail goes: the top bar gains the mark, the
// links become a tab bar fixed to the bottom, past five the last ones fold
// under More, and the theme toggle moves into the account menu. It renders
// and links and does nothing else: when /api/nav fails it shows the mark,
// Home and Memory alone, and it never redirects or refreshes the session.
//
// Once it has drawn, it asks each feature that names an attention address,
// `attention_url` in /api/nav, whether something there needs the person, and
// draws the dot on that feature's link when the answer is
// {"attention": true}. It never waits for these to draw, and any failure is
// no dot and nothing shown. An answer is remembered for a minute in the tab's
// sessionStorage, and a page that changed what needs the person dispatches
// `platform-nav-attention` on window to make the bar forget and ask again.
// See "The attention endpoint" in High-level/docs/feature-contract.md.
// See "Navigation" in High-level/docs/platform-architecture.md, and section 3
// of design/DESIGN.md for how it looks.
//
// Plain JavaScript with no build step and no dependency. Its styles live in
// its shadow root and use the tokens, which the page's root defines and the
// shadow root inherits. Every value from the endpoint is set with
// textContent, since names and the email are data.

(() => {
  // The same key theme.js reads before the page paints.
  const THEME_KEY = "platform-theme";
  // At most this many tabs on phone; past it, the first four and More.
  const MAX_TABS = 5;
  // The account pill shows the email cut at this many characters.
  const EMAIL_SHOWN = 24;
  // An attention answer is remembered under this prefix and the address, in
  // sessionStorage, so it ends with the tab and another account on the same
  // browser never sees it, and is used without asking for this long.
  const ATTENTION_KEY = "platform-nav-attention:";
  const ATTENTION_KEPT_MS = 60 * 1000;
  // An attention address that has not answered by then has no dot.
  const ATTENTION_TIMEOUT_MS = 5 * 1000;
  // The event a page dispatches on window to make the bar ask again.
  const ATTENTION_EVENT = "platform-nav-attention";

  // Icons from Lucide 1.72.0 (ISC licence), https://lucide.dev, the path
  // data copied from lucide-react's icons/<name>.mjs, since a plain script
  // cannot import the package. Drawn with Lucide's own defaults below. A
  // feature's icon is named in infra/features.toml and added here in the
  // same slice; the bar draws only the icons this object holds.
  const ICONS = {
    // house
    house: [
      ["path", { d: "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8" }],
      [
        "path",
        {
          d: "M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
        },
      ],
    ],
    // database
    database: [
      ["ellipse", { cx: "12", cy: "5", rx: "9", ry: "3" }],
      ["path", { d: "M3 5V19A9 3 0 0 0 21 19V5" }],
      ["path", { d: "M3 12A9 3 0 0 0 21 12" }],
    ],
    // box
    box: [
      [
        "path",
        {
          d: "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z",
        },
      ],
      ["path", { d: "m3.3 7 8.7 5 8.7-5" }],
      ["path", { d: "M12 22V12" }],
    ],
    // menu
    menu: [
      ["path", { d: "M4 5h16" }],
      ["path", { d: "M4 12h16" }],
      ["path", { d: "M4 19h16" }],
    ],
    // sun
    sun: [
      ["circle", { cx: "12", cy: "12", r: "4" }],
      ["path", { d: "M12 2v2" }],
      ["path", { d: "M12 20v2" }],
      ["path", { d: "m4.93 4.93 1.41 1.41" }],
      ["path", { d: "m17.66 17.66 1.41 1.41" }],
      ["path", { d: "M2 12h2" }],
      ["path", { d: "M20 12h2" }],
      ["path", { d: "m6.34 17.66-1.41 1.41" }],
      ["path", { d: "m19.07 4.93-1.41 1.41" }],
    ],
    // moon
    moon: [
      [
        "path",
        {
          d: "M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401",
        },
      ],
    ],
    // bird, Canary's
    bird: [
      ["path", { d: "M16 7h.01" }],
      ["path", { d: "M3.4 18H12a8 8 0 0 0 8-8V7a4 4 0 0 0-7.28-2.3L2 20" }],
      ["path", { d: "m20 7 2 .5-2 .5" }],
      ["path", { d: "M10 18v3" }],
      ["path", { d: "M14 17.75V21" }],
      ["path", { d: "M7 18a6 6 0 0 0 3.84-10.61" }],
    ],
    // layers, Review's
    layers: [
      [
        "path",
        {
          d: "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z",
        },
      ],
      [
        "path",
        {
          d: "M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12",
        },
      ],
      [
        "path",
        {
          d: "M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17",
        },
      ],
    ],
    // git-pull-request, Dev flow's
    "git-pull-request": [
      ["circle", { cx: "18", cy: "18", r: "3" }],
      ["circle", { cx: "6", cy: "6", r: "3" }],
      ["path", { d: "M13 6h3a2 2 0 0 1 2 2v7" }],
      ["line", { x1: "6", x2: "6", y1: "9", y2: "21" }],
    ],
  };

  const STYLE = `
    :host {
      display: block;
      min-height: var(--top-bar-h);
      font-family: var(--font-ui);
      font-size: var(--text-body-size);
      line-height: var(--text-body-line);
      color: var(--text);
    }
    [hidden] {
      display: none !important;
    }
    ul {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    a {
      text-decoration: none;
    }
    button {
      font: inherit;
      cursor: pointer;
    }
    :is(a, button):focus-visible {
      outline: var(--focus-ring);
      outline-offset: var(--focus-offset);
    }
    svg {
      flex: none;
      width: 20px;
      height: 20px;
    }

    /* The mark, linking to the front page. */
    .mark {
      box-sizing: border-box;
      flex: none;
      width: 44px;
      height: 44px;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: var(--radius-sm);
      background: var(--accent);
      color: var(--on-accent);
    }

    /* The rail, fixed to the window in the room page.css keeps for it:
       --page-pad from the window's top, bottom and left edges, at every
       desktop width. */
    .rail {
      box-sizing: border-box;
      position: fixed;
      z-index: 10;
      top: var(--page-pad);
      bottom: var(--page-pad);
      left: var(--page-pad);
      width: var(--rail-w);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-4) 0;
      overflow: hidden auto;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
    }
    .rule {
      flex: none;
      width: 32px;
      height: 1px;
      background: var(--border);
    }

    /* Home, Memory and the features: a column of round items in the rail. */
    .links {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-2);
    }
    .links > li {
      display: flex;
    }
    .links > li > a,
    .item {
      box-sizing: border-box;
      position: relative;
      flex: none;
      width: 44px;
      height: 44px;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border: 0;
      border-radius: var(--radius-full);
      background: var(--surface-2);
      color: var(--text-2);
    }
    .links > li > a:hover,
    .item:hover {
      color: var(--text);
    }
    /* Active means inverted, hard rule 11. */
    .links > li > a[aria-current="page"] {
      background: var(--text);
      color: var(--bg);
    }
    .links > li > a .label,
    .links > .more-item {
      display: none;
    }
    /* Something behind the item needs the person: on its top right
       corner, ringed in the rail's fill. */
    .dot {
      position: absolute;
      top: 2px;
      right: 2px;
      width: 8px;
      height: 8px;
      border-radius: var(--radius-full);
      background: var(--accent);
      box-shadow: 0 0 0 2px var(--surface);
    }
    /* The theme toggle, at the rail's bottom. */
    .rail > .theme {
      margin-top: auto;
    }

    /* The top bar, the element itself in the page's flow, so it runs from
       right of the rail to the window's right edge less the shell's pad:
       the crumb, and the account pill on the right. It sits on the ground. */
    .top {
      box-sizing: border-box;
      height: var(--top-bar-h);
      display: flex;
      align-items: center;
      gap: var(--space-3);
    }
    .top > .mark {
      display: none;
    }
    .crumb {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-family: var(--font-mono);
      font-size: var(--text-mono-size);
      line-height: var(--text-mono-line);
      text-transform: uppercase;
      color: var(--text-3);
    }
    .user {
      position: relative;
      flex: none;
      display: flex;
    }
    .pill {
      box-sizing: border-box;
      position: relative;
      height: 44px;
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      padding: 0 var(--space-4) 0 var(--space-1);
      border: 1px solid var(--border);
      border-radius: var(--radius-full);
      background: var(--surface);
      color: var(--text);
      font-weight: 500;
    }
    .pill:hover,
    .pill[aria-expanded="true"] {
      background: var(--surface-2);
    }
    .avatar {
      flex: none;
      width: 34px;
      height: 34px;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: var(--radius-full);
      background: var(--surface-3);
      font-weight: 600;
    }
    .email {
      white-space: nowrap;
    }
    /* Read out, never shown. */
    .unseen {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }

    /* A list that opens from a button: the account menu, and More on phone. */
    .popover {
      box-sizing: border-box;
      position: absolute;
      z-index: 10;
      min-width: 100%;
      padding: var(--space-1);
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      box-shadow: var(--shadow-1);
    }
    .popover :is(a, button) {
      box-sizing: border-box;
      width: 100%;
      height: var(--row-h);
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: 0 var(--space-3);
      border: 0;
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      text-align: left;
      white-space: nowrap;
    }
    .popover :is(a, button):hover {
      background: var(--surface-2);
    }
    .menu {
      top: calc(100% + var(--space-1));
      right: 0;
    }
    /* The theme toggle is in the account menu on phone only. */
    .menu > .theme-item {
      display: none;
    }

    /* Phone: no rail. The top bar gains the mark and the pill is the
       avatar alone; the links become a tab bar fixed to the bottom, and the
       theme toggle moves into the account menu. */
    @media (max-width: 639.98px) {
      .rail {
        display: contents;
      }
      .rail > .mark,
      .rule,
      .rail > .theme {
        display: none;
      }
      .top {
        padding: 0 var(--page-pad);
      }
      .top > .mark {
        display: flex;
        width: 40px;
        height: 40px;
      }
      .pill {
        padding: 0 var(--space-1);
      }
      .email {
        display: none;
      }
      .menu > .theme-item {
        display: block;
      }

      .links {
        box-sizing: border-box;
        position: fixed;
        z-index: 10;
        left: 0;
        right: 0;
        bottom: 0;
        height: calc(var(--tab-bar-h) + env(safe-area-inset-bottom, 0px));
        padding: 0 var(--space-2) env(safe-area-inset-bottom, 0px);
        display: grid;
        grid-auto-flow: column;
        grid-auto-columns: minmax(0, 1fr);
        align-items: center;
        gap: var(--space-1);
        background: var(--surface);
        border-top: 1px solid var(--border);
      }
      .links > li {
        min-width: 0;
        display: flex;
      }
      .links > li > a,
      .links > li > .more {
        box-sizing: border-box;
        position: relative;
        flex: 1;
        min-width: 0;
        width: auto;
        height: auto;
        min-height: 56px;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: var(--space-1);
        padding: 0 var(--space-1);
        border: 0;
        border-radius: var(--radius-md);
        background: transparent;
        color: var(--text-2);
      }
      .links > li > a[aria-current="page"] {
        background: var(--text);
        color: var(--bg);
      }
      .links > li > a .label,
      .more .label {
        display: block;
        max-width: 100%;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-size: var(--text-caption-size);
        line-height: var(--text-caption-line);
        font-weight: 500;
      }
      /* The dot sits on the icon's corner, since the label has no room. */
      .links > li > a .dot {
        top: var(--space-2);
        right: auto;
        left: calc(50% + var(--space-2));
      }
      .links.folded > .overflow {
        display: none;
      }
      .links.folded > .more-item {
        display: flex;
      }
      .sheet {
        position: fixed;
        right: var(--space-2);
        bottom: calc(
          var(--tab-bar-h) + env(safe-area-inset-bottom, 0px) + var(--space-2)
        );
        min-width: 50%;
      }
    }
  `;

  // A path the nav will link to: one on this origin, never another scheme
  // or a protocol relative URL, whatever /api/nav says.
  const isLocalPath = (path) =>
    typeof path === "string" && path.startsWith("/") && !path.startsWith("//");

  // An attention address the bar will ask: a path under the feature's own,
  // as the registry requires of it, so a feature can only speak for itself
  // and the request never leaves the origin.
  const isAttentionPath = (path, feature) =>
    isLocalPath(path) && path.startsWith(`${feature}/`);

  // The answer an attention address gave, when it is exactly
  // {"attention": true|false}; anything else is undefined, which is no dot.
  const attentionOf = (body) => {
    if (body === null || typeof body !== "object" || Array.isArray(body)) {
      return undefined;
    }
    const keys = Object.keys(body);
    if (keys.length !== 1 || keys[0] !== "attention") {
      return undefined;
    }
    return typeof body.attention === "boolean" ? body.attention : undefined;
  };

  // The answer remembered for the address, while it is under a minute old.
  // Storage that is blocked or holds something else remembers nothing.
  const remembered = (path) => {
    try {
      const stored = JSON.parse(sessionStorage.getItem(ATTENTION_KEY + path));
      const age = Date.now() - stored?.at;
      if (
        typeof stored?.attention === "boolean" &&
        age >= 0 &&
        age < ATTENTION_KEPT_MS
      ) {
        return stored.attention;
      }
    } catch {
      // Nothing usable is stored.
    }
    return undefined;
  };

  const remember = (path, attention) => {
    try {
      sessionStorage.setItem(
        ATTENTION_KEY + path,
        JSON.stringify({ attention, at: Date.now() }),
      );
    } catch {
      // Storage is blocked: the next page asks again.
    }
  };

  const forget = (path) => {
    try {
      sessionStorage.removeItem(ATTENTION_KEY + path);
    } catch {
      // Storage is blocked, so nothing was remembered.
    }
  };

  // Asks the address, as the person, and answers true, false, or undefined
  // for any failure: a status other than 200, a redirect, a body that is not
  // the shape, a network error or no answer within ATTENTION_TIMEOUT_MS.
  const askAttention = async (path) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), ATTENTION_TIMEOUT_MS);
    try {
      const response = await fetch(path, {
        credentials: "same-origin",
        headers: { Accept: "application/json" },
        redirect: "error",
        signal: controller.signal,
      });
      if (response.status !== 200) {
        return undefined;
      }
      return attentionOf(await response.json());
    } catch {
      return undefined;
    } finally {
      clearTimeout(timer);
    }
  };

  // Gives a feature's link the dot and its words, or takes them away. Its
  // name is the link's label, written when the link was drawn.
  const markAttention = (link, on) => {
    const name = link.querySelector(".label").textContent;
    link.querySelector(".dot")?.remove();
    // Colour is never the only signal: the dot has words for a reader.
    link.setAttribute("aria-label", on ? `${name}, needs you` : name);
    if (on) {
      link.append(element("span", { class: "dot", "aria-hidden": "true" }));
    }
  };

  const element = (tag, attributes = {}, ...children) => {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
      node.setAttribute(name, value);
    }
    node.append(...children);
    return node;
  };

  // Text from the endpoint goes in through here, so it is never markup.
  const text = (tag, className, value) => {
    const node = element(tag, { class: className });
    node.textContent = value;
    return node;
  };

  // The icon a feature from /api/nav gets: the one it names when ICONS holds
  // it, else box. The name is only ever a key into ICONS, looked up as the
  // object's own, so one such as "constructor" is not found on its prototype.
  const iconOf = (feature) =>
    typeof feature.icon === "string" && Object.hasOwn(ICONS, feature.icon)
      ? feature.icon
      : "box";

  const SVG = "http://www.w3.org/2000/svg";
  const icon = (name) => {
    const svg = document.createElementNS(SVG, "svg");
    const defaults = {
      viewBox: "0 0 24 24",
      fill: "none",
      stroke: "currentColor",
      "stroke-width": "2",
      "stroke-linecap": "round",
      "stroke-linejoin": "round",
      "aria-hidden": "true",
      focusable: "false",
    };
    for (const [attribute, value] of Object.entries(defaults)) {
      svg.setAttribute(attribute, value);
    }
    for (const [tag, attributes] of ICONS[name]) {
      const shape = document.createElementNS(SVG, tag);
      for (const [attribute, value] of Object.entries(attributes)) {
        shape.setAttribute(attribute, value);
      }
      svg.append(shape);
    }
    return svg;
  };

  const systemDark = window.matchMedia("(prefers-color-scheme: dark)");

  // The theme the page shows: the one set on the root, or else the system's.
  const shownTheme = () => {
    const set = document.documentElement.getAttribute("data-theme");
    if (set === "light" || set === "dark") {
      return set;
    }
    return systemDark.matches ? "dark" : "light";
  };

  class PlatformNav extends HTMLElement {
    static observedAttributes = ["current", "crumb"];

    #contents = null;
    #loading = false;
    // Ends the listeners a render added outside the shadow root.
    #listeners = null;
    // Ends the listener for ATTENTION_EVENT, while the element is connected.
    #connected = null;
    // What each attention address last answered, true or false, by path;
    // an address with no answer, or a failed one, is not in it.
    #attention = new Map();
    // How many times each address has been asked, so only the latest
    // question's answer is drawn.
    #asked = new Map();

    connectedCallback() {
      if (!this.shadowRoot) {
        this.attachShadow({ mode: "open" });
      }
      this.#connected?.abort();
      this.#connected = new AbortController();
      window.addEventListener(
        ATTENTION_EVENT,
        () => this.#updateAttention(true),
        { signal: this.#connected.signal },
      );
      this.#render();
      if (!this.#contents && !this.#loading) {
        this.#load();
      }
    }

    disconnectedCallback() {
      this.#listeners?.abort();
      this.#listeners = null;
      this.#connected?.abort();
      this.#connected = null;
    }

    attributeChangedCallback() {
      if (this.shadowRoot) {
        this.#render();
      }
    }

    async #load() {
      this.#loading = true;
      try {
        // A redirect is a failure: /api/nav answers 401, never a redirect.
        const response = await fetch("/api/nav", {
          credentials: "same-origin",
          headers: { Accept: "application/json" },
          redirect: "error",
        });
        if (response.ok) {
          this.#contents = await response.json();
        }
      } catch {
        // A network error or a body that is not JSON: Home alone.
      } finally {
        this.#loading = false;
      }
      this.#render();
      // Only now, with the links drawn, so no answer can hold them up.
      this.#updateAttention(false);
    }

    // The attention addresses of the features the bar lists.
    #attentionPaths() {
      return this.#entries()
        .map((entry) => entry.attentionUrl)
        .filter((path) => path !== undefined);
    }

    // Draws what each attention address answers: the remembered answer while
    // it is under a minute old, else a new one, asked without waiting for
    // the others. `again` forgets what was remembered first, for a page that
    // dispatched ATTENTION_EVENT.
    #updateAttention(again) {
      for (const path of this.#attentionPaths()) {
        if (again) {
          forget(path);
        }
        const known = remembered(path);
        if (known !== undefined) {
          this.#setAttention(path, known);
          continue;
        }
        const question = (this.#asked.get(path) ?? 0) + 1;
        this.#asked.set(path, question);
        askAttention(path).then((answer) => {
          // A later question was asked meanwhile: its answer is the one.
          if (this.#asked.get(path) !== question) {
            return;
          }
          if (answer !== undefined) {
            remember(path, answer);
          }
          this.#setAttention(path, answer);
        });
      }
    }

    #setAttention(path, answer) {
      if (this.#attention.get(path) === answer) {
        return;
      }
      if (answer === undefined) {
        this.#attention.delete(path);
      } else {
        this.#attention.set(path, answer);
      }
      // Only the links it is about change, in place: drawing the bar again
      // would close an open menu and drop the focus under the person.
      for (const link of this.shadowRoot.querySelectorAll("a[data-attention]")) {
        if (link.dataset.attention === path) {
          markAttention(link, answer === true);
        }
      }
    }

    // Home and Memory, which are fixed, then each feature with a name and a
    // local path, in order.
    #entries() {
      const current = this.getAttribute("current");
      const entries = [
        { name: "Home", path: "/", icon: "house", here: current === "home" },
        { name: "Memory", path: "/brain", icon: "database", here: current === "brain" },
      ];
      const features = Array.isArray(this.#contents?.features) ? this.#contents.features : [];
      for (const feature of features) {
        if (typeof feature?.name !== "string" || !isLocalPath(feature.path)) {
          continue;
        }
        const attentionUrl = isAttentionPath(feature.attention_url, feature.path)
          ? feature.attention_url
          : undefined;
        entries.push({
          name: feature.name,
          path: feature.path,
          icon: iconOf(feature),
          here: current === feature.path.slice(1),
          attentionUrl,
          attention:
            attentionUrl !== undefined &&
            this.#attention.get(attentionUrl) === true,
        });
      }
      return entries;
    }

    #link(entry) {
      const a = element(
        "a",
        { href: entry.path },
        icon(entry.icon),
        text("span", "label", entry.name),
      );
      // A rail item shows only its icon, so its name is its label.
      a.setAttribute("aria-label", entry.name);
      if (entry.here) {
        a.setAttribute("aria-current", "page");
      }
      if (entry.attentionUrl !== undefined) {
        // So an answer that arrives later finds the links it is about.
        a.dataset.attention = entry.attentionUrl;
        markAttention(a, entry.attention);
      }
      return a;
    }

    // A button that shows and hides a list: it closes on Escape, giving
    // focus back to the button, on a click outside, and when focus leaves.
    #disclosure(wrapper, button, list, signal) {
      const setOpen = (open) => {
        button.setAttribute("aria-expanded", String(open));
        list.hidden = !open;
      };
      setOpen(false);
      button.addEventListener("click", () => setOpen(list.hidden));
      wrapper.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && !list.hidden) {
          event.preventDefault();
          setOpen(false);
          button.focus();
        }
      });
      wrapper.addEventListener("focusout", (event) => {
        // No new target means a click on something that takes no focus,
        // which the click handler below decides.
        if (event.relatedTarget && !wrapper.contains(event.relatedTarget)) {
          setOpen(false);
        }
      });
      document.addEventListener(
        "click",
        (event) => {
          if (!event.composedPath().includes(wrapper)) {
            setOpen(false);
          }
        },
        { signal },
      );
    }

    // A button that switches between light and dark: `draw` fills it for
    // the theme a press would switch to. The rail's shows an icon, the
    // phone's account menu an icon and words.
    #themeToggle(className, draw, signal) {
      const button = element("button", { type: "button", class: className });
      const show = () => draw(button, shownTheme() === "dark" ? "light" : "dark");
      button.addEventListener("click", () => {
        const next = shownTheme() === "dark" ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", next);
        try {
          localStorage.setItem(THEME_KEY, next);
        } catch {
          // Storage is blocked: the choice holds until the page is left.
        }
        show();
      });
      // With no choice made, the system setting may change underneath.
      systemDark.addEventListener("change", show, { signal });
      show();
      return button;
    }

    // The rail's toggle: the sun in dark, the moon in light.
    #railTheme(signal) {
      return this.#themeToggle(
        "item theme",
        (button, next) => {
          button.setAttribute("aria-label", `Switch to ${next} mode`);
          button.replaceChildren(icon(next === "dark" ? "moon" : "sun"));
        },
        signal,
      );
    }

    // The account menu's toggle on phone: an icon and the mode it sets.
    #menuTheme(signal) {
      const name = { light: "Light mode", dark: "Dark mode" };
      return this.#themeToggle(
        "",
        (button, next) => {
          button.replaceChildren(
            icon(next === "dark" ? "moon" : "sun"),
            text("span", "", name[next]),
          );
        },
        signal,
      );
    }

    // The account pill: the email's first letter in the avatar and, on
    // desktop, the email cut at EMAIL_SHOWN characters. Its name is the
    // whole email.
    #userMenu(email, signal) {
      const characters = Array.from(email);
      const shown =
        characters.length > EMAIL_SHOWN
          ? `${characters.slice(0, EMAIL_SHOWN).join("")}…`
          : email;
      // What shows, the letter and the cut email, is hidden from a reader,
      // who hears the whole email instead. A name from the content rather
      // than an aria-label, which a cut word would not match.
      const hidden = (node) => {
        node.setAttribute("aria-hidden", "true");
        return node;
      };
      const button = element(
        "button",
        { type: "button", class: "pill", "aria-controls": "user-menu" },
        hidden(text("span", "avatar", (characters[0] ?? "").toUpperCase())),
        hidden(text("span", "email", shown)),
        text("span", "unseen", email),
      );
      button.title = email;
      const link = (name, href) => element("li", {}, element("a", { href }, name));
      const list = element(
        "ul",
        { id: "user-menu", class: "popover menu" },
        link("Change password", "/settings/password"),
        link("Log out", "/auth/logout"),
        element("li", { class: "theme-item" }, this.#menuTheme(signal)),
      );
      const wrapper = element("div", { class: "user" }, button, list);
      this.#disclosure(wrapper, button, list, signal);
      return wrapper;
    }

    // On phone, past MAX_TABS links the ones after the fourth are hidden from
    // the tab bar and listed under More instead. On desktop every link shows
    // and More does not, which the styles decide.
    #links(entries, signal) {
      const links = element("ul", { class: "links" });
      const folded = entries.length > MAX_TABS;
      const shown = folded ? MAX_TABS - 1 : entries.length;
      entries.forEach((entry, index) => {
        const item = element("li", {}, this.#link(entry));
        if (index >= shown) {
          item.className = "overflow";
        }
        links.append(item);
      });
      if (folded) {
        links.classList.add("folded");
        const more = element(
          "button",
          { type: "button", class: "more", "aria-controls": "more-sheet" },
          icon("menu"),
          text("span", "label", "More"),
        );
        const sheet = element("ul", { id: "more-sheet", class: "popover sheet" });
        for (const entry of entries.slice(shown)) {
          sheet.append(element("li", {}, this.#link(entry)));
        }
        const item = element("li", { class: "more-item" }, more, sheet);
        this.#disclosure(item, more, sheet, signal);
        links.append(item);
      }
      return links;
    }

    // The crumb: the page's own, or else the name of the link `current`
    // names, or else nothing.
    #crumb(entries) {
      if (this.hasAttribute("crumb")) {
        return this.getAttribute("crumb");
      }
      return entries.find((entry) => entry.here)?.name ?? "";
    }

    #render() {
      this.#listeners?.abort();
      this.#listeners = new AbortController();
      const { signal } = this.#listeners;
      const entries = this.#entries();

      // The mark, in the rail on desktop and in the top bar on phone.
      const mark = () =>
        element(
          "a",
          { class: "mark", href: "/", "aria-label": "Platform" },
          icon("box"),
        );

      const rail = element(
        "div",
        { class: "rail" },
        mark(),
        element("span", { class: "rule", "aria-hidden": "true" }),
        this.#links(entries, signal),
        this.#railTheme(signal),
      );

      const top = element(
        "div",
        { class: "top" },
        mark(),
        text("span", "crumb", this.#crumb(entries)),
      );
      const email = this.#contents?.user?.email;
      if (typeof email === "string") {
        top.append(this.#userMenu(email, signal));
      }

      const nav = element("nav", { "aria-label": "Platform" }, rail, top);
      const style = element("style");
      style.textContent = STYLE;
      this.shadowRoot.replaceChildren(style, nav);
    }
  }

  if (!customElements.get("platform-nav")) {
    customElements.define("platform-nav", PlatformNav);
  }
})();

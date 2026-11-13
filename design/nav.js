// <platform-nav>, the platform's shared nav bar, served by the proxy at
// /design/nav.js. A page includes it with the design files and the element:
//
//   <link rel="stylesheet" href="/design/tokens.css">
//   <link rel="stylesheet" href="/design/fonts.css">
//   <link rel="stylesheet" href="/design/page.css">
//   <script src="/design/theme.js"></script>
//   <script src="/design/nav.js" defer></script>
//   ...
//   <platform-nav current="canary"></platform-nav>
//
// `current` is `home`, `brain` or a feature's prefix without the slash; the
// matching link gets aria-current="page". The bar fetches the core's /api/nav
// and renders the mark and the name, Home, Memory (the brain's memory centre,
// which is not a feature and so is fixed here), one link per feature in the
// order the endpoint gives, a theme toggle, and who is logged in as a menu
// holding "Change password" and "Log out". On phone the links move to a tab
// bar fixed to the bottom, and past five the last ones fold under More. It
// renders and links and does nothing else: when /api/nav fails it shows the
// mark, Home and Memory alone, and it never redirects or refreshes the
// session. See "Navigation" in
// High-level/docs/platform-architecture.md, and section 3 of design/DESIGN.md
// for how it looks.
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

  // Icons from Lucide 1.72.0 (ISC licence), https://lucide.dev, the path
  // data copied from lucide-react's icons/<name>.mjs, since a plain script
  // cannot import the package. Drawn with Lucide's own defaults below.
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
  };

  const STYLE = `
    :host {
      display: block;
      min-height: var(--nav-h);
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

    /* The top bar. */
    .top {
      box-sizing: border-box;
      height: var(--nav-h);
      display: flex;
      align-items: center;
      gap: var(--space-5);
      padding: 0 var(--page-pad);
      background: var(--surface);
      border-bottom: 1px solid var(--border);
    }
    .brand {
      flex: none;
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      border-radius: var(--radius-sm);
      color: var(--text);
      font-family: var(--font-heading);
      font-size: var(--text-body-lg-size);
      line-height: var(--text-body-lg-line);
      font-weight: 600;
    }
    .mark {
      box-sizing: border-box;
      width: var(--space-4);
      height: var(--space-4);
      border-radius: var(--radius-sm);
      background: var(--accent);
      border: 1px solid var(--accent-border);
    }

    /* Home and the features, in the top bar on desktop. */
    .links {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-width: 0;
    }
    .links > li > a {
      box-sizing: border-box;
      height: var(--control-h);
      display: inline-flex;
      align-items: center;
      padding: 0 var(--space-3);
      border: 1px solid transparent;
      border-radius: var(--radius-md);
      color: var(--text-2);
      font-weight: 500;
      white-space: nowrap;
    }
    .links > li > a:hover {
      color: var(--text);
    }
    .links > li > a[aria-current="page"] {
      color: var(--text);
      background: var(--surface-2);
      border-color: var(--accent-border);
    }
    .links > li > a svg,
    .links > .more-item {
      display: none;
    }
    /* Something behind the link needs the person. */
    .dot {
      flex: none;
      width: 6px;
      height: 6px;
      margin-left: var(--space-2);
      border-radius: var(--radius-full);
      background: var(--accent-text);
    }

    /* The theme toggle and the user menu, on the right. */
    .end {
      margin-left: auto;
      min-width: 0;
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }
    .ghost {
      box-sizing: border-box;
      height: var(--control-h);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0 10px;
      border: 1px solid transparent;
      border-radius: var(--radius-md);
      background: transparent;
      color: var(--text-2);
      font-weight: 500;
    }
    .ghost:hover,
    .ghost[aria-expanded="true"] {
      background: var(--surface-2);
      color: var(--text);
    }
    .theme {
      flex: none;
      width: var(--control-h);
      padding: 0;
    }
    .user {
      position: relative;
      min-width: 0;
      display: flex;
    }
    .email {
      min-width: 0;
      max-width: 32ch;
    }
    .email > span {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    /* A list that opens from a button: the user menu, and More on phone. */
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
    .popover a {
      box-sizing: border-box;
      height: var(--row-h);
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: 0 var(--space-3);
      border-radius: var(--radius-sm);
      color: var(--text);
      white-space: nowrap;
    }
    .popover a:hover {
      background: var(--surface-2);
    }
    .menu {
      top: calc(100% + var(--space-1));
      right: 0;
    }

    /* Phone: the links become a tab bar fixed to the bottom. */
    @media (max-width: 639.98px) {
      .top {
        gap: var(--space-3);
      }
      .links {
        box-sizing: border-box;
        position: fixed;
        z-index: 10;
        left: 0;
        right: 0;
        bottom: 0;
        height: calc(var(--nav-h) + env(safe-area-inset-bottom, 0px));
        padding-bottom: env(safe-area-inset-bottom, 0px);
        display: grid;
        grid-auto-flow: column;
        grid-auto-columns: minmax(0, 1fr);
        gap: 0;
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
        flex: 1;
        min-width: 0;
        height: 100%;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: var(--space-1);
        padding: 0 var(--space-1);
        border: 0;
        border-top: 2px solid transparent;
        border-radius: 0;
        background: transparent;
        color: var(--text-2);
        font-weight: 500;
      }
      .links > li > a[aria-current="page"] {
        color: var(--text);
        background: transparent;
        border-top-color: var(--accent);
      }
      .links > li > a svg {
        display: block;
      }
      .label {
        max-width: 100%;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-size: var(--text-caption-size);
        line-height: var(--text-caption-line);
      }
      /* The dot sits on the icon's corner, since the label has no room. */
      .links > li > a {
        position: relative;
      }
      .links > li > a .dot {
        position: absolute;
        top: var(--space-2);
        left: calc(50% + var(--space-2));
        margin: 0;
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
        bottom: calc(var(--nav-h) + env(safe-area-inset-bottom, 0px) + var(--space-2));
        min-width: 50%;
      }
      .sheet .label {
        font-size: inherit;
        line-height: inherit;
      }
    }
  `;

  // A path the nav will link to: one on this origin, never another scheme
  // or a protocol relative URL, whatever /api/nav says.
  const isLocalPath = (path) =>
    typeof path === "string" && path.startsWith("/") && !path.startsWith("//");

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
    static observedAttributes = ["current"];

    #contents = null;
    #loading = false;
    // Ends the listeners a render added outside the shadow root.
    #listeners = null;

    connectedCallback() {
      if (!this.shadowRoot) {
        this.attachShadow({ mode: "open" });
      }
      this.#render();
      if (!this.#contents && !this.#loading) {
        this.#load();
      }
    }

    disconnectedCallback() {
      this.#listeners?.abort();
      this.#listeners = null;
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
        entries.push({
          name: feature.name,
          path: feature.path,
          icon: "box",
          here: current === feature.path.slice(1),
          attention: feature.attention === true,
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
      if (entry.here) {
        a.setAttribute("aria-current", "page");
      }
      if (entry.attention) {
        // Colour is never the only signal: the dot has words for a reader.
        a.setAttribute("aria-label", `${entry.name}, needs you`);
        a.append(element("span", { class: "dot", "aria-hidden": "true" }));
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

    #themeToggle(signal) {
      const button = element("button", { type: "button", class: "ghost theme" });
      const show = () => {
        const next = shownTheme() === "dark" ? "light" : "dark";
        button.setAttribute("aria-label", `Switch to ${next} theme`);
        button.replaceChildren(icon(next === "dark" ? "moon" : "sun"));
      };
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

    #userMenu(email, signal) {
      const button = element(
        "button",
        { type: "button", class: "ghost email", "aria-controls": "user-menu" },
        text("span", "", email),
      );
      button.title = email;
      const link = (name, href) => element("li", {}, element("a", { href }, name));
      const list = element(
        "ul",
        { id: "user-menu", class: "popover menu" },
        link("Change password", "/settings/password"),
        link("Log out", "/auth/logout"),
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

    #render() {
      this.#listeners?.abort();
      this.#listeners = new AbortController();
      const { signal } = this.#listeners;

      const brand = element(
        "a",
        { class: "brand", href: "/" },
        element("span", { class: "mark", "aria-hidden": "true" }),
        text("span", "name", "Platform"),
      );

      const end = element("div", { class: "end" }, this.#themeToggle(signal));
      const email = this.#contents?.user?.email;
      if (typeof email === "string") {
        end.append(this.#userMenu(email, signal));
      }

      const nav = element(
        "nav",
        { "aria-label": "Platform" },
        element("div", { class: "top" }, brand, this.#links(this.#entries(), signal), end),
      );
      const style = element("style");
      style.textContent = STYLE;
      this.shadowRoot.replaceChildren(style, nav);
    }
  }

  if (!customElements.get("platform-nav")) {
    customElements.define("platform-nav", PlatformNav);
  }
})();

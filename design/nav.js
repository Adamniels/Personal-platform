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
// `current` is `home` or a feature's prefix without the slash; the matching
// link gets aria-current="page". The bar fetches the core's /api/nav and
// renders the mark and the name, Home, one link per feature in the order the
// endpoint gives, and who is logged in. It renders and links and does nothing
// else: when /api/nav fails it shows the mark and Home alone, and it never
// redirects or refreshes the session. See "Navigation" in
// High-level/docs/platform-architecture.md, and section 3 of design/DESIGN.md
// for how it looks.
//
// Plain JavaScript with no build step and no dependency. Its styles live in
// its shadow root and use the tokens, which the page's root defines and the
// shadow root inherits. Every value from the endpoint is set with
// textContent, since names and the email are data.

(() => {
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
    :is(a, button):focus-visible {
      outline: var(--focus-ring);
      outline-offset: var(--focus-offset);
    }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      margin: -1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
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

    /* Home and the features. */
    .links {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      min-width: 0;
    }
    .links a {
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
    .links a:hover {
      color: var(--text);
    }
    .links a[aria-current="page"] {
      color: var(--text);
      background: var(--surface-2);
      border-color: var(--accent-border);
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

    /* Who is logged in, on the right. */
    .end {
      margin-left: auto;
      min-width: 0;
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }
    .email {
      min-width: 0;
      max-width: 24ch;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: var(--text-2);
    }
    .account {
      color: var(--text-2);
      font-weight: 500;
      white-space: nowrap;
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

  class PlatformNav extends HTMLElement {
    static observedAttributes = ["current"];

    #contents = null;
    #loading = false;

    connectedCallback() {
      if (!this.shadowRoot) {
        this.attachShadow({ mode: "open" });
      }
      this.#render();
      if (!this.#contents && !this.#loading) {
        this.#load();
      }
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

    // Home, then each feature with a name and a local path, in order.
    #entries() {
      const current = this.getAttribute("current");
      const entries = [{ name: "Home", path: "/", here: current === "home" }];
      const features = Array.isArray(this.#contents?.features) ? this.#contents.features : [];
      for (const feature of features) {
        if (typeof feature?.name !== "string" || !isLocalPath(feature.path)) {
          continue;
        }
        entries.push({
          name: feature.name,
          path: feature.path,
          here: current === feature.path.slice(1),
          attention: feature.attention === true,
        });
      }
      return entries;
    }

    #link(entry) {
      const a = element("a", { href: entry.path }, text("span", "label", entry.name));
      if (entry.here) {
        a.setAttribute("aria-current", "page");
      }
      if (entry.attention) {
        a.append(element("span", { class: "dot" }), text("span", "sr-only", ", needs you"));
      }
      return a;
    }

    #render() {
      const brand = element(
        "a",
        { class: "brand", href: "/" },
        element("span", { class: "mark", "aria-hidden": "true" }),
        text("span", "name", "Platform"),
      );

      const links = element("ul", { class: "links" });
      for (const entry of this.#entries()) {
        links.append(element("li", {}, this.#link(entry)));
      }

      const end = element("div", { class: "end" });
      const email = this.#contents?.user?.email;
      if (typeof email === "string") {
        const user = text("span", "email", email);
        user.title = email;
        const account = (name, href) => element("a", { class: "account", href }, name);
        end.append(
          user,
          account("Change password", "/settings/password"),
          account("Log out", "/auth/logout"),
        );
      }

      const nav = element(
        "nav",
        { "aria-label": "Platform" },
        element("div", { class: "top" }, brand, links, end),
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

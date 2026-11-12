// <platform-nav>, the platform's shared nav bar, served by the core at
// /nav.js. A page includes it with two lines:
//
//   <script src="/nav.js" defer></script>
//   <platform-nav current="canary"></platform-nav>
//
// `current` is `home` or a feature's prefix without the slash; the matching
// link gets aria-current="page". The bar fetches /api/nav and renders a home
// link, one link per feature, who is logged in and a link to log out. It
// renders and links and does nothing else: when /api/nav fails it shows the
// home link alone, and it never redirects or refreshes the session. See
// "Navigation" in High-level/docs/platform-architecture.md.
//
// Plain JavaScript with no build step and no dependency. Its styles live in
// its shadow root, so it looks the same on a page that shares no stylesheet
// with the core. Every value is set with textContent, since names and the
// email are data.

(() => {
  const STYLE = `
    :host {
      display: block;
      min-height: 3rem;
      color-scheme: light dark;
      font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
      font-size: 1rem;
      line-height: 1.5;
    }
    nav {
      box-sizing: border-box;
      min-height: 3rem;
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.25rem 1.25rem;
      padding: 0.5rem 1rem;
      border-bottom: 1px solid color-mix(in srgb, currentColor 20%, transparent);
    }
    a {
      color: inherit;
      text-decoration: none;
      padding: 0.125rem 0;
      border-bottom: 2px solid transparent;
    }
    a:hover {
      border-bottom-color: color-mix(in srgb, currentColor 40%, transparent);
    }
    a:focus-visible {
      outline: 2px solid #2f6fdb;
      outline-offset: 2px;
    }
    a[aria-current="page"] {
      border-bottom-color: #2f6fdb;
    }
    .home {
      font-weight: 700;
    }
    /* One line, cut short with an ellipsis rather than wrapped, so a long
       email does not grow the bar past the height the page reserves. */
    .user {
      flex: 1 1 0;
      min-width: 6rem;
      text-align: right;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      font-size: 0.875rem;
      color: color-mix(in srgb, currentColor 60%, transparent);
    }
  `;

  // A path the nav will link to: one on this origin, never another scheme
  // or a protocol relative URL, whatever /api/nav says.
  const isLocalPath = (path) =>
    typeof path === "string" && path.startsWith("/") && !path.startsWith("//");

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
        // A network error or a body that is not JSON: the home link alone.
      } finally {
        this.#loading = false;
      }
      this.#render();
    }

    #render() {
      const current = this.getAttribute("current");
      const link = (text, href, here, className) => {
        const a = document.createElement("a");
        a.href = href;
        a.textContent = text;
        if (className) {
          a.className = className;
        }
        if (here) {
          a.setAttribute("aria-current", "page");
        }
        return a;
      };

      const nav = document.createElement("nav");
      nav.setAttribute("aria-label", "Platform");
      nav.append(link("Platform", "/", current === "home", "home"));

      const features = Array.isArray(this.#contents?.features) ? this.#contents.features : [];
      for (const feature of features) {
        if (typeof feature?.name !== "string" || !isLocalPath(feature.path)) {
          continue;
        }
        nav.append(link(feature.name, feature.path, current === feature.path.slice(1)));
      }

      const email = this.#contents?.user?.email;
      if (typeof email === "string") {
        const user = document.createElement("span");
        user.className = "user";
        user.textContent = email;
        user.title = email;
        // A link to the core's logout page rather than a form, so the bar
        // still only renders and links, and logging out works from every
        // feature's page without any of them changing.
        nav.append(user, link("Log out", "/auth/logout", false));
      }

      const style = document.createElement("style");
      style.textContent = STYLE;
      this.shadowRoot.replaceChildren(style, nav);
    }
  }

  if (!customElements.get("platform-nav")) {
    customElements.define("platform-nav", PlatformNav);
  }
})();

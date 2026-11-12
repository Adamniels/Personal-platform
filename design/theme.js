// The theme a person chose, applied before the page paints. Every page with
// the nav bar loads this in its head, without defer, beside the stylesheets:
//
//   <script src="/design/theme.js"></script>
//
// It reads localStorage["platform-theme"], which the bar's toggle in nav.js
// writes, and sets data-theme on the root, which tokens.css reads. With
// nothing stored it sets nothing, and the system setting applies. A few lines
// and nothing else, since it blocks the page while it runs.

(() => {
  try {
    const theme = localStorage.getItem("platform-theme");
    if (theme === "light" || theme === "dark") {
      document.documentElement.setAttribute("data-theme", theme);
    }
  } catch {
    // Storage is blocked: the system setting applies.
  }
})();

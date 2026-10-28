// The public status page refreshes itself: every data-status-refresh seconds (while the tab is visible) it fetches
// itself again and swaps the page body in place. A failed fetch keeps what is shown - an unreachable server must not
// blank a page people open precisely because something is down.
function initializeStatusRefresh() {
  const host = document.querySelector("[data-status-refresh]");
  const body = document.querySelector(".mm-status");
  if (!host || !body) {
    return;
  }
  const seconds = Math.max(30, Number(host.getAttribute("data-status-refresh")) || 60);
  const refresh = async () => {
    if (document.visibilityState !== "visible") {
      return;
    }
    try {
      const response = await fetch(window.location.href, { cache: "no-store", headers: { Accept: "text/html" } });
      if (!response.ok) {
        return;
      }
      const html = await response.text();
      const parsed = new DOMParser().parseFromString(html, "text/html");
      const next = parsed.querySelector(".mm-status");
      const current = document.querySelector(".mm-status");
      if (next && current) {
        current.replaceWith(document.importNode(next, true));
        document.title = parsed.title || document.title;
      }
    } catch {
      // keep the last good page
    }
  };
  window.setInterval(refresh, seconds * 1000);
}

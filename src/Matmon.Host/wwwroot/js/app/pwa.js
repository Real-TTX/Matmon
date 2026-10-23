// Matmon - the instance as an installed app (PWA), and what a page does when the connection to the server drops.
//   - The service worker (sw.js): registered here, told which files a page is made of (so the next start works offline)
//     and told to forget the kept pages when someone signs out.
//   - The status bar: the colour of the top bar, in step with the theme (light, dark, a scheme) - set on theme-color.
//   - The connection: every request this page makes to its own server goes through fetch, so a failed one (or the
//     browser saying it is offline) shows a bar at the bottom - "No connection", with Try again - and the server is asked
//     again by itself, at growing intervals. While it is away, a form is not sent (it would end in an error page), and
//     when it is back the live values refresh at once. A page that was only a kept copy (the service worker served it
//     offline) is loaded again; a page that was offline for a minute or more too - unless someone has typed into it, then
//     the bar offers Reload instead of throwing their input away.
// Not inside the Matmon.Cloud console (that page belongs to the cloud) and not on the public wallboard, which keeps its
// last values on a TV by design.

function initializePwa() {
  const root = document.documentElement;
  const wallboard = document.querySelector(".public-map-shell") !== null;
  const embedded = root.dataset.embedded === "1" || window.top !== window || location.pathname.toLowerCase().startsWith("/instances/");

  if (!embedded && !wallboard) {
    registerMatmonServiceWorker();
    keepStatusBarInStep();
  }

  if (!wallboard) {
    watchConnection();
  }
}

function registerMatmonServiceWorker() {
  const address = document.querySelector('meta[name="matmon-sw"]')?.getAttribute("content");
  if (!address || !("serviceWorker" in navigator) || !window.isSecureContext) {
    return; // a service worker needs https (or localhost)
  }

  const path = location.pathname.toLowerCase();
  const signedOut = path === "/login" || path.startsWith("/login/") || path === "/logintwofactor" || path === "/setup" || path === "/logout";

  navigator.serviceWorker.register(address, { scope: "/" }).then(() => navigator.serviceWorker.ready).then((registration) => {
    const worker = registration.active;
    if (!worker) {
      return;
    }

    if (signedOut) {
      worker.postMessage({ type: "forget" });
    }

    const urls = Array.from(document.querySelectorAll("link[rel='stylesheet'][href], link[rel='icon'][href], script[src], img[src]"))
      .map((element) => element.href || element.src)
      .filter((url) => url && url.startsWith(location.origin));
    worker.postMessage({ type: "cache-assets", urls });
  }).catch(() => {
    // No service worker (private mode, a policy): the app works as a plain web page.
  });

  // Signing out: the kept pages belong to whoever was signed in.
  document.addEventListener("submit", (event) => {
    const form = event.target;
    if (form instanceof HTMLFormElement && /\/logout$/i.test(new URL(form.action, location.href).pathname)) {
      navigator.serviceWorker.controller?.postMessage({ type: "forget" });
    }
  }, true);
}

// The status bar of an installed app (and the browser's bar on a phone) in the colour of the top bar, so the two read as
// one. The server writes one per system scheme; this follows the theme the person chose (and the colour schemes).
function keepStatusBarInStep() {
  const root = document.documentElement;
  const apply = () => {
    const colour = getComputedStyle(root).getPropertyValue("--mat-glass").trim() || getComputedStyle(document.body).backgroundColor;
    if (!colour) {
      return;
    }

    document.querySelectorAll('meta[name="theme-color"]').forEach((meta) => meta.setAttribute("content", colour));
  };

  apply();
  new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ["data-theme", "data-scheme"] });
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", apply);
}

function watchConnection() {
  const state = {
    offline: false,
    since: 0,
    delay: 3,
    timer: 0,
    countdown: 0,
    dirty: false,
    copy: document.querySelector('meta[name="matmon-offline-copy"]') !== null,
    bar: null
  };
  const unreachable = new Set([502, 503, 504]);
  const ownFetch = window.fetch.bind(window);

  // Every request of this page to its own server says whether the server is there.
  window.fetch = async (input, init) => {
    let sameOrigin = false;
    try {
      const target = new URL(input instanceof Request ? input.url : String(input), location.href);
      sameOrigin = target.origin === location.origin;
    } catch {
      // not a URL we can read - not ours to judge
    }

    try {
      const response = await ownFetch(input, init);
      if (sameOrigin) {
        if (unreachable.has(response.status)) {
          goneOffline();
        } else {
          backOnline();
        }
      }

      return response;
    } catch (error) {
      if (sameOrigin && error?.name !== "AbortError") {
        goneOffline();
      }

      throw error;
    }
  };

  // Someone typing into a form: a reload after the connection is back would throw their input away.
  const markDirty = (event) => {
    if (event.target instanceof Element && event.target.closest("form") && !event.target.matches("[type='search']")) {
      state.dirty = true;
    }
  };
  document.addEventListener("input", markDirty, true);
  document.addEventListener("change", markDirty, true);

  // A form is not sent while the server is away - it would end on an error page (or, installed on iOS, nowhere).
  window.addEventListener("submit", (event) => {
    if (!state.offline && navigator.onLine !== false) {
      state.dirty = false;
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    goneOffline();
    window.MatUi?.toast("No connection - nothing was sent. Send it again once the connection is back.", { tone: "warning" });
  }, true);

  window.addEventListener("offline", goneOffline);
  window.addEventListener("online", () => check(true));
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && state.offline) {
      check(true);
    }
  });

  if (state.copy || navigator.onLine === false) {
    goneOffline();
  }

  function goneOffline() {
    if (!state.offline) {
      state.offline = true;
      state.since = Date.now();
      state.delay = 3;
      render("offline");
    }

    if (!state.timer) {
      schedule();
    }
  }

  function backOnline() {
    if (!state.offline) {
      return;
    }

    state.offline = false;
    clearTimeout(state.timer);
    clearInterval(state.countdown);
    state.timer = 0;
    const away = Date.now() - state.since;

    if ((state.copy || away >= 60_000) && !state.dirty && !document.querySelector("dialog[open]")) {
      location.reload(); // what the page shows was built before the outage - get the current state
      return;
    }

    if (state.copy || away >= 60_000) {
      render("stale");
    } else {
      render(null);
      window.MatUi?.toast("The connection is back.", { tone: "success" });
    }

    window.dispatchEvent(new CustomEvent("matmon:online"));
  }

  function schedule() {
    clearTimeout(state.timer);
    const wait = state.delay;
    state.delay = Math.min(state.delay * 2, 30);
    let left = wait;
    setStatus(`Trying again in ${left} s.`);
    clearInterval(state.countdown);
    state.countdown = setInterval(() => {
      left -= 1;
      if (left > 0) {
        setStatus(`Trying again in ${left} s.`);
      }
    }, 1000);
    state.timer = setTimeout(() => check(false), wait * 1000);
  }

  async function check(byHand) {
    clearTimeout(state.timer);
    clearInterval(state.countdown);
    state.timer = 0;
    if (byHand) {
      state.delay = 3;
    }

    setStatus("Checking…");
    try {
      const response = await ownFetch("/healthz", { cache: "no-store" });
      if (response.ok) {
        backOnline();
        return;
      }
    } catch {
      // still away
    }

    if (state.offline) {
      schedule();
    } else {
      goneOffline();
    }
  }

  function setStatus(text) {
    const status = state.bar?.querySelector("[data-connection-status]");
    if (status) {
      status.textContent = text;
    }
  }

  // The bar at the bottom (above a phone's home indicator): offline, "stale" (back, but the page is out of date and
  // someone typed into it), or gone (null).
  function render(mode) {
    if (!mode) {
      state.bar?.remove();
      state.bar = null;
      return;
    }

    if (!state.bar) {
      state.bar = document.createElement("div");
      state.bar.className = "mm-connection";
      state.bar.setAttribute("role", "status");
      state.bar.setAttribute("aria-live", "polite");
      document.body.appendChild(state.bar);
    }

    const offline = mode === "offline";
    const rendered = Number(document.documentElement.dataset.rendered || 0);
    const asOf = rendered ? new Date(rendered).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : null;
    const title = offline ? "No connection to the server." : "The connection is back.";
    const detail = offline
      ? (state.copy && asOf ? `This page is as it was at ${asOf}.` : "Live values are paused.")
      : "This page may be out of date.";

    const alert = document.createElement("div");
    alert.className = offline ? "mat-alert mat-alert--warning" : "mat-alert mat-alert--info";
    alert.innerHTML = `<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-${offline ? "alert" : "refresh"}"></use></svg>`;

    const body = document.createElement("div");
    body.className = "mat-alert__body";
    const strong = document.createElement("strong");
    strong.textContent = title;
    const text = document.createElement("span");
    text.textContent = " " + detail + " ";
    const status = document.createElement("span");
    status.className = "mat-text-muted";
    status.dataset.connectionStatus = "";
    body.append(strong, text, status);

    const actions = document.createElement("div");
    actions.className = "mat-alert__actions";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "mat-btn mat-btn--secondary mat-btn--sm";
    button.textContent = offline ? "Try again" : "Reload";
    button.addEventListener("click", () => (offline ? check(true) : location.reload()));
    actions.append(button);
    body.append(actions);

    alert.append(body);
    state.bar.replaceChildren(alert);
  }
}

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
  // The public wallboard and the public status page are for people who are not users of the instance.
  const wallboard = document.querySelector(".public-map-shell, .mm-status-public") !== null;
  const embedded = root.dataset.embedded === "1" || window.top !== window || location.pathname.toLowerCase().startsWith("/instances/");

  if (!embedded && !wallboard) {
    registerMatmonServiceWorker();
    keepStatusBarInStep();
  }

  // The update screen (System > Updates) watches the restart itself - the bar, and its reload once the server is
  // back, would only get in its way.
  if (!wallboard && !document.querySelector("[data-update-screen]")) {
    watchConnection();
  }

  initializeDevicePush();
}

// The Account page's "This device": browser notifications on or off for THIS browser. On = permission, a push subscription
// with the instance's key, and the subscription registered for the signed-in user. The page lists the endpoints the server
// knows for this user, so a subscription the server forgot (removed in the list, gone) shows as off and can be turned on
// again - re-registering it by itself would undo a Remove.
function initializeDevicePush() {
  const card = document.querySelector("[data-push-device]");
  if (!card) {
    return;
  }

  const stateText = card.querySelector("[data-push-state]");
  const enable = card.querySelector("[data-push-enable]");
  const disable = card.querySelector("[data-push-disable]");
  const say = (text) => { stateText.textContent = text; };
  const known = new Set(JSON.parse(card.dataset.pushEndpoints || "[]"));
  const standalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  const ios = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

  if (!window.isSecureContext) {
    say("Notifications need Matmon on https (or localhost) - this page is not.");
    return;
  }

  if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
    say(ios && !standalone
      ? "On iPhone and iPad, notifications work in the installed app: Share → Add to Home Screen, then open Matmon from the Home Screen and turn them on here."
      : "This browser cannot show notifications from a web app.");
    return;
  }

  const post = (url, body) => fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Matmon-Request": "1" },
    body: JSON.stringify(body)
  });
  const ready = () => Promise.race([
    navigator.serviceWorker.ready,
    new Promise((_, reject) => setTimeout(() => reject(new Error("the app's service worker is not running - reload the page")), 8000))
  ]);
  const keyBytes = (base64Url) => {
    const base64 = (base64Url + "=".repeat((4 - base64Url.length % 4) % 4)).replace(/-/g, "+").replace(/_/g, "/");
    return Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
  };

  const show = async () => {
    const subscription = await (await ready()).pushManager.getSubscription();
    const on = subscription !== null && known.has(subscription.endpoint);
    enable.hidden = on || Notification.permission === "denied";
    disable.hidden = !subscription;
    say(Notification.permission === "denied"
      ? "Notifications are blocked for this site in the browser's settings - allow them there, then come back."
      : on ? "Notifications are on for this device." : "Notifications are off for this device.");
  };

  enable.addEventListener("click", async () => {
    enable.disabled = true;
    try {
      if (await Notification.requestPermission() !== "granted") {
        say("Notifications were not allowed.");
        return;
      }

      const { publicKey } = await (await fetch("/api/browser-push/key", { headers: { Accept: "application/json" } })).json();
      const registration = await ready();
      const subscription = await registration.pushManager.getSubscription()
        || await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
      const response = await post("/api/browser-push/devices", subscription.toJSON());
      if (!response.ok) {
        say("Matmon did not take this device: " + ((await response.json().catch(() => ({}))).error || response.status));
        return;
      }

      location.reload();
    } catch (error) {
      say("Notifications could not be turned on: " + error.message);
    } finally {
      enable.disabled = false;
    }
  });

  disable.addEventListener("click", async () => {
    disable.disabled = true;
    try {
      const subscription = await (await ready()).pushManager.getSubscription();
      if (subscription) {
        await post("/api/browser-push/devices/remove", { endpoint: subscription.endpoint });
        await subscription.unsubscribe();
      }

      location.reload();
    } catch (error) {
      say("Notifications could not be turned off: " + error.message);
      disable.disabled = false;
    }
  });

  show().catch((error) => say("Notifications are not available: " + error.message));
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

// Matmon service worker - the instance as an installed app, and what it does without a connection.
//   - Styles, scripts, icons: from the cache first. Their addresses carry a version (?v=...), so a new build brings new
//     ones, and the caches of an older build are dropped when this one takes over.
//   - Pages come from the network. A few overview pages (dashboard, sensors, alerts, events, maps, probes, maintenance)
//     are kept as they were last loaded: without a connection that copy is shown, marked, and pwa.js says how old it is
//     and loads the page again the moment the connection is back. Nothing else is ever kept - not the account page, not
//     System, not an editor, not a sensor's page (it can show a push address, which is a credential).
//   - Without a connection any other page is the offline page; a form that could not be sent says so and offers Back,
//     instead of a dead end (an installed app on iOS has no back button).
//   - The APIs (live values, the alert counts) are never cached: a monitor must not show yesterday's numbers as today's.
//   - Signing out (or reaching the sign-in page) forgets every kept page: they belong to whoever was signed in.
// Registered by pwa.js as /sw.js?v=<build>: a new build is a new address, so the browser installs it at once.
"use strict";

const VERSION = new URL(self.location.href).searchParams.get("v") || "dev";
const ASSETS = "matmon-assets-" + VERSION;
const PAGES = "matmon-pages-" + VERSION;
const SHELL = ["/offline.html", "/js/offline.js", "/matui/matui.css", "/matui/matui.boot.js", "/favicon.svg", "/brand/app-icon-192.png", "/brand/matmon-logo-horizontal.svg", "/brand/matmon-logo-horizontal-dark.svg"];
const MAX_PAGES = 30;

// The overview pages an offline copy is kept of (path, case-insensitive; the query is part of the copy's key).
const OFFLINE_PAGES = [/^\/$/, /^\/index$/, /^\/alerts$/, /^\/monitoring$/, /^\/events$/, /^\/maps$/, /^\/probes$/, /^\/maintenance$/];

self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(ASSETS);
    for (const address of SHELL) {
      try {
        const response = await fetch(address, { cache: "no-cache" });
        if (response.ok) { await cache.put(address, response); }
      } catch (error) { /* the next visit fills it */ }
    }
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith("matmon-") && name !== ASSETS && name !== PAGES) { await caches.delete(name); }
    }
    await self.clients.claim();
  })());
});

self.addEventListener("message", (event) => {
  const data = event.data || {};
  if (data.type === "cache-assets" && Array.isArray(data.urls)) {
    // The page names the files it is made of, so the first visit is enough to open the app offline next time.
    event.waitUntil((async () => {
      const cache = await caches.open(ASSETS);
      for (const address of data.urls.slice(0, 200)) {
        const url = new URL(address, self.location.origin);
        if (url.origin !== self.location.origin || !isAsset(url) || await cache.match(url.href)) { continue; }
        try {
          const response = await fetch(url.href);
          if (response.ok) { await cache.put(url.href, response); }
        } catch (error) { /* the next visit */ }
      }
    })());
  } else if (data.type === "forget") {
    event.waitUntil(caches.delete(PAGES));
  }
});

function isAsset(url) {
  return /^\/(matui|css|js|brand)\//.test(url.pathname) || url.pathname === "/favicon.svg" || url.pathname === "/offline.html";
}

function keepsOfflineCopy(url) {
  const path = url.pathname.toLowerCase().replace(/\/+$/, "") || "/";
  return OFFLINE_PAGES.some((pattern) => pattern.test(path));
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === "/sw.js") { return; }

  if (request.mode === "navigate") {
    event.respondWith(request.method === "GET" ? page(request, url) : sent(request));
    return;
  }

  if (request.method !== "GET") { return; }
  if (isAsset(url)) {
    event.respondWith(fromCacheFirst(request));
  } else if (url.pathname === "/api/branding/logo" || url.pathname === "/api/branding/favicon") {
    event.respondWith(fromCacheThenRefresh(request));
  }
});

async function fromCacheFirst(request) {
  const cache = await caches.open(ASSETS);
  const hit = await cache.match(request);
  if (hit) { return hit; }
  const response = await fetch(request);
  if (response.ok && response.type === "basic") { await cache.put(request, response.clone()); }
  return response;
}

/** A partner logo: what is kept, refreshed in the background (it can change, the page must not wait for it). */
async function fromCacheThenRefresh(request) {
  const cache = await caches.open(ASSETS);
  const hit = await cache.match(request);
  const fresh = fetch(request).then(async (response) => {
    if (response.ok) { await cache.put(request, response.clone()); }
    else if (response.status === 404) { await cache.delete(request); }
    return response;
  }).catch(() => hit || Response.error());
  return hit || fresh;
}

/** A page: from the network; an overview page also from its last copy when there is none. */
async function page(request, url) {
  const keep = keepsOfflineCopy(url);
  try {
    const response = await fetch(request);
    // Kept: the page itself - not a redirect to the sign-in page, not an error.
    if (keep && response.ok && response.type === "basic" && !response.redirected
        && (response.headers.get("content-type") || "").includes("text/html")) {
      const cache = await caches.open(PAGES);
      await cache.put(stripHash(url), response.clone());
      await trim(cache);
    }
    return response;
  } catch (error) {
    if (keep) {
      const cache = await caches.open(PAGES);
      const hit = await cache.match(stripHash(url)) || await cache.match(stripHash(url), { ignoreSearch: true });
      if (hit) { return markAsCopy(hit); }
    }
    return offlinePage("");
  }
}

/** A form sent as a page (POST): passed on; when the connection is gone, a page that says so - with Back. */
async function sent(request) {
  try {
    return await fetch(request);
  } catch (error) {
    return offlinePage("post");
  }
}

function stripHash(url) {
  const copy = new URL(url.href);
  copy.hash = "";
  return copy.href;
}

async function trim(cache) {
  const keys = await cache.keys();
  for (let index = 0; index < keys.length - MAX_PAGES; index++) { await cache.delete(keys[index]); }
}

/** The kept copy, marked so the page knows it is one (pwa.js shows how old it is and reloads once the connection is back). */
async function markAsCopy(hit) {
  const html = (await hit.text()).replace("<head>", "<head><meta name=\"matmon-offline-copy\" content=\"1\">");
  const headers = new Headers(hit.headers);
  headers.delete("content-length");
  return new Response(html, { status: 200, headers });
}

async function offlinePage(reason) {
  const hit = await (await caches.open(ASSETS)).match("/offline.html");
  const html = hit
    ? (await hit.text()).replace("data-offline-reason=\"\"", "data-offline-reason=\"" + reason + "\"")
    : "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>No connection</title></head>"
      + "<body><h1>No connection</h1><p>The server cannot be reached right now.</p><p><a href=\"\">Try again</a></p></body></html>";
  return new Response(html, { status: 503, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
}

// ---- browser notifications (Web Push; NotificationTransport.Push sends, the Account page subscribes) -------------------
// A message is JSON: title, body, url (opened by a tap), tag (one notification per alert - a newer one replaces it),
// badge (open alerts: the number on the app icon, where the system shows one).
self.addEventListener("push", (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch (error) { data = { body: event.data ? event.data.text() : "" }; }
  const badge = typeof data.badge === "number" && "setAppBadge" in self.navigator
    ? (data.badge > 0 ? self.navigator.setAppBadge(data.badge) : self.navigator.clearAppBadge()).catch(() => {})
    : Promise.resolve();
  event.waitUntil(Promise.all([
    self.registration.showNotification(data.title || "Matmon", {
      body: data.body || "",
      icon: "/brand/app-icon-192.png",
      badge: "/brand/badge-96.png",
      tag: data.tag || undefined,
      renotify: !!data.tag,
      timestamp: Date.now(),
      data: { url: data.url || "/" }
    }),
    badge
  ]));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const target = new URL((event.notification.data && event.notification.data.url) || "/", self.location.origin);
  if (target.origin !== self.location.origin) { return; }
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    const open = windows.find((client) => new URL(client.url).origin === self.location.origin);
    if (open) {
      await open.focus();
      if ("navigate" in open) { try { await open.navigate(target.href); return; } catch (error) { /* not controlled - open one */ } }
    }
    await self.clients.openWindow(target.href);
  })());
});

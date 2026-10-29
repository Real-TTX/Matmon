// Matmon - maps and wallboards: clocks, live data, the carousel, the stage fit and the map designer.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Ticks every [data-map-clock] in the MAP's timezone (not the browser's): the public wallboard has nobody
// signed in, and a browser-local clock would contradict every server-rendered timestamp beside it on the
// same board. The first paint is server-rendered, so this only keeps it current.
function initializeMapClocks() {
  const clocks = Array.from(document.querySelectorAll("[data-map-clock]"));
  if (clocks.length === 0) {
    return;
  }

  const formatters = new Map();
  const formatterFor = (zone, options) => {
    const key = (zone || "") + "|" + JSON.stringify(options);
    if (!formatters.has(key)) {
      // An invalid/unknown zone id must not blank the board - fall back to the browser's own zone.
      try {
        formatters.set(key, new Intl.DateTimeFormat(undefined, zone ? { ...options, timeZone: zone } : options));
      } catch {
        formatters.set(key, new Intl.DateTimeFormat(undefined, options));
      }
    }
    return formatters.get(key);
  };

  const tick = () => {
    const now = new Date();
    clocks.forEach((clock) => {
      const zone = clock.dataset.timeZone || "";
      const time = clock.querySelector("[data-clock-time]");
      const date = clock.querySelector("[data-clock-date]");
      if (time) {
        time.textContent = formatterFor(zone, { hour: "2-digit", minute: "2-digit", hour12: false }).format(now);
      }
      if (date) {
        date.textContent = formatterFor(zone, { weekday: "short", day: "2-digit", month: "short" }).format(now);
      }
    });
  };

  tick();
  // Align to the next minute, then tick once a minute - the display has no seconds, so a per-second timer
  // would just wake the wallboard 60x more often for nothing.
  window.setTimeout(() => {
    tick();
    window.setInterval(tick, 60000);
  }, (60 - new Date().getSeconds()) * 1000);
}

// Refreshes a wallboard IN PLACE from [data-map-live]'s JSON endpoint, replacing the 30-second meta-refresh
// the public board used to carry. That refresh had a failure mode worth naming: a full reload resets the
// slide carousel to slide 1, so on a board with more than one slide every later slide was effectively
// unreachable on a TV - it would appear for a few seconds and then be yanked back.
//
// Only VALUES are patched. Anything structural (a tile added, removed, moved, resized) is baked into the
// server-rendered markup, so the payload carries a revision over exactly that shape and a change there does
// one honest full reload.
/**
 * The multi-graph's hover read-out: a hairline at the pointer and a card with the time there and every line's
 * value at that moment. It reads the SAME points the lines were drawn from (data-series-points, "x,value;..."
 * with x = 0..1 across the window) - not a value guessed back out of the SVG path - and the window from the
 * chart, which the live patch keeps current. Delegated on the document, so charts re-rendered by the designer
 * or a revision reload need no re-wiring. Never in the designer, where the pointer is busy dragging tiles.
 */
/**
 * A tile panel carries some properties TWICE, one field per widget kind that uses it - "Rank by channel" and
 * "Compare" are both ListChannelKey, "Rows" and "Lines" both ListLimit. Hiding the unused one is not enough:
 * a hidden input still posts, the model binder takes the FIRST value, and the first in the DOM was the hidden
 * one - so an edit to a multi-graph's channel or line count was silently thrown away on save (and the live
 * preview read the stale one too). Of each same-named group only the visible field stays enabled; typing into
 * it mirrors the value into its twins, so switching the kind keeps what was entered.
 */
function syncDuplicateFields(panel) {
  const groups = new Map();
  panel.querySelectorAll("input[name], select[name], textarea[name]").forEach((field) => {
    if (field.type === "hidden" || field.type === "checkbox" || field.type === "radio" || field.type === "file") {
      return;
    }
    const list = groups.get(field.name) || [];
    list.push(field);
    groups.set(field.name, list);
  });

  // "Unused" means hidden by a KIND group (data-map-property-*-only) - not merely inside a closed tab: the
  // Data tab being shut while the kind changes must not disable the very field that tab is about to show.
  const unusedForKind = (field) => {
    for (let element = field.parentElement; element && element !== panel; element = element.parentElement) {
      if (element.hidden && [...element.attributes].some((attribute) => /^data-map-property-.+-only$/.test(attribute.name))) {
        return true;
      }
    }
    return false;
  };

  groups.forEach((fields) => {
    if (fields.length < 2) {
      return;
    }
    const used = fields.filter((field) => !unusedForKind(field));
    const keep = used[0] || fields[0];
    fields.forEach((field) => {
      field.disabled = field !== keep;
    });
  });

  if (panel.dataset.duplicateSync !== "1") {
    panel.dataset.duplicateSync = "1";
    const mirror = (event) => {
      const source = event.target;
      if (!(source instanceof HTMLInputElement || source instanceof HTMLSelectElement || source instanceof HTMLTextAreaElement) || !source.name) {
        return;
      }
      panel.querySelectorAll(`[name="${CSS.escape(source.name)}"]`).forEach((twin) => {
        if (twin === source || twin.type === "hidden" || twin.type === "checkbox") {
          return;
        }
        if (twin instanceof HTMLSelectElement && ![...twin.options].some((option) => option.value === source.value)) {
          // A select can only hold one of its options; offer the typed value rather than lose it.
          twin.add(new Option(source.value, source.value));
        }
        twin.value = source.value;
      });
    };
    panel.addEventListener("input", mirror);
    panel.addEventListener("change", mirror);
  }
}

// A tile's state word is MatUi's pill, whose colour is a tone CLASS (mat-pill--ok / --warn / --error ...), not
// an attribute - so a live state change has to swap the class, the way MatUi.PillClass picked it on the server.
// data-state stays on the pill as well, for anything that still reads it.
const MapPillTones = { ok: "mat-pill--ok", warning: "mat-pill--warn", error: "mat-pill--error", ack: "mat-pill--info", paused: "mat-pill--plain", disabled: "mat-pill--plain" };
function setMapTilePillTone(pill, stateKey) {
  if (!pill) {
    return;
  }
  const key = stateKey || "unknown";
  pill.classList.remove("mat-pill--ok", "mat-pill--warn", "mat-pill--error", "mat-pill--info", "mat-pill--plain", "mat-pill--busy");
  if (MapPillTones[key]) {
    pill.classList.add(MapPillTones[key]);
  }
  pill.dataset.state = key;
}

function initializeMultiGraphTooltips() {
  const parsed = new WeakMap();
  const pointsOf = (path) => {
    const raw = path.dataset.seriesPoints || "";
    const cached = parsed.get(path);
    if (cached && cached.raw === raw) {
      return cached.points;
    }
    const points = raw.split(";").map((pair) => pair.split(",").map(Number)).filter((pair) => pair.length === 2 && pair.every(Number.isFinite));
    parsed.set(path, { raw, points });
    return points;
  };

  const nearest = (points, x) => {
    let best = null;
    for (const point of points) {
      if (!best || Math.abs(point[0] - x) < Math.abs(best[0] - x)) {
        best = point;
      }
    }
    return best;
  };

  const formatValue = (value, unit) => {
    const digits = Math.abs(value) >= 100 ? 0 : Math.abs(value) >= 10 ? 1 : 2;
    const number = value.toLocaleString(undefined, { maximumFractionDigits: digits });
    return !unit ? number : unit === "%" ? `${number}%` : `${number} ${unit}`;
  };

  const hide = (plot) => {
    const tooltip = plot.querySelector(".map-multigraph-tooltip");
    const cursor = plot.querySelector(".map-multigraph-cursor");
    if (tooltip) {
      tooltip.hidden = true;
    }
    if (cursor) {
      cursor.hidden = true;
    }
  };

  const show = (plot, event) => {
    const chart = plot.closest("[data-map-multigraph]");
    const svg = plot.querySelector("svg");
    const tooltip = plot.querySelector(".map-multigraph-tooltip");
    const cursor = plot.querySelector(".map-multigraph-cursor");
    if (!chart || !svg || !tooltip || !cursor) {
      return;
    }

    const rect = svg.getBoundingClientRect();
    if (rect.width <= 0) {
      return;
    }
    const x = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
    const start = Number(chart.dataset.windowStart);
    const end = Number(chart.dataset.windowEnd);
    const unit = chart.dataset.unit || "";

    // A point more than ~4 % of the window away is a gap in that sensor's data, not its reading here.
    const entries = [...plot.querySelectorAll(".map-graph-line")]
      .map((path) => ({ path, point: nearest(pointsOf(path), x) }))
      .filter((entry) => entry.point && Math.abs(entry.point[0] - x) <= 0.04)
      .map((entry) => ({
        label: entry.path.dataset.seriesLabel || "",
        // The line's colour is its --series-color (the stylesheet makes it the stroke).
        color: entry.path.style.getPropertyValue("--series-color"),
        value: entry.point[1]
      }))
      .sort((a, b) => b.value - a.value);

    if (entries.length === 0 || !Number.isFinite(start) || !Number.isFinite(end)) {
      hide(plot);
      return;
    }

    // Built with textContent only - the labels are sensor names, which a user typed.
    const time = document.createElement("time");
    const at = new Date(start + x * (end - start));
    time.textContent = at.toLocaleString(undefined, end - start > 36 * 3600e3
      ? { weekday: "short", hour: "2-digit", minute: "2-digit" }
      : { hour: "2-digit", minute: "2-digit" });
    const rows = entries.map((entry) => {
      const row = document.createElement("span");
      row.className = "map-multigraph-tooltip-row";
      const swatch = document.createElement("span");
      swatch.className = "map-graph-swatch";
      swatch.style.setProperty("--series-color", entry.color);
      const name = document.createElement("span");
      name.textContent = entry.label;
      const value = document.createElement("strong");
      value.textContent = formatValue(entry.value, unit);
      row.append(swatch, name, value);
      return row;
    });
    tooltip.replaceChildren(time, ...rows);

    cursor.style.left = `${x * 100}%`;
    cursor.hidden = false;
    tooltip.hidden = false;
    // Beside the pointer, on whichever side has the room.
    if (x > 0.55) {
      tooltip.style.left = "";
      tooltip.style.right = `calc(${(1 - x) * 100}% + 8px)`;
    } else {
      tooltip.style.right = "";
      tooltip.style.left = `calc(${x * 100}% + 8px)`;
    }
  };

  document.addEventListener("pointermove", (event) => {
    const plot = event.target instanceof Element ? event.target.closest(".map-multigraph-plot") : null;
    if (!plot || plot.closest("[data-map-designer]")) {
      return;
    }
    show(plot, event);
  });
  document.addEventListener("pointerout", (event) => {
    const plot = event.target instanceof Element ? event.target.closest(".map-multigraph-plot") : null;
    if (plot && !(event.relatedTarget instanceof Node && plot.contains(event.relatedTarget))) {
      hide(plot);
    }
  });
}

function initializeMapLiveData() {
  const root = document.querySelector("[data-map-live]");
  if (!root) {
    return;
  }

  const url = root.dataset.mapLive;
  const seconds = Math.max(5, Number(root.dataset.mapLiveInterval) || 20);
  let revision = root.dataset.mapLiveRevision || "";
  let failures = 0;

  const setText = (scope, selector, text) => {
    const target = scope.querySelector(selector);
    if (target && text !== null && text !== undefined && target.textContent !== text) {
      target.textContent = text;
    }
  };

  const patchRows = (tile, rows) => {
    if (!rows) {
      return;
    }
    // A honeycomb's cells are rows too (one per sensor, by position); a timeline row also carries its bands.
    const items = tile.querySelectorAll(".map-tile-row, .map-hex");
    // Row COUNT is part of the revision, so a mismatch here means the payload and the markup are already out
    // of step and a reload is on its way - patching half a list would just look broken in the meantime.
    if (items.length !== rows.length) {
      return;
    }
    items.forEach((item, index) => {
      const row = rows[index];
      item.dataset.state = row.tone || "unknown";
      setText(item, ".map-tile-row-label strong", row.label);
      setText(item, ".map-tile-row-label small", row.detail || "");
      setText(item, ".map-tile-row-value", row.value || "");
      setText(item, ".map-tile-row-time", row.timeText || "");
      if (item.classList.contains("map-hex")) {
        const hint = row.detail ? `${row.label} - ${row.detail}` : row.label;
        item.title = hint;
      }
      const barLength = item.querySelector("[data-row-percent]");
      if (barLength && typeof row.percent === "number") {
        barLength.style.setProperty("--bar-w", `${row.percent}%`);
      }
      const bar = item.querySelector("[data-row-segments]");
      if (bar && typeof row.segments === "string") {
        bar.replaceChildren(...row.segments.split(";").filter(Boolean).map((part) => {
          const [x, w, tone] = part.split(",");
          const segment = document.createElement("span");
          segment.dataset.tone = tone;
          segment.style.setProperty("--seg-x", `${x}%`);
          segment.style.setProperty("--seg-w", `${w}%`);
          return segment;
        }));
      }
    });
  };

  const patchPins = (tile, pins) => {
    if (!pins) {
      return;
    }
    const markers = tile.querySelectorAll(".map-pin");
    if (markers.length !== pins.length) {
      return;
    }
    markers.forEach((marker, index) => {
      const pin = pins[index];
      marker.dataset.state = pin.tone || "unknown";
      marker.title = pin.label || "";
      setText(marker, ".map-pin-label strong", pin.label);
      setText(marker, ".map-pin-label small", pin.value || "");
    });
  };

  // A link is found by its id (the markup carries it), so its colour, width and traffic follow the sensor.
  const patchLinks = (tile, links) => {
    (links || []).forEach((link) => {
      const line = tile.querySelector(`.map-link[data-link-id="${link.id}"]`);
      if (line) {
        line.dataset.state = link.tone || "neutral";
        line.style.setProperty("--link-width", `${Number(link.width) || 2}px`);
      }
      const label = tile.querySelector(`[data-link-label="${link.id}"]`);
      if (label) {
        label.dataset.state = link.tone || "neutral";
        setText(label, "[data-link-traffic]", link.traffic || "");
      }
    });
  };

  // A rack device keeps its place (that is markup); its colour and detail follow its sensors.
  const patchRack = (tile, devices) => {
    (devices || []).forEach((device) => {
      const element = tile.querySelector(`[data-rack-id="${device.id}"]`);
      if (element) {
        element.dataset.state = device.tone || "neutral";
        setText(element, "[data-rack-detail]", device.detail || "");
      }
    });
  };

  // A donut's ring follows its legend rows: each slice starts where the previous one ended.
  const patchDonut = (tile, rows, value) => {
    const slices = tile.querySelectorAll(".map-donut-seg");
    if (!rows || slices.length !== rows.length) {
      return;
    }
    let offset = 0;
    slices.forEach((slice, index) => {
      const share = Number(rows[index].percent) || 0;
      slice.style.setProperty("--seg", String(share));
      slice.style.setProperty("--offset", String(offset));
      offset += share;
    });
    setText(tile, ".map-donut-figure", value || "");
  };

  const patchTile = (tile, data) => {
    const before = tile.dataset.state;
    tile.dataset.state = data.stateKey || "unknown";
    if (data.stateKey === "error" && before !== "error") {
      window.dispatchEvent(new CustomEvent("matmon:map-tile-failed", { detail: { tile } }));
    }
    const badge = tile.querySelector("[data-tile-state-label]");
    if (badge) {
      setMapTilePillTone(badge, data.stateKey);
      badge.textContent = data.stateLabel || "";
    }
    setText(tile, "[data-tile-value]", data.value || "");
    setText(tile, "[data-tile-subtitle]", data.subtitle || "");
    setText(tile, "[data-tile-progress-label]", data.progressLabel || "");

    const progress = tile.querySelector("[data-tile-progress]");
    if (progress && data.progressPercent !== null && data.progressPercent !== undefined) {
      const percent = Math.min(100, Math.max(0, Number(data.progressPercent)));
      // Explicit fixed formatting: a locale-formatted "42,5" would make the whole declaration invalid.
      progress.style.setProperty("--map-progress", percent.toFixed(2));
      // Deliberately NOT writing the percentage into the dial's <strong>: that is the reading (with its
      // unit), kept in step by the [data-tile-value] setter above. Overwriting it here turned "12 ms" into
      // "2.4%" on the first poll after load.
    }

    const line = tile.querySelector("[data-tile-graph-line]");
    if (line && data.graphLinePath) {
      line.setAttribute("d", data.graphLinePath);
    }
    const area = tile.querySelector("[data-tile-graph-area]");
    if (area && data.graphAreaPath) {
      area.setAttribute("d", data.graphAreaPath);
    }
    const bars = tile.querySelector("[data-tile-graph-bars]");
    if (bars && data.graphBarPath) {
      bars.setAttribute("d", data.graphBarPath);
    }

    // A multi-graph's lines ARE its values, so the paths themselves are patched, by POSITION (a change to
    // the series COUNT bumps the revision and reloads instead). Everything of a position travels together -
    // name, colour, line, value, tooltip points - because the legend is ordered by the last reading and two
    // sensors swap places whenever another becomes the highest.
    if (Array.isArray(data.series)) {
      const paths = tile.querySelectorAll(".map-tile-multigraph .map-graph-line");
      const items = tile.querySelectorAll(".map-graph-legend li");
      data.series.forEach((entry, index) => {
        const path = paths[index];
        if (path) {
          path.setAttribute("d", entry.linePath || "");
          path.style.setProperty("--series-color", entry.color);
          path.dataset.seriesPoints = entry.points || "";
          path.dataset.seriesLabel = entry.label || "";
        }
        const item = items[index];
        if (item) {
          setText(item, ".map-graph-legend-name", entry.label || "");
          setText(item, ".map-graph-legend-value", entry.value || "");
          const swatch = item.querySelector(".map-graph-swatch");
          if (swatch) {
            swatch.style.setProperty("--series-color", entry.color);
          }
        }
      });
    }
    const chart = tile.querySelector("[data-map-multigraph]");
    if (chart && data.axis) {
      setText(chart, "[data-axis-top]", data.axis.top);
      setText(chart, "[data-axis-middle]", data.axis.middle);
      setText(chart, "[data-axis-bottom]", data.axis.bottom);
      chart.dataset.windowStart = String(data.axis.startMs);
      chart.dataset.windowEnd = String(data.axis.endMs);
    }

    const sla = tile.querySelector(".map-tile-sla");
    if (sla && data.sla) {
      setText(sla, "strong", data.sla.percent === null || data.sla.percent === undefined
        ? "-"
        : `${Number(data.sla.percent).toFixed(2).replace(/\.?0+$/, "")} %`);
      setText(sla, "small", data.sla.label || "");
    }

    patchRows(tile, data.rows);
    patchPins(tile, data.pins);
    patchLinks(tile, data.links);
    patchDonut(tile, data.rows, data.value);
    patchRack(tile, data.rack);
  };

  const refresh = async () => {
    try {
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      if (!response.ok) {
        failures += 1;
        return;
      }

      const snapshot = await response.json();
      failures = 0;

      if (revision && snapshot.revision && snapshot.revision !== revision) {
        // The board's shape changed (a tile added/moved/resized) - that cannot be patched into markup the
        // server rendered, so take the one reload it costs.
        window.location.reload();
        return;
      }
      revision = snapshot.revision || revision;

      (snapshot.slides || []).forEach((slide) => {
        (slide.tiles || []).forEach((data) => {
          const tile = document.querySelector(`.map-tile[data-tile-id="${data.id}"]`);
          if (tile) {
            patchTile(tile, data);
          }
        });
      });
    } catch {
      // A wallboard is unattended: a transient network blip must leave the last good values on screen, not
      // blank the board or stop polling.
      failures += 1;
    }
  };

  refresh();
  window.setInterval(refresh, seconds * 1000);
}
// The NOC switches of a viewed map: "only problems" dims every tile that is fine (the board keeps its layout, so the
// eye still finds a tile where it always is), and a tile that turns red flashes for a while and - when the sound is on -
// beeps. In the app both are per-browser switches; on the public wallboard ?focus=problems and the map's own
// "beep" setting decide, since a TV has no buttons.
function initializeMapNoc() {
  const host = document.querySelector("[data-map-noc]");
  if (!host) {
    return;
  }
  const stage = host.querySelector("[data-map-stage]") || host;
  const focusButton = document.querySelector("[data-map-focus-toggle]");
  const soundButton = document.querySelector("[data-map-sound-toggle]");
  const read = (key) => { try { return localStorage.getItem(key) === "1"; } catch { return false; } };
  const write = (key, on) => { try { localStorage.setItem(key, on ? "1" : "0"); } catch { /* nothing to keep */ } };

  const params = new URLSearchParams(window.location.search);
  let focus = focusButton ? read("matmon.mapFocus") : params.get("focus") === "problems";
  let sound = soundButton ? read("matmon.mapSound") : host.getAttribute("data-map-alert-sound") === "true";

  const applyFocus = () => {
    stage.classList.toggle("is-focus-problems", focus);
    focusButton?.setAttribute("aria-pressed", focus ? "true" : "false");
  };
  const applySound = () => soundButton?.setAttribute("aria-pressed", sound ? "true" : "false");
  applyFocus();
  applySound();

  focusButton?.addEventListener("click", () => { focus = !focus; write("matmon.mapFocus", focus); applyFocus(); });

  let audio = null;
  const unlock = () => {
    try {
      audio = audio || new (window.AudioContext || window.webkitAudioContext)();
      if (audio.state === "suspended") {
        audio.resume();
      }
    } catch { audio = null; }
  };
  soundButton?.addEventListener("click", () => {
    sound = !sound;
    write("matmon.mapSound", sound);
    applySound();
    if (sound) {
      unlock(); // a click is the gesture a browser wants before it plays anything
    }
  });
  if (sound) {
    // Without a click a browser may keep the sound back; the first click anywhere on the page lets it through.
    document.addEventListener("pointerdown", unlock, { once: true });
    unlock();
  }

  let lastBeep = 0;
  const beep = () => {
    if (!sound || !audio || audio.state !== "running" || Date.now() - lastBeep < 5000) {
      return;
    }
    lastBeep = Date.now();
    [0, 0.22].forEach((offset) => {
      const tone = audio.createOscillator();
      const gain = audio.createGain();
      tone.type = "sine";
      tone.frequency.value = 880;
      gain.gain.setValueAtTime(0.0001, audio.currentTime + offset);
      gain.gain.exponentialRampToValueAtTime(0.25, audio.currentTime + offset + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, audio.currentTime + offset + 0.18);
      tone.connect(gain).connect(audio.destination);
      tone.start(audio.currentTime + offset);
      tone.stop(audio.currentTime + offset + 0.2);
    });
  };

  window.addEventListener("matmon:map-tile-failed", (event) => {
    const tile = event.detail?.tile;
    if (!tile) {
      return;
    }
    tile.classList.remove("is-newly-failed");
    void tile.offsetWidth; // restart the animation when it fails again
    tile.classList.add("is-newly-failed");
    window.setTimeout(() => tile.classList.remove("is-newly-failed"), 15000);
    beep();
  });
}

// An Embed tile showing a picture reloads it on its own timer (a camera snapshot); a failed load keeps the last picture.
function initializeMapEmbeds() {
  document.querySelectorAll("img[data-embed-refresh]").forEach((image) => {
    if (image.closest("[data-map-designer]")) {
      return;
    }
    const seconds = Math.max(5, Number(image.getAttribute("data-embed-refresh")) || 30);
    const base = image.getAttribute("src") || "";
    window.setInterval(() => {
      if (document.visibilityState !== "visible") {
        return;
      }
      const next = new Image();
      const url = `${base}${base.includes("?") ? "&" : "?"}_=${Date.now()}`;
      next.onload = () => { image.src = url; };
      next.src = url;
    }, seconds * 1000);
  });
}

function initializeMapCarousel() {
  document.querySelectorAll("[data-map-carousel]").forEach((carousel) => {
    const slides = Array.from(carousel.querySelectorAll("[data-map-slide]"));
    if (slides.length <= 1) {
      return;
    }

    // Walk up until the controls are in scope. In "Below the board" mode the nav is a SIBLING OF THE STAGE,
    // not a child of it - the stage is a uniformly scaled canvas, so anything inside it scales with the board
    // instead of sitting under it. Bounded so a page with two boards cannot adopt the other one's controls.
    let scope = carousel.parentElement || carousel;
    for (let hops = 0; hops < 3 && scope.parentElement && !scope.querySelector("[data-map-carousel-nav]"); hops += 1) {
      scope = scope.parentElement;
    }
    const dots = Array.from(scope.querySelectorAll("[data-map-carousel-dot]"));
    const prev = scope.querySelector("[data-map-carousel-prev]");
    const next = scope.querySelector("[data-map-carousel-next]");
    // By value: Razor writes a null data-* attribute as data-x="" instead of dropping it, so presence meant nothing and
    // the public wallboard turned its slides whatever the map said.
    const autoplay = carousel.dataset.mapCarouselAutoplay === "true";
    const intervalSeconds = parseInt(carousel.dataset.mapCarouselInterval || "", 10);
    const intervalMs = Math.max(3, Number.isFinite(intervalSeconds) ? intervalSeconds : 12) * 1000;
    let active = 0;
    let timer = null;

    // Page controls either sit under the board, float over it, or float over it and fade out while nothing
    // happens (MonitoringMapPaginationMode). These three were REFERENCED further down but never declared,
    // so the function threw a ReferenceError before wiring the arrows, dots and autoplay - which is why a
    // multi-slide wallboard never advanced past slide 1.
    const nav = scope.querySelector("[data-map-carousel-nav]");
    const paginationMode = (nav?.dataset.mapPagination || "below").toLowerCase();
    const autoHideNav = paginationMode === "overlayonactivity";
    const stage = carousel.closest("[data-map-stage]");
    let idleTimer = null;
    const pingActivity = () => {
      if (!nav) {
        return;
      }
      nav.classList.remove("is-idle");
      window.clearTimeout(idleTimer);
      idleTimer = window.setTimeout(() => nav.classList.add("is-idle"), 3000);
    };

    const show = (index) => {
      active = (index + slides.length) % slides.length;
      slides.forEach((slide, i) => {
        slide.hidden = i !== active;
      });
      dots.forEach((dot, i) => {
        dot.classList.toggle("is-active", i === active);
        if (i === active) {
          dot.setAttribute("aria-current", "true");
        } else {
          dot.removeAttribute("aria-current");
        }
      });
      if (autoHideNav) {
        pingActivity();
      }
    };

    const stop = () => {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    };

    const start = () => {
      if (!autoplay) {
        return;
      }
      stop();
      timer = setInterval(() => show(active + 1), intervalMs);
    };

    if (prev) {
      prev.addEventListener("click", () => { show(active - 1); start(); });
    }
    if (next) {
      next.addEventListener("click", () => { show(active + 1); start(); });
    }
    dots.forEach((dot, index) => dot.addEventListener("click", () => { show(index); start(); }));

    if (autoplay) {
      carousel.addEventListener("mouseenter", stop);
      carousel.addEventListener("mouseleave", start);
    }

    if (autoHideNav && stage) {
      stage.addEventListener("pointermove", pingActivity);
    }

    show(0);
    start();
  });
}

// Below this stage width (CSS px), a slide switches to the single-column Auto-Stack layout instead of the
// scaled canvas - based on the STAGE's own rendered width, not the viewport, so it is also correct inside an
// embedded/tunneled console whose iframe may be narrower than the outer window.
const MapStackBreakpoint = 640;
// Mirrors MonitoringMapTileConstraints.MaxRowSpan (Matmon.Core) - JS cannot reference Core, and a designer
// that let you drag a tile taller than the store will save is a silent data loss on the next save.
const MapMaxRowSpan = 24;

// v3 has no transform-scaled canvas to fit: the slide is a real CSS grid that lays itself out, and the
// wallboard's row height comes from a pure-CSS calc against the viewport. All that is left for JS is the one
// thing CSS cannot decide - whether this STAGE (not the viewport, so it is right inside an embedded console
// too) is narrow enough to collapse to a single column.
// The wallboard's row unit is a pure CSS calc against the viewport, which is right only while the flow uses
// no more rows than the board says fill a screen. A ragged band (one tile in a row shorter than its
// neighbour) can push it past that, and then the TV shows a board you would have to scroll - which nobody is
// standing there to do. So measure once and shrink the unit until it fits.
function fitWallboard() {
  document.querySelectorAll(".public-map-shell .map-slide:not([hidden])").forEach((slide) => {
    const stage = slide.closest("[data-map-stage]");
    if (!stage) {
      return;
    }

    slide.style.removeProperty("--map-row-unit");
    const available = stage.clientHeight;
    const needed = slide.scrollHeight;
    if (available <= 0 || needed <= available + 1) {
      return;
    }

    const unit = parseFloat(getComputedStyle(slide).getPropertyValue("--map-row-unit"));
    if (!Number.isFinite(unit) || unit <= 0) {
      return;
    }

    // The gaps and the padding do not shrink with the unit, so scaling by the height ratio alone would
    // overshoot; taking the ratio of the ROW part is the honest correction.
    const chrome = needed - unit * Math.max(1, Math.round(needed / unit));
    const rowsPart = Math.max(1, needed - chrome);
    slide.style.setProperty("--map-row-unit", `${Math.max(24, unit * ((available - chrome) / rowsPart))}px`);
  });
}

function fitMapStages() {
  fitWallboard();
  document.querySelectorAll("[data-map-stage]").forEach((stage) => {
    const width = stage.getBoundingClientRect().width;
    // Never stack the DESIGNER canvas: the editor showing a one-column list while the user arranges a
    // multi-column board is the exact opposite of WYSIWYG.
    const isDesigner = stage.querySelector("[data-map-designer]") !== null;
    const shouldStack = !isDesigner && width > 0 && width < MapStackBreakpoint;
    stage.querySelectorAll(".map-slide").forEach((slide) => {
      if (shouldStack) {
        slide.dataset.layout = "stack";
      } else {
        delete slide.dataset.layout;
      }
    });
  });
}

function initializeMapStages() {
  const stages = document.querySelectorAll("[data-map-stage]");
  if (stages.length === 0) {
    return;
  }

  fitMapStages();

  if (typeof ResizeObserver !== "undefined") {
    const observer = new ResizeObserver(() => fitMapStages());
    stages.forEach((stage) => observer.observe(stage));
  } else {
    window.addEventListener("resize", fitMapStages);
  }
}


function initializeMapDesigner() {
  // The designer canvas IS the (single, always-visible) .map-slide - slide switching just filters which
  // tiles are shown on it (setActiveSlide/applySlideFilter below), matching the pre-Phase-A behaviour.
  const canvas = document.querySelector("[data-map-designer]");
  if (!canvas) {
    return;
  }

  const stage = canvas.closest("[data-map-stage]") || canvas;
  const form = document.querySelector("[data-map-designer-form]");

  // "Full screen" in the header bar: the canvas alone, as big as the screen - a quick look at the board the
  // size it will be on the wall, without leaving the designer. Pressed again (or Escape) it comes back.
  document.querySelector("[data-map-fullscreen]")?.addEventListener("click", () => {
    const target = canvas.closest(".map-designer-workbench") || stage;
    if (document.fullscreenElement) {
      document.exitFullscreen?.();
    } else {
      target.requestFullscreen?.().catch(() => {
        // Refused (an iframe without allowfullscreen, a kiosk policy): the designer simply stays as it is.
      });
    }
  });

  // NB: no zoom or aspect handles here any more - a flow has neither. The aspect fields still exist as hidden
  // inputs so an older board round-trips, but nothing in the designer reads them.
  const scaleInput = form?.querySelector("[data-map-scale]");
  const scaleOutput = form?.querySelector("[data-map-scale-output]");
  const readScale = () => {
    const value = parseFloat(scaleInput?.value || "1");
    return Number.isFinite(value) && value > 0 ? value : 1;
  };
  const mapNameInput = form?.querySelector("[data-map-name]");
  const mapDescriptionInput = form?.querySelector("[data-map-description]");
  const aspectWidthInput = form?.querySelector("[data-map-aspect-w]");
  const aspectHeightInput = form?.querySelector("[data-map-aspect-h]");
  const mapSelectButton = form?.querySelector("[data-map-select-map]");
  const mapTitlePreview = form?.querySelector("[data-map-title-preview]");
  const mapDescriptionPreview = form?.querySelector("[data-map-description-preview]");
  const mapGridPreview = form?.querySelector("[data-map-grid-preview]");
  const propertyHost = form?.querySelector("[data-map-property-host]");
  const propertyEmpty = form?.querySelector("[data-map-property-empty]");
  const template = form?.querySelector("template[data-map-tile-template]");
  // Resolved from the FORM, not from the old [data-map-slide-strip] wrapper the slide list used to sit in.
  // When that wrapper became the bottom-row card these all silently became null, and the failure was quiet
  // and total: renderSlideInputs() bailed out, so the hidden Input.Slides[..] fields were never written and
  // a save would have dropped every slide - while the tiles, filtered against a slide id that then existed
  // nowhere, all vanished from the canvas.
  const slideTabsHost = form?.querySelector("[data-map-slide-tabs]");
  // The canvas-header switcher. The LIST lives on the Slides tab; this is the one control that has to stay
  // with the board, so it is rendered from the same `slides` array rather than being a second list.
  const slideSwitch = form?.querySelector("[data-map-slide-switch]");
  const slideInputsHost = form?.querySelector("[data-map-slide-inputs]");
  const slideAddButton = form?.querySelector("[data-map-slide-add]");
  let slides = [];
  let activeSlideId = "";
  // Forward reference: renderSlideTabs is declared long before renderSlidePreviews, and `typeof` on a const
  // still in its temporal dead zone throws - so the strip is refreshed through a mutable hook that is a no-op
  // until the real renderer exists.
  let refreshSlidePreviews = () => {};
  // Numeric keys because an enum round-tripped through a hidden input can arrive as either the name or the
  // underlying int depending on which path wrote it.
  const numericKindMap = {
    "0": "Text",
    "1": "Element",
    "2": "Status",
    "3": "Value",
    "4": "Graph",
    "5": "SensorList",
    "6": "AlertFeed",
    "7": "Sla",
    "8": "Clock",
    "9": "Heading",
    "10": "Image",
    "11": "GeoMap",
    "12": "MultiGraph",
    "13": "Topology",
    "14": "Honeycomb",
    "15": "Timeline",
    "16": "Table",
    "17": "Bars",
    "18": "Donut",
    "19": "Embed",
    "20": "Rack"
  };
  const kindLabels = {
    "0": "Text",
    "1": "State",
    "2": "Summary",
    "3": "Value",
    "4": "Graph",
    "5": "List",
    "6": "Alerts",
    "7": "SLA",
    "8": "Clock",
    "9": "Heading",
    "10": "Image",
    "11": "World map",
    "12": "Multi-graph",
    "13": "Topology",
    "14": "Honeycomb",
    "15": "Timeline",
    "16": "Table",
    "17": "Bars",
    "18": "Donut",
    "19": "Embed",
    "20": "Rack",
    Text: "Text",
    Element: "State",
    Status: "Summary",
    Value: "Value",
    Graph: "Graph",
    SensorList: "List",
    AlertFeed: "Alerts",
    Sla: "SLA",
    Clock: "Clock",
    Heading: "Heading",
    Image: "Image",
    GeoMap: "World map",
    MultiGraph: "Multi-graph",
    Topology: "Topology",
    Honeycomb: "Honeycomb",
    Timeline: "Timeline",
    Table: "Table",
    Bars: "Bars",
    Donut: "Donut",
    Embed: "Embed",
    Rack: "Rack"
  };
  const kindHints = {
    SensorList: "Lists the sensors under the target, ordered by state or by a channel value.",
    AlertFeed: "Newest open alerts. With no target it shows the whole workspace.",
    Sla: "Uptime over a window, from the statistics buckets - not live state.",
    Clock: "Shows the board timezone (Map properties > Wallboard).",
    Heading: "A section title. Use Text for the sub-line.",
    Image: "Upload a picture, then place status pins on it.",
    GeoMap: "The shipped offline world map - pins are placed by latitude/longitude.",
    Honeycomb: "Every sensor under the target as one cell in its state colour - hundreds at a glance.",
    Bars: "One reading across the sensors under the target as bars. Pick the channel (Rank by channel) and how many rows.",
    Donut: "How the sensors under the target split by state.",
    Rack: "A rack to scale: add its devices with the unit they sit in and their height; each takes its colour from its own target.",
    Embed: "Another page in a frame, or a picture (a camera snapshot) reloaded on a timer.",
    Table: "The sensors under the target with their machine, reading and last check. Ordered like a sensor list.",
    Timeline: "A state band per sensor over the time window (up to 72 h): when it went red and for how long.",
    Topology: "Place devices, then join them with links - give a link the port's interface sensor and it shows the traffic and turns red when the port goes down.",
    "0": "Text tiles do not need a target.",
    "1": "Shows one target state or value. Progress and gauge use the default channel when possible.",
    "2": "Aggregates all child sensors below the selected target. Progress and gauge show healthy percentage.",
    "3": "Shows the default channel value large.",
    "4": "Uses the selected sensor history as a compact trend graph.",
    Text: "Text tiles do not need a target.",
    Element: "Shows one target state or value. Progress and gauge use the default channel when possible.",
    Status: "Aggregates all child sensors below the selected target. Progress and gauge show healthy percentage.",
    Value: "Shows the default channel value large.",
    Graph: "Uses the selected sensor history as a compact trend graph."
  };
  const colorPattern = /^#[0-9a-fA-F]{6}$/;

  // Everything the designer needs that is authored server-side, in ONE JSON block: the single
  // MonitoringMapTileConstraints table (a second hand-duplicated JS copy could silently drift from the Core
  // one), the MapWidgetCatalog palette and its layout templates. Constraints are cell-based (v2):
  // {minColumns, minRows, defaultColumns, defaultRows}. A template slot names a WIDGET key, not a tile kind,
  // which is why the palette has to be here too.
  const configEl = form?.querySelector("[data-map-designer-config]");
  let designerConfig = {};
  try {
    designerConfig = configEl ? JSON.parse(configEl.textContent || "{}") : {};
  } catch {
    designerConfig = {};
  }
  const constraints = designerConfig.constraints || {};
  const widgetCatalog = designerConfig.widgets || {};
  const layoutTemplates = Array.isArray(designerConfig.templates) ? designerConfig.templates : [];
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const getLimits = (kind) =>
    constraints[normalizeKind(kind)] || constraints.Element || { minColumns: 2, minRows: 2, defaultColumns: 2, defaultRows: 2 };

  // Grid geometry - real inputs in the Grid property tab now. Editing one reflows every widget, which is the
  // whole reason v2 stores cells instead of pixels.
  const columnsInput = form?.querySelector("[data-map-columns]");
  const rowsInput = form?.querySelector("[data-map-rows]");
  const tilePaddingInput = form?.querySelector("[data-map-tile-padding]");
  const outerMarginInput = form?.querySelector("[data-map-outer-margin]");
  const gridGuides = canvas.querySelector(".map-grid-guides");
  const propertyTabs = form?.querySelector("[data-map-property-tabs]");
  const slidePanel = form?.querySelector("[data-map-slide-panel]");
  const scopeLabel = form?.querySelector("[data-map-property-scope-label]");
  // The tile tab bar lives outside the per-tile panels, so the chosen tab survives clicking tile to tile.
  let activePropertyTab = "general";

  // v3 has no canvas and no pixel math. The slide is a CSS grid, so the only thing the designer needs to
  // know is the shape of that grid - everything that used to mirror MonitoringMapGeometry here (cell rects,
  // px<->cell conversions, the zoom/scale transform) went with the canvas.
  const readGrid = () => ({
    columns: Math.max(1, Math.round(Number(columnsInput?.value) || 12)),
    rows: Math.max(1, Math.round(Number(rowsInput?.value) || 6)),
    tilePadding: Math.max(0, Number(tilePaddingInput?.value) || 16),
    outerMargin: Math.max(0, Number(outerMarginInput?.value) || 24)
  });

  const syncMapSummary = () => {
    const name = mapNameInput?.value?.trim() || "New Map";
    const description = mapDescriptionInput?.value?.trim() || "No description";
    if (mapTitlePreview) {
      mapTitlePreview.textContent = name;
    }
    if (mapDescriptionPreview) {
      mapDescriptionPreview.textContent = description;
    }
    if (mapGridPreview) {
      mapGridPreview.textContent = String(readGrid().columns);
    }
  };

  // A cell per grid position, laid out by the same grid definition as the tiles - so a guide cannot drift
  // from where a tile actually lands. The row count is whatever the slide currently needs (it grows as the
  // flow wraps), never fewer than the board's own rows-per-screen, and the last on-screen row is marked so
  // you can see where a TV would cut the board off.
  const renderGridGuides = (grid = readGrid()) => {
    if (!gridGuides) {
      return;
    }
    // The USED row height in px. Reading the custom property gives its authored text ("4.6rem"), and
    // parseFloat of that is 4.6 - which asked for 28 rows where 8 were needed.
    const rowUnit = Number.parseFloat(getComputedStyle(gridGuides).gridAutoRows) || 0;
    const pitch = rowUnit + grid.tilePadding;
    const available = canvas.clientHeight - 2 * grid.outerMargin + grid.tilePadding;
    const needed = pitch > 0 ? Math.ceil(available / pitch) : 0;
    const rows = Math.max(grid.rows, needed, 1);

    gridGuides.replaceChildren();
    const fragment = document.createDocumentFragment();
    for (let cell = 0; cell < rows * grid.columns; cell += 1) {
      const box = document.createElement("span");
      box.className = "map-grid-cell-guide";
      if (Math.floor(cell / grid.columns) === grid.rows - 1 && rows > grid.rows) {
        box.classList.add("is-fold");
      }
      fragment.appendChild(box);
    }
    gridGuides.appendChild(fragment);
  };

  const syncCanvas = () => {
    const grid = readGrid();
    canvas.style.setProperty("--map-columns", String(grid.columns));
    canvas.style.setProperty("--map-rows", String(grid.rows));
    canvas.style.setProperty("--tile-gap", `${grid.tilePadding}px`);
    canvas.style.setProperty("--map-margin", `${grid.outerMargin}px`);
    renderGridGuides(grid);
    syncMapSummary();
    fitMapStages();
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => applyTileSize(tile));
  };

  const getPanel = (index) => propertyHost?.querySelector(`[data-map-property-panel][data-tile-index="${index}"]`);
  const getTile = (index) => canvas.querySelector(`[data-map-tile][data-tile-index="${index}"]`);
  const normalizeKind = (kind) => numericKindMap[String(kind)] || String(kind || "Element");
  const getKindLabel = (kind) => kindLabels[String(kind)] || kindLabels[normalizeKind(kind)] || "Tile";
  const createId = () => {
    if (window.crypto?.randomUUID) {
      return window.crypto.randomUUID();
    }

    return `00000000-0000-4000-8000-${Date.now().toString(16).padStart(12, "0").slice(-12)}`;
  };

  const getTileControls = (tile) => {
    const panel = getPanel(tile.dataset.tileIndex || "");
    return {
      order: tile.querySelector("[data-map-tile-order]"),
      column: tile.querySelector("[data-map-tile-column]"),
      row: tile.querySelector("[data-map-tile-row]"),
      columnSpan: panel?.querySelector("[data-map-tile-column-span]"),
      rowSpan: panel?.querySelector("[data-map-tile-row-span]")
    };
  };

  // One cell in px, for turning pointer movement into grid steps. The column pitch is derived from the
  // slide's own content width and the row pitch from the used grid-auto-rows, so it is right at any zoom,
  // font size or column count without parsing a unit anywhere.
  const cellMetrics = () => {
    const grid = readGrid();
    const rowUnit = Number.parseFloat(getComputedStyle(canvas).gridAutoRows) || 0;
    const content = canvas.clientWidth - 2 * grid.outerMargin;
    return {
      columns: grid.columns,
      columnPitch: (content + grid.tilePadding) / grid.columns,
      rowPitch: rowUnit + grid.tilePadding
    };
  };

  const tileRect = (tile) => {
    const controls = getTileControls(tile);
    return {
      column: Math.max(1, Number(controls.column?.value || 1)),
      row: Math.max(1, Number(controls.row?.value || 1)),
      columnSpan: Math.max(1, Number(controls.columnSpan?.value || tile.style.getPropertyValue("--tile-span") || 3)),
      rowSpan: Math.max(1, Number(controls.rowSpan?.value || tile.style.getPropertyValue("--tile-rows") || 2))
    };
  };

  // Where the tile sits, written to the hidden inputs AND the CSS variables in one place - so the model and
  // what you see cannot drift apart mid-drag.
  const applyTilePlacement = (tile, column, row) => {
    const grid = readGrid();
    const rect = tileRect(tile);
    const placedColumn = clamp(Math.round(column), 1, Math.max(1, grid.columns - rect.columnSpan + 1));
    const placedRow = Math.max(1, Math.round(row));

    const controls = getTileControls(tile);
    if (controls.column) {
      controls.column.value = String(placedColumn);
    }
    if (controls.row) {
      controls.row.value = String(placedRow);
    }
    tile.style.setProperty("--tile-col", String(placedColumn));
    tile.style.setProperty("--tile-row", String(placedRow));
  };

  const activeTiles = () => Array.from(canvas.querySelectorAll("[data-map-tile]")).filter((tile) =>
    (tile.dataset.slideId || "") === activeSlideId
    && tile.querySelector("[data-map-tile-deleted]")?.value !== "true");

  // Mirrors MonitoringMapPlacement.FindFreeSpot: first cell, left to right then down, where the tile fits
  // without touching one that is already there. A palette click that dropped every widget on 1,1 would bury
  // four of them under each other.
  const findFreeCell = (columnSpan, rowSpan) => {
    const grid = readGrid();
    const placed = activeTiles().map(tileRect);
    const hits = (column, row) => placed.some((other) =>
      other.column < column + columnSpan
      && column < other.column + other.columnSpan
      && other.row < row + rowSpan
      && row < other.row + other.rowSpan);

    const lastRow = placed.reduce((max, other) => Math.max(max, other.row + other.rowSpan - 1), 0);
    for (let row = 1; row <= lastRow + 1; row += 1) {
      for (let column = 1; column <= grid.columns - columnSpan + 1; column += 1) {
        if (!hits(column, row)) {
          return { column, row };
        }
      }
    }

    return { column: 1, row: lastRow + 1 };
  };

  // A tile has a size, not a position: the grid decides where it lands. Writing the spans back into their
  // inputs after clamping is what keeps the keyboard fields, the resize grip and what you see in agreement.
  const applyTileSize = (tile) => {
    const panel = getPanel(tile.dataset.tileIndex || "");
    const { columnSpan: columnSpanInput, rowSpan: rowSpanInput } = getTileControls(tile);
    const kind = normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || tile.dataset.kind);
    const limits = getLimits(kind);
    const grid = readGrid();

    // The kind's minimum, unless the board itself is narrower than that - then the board wins, or the tile
    // would be wider than the slide it lives on.
    const minColumns = Math.min(limits.minColumns, grid.columns);
    const columnSpan = clamp(Math.round(Number(columnSpanInput?.value || limits.defaultColumns)), minColumns, grid.columns);
    const rowSpan = clamp(Math.round(Number(rowSpanInput?.value || limits.defaultRows)), limits.minRows, MapMaxRowSpan);
    if (columnSpanInput) {
      columnSpanInput.value = String(columnSpan);
      // The field's max follows the grid: it was rendered with the column count the page loaded with, and a
      // board widened on the Board tab would otherwise fail the form's validation on Save - silently, from a
      // field in a panel nobody can see.
      columnSpanInput.max = String(grid.columns);
    }
    if (rowSpanInput) {
      rowSpanInput.value = String(rowSpan);
    }

    tile.style.setProperty("--tile-span", String(columnSpan));
    tile.style.setProperty("--tile-rows", String(rowSpan));

    const readout = panel?.querySelector("[data-map-property-size]");
    if (readout) {
      readout.textContent = `${columnSpan} of ${grid.columns} columns · ${rowSpan} rows tall · min ${limits.minColumns} x ${limits.minRows}`;
    }
    const badge = tile.querySelector("[data-map-tile-size-badge]");
    if (badge) {
      badge.textContent = `${columnSpan} × ${rowSpan}`;
    }
  };

  // The POSITIONS are the layout; Order is only the reading order a stacked phone view follows, so it is
  // derived from them here rather than kept by hand - exactly as MonitoringMapPlacement.Normalize does it on
  // save, so the two cannot disagree. (A deleted tile stays in the DOM to keep posting IsDeleted=true and is
  // skipped, or the survivors would be numbered from 2.)
  const commitPlacement = () => {
    activeTiles()
      .map((tile) => ({ tile, ...tileRect(tile) }))
      .sort((a, b) => a.row - b.row || a.column - b.column)
      .forEach((entry, index) => {
        const field = entry.tile.querySelector("[data-map-tile-order]");
        if (field) {
          field.value = String(index);
        }
      });
    syncCanvasEmpty?.();
  };

  const applyTileAppearance = (tile, panel) => {
    const background = panel?.querySelector("[data-map-property-background]")?.value?.trim();
    const accent = panel?.querySelector("[data-map-property-accent]")?.value?.trim();
    const text = panel?.querySelector("[data-map-property-text-color]")?.value?.trim();
    const setColor = (property, value) => {
      if (value && colorPattern.test(value)) {
        tile.style.setProperty(property, value);
      } else {
        tile.style.removeProperty(property);
      }
    };

    setColor("--map-tile-custom-bg", background);
    setColor("--map-tile-custom-accent", accent);
    setColor("--map-tile-custom-text", text);
  };

  // WYSIWYG for the picture widgets. The upload handler only wrote the asset id into a hidden field, so the
  // canvas kept showing "No image uploaded" until the map was saved and the page came back from the server -
  // on an editor whose whole promise is that it renders the real tile, that is the one widget that did not.
  const imageFitToCss = (value) => {
    switch (String(value || "").toLowerCase()) {
      case "cover": return "cover";
      case "stretch": return "fill";
      default: return "contain";
    }
  };

  const syncTileImage = (tile, panel) => {
    const pinCanvas = tile.querySelector("[data-map-pin-canvas]");
    if (!pinCanvas) {
      return;
    }
    // The world map is a fixed shipped asset, not an upload - leave its <img> alone.
    const kind = normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || tile.dataset.kind);
    if (kind === "GeoMap") {
      return;
    }
    const topology = kind === "Topology";

    const assetId = (panel?.querySelector("[data-map-property-image-id]")?.value || "").trim();
    let image = pinCanvas.querySelector("img.map-tile-image");
    let empty = pinCanvas.querySelector(".map-tile-canvas-empty");

    if (!assetId) {
      image?.remove();
      // A topology without a picture is a plain drawing surface, not a missing upload.
      if (topology) {
        empty?.remove();
        return;
      }
      if (!empty) {
        empty = document.createElement("span");
        empty.className = "map-tile-canvas-empty";
        empty.textContent = "No image uploaded";
        pinCanvas.prepend(empty);
      }
      return;
    }

    empty?.remove();
    if (!image) {
      image = document.createElement("img");
      image.className = "map-tile-image";
      image.alt = "";
      // Before the pins, so they keep painting on top of it.
      pinCanvas.prepend(image);
    }
    const source = "/api/map-assets/" + encodeURIComponent(assetId);
    if (!image.getAttribute("src") || image.getAttribute("src") !== source) {
      image.setAttribute("src", source);
    }
    // Same data-fit the server renders; the stylesheet maps it to object-fit.
    image.dataset.fit = imageFitToCss(panel?.querySelector("[data-map-property-image-fit]")?.value);
  };

  // The swatch is a convenience on top of the hex field, never a second source of truth: the TEXT input is
  // what posts, because only it can hold "" = keep the theme colour, which a colour input cannot express.
  const setupColorRules = (panel) => {
    panel?.querySelectorAll("[data-map-color-rule]").forEach((text) => {
      const key = text.dataset.mapColorRule;
      const swatch = panel.querySelector('[data-map-color-swatch="' + key + '"]');
      const clear = panel.querySelector('[data-map-color-clear="' + key + '"]');
      swatch?.addEventListener("input", () => {
        text.value = swatch.value.toUpperCase();
      });
      text.addEventListener("input", () => {
        const value = text.value.trim();
        if (swatch && colorPattern.test(value)) {
          swatch.value = value;
        }
      });
      clear?.addEventListener("click", () => {
        text.value = "";
        text.dispatchEvent(new Event("input", { bubbles: true }));
      });
    });
  };

  // --- Image upload + pin editor ----------------------------------------------------------------------
  // Pins are held as JSON in one hidden field per tile (see MapTileInput.PinsJson): they are an unbounded
  // per-tile collection placed by clicking a picture, and the designer clones whole tiles by rewriting field
  // names - index-bound nested fields would have to be re-indexed on every clone and delete.
  const pinTemplate = form?.querySelector("template[data-map-pin-template]");

  // Every sync of a tile (syncTileFromPanel -> syncPanelVisibility, also 350 ms after each keystroke in a row)
  // used to rebuild the pin / link / rack / target rows from scratch: the field being typed in lost focus and a
  // picker that had just been chosen came back blank. The rows now remember the data they were built from and
  // are only rebuilt when it changed. Keyed by the host ELEMENT, not an attribute: a cloned panel is a new
  // element with no listeners and must be built afresh.
  const renderedRows = new WeakMap();
  const rowsUpToDate = (host, key) => renderedRows.get(host) === key;
  const markRows = (host, key) => renderedRows.set(host, key);
  const pinRowsKey = (panel) =>
    `${normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value)}|${panel?.querySelector("[data-map-property-pins]")?.value || ""}`;
  const linkRowsKey = (panel) =>
    `${panel?.querySelector("[data-map-property-links]")?.value || ""}|${readPins(panel).map((pin) => pin.id).join(",")}`;
  const rackRowsKey = (panel) => panel?.querySelector("[data-map-property-rack]")?.value || "";
  const targetRowsKey = (panel) => panel?.querySelector("[data-map-targets-value]")?.value || "";

  const readPins = (panel) => {
    const field = panel?.querySelector("[data-map-property-pins]");
    if (!field?.value) {
      return [];
    }
    try {
      const parsed = JSON.parse(field.value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  };

  // Pins and links are part of the tile's server render (the designer shows the real topology), so a change
  // re-renders it - debounced, because a label is typed a key at a time.
  const canvasRefreshTimers = new WeakMap();
  const queueCanvasRefresh = (panel) => {
    clearTimeout(canvasRefreshTimers.get(panel));
    canvasRefreshTimers.set(panel, setTimeout(() => syncTileFromPanel(panel), 350));
  };

  const writePins = (panel, pins) => {
    const field = panel?.querySelector("[data-map-property-pins]");
    if (field) {
      field.value = pins.length === 0 ? "" : JSON.stringify(pins);
      field.dispatchEvent(new Event("change", { bubbles: true }));
      queueCanvasRefresh(panel);
    }
  };

  const linkTemplate = form?.querySelector("template[data-map-link-template]");

  const readLinks = (panel) => {
    const field = panel?.querySelector("[data-map-property-links]");
    if (!field?.value) {
      return [];
    }
    try {
      const parsed = JSON.parse(field.value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  };

  const writeLinks = (panel, links) => {
    const field = panel?.querySelector("[data-map-property-links]");
    if (field) {
      field.value = links.length === 0 ? "" : JSON.stringify(links);
      field.dispatchEvent(new Event("change", { bubbles: true }));
      queueCanvasRefresh(panel);
    }
  };

  const pinName = (pin, index) => (pin.label || "").trim() || `Pin ${index + 1}`;

  // Fills a link's two pin selects from the tile's CURRENT pins, keeping the chosen one.
  const fillPinSelect = (select, pins, value) => {
    select.replaceChildren(...pins.map((pin, index) => {
      const option = document.createElement("option");
      option.value = pin.id;
      option.textContent = pinName(pin, index);
      return option;
    }));
    select.value = value || "";
  };

  const refreshLinkPinOptions = (panel) => {
    const pins = readPins(panel);
    panel?.querySelectorAll("[data-map-link-row]").forEach((row) => {
      row.querySelectorAll('[data-link-field="fromPinId"], [data-link-field="toPinId"]').forEach((select) => {
        fillPinSelect(select, pins, select.value);
      });
    });
  };

  const renderLinkRows = (panel) => {
    const host = panel?.querySelector("[data-map-link-editor]");
    if (!host || !linkTemplate) {
      return;
    }
    const pins = readPins(panel);
    const pinIds = new Set(pins.map((pin) => pin.id));
    // A link to a pin that was removed goes with it.
    const all = readLinks(panel);
    const links = all.filter((link) => pinIds.has(link.fromPinId) && pinIds.has(link.toPinId));
    if (links.length !== all.length) {
      writeLinks(panel, links);
    }
    if (rowsUpToDate(host, linkRowsKey(panel))) {
      return;
    }
    host.replaceChildren();

    links.forEach((link, index) => {
      const html = linkTemplate.innerHTML.replaceAll("__linkId__", link.id || `l${index}`);
      const row = document.createRange().createContextualFragment(html).querySelector("[data-map-link-row]");
      if (!row) {
        return;
      }
      fillPinSelect(row.querySelector('[data-link-field="fromPinId"]'), pins, link.fromPinId);
      fillPinSelect(row.querySelector('[data-link-field="toPinId"]'), pins, link.toPinId);
      row.querySelector('[data-link-field="label"]').value = link.label || "";
      row.querySelector('[data-link-field="showTraffic"]').checked = link.showTraffic !== false;
      const target = row.querySelector("[data-link-field-target]");
      if (target) {
        target.value = link.targetToken || "";
      }

      const collect = () => {
        const current = readLinks(panel);
        const entry = current.find((candidate) => candidate.id === link.id);
        if (!entry) {
          return;
        }
        entry.fromPinId = row.querySelector('[data-link-field="fromPinId"]').value;
        entry.toPinId = row.querySelector('[data-link-field="toPinId"]').value;
        entry.label = row.querySelector('[data-link-field="label"]').value || null;
        entry.showTraffic = row.querySelector('[data-link-field="showTraffic"]').checked;
        entry.targetToken = row.querySelector("[data-link-field-target]")?.value || null;
        writeLinks(panel, current);
        markRows(host, linkRowsKey(panel));
      };
      row.querySelectorAll("[data-link-field], [data-link-field-target]").forEach((field) => {
        field.addEventListener("change", collect);
        field.addEventListener("input", collect);
      });
      row.querySelector("[data-map-link-remove]")?.addEventListener("click", () => {
        writeLinks(panel, readLinks(panel).filter((candidate) => candidate.id !== link.id));
        renderLinkRows(panel);
      });
      host.appendChild(row);
    });

    const add = panel.querySelector("[data-map-link-add]");
    if (add) {
      add.disabled = pins.length < 2;
      add.title = pins.length < 2 ? "Place two pins first." : "";
    }
    markRows(host, linkRowsKey(panel));
    initializeElementPickers();
  };

  // The rack's devices: one row each, serialised into the tile's RackJson like the pins.
  const rackTemplate = form?.querySelector("template[data-map-rack-template]");

  const readRack = (panel) => {
    const field = panel?.querySelector("[data-map-property-rack]");
    if (!field?.value) {
      return [];
    }
    try {
      const parsed = JSON.parse(field.value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  };

  const writeRack = (panel, devices) => {
    const field = panel?.querySelector("[data-map-property-rack]");
    if (field) {
      field.value = devices.length === 0 ? "" : JSON.stringify(devices);
      field.dispatchEvent(new Event("change", { bubbles: true }));
      queueCanvasRefresh(panel);
    }
  };

  const rackUnitsOf = (panel) => Math.min(52, Math.max(4, Number(panel?.querySelector("[data-map-property-rack-units]")?.value) || 42));

  const renderRackRows = (panel) => {
    const host = panel?.querySelector("[data-map-rack-editor]");
    if (!host || !rackTemplate) {
      return;
    }
    const devices = readRack(panel);
    if (rowsUpToDate(host, rackRowsKey(panel))) {
      return;
    }
    host.replaceChildren();
    devices.forEach((device, index) => {
      const html = rackTemplate.innerHTML.replaceAll("__rackId__", device.id || `r${index}`);
      const row = document.createRange().createContextualFragment(html).querySelector("[data-map-rack-row]");
      if (!row) {
        return;
      }
      const field = (key) => row.querySelector(`[data-rack-field="${key}"]`);
      field("name").value = device.name || "";
      field("icon").value = device.icon || "server";
      field("position").value = device.position || 1;
      field("height").value = device.height || 1;
      const target = row.querySelector("[data-rack-field-target]");
      if (target) {
        target.value = device.targetToken || "";
      }
      const collect = () => {
        const current = readRack(panel);
        const entry = current.find((candidate) => candidate.id === device.id);
        if (!entry) {
          return;
        }
        entry.name = field("name").value;
        entry.icon = field("icon").value || null;
        entry.position = Math.max(1, Number(field("position").value) || 1);
        entry.height = Math.max(1, Number(field("height").value) || 1);
        entry.targetToken = row.querySelector("[data-rack-field-target]")?.value || null;
        writeRack(panel, current);
        markRows(host, rackRowsKey(panel));
      };
      row.querySelectorAll("[data-rack-field], [data-rack-field-target]").forEach((input) => {
        input.addEventListener("change", collect);
        input.addEventListener("input", collect);
      });
      row.querySelector("[data-map-rack-remove]")?.addEventListener("click", () => {
        writeRack(panel, readRack(panel).filter((candidate) => candidate.id !== device.id));
        renderRackRows(panel);
      });
      host.appendChild(row);
    });
    markRows(host, rackRowsKey(panel));
    initializeElementPickers();
  };

  // A new device goes into the lowest free unit, 1U high - stacking from the bottom like a real rack.
  const addRackDevice = (panel) => {
    const devices = readRack(panel);
    const units = rackUnitsOf(panel);
    const taken = new Set();
    devices.forEach((device) => {
      for (let unit = device.position || 1; unit < (device.position || 1) + (device.height || 1); unit += 1) {
        taken.add(unit);
      }
    });
    let position = 1;
    while (taken.has(position) && position < units) {
      position += 1;
    }
    devices.push({ id: createId(), name: "", icon: "server", position, height: 1, targetToken: null });
    writeRack(panel, devices);
    renderRackRows(panel);
  };

  const addLink = (panel) => {
    const pins = readPins(panel);
    if (pins.length < 2) {
      return;
    }
    const links = readLinks(panel);
    // Start from a pair that is not joined yet, newest pins first - usually the device just placed.
    const joined = new Set(links.flatMap((link) => [`${link.fromPinId}|${link.toPinId}`, `${link.toPinId}|${link.fromPinId}`]));
    let pair = [pins[pins.length - 2], pins[pins.length - 1]];
    search:
    for (let to = pins.length - 1; to > 0; to -= 1) {
      for (let from = to - 1; from >= 0; from -= 1) {
        if (!joined.has(`${pins[from].id}|${pins[to].id}`)) {
          pair = [pins[from], pins[to]];
          break search;
        }
      }
    }
    links.push({
      id: createId(),
      fromPinId: pair[0].id,
      toPinId: pair[1].id,
      targetToken: null,
      label: null,
      showTraffic: true
    });
    writeLinks(panel, links);
    renderLinkRows(panel);
  };

  const isGeoPanel = (panel) =>
    normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value) === "GeoMap";

  const targetTemplate = form?.querySelector("[data-map-target-template]");

  const readTargets = (panel) => (panel?.querySelector("[data-map-targets-value]")?.value || "")
    .split(/[\n\r,;]+/)
    .map((token) => token.trim())
    .filter(Boolean);

  const writeTargets = (panel, tokens) => {
    const field = panel?.querySelector("[data-map-targets-value]");
    if (!field) {
      return;
    }
    // De-duplicated on write, so two rows pointing at the same host cannot become two identical lines.
    field.value = [...new Set(tokens.map((token) => token.trim()).filter(Boolean))].join("\n");
  };

  // A target change has to re-run the tile's server render, or the canvas keeps showing the old series.
  const refreshTargetedTile = (panel) => {
    const tile = getTile(panel?.dataset.tileIndex || "");
    if (tile) {
      structureCache.delete(tile);
      // syncTileFromPanel takes the PANEL, not (tile, panel) - passing the tile made every field lookup
      // inside it return null, so the re-render asked the server for a default Element tile.
      syncTileFromPanel(panel);
    }
  };

  // The extra targets of a multi-series widget. Rendered from the ONE hidden field rather than bound as
  // indexed inputs: the designer clones whole tiles by rewriting field names, and a nested indexed collection
  // would have to be re-indexed on every clone and delete.
  //
  // There is always one empty row at the end - that IS the "add" affordance, so there is no button whose
  // only job is to create a row you then have to fill in anyway.
  const renderTargetRows = (panel) => {
    const host = panel?.querySelector("[data-map-targets-editor]");
    if (!host || !targetTemplate) {
      return;
    }

    const tokens = readTargets(panel);
    if (rowsUpToDate(host, targetRowsKey(panel))) {
      return;
    }
    host.replaceChildren();

    [...tokens, ""].forEach((token, index) => {
      const isNew = index === tokens.length;
      // Unique picker id per row - a duplicated DOM id would make one row's picker drive another's value.
      const html = targetTemplate.innerHTML.replaceAll(
        "__rowId__",
        `${panel.dataset.tileIndex || "t"}-${index}-${Math.random().toString(36).slice(2, 8)}`);
      const row = document.createRange().createContextualFragment(html).querySelector("[data-map-target-row]");
      if (!row) {
        return;
      }

      const value = row.querySelector("[data-map-target-value]");
      if (value) {
        value.value = token;
        value.addEventListener("change", () => {
          const current = readTargets(panel);
          if (isNew) {
            current.push(value.value);
          } else {
            current[index] = value.value;
          }
          writeTargets(panel, current);
          renderTargetRows(panel);
          refreshTargetedTile(panel);
        });
      }

      const remove = row.querySelector("[data-map-target-remove]");
      if (remove) {
        // Nothing to remove on the trailing invitation row.
        remove.hidden = isNew;
        remove.addEventListener("click", () => {
          const current = readTargets(panel);
          current.splice(index, 1);
          writeTargets(panel, current);
          renderTargetRows(panel);
          refreshTargetedTile(panel);
        });
      }

      host.appendChild(row);
    });

    markRows(host, targetRowsKey(panel));
    initializeElementPickers();
  };

  const renderPinRows = (panel) => {
    const host = panel?.querySelector("[data-map-pin-editor]");
    if (!host || !pinTemplate) {
      return;
    }

    const pins = readPins(panel);
    const geo = isGeoPanel(panel);
    // Every pin needs an id to be the end of a link (an older pin may predate that).
    if (pins.some((pin) => !pin.id)) {
      pins.forEach((pin) => { pin.id = pin.id || createId(); });
      writePins(panel, pins);
    }
    if (rowsUpToDate(host, pinRowsKey(panel))) {
      return;
    }
    host.replaceChildren();

    pins.forEach((pin, index) => {
      // Rewrite the picker's DOM id per pin - a duplicated id would make the picker in one row drive
      // another row's hidden value.
      const html = pinTemplate.innerHTML.replaceAll("__pinId__", pin.id || `p${index}`);
      const row = document.createRange().createContextualFragment(html).querySelector("[data-map-pin-row]");
      if (!row) {
        return;
      }

      row.querySelector('[data-pin-field="label"]').value = pin.label || "";
      row.querySelector('[data-pin-field="style"]').value = pin.style || "Dot";
      const iconField = row.querySelector('[data-pin-field="icon"]');
      const syncDevice = () => {
        if (iconField) {
          iconField.hidden = row.querySelector('[data-pin-field="style"]').value !== "Device";
        }
      };
      if (iconField) {
        iconField.value = pin.icon || "server";
      }
      syncDevice();
      const mapField = row.querySelector('[data-pin-field="linkMapId"]');
      if (mapField) {
        mapField.value = pin.linkMapId || "";
      }
      const target = row.querySelector("[data-pin-field-target]");
      if (target) {
        target.value = pin.targetToken || "";
      }
      const geoBox = row.querySelector("[data-map-pin-geo]");
      if (geoBox) {
        geoBox.hidden = !geo;
        row.querySelector('[data-pin-field="latitude"]').value = pin.latitude ?? "";
        row.querySelector('[data-pin-field="longitude"]').value = pin.longitude ?? "";
      }

      const collect = () => {
        const current = readPins(panel);
        const entry = current[index];
        if (!entry) {
          return;
        }
        entry.label = row.querySelector('[data-pin-field="label"]').value;
        entry.style = row.querySelector('[data-pin-field="style"]').value;
        entry.icon = iconField?.value || null;
        entry.linkMapId = mapField?.value || null;
        syncDevice();
        entry.targetToken = row.querySelector("[data-pin-field-target]")?.value || null;
        if (geo) {
          const lat = Number(row.querySelector('[data-pin-field="latitude"]').value);
          const lon = Number(row.querySelector('[data-pin-field="longitude"]').value);
          entry.latitude = Number.isFinite(lat) ? lat : null;
          entry.longitude = Number.isFinite(lon) ? lon : null;
        }
        writePins(panel, current);
        // The rows already show this - the refresh the write queues must not rebuild them under the cursor.
        markRows(host, pinRowsKey(panel));
        refreshLinkPinOptions(panel);
      };

      row.querySelectorAll("[data-pin-field], [data-pin-field-target]").forEach((field) => {
        field.addEventListener("change", collect);
        field.addEventListener("input", collect);
      });
      row.querySelector("[data-map-pin-remove]")?.addEventListener("click", () => {
        const current = readPins(panel);
        current.splice(index, 1);
        writePins(panel, current);
        renderPinRows(panel);
        renderLinkRows(panel);
      });

      host.appendChild(row);
    });

    const hint = panel.querySelector("[data-map-pin-hint]");
    if (hint) {
      hint.textContent = geo
        ? "Enter latitude/longitude, or drag the pin on the map - the world map is equirectangular, so a pin lands exactly on its coordinates."
        : 'Use "Place on picture" and then click the tile where the pin belongs. Drag a pin on the tile to move it.';
    }
    const placeButton = panel.querySelector("[data-map-pin-place]");
    if (placeButton) {
      placeButton.hidden = geo;
      setButtonLabel(placeButton, isTopologyPanel(panel) ? "Place on canvas" : "Place on picture");
    }
    const addButton = panel.querySelector("[data-map-pin-add]");
    if (addButton) {
      setButtonLabel(addButton, isTopologyPanel(panel) ? "Add device" : "Add pin");
    }
    markRows(host, pinRowsKey(panel));
    initializeElementPickers();
  };

  const setButtonLabel = (button, text) => {
    const label = button.querySelector("span");
    if (label) {
      label.textContent = text;
    }
  };

  const isTopologyPanel = (panel) =>
    normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value) === "Topology";

  const addPin = (panel, x, y) => {
    const pins = readPins(panel);
    const topology = isTopologyPanel(panel);
    pins.push({
      id: createId(),
      label: "",
      showLabel: true,
      // A topology's nodes are devices; a picture's pins start as dots.
      style: topology ? "Device" : "Dot",
      icon: topology ? "server" : null,
      linkMapId: null,
      targetToken: null,
      x: x ?? 0.5,
      y: y ?? 0.5,
      latitude: null,
      longitude: null
    });
    writePins(panel, pins);
    renderPinRows(panel);
    renderLinkRows(panel);
  };

  // "Place on picture" arms the tile's canvas for ONE click. The canvas is inside the tile, whose whole
  // surface is the drag handle, so the armed listener runs in the capture phase and stops the event before
  // the drag logic sees it.
  let placementCleanup = null;
  const armPinPlacement = (panel) => {
    placementCleanup?.();
    const index = panel.dataset.tileIndex;
    const canvas2 = getTile(index)?.querySelector("[data-map-pin-canvas]");
    if (!canvas2) {
      MatUi.alert("Upload a picture first.", { title: "No picture yet" });
      return;
    }

    canvas2.classList.add("is-placing");
    const onClick = (event) => {
      event.preventDefault();
      event.stopPropagation();
      const rect = canvas2.getBoundingClientRect();
      addPin(panel, (event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height);
      placementCleanup?.();
    };
    const onPointerDown = (event) => {
      event.preventDefault();
      event.stopPropagation();
    };
    canvas2.addEventListener("click", onClick, true);
    canvas2.addEventListener("pointerdown", onPointerDown, true);
    placementCleanup = () => {
      canvas2.classList.remove("is-placing");
      canvas2.removeEventListener("click", onClick, true);
      canvas2.removeEventListener("pointerdown", onPointerDown, true);
      placementCleanup = null;
    };
  };

  propertyHost?.addEventListener("change", (event) => {
    const units = event.target.closest?.("[data-map-property-rack-units]");
    if (units) {
      queueCanvasRefresh(units.closest("[data-map-property-panel]"));
    }
  });

  propertyHost?.addEventListener("click", (event) => {
    const panel = event.target.closest("[data-map-property-panel]");
    if (!panel) {
      return;
    }
    if (event.target.closest("[data-map-pin-add]")) {
      addPin(panel, 0.5, 0.5);
    } else if (event.target.closest("[data-map-link-add]")) {
      addLink(panel);
    } else if (event.target.closest("[data-map-rack-add]")) {
      addRackDevice(panel);
    } else if (event.target.closest("[data-map-pin-place]")) {
      armPinPlacement(panel);
    } else if (event.target.closest("[data-map-image-clear]")) {
      const field = panel.querySelector("[data-map-property-image-id]");
      if (field) {
        field.value = "";
        field.dispatchEvent(new Event("change", { bubbles: true }));
      }
    }
  });

  // Uploaded via fetch, not a form post: a full post would store the picture and throw away every unsaved
  // tile position on the canvas.
  propertyHost?.addEventListener("change", async (event) => {
    const input = event.target.closest("[data-map-image-file]");
    if (!input || !input.files?.length) {
      return;
    }
    const panel = input.closest("[data-map-property-panel]");
    const status = panel?.querySelector("[data-map-image-status]");
    const body = new FormData();
    body.append("file", input.files[0]);
    const token = form?.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (status) {
      status.textContent = "Uploading…";
    }

    try {
      const response = await fetch(`${location.pathname}?handler=UploadImage`, {
        method: "POST",
        headers: token ? { RequestVerificationToken: token } : {},
        body
      });
      const result = await response.json();
      if (result.id) {
        const field = panel.querySelector("[data-map-property-image-id]");
        if (field) {
          field.value = result.id;
          field.dispatchEvent(new Event("change", { bubbles: true }));
        }
        if (status) {
          status.textContent = "Uploaded. Save the map to keep it.";
        }
      } else if (status) {
        status.textContent = result.error || "Upload failed.";
      }
    } catch {
      if (status) {
        status.textContent = "Upload failed.";
      }
    } finally {
      input.value = "";
    }
  });

  const syncPanelVisibility = (panel) => {
    if (!panel) {
      return;
    }

    const kind = normalizeKind(panel.querySelector("[data-map-property-kind]")?.value || "Element");
    const isText = kind === "Text" || kind === "Heading";
    const isGraph = kind === "Graph";
    // A table orders and limits its rows exactly like a sensor list.
    const isList = kind === "SensorList" || kind === "Table" || kind === "Bars";
    const isEmbed = kind === "Embed";
    panel.querySelectorAll("[data-map-property-embed-only]").forEach((field) => { field.hidden = !isEmbed; });
    const isRack = kind === "Rack";
    panel.querySelectorAll("[data-map-property-rack-only]").forEach((field) => { field.hidden = !isRack; });
    if (isRack) {
      renderRackRows(panel);
    }
    const isRows = isList || kind === "AlertFeed";
    const isSla = kind === "Sla";
    const isSeries = kind === "MultiGraph";
    // A topology may have a picture behind it (a floorplan, a rack photo) - optional, unlike an Image tile.
    const isImage = kind === "Image" || kind === "Topology";
    const isPinned = isImage || kind === "GeoMap";
    // A clock has no target at all; a heading and a text tile carry their own copy instead of one.
    // An image / world map has no single target of its own - each PIN carries one.
    const isTargetless = kind === "Clock" || kind === "Text" || kind === "Heading"
      || kind === "Image" || kind === "GeoMap" || kind === "Topology" || kind === "Embed" || kind === "Rack";
    const targetField = panel.querySelector("[data-map-property-target]");
    const visualField = panel.querySelector("[data-map-property-visual]");
    const textField = panel.querySelector("[data-map-property-text-only]");
    const graphFields = panel.querySelectorAll("[data-map-property-graph-only]");
    const hint = panel.querySelector("[data-map-property-hint]");
    if (targetField) {
      targetField.hidden = isTargetless;
    }
    // Channel + scale belong to the tiles that show ONE number: value, gauge and progress are all Value.
    const isValue = kind === "Value";
    panel.querySelectorAll("[data-map-property-value-only]").forEach((field) => { field.hidden = !isValue; });
    panel.querySelectorAll("[data-map-property-list-only]").forEach((field) => { field.hidden = !isList; });
    const isTimeline = kind === "Timeline";
    // A timeline has rows (how many sensors) and a time window; a honeycomb only its target.
    panel.querySelectorAll("[data-map-property-rows-only]").forEach((field) => { field.hidden = !(isRows || isTimeline); });
    panel.querySelectorAll("[data-map-property-sla-only]").forEach((field) => { field.hidden = !isSla; });
    panel.querySelectorAll("[data-map-property-series-only]").forEach((field) => { field.hidden = !isSeries; });
    if (isSeries) {
      renderTargetRows(panel);
    }
    panel.querySelectorAll("[data-map-property-image-only]").forEach((field) => { field.hidden = !isImage; });
    panel.querySelectorAll("[data-map-property-pins-only]").forEach((field) => { field.hidden = !isPinned; });
    if (isPinned) {
      renderPinRows(panel);
      renderLinkRows(panel);
    }
    if (visualField) {
      visualField.hidden = isText || isGraph || isSeries || isRows || isSla || isPinned || kind === "Clock"
        || kind === "Honeycomb" || kind === "Timeline" || kind === "Donut" || kind === "Embed" || kind === "Rack";
    }
    if (textField) {
      textField.hidden = !isText;
    }
    // ALL graph-only fields (type and window) - a single querySelector toggled only the first.
    graphFields.forEach((field) => {
      field.hidden = !(isGraph || (isTimeline && field.querySelector('[name$=".GraphWindowHours"]')));
    });
    syncDuplicateFields(panel);
    if (hint) {
      const limits = getLimits(kind);
      hint.textContent = `${kindHints[kind] || "Select a target and place the tile on the canvas."} Drag to move, resize from the bottom-right corner. Minimum size: ${limits.minColumns} x ${limits.minRows} cells.`;
    }
  };

  // --- Live tile preview (real value / state / graph while designing) ---
  const previewKeyCache = new WeakMap();
  const previewTimers = new WeakMap();

  const previewKeyFor = (panel) => {
    const token = (panel.querySelector("[data-map-property-element]")?.value || "").trim();
    const kind = normalizeKind(panel.querySelector("[data-map-property-kind]")?.value || "Element");
    const visual = (panel.querySelector("[data-map-property-visual-type]")?.value || "Card").trim() || "Card";
    const graphType = (panel.querySelector("[data-map-property-graph-type]")?.value || "Line").trim() || "Line";
    return { token, kind, visual, graphType, key: [token, kind, visual, graphType].join("|") };
  };

  // Updates the REAL tile hooks (shared with the display partial _MapTile.cshtml - the mock system that
  // used to live here was deleted). A just-added tile has no target/value yet, so none of these elements
  // exist until the tile is actually saved and reloaded with a resolved preview - each update below is a
  // no-op until then, which is fine: the state/value/graph fragments are Phase C's live-JSON job anyway.
  const applyLivePreview = (tile, data) => {
    if (!tile || !data) {
      return;
    }
    if (data.stateKey) {
      tile.dataset.state = data.stateKey;
    }
    const stateLabelEl = tile.querySelector("[data-tile-state-label]");
    if (stateLabelEl) {
      if (data.stateKey) {
        setMapTilePillTone(stateLabelEl, data.stateKey);
      }
      if (data.stateLabel) {
        stateLabelEl.textContent = data.stateLabel;
      }
    }
    const valueEl = tile.querySelector("[data-tile-value]");
    if (valueEl) {
      valueEl.textContent = data.hasValue ? data.value : "—";
    }
    const subtitleEl = tile.querySelector("[data-tile-subtitle]");
    if (subtitleEl && data.subtitle !== undefined && data.subtitle !== null) {
      subtitleEl.textContent = data.subtitle;
    }
    const pct = (data.progressPercent === null || data.progressPercent === undefined)
      ? null
      : Math.round(data.progressPercent);
    if (pct !== null) {
      const progressEl = tile.querySelector("[data-tile-progress]");
      if (progressEl) {
        progressEl.style.setProperty("--map-progress", pct);
        const strong = progressEl.querySelector("strong");
        if (strong) {
          strong.textContent = pct;
        }
      }
    }
    const progressLabelEl = tile.querySelector("[data-tile-progress-label]");
    if (progressLabelEl && data.progressLabel) {
      progressLabelEl.textContent = data.progressLabel;
    }
    if (data.graphLinePath) {
      const path = tile.querySelector("[data-tile-graph-line]");
      if (path) {
        path.setAttribute("d", data.graphLinePath);
      }
    }
  };

  // Mirrors MapDisplayProvider.IsCollectionKind - these render entirely from the server-side partial.
  const CollectionTileKinds = new Set(["SensorList", "AlertFeed", "Sla", "Clock", "Heading", "Image", "GeoMap", "MultiGraph", "Topology", "Honeycomb", "Timeline", "Table", "Bars", "Donut", "Embed", "Rack"]);

  const refreshTilePreview = (tile, panel) => {
    if (!tile || !panel) {
      return;
    }
    const info = previewKeyFor(panel);
    if (previewKeyCache.get(tile) === info.key) {
      return; // unchanged target/kind/visual - nothing new to fetch (title/colour edits skip)
    }
    previewKeyCache.set(tile, info.key);
    if (info.kind === "Text" || !info.token) {
      return; // no live value to resolve; keep the placeholder mock
    }
    // The cheap JSON preview carries ONE value and ONE subtitle, which is all a single-element tile has. A
    // collection widget's content is built from its own options (targets, channel, limit, window) that this
    // endpoint knows nothing about, so its answer for them is an element name where the tile had computed
    // something better - it overwrote a multi-graph's "3 sensors - last 24h" with the probe's name. Those
    // kinds are rendered in full by the TileMarkup partial anyway, so there is nothing here to add.
    if (CollectionTileKinds.has(info.kind)) {
      return;
    }
    const url = `${window.location.pathname}?handler=TilePreview`
      + `&token=${encodeURIComponent(info.token)}`
      + `&kind=${encodeURIComponent(info.kind)}`
      + `&visualType=${encodeURIComponent(info.visual)}`
      + `&graphType=${encodeURIComponent(info.graphType)}`;
    clearTimeout(previewTimers.get(tile));
    previewTimers.set(tile, setTimeout(() => {
      fetch(url, { headers: { Accept: "application/json" } })
        .then((response) => (response.ok ? response.json() : null))
        .then((data) => {
          // Only apply if this is still the current target for the tile (avoid a stale race).
          if (data && previewKeyCache.get(tile) === info.key) {
            applyLivePreview(tile, data);
          }
        })
        .catch(() => {});
    }, 200));
  };

  // Which parts of a tile change its STRUCTURE rather than its value. A value comes from the cheap JSON
  // preview; a structure change means the markup itself is different (a gauge has an arc, a list has rows),
  // and the only honest source for that is the same partial the viewer renders.
  const structureKeyFor = (panel) => [
    // Pins, links and the map a tile opens are drawn by the server render too.
    panel.querySelector("[data-map-property-pins]")?.value || "",
    panel.querySelector("[data-map-property-links]")?.value || "",
    panel.querySelector('[name$=".LinkMapId"]')?.value || "",
    panel.querySelector("[data-map-property-rack]")?.value || "",
    panel.querySelector("[data-map-property-rack-units]")?.value || "",
    // The series inputs change WHAT the tile is made of, not just a value, so they belong in the key that
    // decides whether the body has to be re-fetched.
    panel.querySelector("[data-map-targets-value]")?.value || "",
    panel.querySelector('[name$=".ListLimit"]:not(:disabled)')?.value || "",
    panel.querySelector('[name$=".ListChannelKey"]:not(:disabled)')?.value || "",
    panel.querySelector('[name$=".GraphWindowHours"]:not(:disabled)')?.value || "",
    normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || "Element"),
    panel?.querySelector("[data-map-property-visual-type]")?.value || "",
    panel?.querySelector("[data-map-property-graph-type]")?.value || "",
    panel?.querySelector("[data-map-property-image-id]")?.value || "",
    panel?.querySelector('[name$=".ChannelKey"]')?.value || "",
    panel?.querySelector('[name$=".GaugeMin"]')?.value || "",
    panel?.querySelector('[name$=".GaugeMax"]')?.value || "",
    (panel?.querySelector("[data-map-property-element]")?.value || "") === "" ? "no-target" : "target"
  ].join("|");

  const structureCache = new WeakMap();

  const refreshTileBody = (tile, panel) => {
    if (!tile || !panel) {
      return;
    }
    const key = structureKeyFor(panel);
    if (structureCache.get(tile) === key) {
      return;
    }
    structureCache.set(tile, key);

    const index = tile.dataset.tileIndex || "0";
    const params = new URLSearchParams({
      handler: "TileMarkup",
      index,
      id: tile.querySelector(`[name="Input.Tiles[${index}].Id"]`)?.value || "",
      slideId: tile.dataset.slideId || "",
      token: panel.querySelector("[data-map-property-element]")?.value || "",
      kind: normalizeKind(panel.querySelector("[data-map-property-kind]")?.value || "Element"),
      visualType: panel.querySelector("[data-map-property-visual-type]")?.value || "Card",
      graphType: panel.querySelector("[data-map-property-graph-type]")?.value || "Line",
      title: panel.querySelector("[data-map-property-title]")?.value || "",
      columnSpan: panel.querySelector("[data-map-tile-column-span]")?.value || "3",
      rowSpan: panel.querySelector("[data-map-tile-row-span]")?.value || "2",
      order: tile.querySelector("[data-map-tile-order]")?.value || "0",
      // The server render writes the placement into the replacement's style, so it has to know it - without
      // these the tile came back unplaced and jumped to wherever auto-placement put it.
      column: tile.querySelector("[data-map-tile-column]")?.value || "0",
      row: tile.querySelector("[data-map-tile-row]")?.value || "0",
      imageAssetId: panel.querySelector("[data-map-property-image-id]")?.value || "",
      imageFit: panel.querySelector("[data-map-property-image-fit]")?.value || "Contain",
      channelKey: panel.querySelector('[name$=".ChannelKey"]')?.value || "",
      gaugeMin: panel.querySelector('[name$=".GaugeMin"]')?.value || "",
      gaugeMax: panel.querySelector('[name$=".GaugeMax"]')?.value || "",
      listLimit: panel.querySelector('[name$=".ListLimit"]:not(:disabled)')?.value || "",
      listChannelKey: panel.querySelector('[name$=".ListChannelKey"]:not(:disabled)')?.value || "",
      graphWindowHours: panel.querySelector('[name$=".GraphWindowHours"]:not(:disabled)')?.value || "24",
      targets: panel.querySelector("[data-map-targets-value]")?.value || "",
      pins: panel.querySelector("[data-map-property-pins]")?.value || "",
      links: panel.querySelector("[data-map-property-links]")?.value || "",
      linkMapId: panel.querySelector('[name$=".LinkMapId"]')?.value || "",
      rack: panel.querySelector("[data-map-property-rack]")?.value || "",
      rackUnits: panel.querySelector("[data-map-property-rack-units]")?.value || "42"
    });
    // A POST: a topology's pins and links would outgrow a query string.
    params.delete("handler");
    const antiforgery = form?.querySelector('input[name="__RequestVerificationToken"]')?.value;

    fetch(`${window.location.pathname}?handler=TileMarkup`, {
      method: "POST",
      headers: antiforgery ? { Accept: "text/html", RequestVerificationToken: antiforgery } : { Accept: "text/html" },
      body: params
    })
      .then((response) => (response.ok ? response.text() : null))
      .then((html) => {
        // Only apply if the tile still wants this structure - a fast kind change must not be overwritten by
        // the answer to the previous one.
        if (!html || structureCache.get(tile) !== key || !tile.isConnected) {
          return;
        }
        const replacement = document.createRange().createContextualFragment(html).querySelector("[data-map-tile]");
        if (!replacement) {
          return;
        }
        // The replacement is a fresh server render and knows nothing about this tile's editor state - which
        // slide filter hid it, whether it is pending deletion, whether it is the selected one. Carry all three
        // across, or a refresh un-hides every tile on the board including the other slides'.
        const wasSelected = tile.classList.contains("is-selected");
        replacement.hidden = tile.hidden;
        const deletedBefore = tile.querySelector("[data-map-tile-deleted]")?.value;
        const deletedAfter = replacement.querySelector("[data-map-tile-deleted]");
        if (deletedAfter && deletedBefore) {
          deletedAfter.value = deletedBefore;
        }
        tile.replaceWith(replacement);
        structureCache.set(replacement, key);
        setupTile(replacement);
        if (wasSelected) {
          replacement.classList.add("is-selected");
        }
      })
      .catch(() => {});
  };

  const syncTileFromPanel = (panel) => {
    if (!panel) {
      return;
    }

    const tile = getTile(panel.dataset.tileIndex || "");
    if (!tile) {
      return;
    }

    const title = panel.querySelector("[data-map-property-title]")?.value || "Tile";
    const kind = normalizeKind(panel.querySelector("[data-map-property-kind]")?.value || "Element");
    const elementSelect = panel.querySelector("[data-map-property-element]");
    const text = panel.querySelector("[data-map-property-text]")?.value || "";
    const subtitle = tile.querySelector("[data-tile-subtitle]");
    const titleElement = tile.querySelector("[data-tile-title]");
    const showTitle = panel.querySelector("[data-map-property-show-title]")?.checked ?? true;
    // Lower-cased to match what the server renders (_MapTile writes Kind.ToString().ToLowerInvariant()).
    // Two spellings of the same attribute depending on how the tile got there is a trap for any future
    // CSS/JS that selects on it.
    tile.dataset.kind = String(kind).toLowerCase();
    if (titleElement) {
      titleElement.hidden = !showTitle;
      titleElement.replaceChildren(document.createTextNode(title));
    }
    // A collection widget computes its own subtitle server-side ("3 sensors - last 24h"), and the whole
    // tile is re-rendered from that partial - so writing a provisional element name here would simply be
    // wrong until the render lands, and stays wrong now that the cheap JSON preview is skipped for them.
    if (subtitle && !CollectionTileKinds.has(String(kind))) {
      const isText = kind === "Text";
      // The target is an element picker: its name lives on the hidden value input's data-selected-name (set
      // when chosen / server-rendered), and a resolved live preview replaces it via applyLivePreview.
      const selectedText = (elementSelect?.dataset.selectedName || "").trim();
      subtitle.textContent = isText
        ? (text.trim() || "Text tile")
        : (selectedText || "No target selected");
    }

    // The properties header card names what you are editing, so it has to follow the same title/kind/icon
    // the tile shows - otherwise the aside can end up claiming to edit a different widget.
    const headTitle = panel.querySelector("[data-map-property-head-title]");
    if (headTitle) {
      headTitle.textContent = title.trim() || getKindLabel(kind);
    }
    const headKind = panel.querySelector("[data-map-property-head-kind]");
    if (headKind) {
      headKind.textContent = getKindLabel(kind);
    }
    const headIcon = panel.querySelector("[data-map-property-head-icon]");
    const iconGlyph = panel.querySelector("[data-icon-picker-trigger-glyph]");
    if (headIcon && iconGlyph) {
      headIcon.replaceChildren(...Array.from(iconGlyph.childNodes).map((node) => node.cloneNode(true)));
    }

    const showCard = panel.querySelector("[data-map-property-show-card]")?.checked ?? true;
    tile.classList.toggle("is-plain", !showCard);

    syncPanelVisibility(panel);
    // Changing the kind can empty a whole property group (a Clock has no target), so the tab bar has to be
    // re-evaluated with it - otherwise the Data tab stays on screen pointing at nothing.
    if (!panel.hidden) {
      applyPropertyTab(panel);
    }
    refreshTileBody(tile, panel);
    syncTileImage(tile, panel);
    applyTileAppearance(tile, panel);
    applyTileSize(tile);
    refreshTilePreview(tile, panel);
  };

  // Which General/Data/Display group of the SELECTED tile panel is visible. The bar is shared by all tile
  // panels (see MapEditor.cshtml), so this is designer state, not per-panel state.
  const applyPropertyTab = (panel) => {
    // Every panel carries every field and hides the ones its kind has no use for, so a whole GROUP can end up
    // empty - a Clock has nothing to point at, a Heading has no data at all. A tab that opens onto nothing is
    // a dead end, so it is hidden, and the selection falls back to General rather than landing there.
    const groupHasFields = (name) => {
      const group = panel?.querySelector(`[data-map-prop-group="${name}"]`);
      if (!group) {
        return false;
      }
      return Array.from(group.children).some((child) => !child.hidden
        && !(child.tagName === "INPUT" && child.type === "hidden"));
    };

    let tab = activePropertyTab;
    if (panel && !groupHasFields(tab)) {
      tab = "general";
    }

    panel?.querySelectorAll("[data-map-prop-group]").forEach((group) => {
      group.hidden = group.dataset.mapPropGroup !== tab;
    });
    propertyTabs?.querySelectorAll("[data-map-property-tab]").forEach((button) => {
      const name = button.dataset.mapPropertyTab;
      const isActive = name === tab;
      button.hidden = panel ? !groupHasFields(name) : false;
      button.classList.toggle("is-active", isActive);
      // Toggle buttons, not role="tab" (see MapEditor.cshtml), so the state is aria-pressed.
      button.setAttribute("aria-pressed", isActive ? "true" : "false");
    });
  };

  // The board's own settings live in the Display/Settings TABS now, so the right-hand column has just two
  // states: a widget is selected, or nothing is. That is what "Properties" means on the mockup.
  const setPropertyScope = (scope, label) => {
    if (propertyTabs) {
      propertyTabs.hidden = scope !== "tile";
    }
    if (scopeLabel) {
      scopeLabel.textContent = label;
    }
    mapSelectButton?.classList.toggle("is-selected", scope === "map");
  };

  const selectTile = (index) => {
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      tile.classList.toggle("is-selected", tile.dataset.tileIndex === String(index) && !tile.hidden);
    });

    let activePanel = null;
    propertyHost?.querySelectorAll("[data-map-property-panel]").forEach((panel) => {
      const isActive = panel.dataset.tileIndex === String(index);
      panel.hidden = !isActive;
      if (isActive) {
        activePanel = panel;
        syncPanelVisibility(panel);
      }
    });

    setPropertyScope("tile", activePanel
      ? (activePanel.querySelector("[data-map-property-kind-label]")?.textContent?.trim() || "Widget")
      : "Properties");
    applyPropertyTab(activePanel);

    if (propertyEmpty) {
      propertyEmpty.hidden = Boolean(activePanel);
    }
  };

  const selectMap = () => {
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      tile.classList.remove("is-selected");
    });

    propertyHost?.querySelectorAll("[data-map-property-panel]").forEach((panel) => {
      panel.hidden = true;
    });

    if (propertyEmpty) {
      propertyEmpty.hidden = false;
    }

    setPropertyScope("map", "Properties");
    syncMapSummary();
  };

  // Slide properties are NOT model-bound - the designer owns the slide list and re-renders the hidden
  // Input.Slides[..] inputs from it, so the panel reads and writes that JS model directly.
  // The Slides tab shows the slide list and, under it, "Selected slide" - so the fields have to follow the
  // ACTIVE slide, not only a click on a row's gear. Filling them is therefore separate from selecting: this
  // half runs on every slide change, the other half also moves the aside and the tab.
  const fillSlidePanel = () => {
    const slide = slides.find((candidate) => candidate.id === activeSlideId);
    if (!slide || !slidePanel) {
      return;
    }
    slidePanel.querySelectorAll("[data-map-slide-field]").forEach((field) => {
      const key = field.dataset.mapSlideField;
      if (field.type === "checkbox") {
        field.checked = slide[key] !== false;
      } else {
        field.value = slide[key] ?? "";
      }
    });
  };

  const selectSlide = () => {
    if (!slides.some((candidate) => candidate.id === activeSlideId) || !slidePanel) {
      return;
    }

    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      tile.classList.remove("is-selected");
    });
    propertyHost?.querySelectorAll("[data-map-property-panel]").forEach((panel) => {
      panel.hidden = true;
    });
    if (propertyEmpty) {
      propertyEmpty.hidden = true;
    }

    fillSlidePanel();

    // The panel lives in the Slides TAB now, so "edit this slide" has to take you there (a click on the MatUi
    // tab, so its selection, its hash and its panel all follow).
    const slidesTab = form?.querySelector("#tab-slides");
    if (slidesTab && slidesTab.getAttribute("aria-selected") !== "true") {
      slidesTab.click();
    }
  };

  // Deletes whatever tile currently carries this index - looked up at call time, because the tile ELEMENT is
  // replaced each time the server hands back a new body and a captured reference would go stale.
  const removeTileByIndex = (index) => {
    const tile = getTile(index);
    const panel = getPanel(index);
    const deleted = tile?.querySelector("[data-map-tile-deleted]");
    if (deleted) {
      deleted.value = "true";
    }
    if (tile) {
      tile.hidden = true;
    }
    panel?.setAttribute("hidden", "hidden");
    // Removing a tile leaves a hole in the order; the flow has to close up behind it.
    commitPlacement();
    selectMap();
  };

  // Everything bound to the tile ELEMENT. Re-run for every replacement body.
  const wireTileElement = (tile) => {
    const index = tile.dataset.tileIndex || "";
    tile.addEventListener("click", () => selectTile(index));
    tile.querySelector("[data-map-remove-tile]")?.addEventListener("click", (event) => {
      event.stopPropagation();
      removeTileByIndex(index);
    });
    wireTileDrag(tile);
  };

  const setupTile = (tile) => {
    applyTileSize(tile);
    const panel = getPanel(tile.dataset.tileIndex || "");

    // The PANEL is wired once. setupTile runs again for every replacement tile body, and binding the panel's
    // inputs a second time would fire syncTileFromPanel twice per keystroke - and three times after the next
    // swap. The tile's own listeners are safe to re-bind: that element is new each time.
    if (panel?.dataset.wired !== "1") {
      if (panel) {
        panel.dataset.wired = "1";
      }
      panel?.querySelectorAll("[data-map-tile-column-span], [data-map-tile-row-span]").forEach((input) => {
        input.addEventListener("input", () => {
          const current = getTile(panel.dataset.tileIndex || "");
          if (current) {
            applyTileSize(current);
          }
        });
      });
      panel?.querySelectorAll("[data-map-property-title], [data-map-property-kind], [data-map-property-visual-type], [data-map-property-element], [data-map-property-text], [data-map-property-graph-type], [data-map-property-background], [data-map-property-accent], [data-map-property-text-color], [data-map-property-show-title], [data-map-property-show-badge], [data-map-property-show-card], [data-map-property-icon], [data-map-property-image-id], [data-map-property-image-fit], [data-map-property-value-only] input").forEach((input) => {
        input.addEventListener("input", () => syncTileFromPanel(panel));
        input.addEventListener("change", () => syncTileFromPanel(panel));
      });
      // Same action as the tile's own X - one function, so the two can never drift apart.
      panel?.querySelector("[data-map-property-remove]")?.addEventListener("click", (event) => {
        event.preventDefault();
        removeTileByIndex(panel.dataset.tileIndex || "");
      });
      setupColorRules(panel);
    }

    // The server already rendered each saved tile's real preview and its real BODY, so seed both caches to
    // skip an identical refetch on load - a board with twenty widgets would otherwise fire twenty markup
    // requests the moment it opens, for markup it already has. Only a stub straight out of the <template>
    // (marked by addTile) is missing its real body.
    if (panel) {
      previewKeyCache.set(tile, previewKeyFor(panel).key);
      if (tile.dataset.stub !== "1") {
        structureCache.set(tile, structureKeyFor(panel));
      }
    }

    wireTileElement(tile);
    syncTileFromPanel(panel);

  };

  // Moving a placed pin: drag it on the canvas. Its point is a fraction of the layer its percentages refer to
  // (the pin's offset parent), so that is what the pointer is measured against - right at any tile size and under
  // the world map's letterboxing. On a world map the fraction goes back to latitude/longitude (the inverse of
  // MonitoringMapGeoProjection.ToFraction), on a picture or topology it IS the stored position.
  const dragPin = (tile, marker, startEvent) => {
    const panel = getPanel(tile.dataset.tileIndex || "");
    const frame = marker.offsetParent;
    if (!panel || !frame) {
      return;
    }
    const startX = startEvent.clientX;
    const startY = startEvent.clientY;
    let moved = false;
    let fraction = null;

    const move = (moveEvent) => {
      if (!moved && Math.abs(moveEvent.clientX - startX) < 3 && Math.abs(moveEvent.clientY - startY) < 3) {
        return;
      }
      moved = true;
      marker.classList.add("is-moving");
      const rect = frame.getBoundingClientRect();
      const clamp = (value) => Math.min(1, Math.max(0, value));
      fraction = {
        x: clamp((moveEvent.clientX - rect.left) / Math.max(1, rect.width)),
        y: clamp((moveEvent.clientY - rect.top) / Math.max(1, rect.height))
      };
      marker.style.setProperty("--pin-x", `${fraction.x * 100}%`);
      marker.style.setProperty("--pin-y", `${fraction.y * 100}%`);
    };

    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", up);
      marker.classList.remove("is-moving");
      if (!moved || !fraction) {
        return;
      }
      const pins = readPins(panel);
      const pin = pins.find((candidate) => candidate.id === marker.dataset.pinId);
      if (!pin) {
        return;
      }
      const round = (value, digits) => Number(value.toFixed(digits));
      if (isGeoPanel(panel)) {
        pin.longitude = round(fraction.x * 360 - 180, 4);
        pin.latitude = round(90 - fraction.y * 180, 4);
      } else {
        pin.x = round(fraction.x, 4);
        pin.y = round(fraction.y, 4);
      }
      writePins(panel, pins);
      // The geo rows show latitude/longitude - rebuilt, since the data changed (nobody is typing in them).
      renderPinRows(panel);
    };

    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
  };

  const wireTileDrag = (tile) => {
    // The WHOLE tile is the drag surface (except its interactive controls and the resize grip). Dragging
    // MOVES the tile to a cell: v4 stores a column and a row, so a widget goes where you drop it and the
    // cells around it stay empty. The tile itself is what moves - its grid variables are rewritten every
    // pointermove - so the preview during the drag IS the real layout, not a ghost that might disagree.
    tile.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) {
        return;
      }

      if (event.target instanceof HTMLElement
        && event.target.closest("input, select, textarea, button, a, [data-map-resize-handle]")) {
        return;
      }

      // A pin is dragged on its own; the tile stays where it is.
      const pinMarker = event.target instanceof Element ? event.target.closest(".map-pin[data-pin-id]") : null;
      if (pinMarker && pinMarker.dataset.pinId) {
        event.preventDefault();
        event.stopPropagation();
        selectTile(tile.dataset.tileIndex || "");
        dragPin(tile, pinMarker, event);
        return;
      }

      event.preventDefault();
      selectTile(tile.dataset.tileIndex || "");

      const startX = event.clientX;
      const startY = event.clientY;
      const origin = tileRect(tile);
      const metrics = cellMetrics();
      let dragging = false;

      const move = (moveEvent) => {
        if (!dragging) {
          // A few pixels of slop, so a plain click to SELECT a tile is not read as a move.
          if (Math.abs(moveEvent.clientX - startX) < 4 && Math.abs(moveEvent.clientY - startY) < 4) {
            return;
          }
          dragging = true;
          tile.classList.add("is-dragging");
        }

        // Whole cells of travel, rounded - so the tile snaps between cells instead of drifting a pixel at a
        // time towards one. There is no position between two of them to land on.
        const columnDelta = metrics.columnPitch > 0
          ? Math.round((moveEvent.clientX - startX) / metrics.columnPitch)
          : 0;
        const rowDelta = metrics.rowPitch > 0
          ? Math.round((moveEvent.clientY - startY) / metrics.rowPitch)
          : 0;

        applyTilePlacement(tile, origin.column + columnDelta, origin.row + rowDelta);
      };

      const up = () => {
        if (dragging) {
          tile.classList.remove("is-dragging");
          commitPlacement();
          syncCanvas();
          refreshSlidePreviews();
        }
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        window.removeEventListener("pointercancel", up);
      };

      // Bound to the WINDOW, not the tile: the cursor regularly outruns the tile mid-drag, and a pointerup
      // the element missed used to strand it in is-dragging with the move handler still attached.
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      window.addEventListener("pointercancel", up);
    });

    const resizeHandle = tile.querySelector("[data-map-resize-handle]");
    resizeHandle?.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      event.stopPropagation();
      selectTile(tile.dataset.tileIndex || "");
      resizeHandle.setPointerCapture(event.pointerId);
      tile.classList.add("is-resizing");

      const controls = getTileControls(tile);
      const startRect = tile.getBoundingClientRect();
      const startColumnSpan = Math.max(1, Number(controls.columnSpan?.value || 3));
      const startRowSpan = Math.max(1, Number(controls.rowSpan?.value || 2));
      const grid = readGrid();
      // Measured off the tile itself rather than computed from the canvas: one column is however wide this
      // tile's own span turned out to be, gaps included. No unit parsing, and it stays right at any zoom or
      // font size the browser happens to be using.
      const columnStep = (startRect.width + grid.tilePadding) / startColumnSpan;
      const rowStep = (startRect.height + grid.tilePadding) / startRowSpan;
      const startX = event.clientX;
      const startY = event.clientY;

      const move = (moveEvent) => {
        if (columnStep > 0 && controls.columnSpan) {
          const width = startRect.width + (moveEvent.clientX - startX);
          controls.columnSpan.value = String(Math.max(1, Math.round((width + grid.tilePadding) / columnStep)));
        }
        if (rowStep > 0 && controls.rowSpan) {
          const height = startRect.height + (moveEvent.clientY - startY);
          controls.rowSpan.value = String(Math.max(1, Math.round((height + grid.tilePadding) / rowStep)));
        }

        applyTileSize(tile);
        // Growing a tile at the right-hand edge would otherwise run it off the board; the column follows the
        // span back inside the grid.
        const rect = tileRect(tile);
        applyTilePlacement(tile, rect.column, rect.row);
      };

      const up = () => {
        tile.classList.remove("is-resizing");
        commitPlacement();
        syncCanvas();
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        window.removeEventListener("pointercancel", up);
        refreshSlidePreviews();
      };

      // Same reasoning as the move drag: the pointer leaves the small grip almost immediately.
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      window.addEventListener("pointercancel", up);
    });
  };

  const addTile = (tool, position, placement) => {
    if (!template || !propertyHost) {
      return;
    }

    const index = Number(canvas.dataset.nextTileIndex || 0);
    const kind = normalizeKind(tool.kind || "Element");
    const baseTitle = tool.title || getKindLabel(kind);
    const title = `${baseTitle} ${index + 1}`;
    const limits = getLimits(kind);
    const grid = readGrid();
    const columnSpan = clamp(placement?.columnSpan ?? limits.defaultColumns, Math.min(limits.minColumns, grid.columns), grid.columns);
    const rowSpan = clamp(placement?.rowSpan ?? limits.defaultRows, limits.minRows, MapMaxRowSpan);
    // Where it lands: the cell you dropped it on, or - for a palette CLICK, which has no drop point - the
    // first free one. "First free" rather than 1,1, because four clicks would otherwise bury four widgets
    // under each other on a board whose whole point is that you can see where things are.
    const dropped = position ?? findFreeCell(columnSpan, rowSpan);
    const cell = {
      column: clamp(dropped.column, 1, Math.max(1, grid.columns - columnSpan + 1)),
      row: Math.max(1, dropped.row)
    };
    const order = canvas.querySelectorAll(`[data-map-tile][data-slide-id="${activeSlideId}"]`).length;
    const html = template.innerHTML
      .replaceAll("__index__", String(index))
      .replaceAll("__id__", createId())
      .replaceAll("__slideId__", activeSlideId || "")
      .replaceAll("__kindLower__", kind.toLowerCase())
      .replaceAll("__kind__", kind)
      .replaceAll("__visual__", (tool.visual || "card").toLowerCase())
      .replaceAll("__kindLabel__", getKindLabel(kind))
      .replaceAll("__title__", title)
      .replaceAll("__order__", String(order))
      .replaceAll("__column__", String(cell.column))
      .replaceAll("__row__", String(cell.row))
      .replaceAll("__colSpan__", String(columnSpan))
      .replaceAll("__rowSpan__", String(rowSpan))
      // Mirrors _MapTile.cshtml: the density a tile can afford follows from its height in row units.
      .replaceAll("__density__", rowSpan <= 1 ? "s" : rowSpan === 2 ? "m" : "l");
    const fragment = document.createRange().createContextualFragment(html);
    const tile = fragment.querySelector("[data-map-tile]");
    const panel = fragment.querySelector("[data-map-property-panel]");
    if (!tile || !panel) {
      return;
    }

    // Straight out of the <template>, so it is a generic stub until the server sends its real body.
    tile.dataset.stub = "1";
    canvas.appendChild(tile);
    propertyHost.appendChild(panel);
    canvas.dataset.nextTileIndex = String(index + 1);
    const kindSelect = panel.querySelector("[data-map-property-kind]");
    if (kindSelect) {
      kindSelect.value = kind;
    }
    const visualSelect = panel.querySelector("[data-map-property-visual-type]");
    if (visualSelect && tool.visual) {
      visualSelect.value = tool.visual;
    }
    setupTile(tile);
    commitPlacement();
    // Initialize the freshly cloned tile's element + icon pickers (guarded so existing
    // ones aren't re-wired).
    initializeElementPickers();
    initializeIconPicker();
    selectTile(index);
  };

  mapSelectButton?.addEventListener("click", selectMap);
  mapNameInput?.addEventListener("input", () => syncMapSummary());
  mapDescriptionInput?.addEventListener("input", () => syncMapSummary());
  // The aspect-ratio and zoom wiring that used to sit here is gone with the canvas: a flow has no scale and
  // no ratio. What follows is everything else that block was sandwiched between - slides, grid size, the
  // initial tile setup and the palette - restored unchanged.

  const renderSlideInputs = () => {
    if (!slideInputsHost) {
      return;
    }
    slideInputsHost.replaceChildren();
    slides.forEach((slide, index) => {
      const hidden = (field, value) => {
        const input = document.createElement("input");
        input.type = "hidden";
        input.name = `Input.Slides[${index}].${field}`;
        input.value = value ?? "";
        return input;
      };
      // Title/Subtitle/DurationSeconds/BackgroundColor/ShowHeader have no editing UI yet (Phase B's slide
      // properties panel) - this just round-trips whatever the slide already had through add/rename/delete.
      slideInputsHost.append(
        hidden("Id", slide.id),
        hidden("Name", slide.name),
        hidden("Title", slide.title),
        hidden("Subtitle", slide.subtitle),
        hidden("DurationSeconds", slide.durationSeconds),
        hidden("BackgroundColor", slide.backgroundColor),
        hidden("ShowHeader", slide.showHeader === false ? "false" : "true"));
    });
  };

  // The first server-rendered row is the prototype every JS-rendered row is cloned from (captured before the
  // list is first re-rendered). A map with no rows yet - a brand new one - falls back to a hand-built row.
  const slideRowPrototype = slideTabsHost?.querySelector("[data-map-slide-tab]")?.cloneNode(true) || null;
  // Static markup only (no data in it): the name, index and duration are written with textContent afterwards.
  const buildFallbackSlideRow = () => {
    const icon = (name) => `<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-${name}"></use></svg>`;
    const action = (hook, label, tip, name, extra) =>
      `<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm${extra || ""}" ${hook} aria-label="${label}" data-tip="${tip}">${icon(name)}</button>`;
    const row = document.createElement("div");
    row.innerHTML =
      `<span class="map-slide-row-grip" aria-hidden="true">${icon("menu")}</span>`
      + '<span class="map-slide-row-index"></span>'
      + '<span class="map-slide-row-name"></span>'
      + '<span class="map-slide-row-duration"></span>'
      + '<span class="map-slide-row-actions">'
      + action("data-map-slide-settings", "Slide properties", "Properties", "settings")
      + action("data-map-slide-duplicate", "Duplicate slide", "Duplicate", "copy")
      + action("data-map-slide-delete", "Delete slide", "Delete", "trash")
      + "</span>";
    return row;
  };

  const canvasEmpty = form?.querySelector("[data-map-canvas-empty]") || null;

  const renderSlideSwitch = () => {
    if (!slideSwitch) {
      return;
    }
    slideSwitch.replaceChildren();
    slides.forEach((slide, index) => {
      const option = document.createElement("option");
      option.value = slide.id;
      // textContent, not innerHTML - a slide name is user input.
      option.textContent = `${index + 1}. ${slide.name}`;
      option.selected = slide.id === activeSlideId;
      slideSwitch.appendChild(option);
    });
  };

  const renderSlideTabs = () => {
    if (!slideTabsHost) {
      return;
    }
    slideTabsHost.replaceChildren();
    slides.forEach((slide, index) => {
      // Cloned from the server's own first row, so an added slide is markup-identical to a loaded one -
      // including the icons. Hand-writing a lookalike here drifted from the server the moment either side
      // changed, and the symptom was cosmetic enough to live for a long time.
      const row = slideRowPrototype
        ? slideRowPrototype.cloneNode(true)
        : buildFallbackSlideRow();
      row.className = "map-slide-row" + (slide.id === activeSlideId ? " is-active" : "");
      row.dataset.slideId = slide.id;
      row.dataset.slideTitle = slide.title || "";
      row.dataset.slideSubtitle = slide.subtitle || "";
      row.dataset.slideDuration = slide.durationSeconds || "";
      row.dataset.slideBg = slide.backgroundColor || "";
      row.dataset.slideShowHeader = slide.showHeader === false ? "false" : "true";
      row.setAttribute("data-map-slide-tab", "");
      row.draggable = true;
      const setText = (selector, value) => {
        const node = row.querySelector(selector);
        if (node) {
          // textContent, not innerHTML - a slide name is user input.
          node.textContent = value;
        }
      };
      setText(".map-slide-row-index", String(index + 1));
      setText(".map-slide-row-name", slide.name);
      setText(".map-slide-row-duration", slide.durationSeconds ? `${slide.durationSeconds}s` : "—");
      slideTabsHost.appendChild(row);
    });
    renderSlideSwitch();
    refreshSlidePreviews();
    const counter = form?.querySelector("[data-map-slide-count]");
    if (counter) {
      counter.textContent = String(slides.length);
    }
  };

  // The widgets actually on the active slide - the one question the empty state, the clear action and the
  // layout guard all ask, so it is asked in one place.
  const activeSlideTiles = () => Array.from(canvas.querySelectorAll("[data-map-tile]")).filter((tile) =>
    (tile.dataset.slideId || "") === activeSlideId
    && tile.querySelector("[data-map-tile-deleted]")?.value !== "true");

  // Layouts are offered ON the empty canvas rather than as a permanent row of cards in the slide's
  // properties: a layout is what you start a slide FROM, and a card that silently replaces a board you have
  // already built is the wrong affordance for a destructive act.
  const syncCanvasEmpty = () => {
    if (!canvasEmpty) {
      return;
    }
    canvasEmpty.hidden = activeSlideTiles().length > 0;
  };

  const applySlideFilter = () => {
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      const deleted = tile.querySelector("[data-map-tile-deleted]")?.value === "true";
      const onActiveSlide = (tile.dataset.slideId || "") === activeSlideId;
      tile.hidden = deleted || !onActiveSlide;
    });
    syncCanvasEmpty();
  };

  const setActiveSlide = (id) => {
    if (!id) {
      return;
    }
    activeSlideId = id;
    if (slideSwitch && slideSwitch.value !== activeSlideId) {
      slideSwitch.value = activeSlideId;
    }
    slideTabsHost?.querySelectorAll("[data-map-slide-tab]").forEach((tab) => {
      tab.classList.toggle("is-active", tab.dataset.slideId === activeSlideId);
    });
    applySlideFilter();
    fillSlidePanel();
    selectMap();
  };

  const addSlide = () => {
    const id = createId();
    slides.push({ id, name: `Slide ${slides.length + 1}`, showHeader: true });
    renderSlideInputs();
    renderSlideTabs();
    setActiveSlide(id);
  };

  // (Renaming is the "Slide name" field of the Slides tab; the old prompt-based rename had no caller left.)

  // Asks in MatUi's own dialog. The slide is captured before the question: while the dialog is open nothing
  // else may change which slide is active, but the answer must act on the slide the user was asked about.
  const deleteSlide = async () => {
    if (slides.length <= 1) {
      MatUi.alert("A map needs at least one slide.", { title: "Cannot delete the last slide" });
      return;
    }
    const slideId = activeSlideId;
    const ok = await MatUi.confirm("Delete this slide and all its tiles?", { ok: "Delete", danger: true });
    if (!ok || !slides.some((candidate) => candidate.id === slideId)) {
      return;
    }
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      if ((tile.dataset.slideId || "") === slideId) {
        const deleted = tile.querySelector("[data-map-tile-deleted]");
        if (deleted) {
          deleted.value = "true";
        }
        tile.hidden = true;
      }
    });
    slides = slides.filter((candidate) => candidate.id !== slideId);
    renderSlideInputs();
    renderSlideTabs();
    setActiveSlide(slides[0].id);
  };

  slides = slideTabsHost
    ? Array.from(slideTabsHost.querySelectorAll("[data-map-slide-tab]")).map((tab) => ({
        id: tab.dataset.slideId,
        name: (tab.querySelector(".map-slide-row-name")?.textContent || tab.textContent || "").trim(),
        title: tab.dataset.slideTitle || "",
        subtitle: tab.dataset.slideSubtitle || "",
        durationSeconds: tab.dataset.slideDuration || "",
        backgroundColor: tab.dataset.slideBg || "",
        showHeader: tab.dataset.slideShowHeader !== "false"
      }))
    : [];
  if (slides.length === 0) {
    slides = [{ id: createId(), name: "Slide 1" }];
  }
  activeSlideId = slides[0].id;
  renderSlideInputs();
  renderSlideTabs();
  slideAddButton?.addEventListener("click", addSlide);
  slideSwitch?.addEventListener("change", () => setActiveSlide(slideSwitch.value));
  // One delegated handler for the whole slide list. Delegation, not per-button listeners, because the rows
  // are re-rendered on every add/reorder/rename - and crucially an action must act on ITS OWN row, not on
  // whatever happened to be active, so the row is selected first.
  slideTabsHost?.addEventListener("click", (event) => {
    const row = event.target.closest("[data-map-slide-tab]");
    if (!row) {
      return;
    }

    const slideId = row.dataset.slideId || "";
    if (slideId && slideId !== activeSlideId) {
      setActiveSlide(slideId);
    }

    if (event.target.closest("[data-map-slide-settings]")) {
      selectSlide();
    } else if (event.target.closest("[data-map-slide-duplicate]")) {
      duplicateActiveSlide();
    } else if (event.target.closest("[data-map-slide-delete]")) {
      deleteSlide();
    }
  });

  // Reorder by dragging a row - same model mutation the filmstrip cards use.
  slideTabsHost?.addEventListener("dragstart", (event) => {
    const row = event.target.closest("[data-map-slide-tab]");
    if (row) {
      event.dataTransfer?.setData("text/plain", row.dataset.slideId || "");
      row.classList.add("is-dragging");
    }
  });
  slideTabsHost?.addEventListener("dragend", (event) => {
    event.target.closest("[data-map-slide-tab]")?.classList.remove("is-dragging");
  });
  slideTabsHost?.addEventListener("dragover", (event) => event.preventDefault());
  slideTabsHost?.addEventListener("drop", (event) => {
    event.preventDefault();
    const target = event.target.closest("[data-map-slide-tab]");
    const movedId = event.dataTransfer?.getData("text/plain");
    const from = slides.findIndex((candidate) => candidate.id === movedId);
    const to = slides.findIndex((candidate) => candidate.id === target?.dataset.slideId);
    if (!target || from < 0 || to < 0 || from === to) {
      return;
    }
    slides.splice(to, 0, slides.splice(from, 1)[0]);
    renderSlideInputs();
    renderSlideTabs();
    renderSlidePreviews();
  });


  canvas.querySelectorAll("[data-map-tile]").forEach(setupTile);
  applySlideFilter();
  fillSlidePanel();
  syncCanvas();

  document.querySelectorAll("[data-map-tool-kind]").forEach((tool) => {
    const payload = {
      kind: tool.getAttribute("data-map-tool-kind"),
      title: tool.getAttribute("data-map-tool-title"),
      visual: tool.getAttribute("data-map-tool-visual")
    };

    tool.addEventListener("click", () => addTile(payload));
    tool.addEventListener("dragstart", (event) => {
      event.dataTransfer?.setData("application/x-matmon-map-tool", JSON.stringify(payload));
      event.dataTransfer?.setData("text/plain", payload.title || "Tile");
      if (event.dataTransfer) {
        event.dataTransfer.effectAllowed = "copy";
      }
    });
  });

  canvas.addEventListener("dragover", (event) => {
    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = "copy";
    }
  });

  // Which cell the pointer is over. The pitch comes from the live grid, so this is right at any column
  // count, zoom or margin - and a drop lands where you let go rather than at the end of a list.
  const pointerToCell = (event) => {
    const grid = readGrid();
    const metrics = cellMetrics();
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left - grid.outerMargin;
    const y = event.clientY - rect.top - grid.outerMargin;
    return {
      column: metrics.columnPitch > 0 ? Math.max(1, Math.floor(x / metrics.columnPitch) + 1) : 1,
      row: metrics.rowPitch > 0 ? Math.max(1, Math.floor(y / metrics.rowPitch) + 1) : 1
    };
  };

  canvas.addEventListener("drop", (event) => {
    event.preventDefault();
    const rawPayload = event.dataTransfer?.getData("application/x-matmon-map-tool");
    if (!rawPayload) {
      return;
    }

    let payload = null;
    try {
      payload = JSON.parse(rawPayload);
    } catch {
      // A drop we cannot read is not a widget. This used to fall back to addTile({kind:"1"}) - and since the
      // cell lookup it called alongside had been deleted with the old canvas, EVERY drop threw and took that
      // branch: dragging any widget onto the board silently produced a State tile.
      return;
    }

    addTile(payload, pointerToCell(event));
  });

  canvas.addEventListener("click", (event) => {
    if (event.target === canvas) {
      selectMap();
    }
  });

  // --- Widget palette: search -------------------------------------------------------------------------
  const toolSearch = form?.querySelector("[data-map-tool-search]");
  const toolEmpty = form?.querySelector("[data-map-tool-empty]");
  const filterTools = () => {
    const needle = (toolSearch?.value || "").trim().toLowerCase();
    let visible = 0;
    form?.querySelectorAll("[data-map-tool-group]").forEach((group) => {
      let groupVisible = 0;
      group.querySelectorAll("[data-map-tool-widget]").forEach((button) => {
        const match = !needle || (button.dataset.mapToolSearch || "").includes(needle);
        button.hidden = !match;
        if (match) {
          groupVisible += 1;
        }
      });
      group.hidden = groupVisible === 0;
      visible += groupVisible;
    });
    if (toolEmpty) {
      toolEmpty.hidden = visible > 0;
    }
  };
  toolSearch?.addEventListener("input", filterTools);

  // --- Canvas view toggles ----------------------------------------------------------------------------
  // Purely how the canvas is drawn for this user, never part of the saved board - hence localStorage and not
  // a map field. There is deliberately no "snap off": v2 stores cells, so there is no position to save
  // between two of them.
  const viewPreference = (key, fallback) => {
    try {
      const stored = window.localStorage?.getItem("matmon.map.view." + key);
      return stored === null || stored === undefined ? fallback : stored === "true";
    } catch {
      return fallback;
    }
  };
  const rememberViewPreference = (key, value) => {
    try {
      window.localStorage?.setItem("matmon.map.view." + key, value ? "true" : "false");
    } catch {
      /* private mode - the toggle still works for this session */
    }
  };
  const bindViewToggle = (selector, key, apply) => {
    const input = form?.querySelector(selector);
    if (!input) {
      return;
    }
    input.checked = viewPreference(key, true);
    apply(input.checked);
    input.addEventListener("change", () => {
      rememberViewPreference(key, input.checked);
      apply(input.checked);
    });
  };
  bindViewToggle("[data-map-view-grid]", "grid", (on) => {
    if (gridGuides) {
      gridGuides.hidden = !on;
    }
  });
  bindViewToggle("[data-map-view-rulers]", "rulers", (on) => {
    form?.querySelector("[data-map-rulers]")?.classList.toggle("is-rulerless", !on);
  });

  // --- Layout templates -------------------------------------------------------------------------------
  // A template is authored for a number of COLUMNS; on a narrower board its widths would be clamped and the
  // arrangement it exists to demonstrate would not survive, so the button is disabled instead of quietly
  // producing a mess. Its row count no longer matters - a flow is as tall as it needs to be.
  const syncLayoutAvailability = () => {
    const grid = readGrid();
    let anyHidden = false;
    form?.querySelectorAll("[data-map-layout]").forEach((button) => {
      const tooSmall = grid.columns < Number(button.dataset.layoutMinColumns || 1);
      button.disabled = tooSmall;
      button.classList.toggle("is-disabled", tooSmall);
      anyHidden = anyHidden || tooSmall;
    });
    const warn = form?.querySelector("[data-map-layout-too-small]");
    if (warn) {
      warn.hidden = !anyHidden;
    }
  };

  // Empties the active slide, asking first (in MatUi's dialog) and naming the cost. Resolves to false when the
  // user backs out, so a caller can abandon whatever it was about to do instead of half-doing it. An empty slide
  // asks nothing.
  const clearActiveSlide = async (because, okLabel) => {
    const existing = activeSlideTiles();
    if (existing.length > 0) {
      const ok = await MatUi.confirm(
        `This slide has ${existing.length} widget${existing.length === 1 ? "" : "s"}. ${because}`,
        { ok: okLabel || "Remove", danger: true });
      if (!ok) {
        return false;
      }
    }
    existing.forEach((tile) => {
      const deleted = tile.querySelector("[data-map-tile-deleted]");
      if (deleted) {
        deleted.value = "true";
      }
      tile.hidden = true;
    });
    commitPlacement();
    selectMap();
    return true;
  };

  form?.querySelector("[data-map-slide-clear]")?.addEventListener("click", () => {
    clearActiveSlide("Remove them all?", "Remove all");
  });

  const applyLayoutTemplate = async (key) => {
    const template = layoutTemplates.find((candidate) => candidate.key === key);
    if (!template) {
      return;
    }
    // Only reachable on an empty slide (the cards live in the canvas empty state), but a layout that
    // silently threw widgets away would be the worst possible surprise - so it still says so out loud.
    if (!(await clearActiveSlide(`Replace them with the "${template.label}" layout?`, "Replace"))) {
      return;
    }

    (template.slots || []).forEach((slot) => {
      const widget = widgetCatalog[slot.widget];
      if (!widget) {
        return;
      }
      addTile(widget, { column: slot.column, row: slot.row }, slot);
    });
    selectMap();
  };

  form?.querySelectorAll("[data-map-layout]").forEach((button) => {
    button.addEventListener("click", () => applyLayoutTemplate(button.dataset.mapLayout));
  });

  // --- Grid size --------------------------------------------------------------------------------------
  // Resizing the grid is DESTRUCTIVE: shrinking it re-clamps every widget into the smaller grid and growing
  // it back cannot undo that. So commit on "change" (blur / Enter), never on "input" - typing "12" passes
  // through "1", which on every keystroke would have flattened the whole board into a single column. The
  // value is also pulled back inside the field's own min/max first, since a number input happily holds "0".
  [columnsInput, rowsInput, tilePaddingInput, outerMarginInput].forEach((input) => {
    input?.addEventListener("change", () => {
      const min = Number(input.min || 0);
      const max = Number(input.max) || Number.MAX_SAFE_INTEGER;
      input.value = String(Math.min(max, Math.max(min, Math.round(Number(input.value) || min))));
      syncCanvas();
      syncLayoutAvailability();
    });
  });

  // --- Property tabs ----------------------------------------------------------------------------------
  propertyTabs?.querySelectorAll("[data-map-property-tab]").forEach((button) => {
    button.addEventListener("click", () => {
      activePropertyTab = button.dataset.mapPropertyTab || "general";
      applyPropertyTab(propertyHost?.querySelector("[data-map-property-panel]:not([hidden])"));
    });
  });

  // --- Slide properties -------------------------------------------------------------------------------
  slidePanel?.querySelectorAll("[data-map-slide-field]").forEach((field) => {
    field.addEventListener("input", () => {
      const slide = slides.find((candidate) => candidate.id === activeSlideId);
      if (!slide) {
        return;
      }
      const key = field.dataset.mapSlideField;
      slide[key] = field.type === "checkbox" ? field.checked : field.value;
      renderSlideInputs();
      if (key === "name") {
        renderSlideTabs();
        setPropertyScope("slide", `Slide - ${slide.name}`);
      }
    });
  });

  // cloneNode copies ATTRIBUTES, not live input state, so anything typed since page load would be lost in a
  // duplicate. Writing the current state back into the attributes first makes the clone faithful.
  const freezeValues = (root) => {
    root.querySelectorAll("input, textarea, select").forEach((field) => {
      if (field.type === "checkbox" || field.type === "radio") {
        field.toggleAttribute("checked", field.checked);
      } else if (field.tagName === "SELECT") {
        Array.from(field.options).forEach((option) => option.toggleAttribute("selected", option.selected));
      } else if (field.tagName === "TEXTAREA") {
        field.textContent = field.value;
      } else {
        field.setAttribute("value", field.value);
      }
    });
  };

  // Named, because the slide list delegates to it - the rows are re-rendered constantly, so a listener
  // bound to one button would be lost the first time a slide is added.
  function duplicateActiveSlide() {
    const source = slides.find((candidate) => candidate.id === activeSlideId);
    if (!source) {
      return;
    }
    const newSlideId = createId();
    slides.splice(slides.indexOf(source) + 1, 0, { ...source, id: newSlideId, name: `${source.name} copy` });

    const sourceTiles = Array.from(canvas.querySelectorAll("[data-map-tile]")).filter((tile) =>
      (tile.dataset.slideId || "") === source.id
      && tile.querySelector("[data-map-tile-deleted]")?.value !== "true");

    sourceTiles.forEach((tile) => {
      const oldIndex = tile.dataset.tileIndex;
      const panel = getPanel(oldIndex);
      if (!panel) {
        return;
      }
      const index = Number(canvas.dataset.nextTileIndex || 0);
      freezeValues(tile);
      freezeValues(panel);
      // Re-index by string so the element picker's DOM ids (picker-map-tile-N) are rewritten too - a cloned
      // duplicate id would make the picker in the copy drive the original's hidden field.
      // Field ids follow ASP.NET's own Input_Tiles_<n>__<Field> shape (asp-for writes them, the template and
      // _MapCompareFields copy it), so the clone's labels point at the clone's fields, not the original's.
      const rewrite = (html) => html
        .replaceAll(`Input.Tiles[${oldIndex}]`, `Input.Tiles[${index}]`)
        .replaceAll(`Input_Tiles_${oldIndex}__`, `Input_Tiles_${index}__`)
        .replaceAll(`picker-map-tile-${oldIndex}`, `picker-map-tile-${index}`)
        .replaceAll(`data-tile-index="${oldIndex}"`, `data-tile-index="${index}"`);
      const tileClone = document.createRange().createContextualFragment(rewrite(tile.outerHTML)).firstElementChild;
      const panelClone = document.createRange().createContextualFragment(rewrite(panel.outerHTML)).firstElementChild;
      if (!tileClone || !panelClone) {
        return;
      }
      const newTileId = createId();
      tileClone.dataset.tileId = newTileId;
      tileClone.dataset.slideId = newSlideId;
      tileClone.querySelector(`input[name="Input.Tiles[${index}].Id"]`)?.setAttribute("value", newTileId);
      const slideField = tileClone.querySelector("[data-map-tile-slide-id]");
      if (slideField) {
        slideField.value = newSlideId;
        slideField.setAttribute("value", newSlideId);
      }
      canvas.appendChild(tileClone);
      propertyHost?.appendChild(panelClone);
      canvas.dataset.nextTileIndex = String(index + 1);
      setupTile(tileClone);
    });

    renderSlideInputs();
    renderSlideTabs();
    initializeElementPickers();
    initializeIconPicker();
    setActiveSlide(newSlideId);
  }


  // --- Slide previews ---------------------------------------------------------------------------------
  // A real scaled clone of the slide, not a hand-drawn thumbnail: a preview that can disagree with the board
  // is worse than no preview at all. Cloned from the live canvas so it reflects unsaved edits too.
  const slidePreviewHost = form?.querySelector("[data-map-slide-previews]");

  // Same prototype trick as the slide rows: clone the server's own card so an added slide's miniature is
  // markup-identical to a loaded one.
  const slideCardPrototype = slidePreviewHost?.querySelector("[data-map-slide-card]")?.cloneNode(true) || null;
  const autoRotateInput = form?.querySelector('[name="Input.AutoRotateSeconds"]');

  // The filmstrip used to repaint only the cards the SERVER had rendered, so adding, duplicating, reordering
  // or deleting a slide left it showing the old running order - and a preview that disagrees with the board
  // is worse than no preview. Reconcile the card list against `slides` first, then paint.
  const syncSlideCards = () => {
    if (!slidePreviewHost || !slideCardPrototype) {
      return;
    }
    const existing = new Map(Array.from(slidePreviewHost.querySelectorAll("[data-map-slide-card]"))
      .map((card) => [card.dataset.slideId || "", card]));
    slides.forEach((slide, index) => {
      let card = existing.get(slide.id);
      if (!card) {
        card = slideCardPrototype.cloneNode(true);
        card.dataset.slideId = slide.id;
      }
      existing.delete(slide.id);
      card.classList.toggle("is-active", slide.id === activeSlideId);
      const title = card.querySelector(".map-slide-card-meta strong");
      if (title) {
        title.textContent = `${index + 1}. ${slide.name}`;
      }
      const duration = card.querySelector(".map-slide-card-meta small");
      if (duration) {
        duration.textContent = `${slide.durationSeconds || autoRotateInput?.value || 15}s`;
      }
      // appendChild on a card already in the host MOVES it, which is how the order follows `slides`.
      slidePreviewHost.appendChild(card);
    });
    existing.forEach((card) => card.remove());
  };

  // Miniatures are a scaled clone of the REAL slide markup, not a hand-drawn set of rectangles. Under v3 a
  // tile has no px rect to copy - it is placed by the grid - so re-deriving one here would have meant
  // reimplementing the layout, which is exactly how a preview starts disagreeing with the board.
  const renderSlidePreviews = () => {
    if (!slidePreviewHost) {
      return;
    }
    syncSlideCards();
    const grid = readGrid();

    slidePreviewHost.querySelectorAll("[data-map-slide-card]").forEach((card) => {
      const frame = card.querySelector("[data-map-slide-preview]");
      if (!frame) {
        return;
      }
      const slideId = card.dataset.slideId || "";
      const board = document.createElement("div");
      board.className = "map-slide map-slide-preview-board";
      board.style.cssText = "position:absolute;top:0;left:0;transform-origin:top left;pointer-events:none;";
      board.style.setProperty("--map-columns", String(grid.columns));
      board.style.setProperty("--map-rows", String(grid.rows));
      board.style.setProperty("--tile-gap", `${grid.tilePadding}px`);
      board.style.setProperty("--map-margin", `${grid.outerMargin}px`);

      canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
        if ((tile.dataset.slideId || "") !== slideId
          || tile.querySelector("[data-map-tile-deleted]")?.value === "true") {
          return;
        }
        const block = document.createElement("span");
        block.className = "map-slide-preview-tile";
        block.dataset.state = tile.dataset.state || "unknown";
        block.style.gridColumn = `span ${tile.style.getPropertyValue("--tile-span") || 3}`;
        block.style.gridRow = `span ${tile.style.getPropertyValue("--tile-rows") || 2}`;
        board.appendChild(block);
      });

      frame.replaceChildren(board);
      // Scale AFTER insertion: the frame has no size until it is in the document, and the board has no
      // width until it has been laid out at the canvas's own width.
      const frameWidth = frame.getBoundingClientRect().width;
      const canvasWidth = canvas.getBoundingClientRect().width || frameWidth;
      if (frameWidth > 0 && canvasWidth > 0) {
        board.style.width = `${canvasWidth}px`;
        board.style.transform = `scale(${frameWidth / canvasWidth})`;
      }
    });
  };

  refreshSlidePreviews = renderSlidePreviews;

  // Delegated, because the cards are rebuilt whenever the slide list changes - per-card listeners would be
  // dropped by the first add and the strip would go inert.
  slidePreviewHost?.addEventListener("click", (event) => {
    const card = event.target.closest("[data-map-slide-card]");
    if (!card) {
      return;
    }
    setActiveSlide(card.dataset.slideId || "");
    selectSlide();
  });

  // Reorder by dragging a card - the mockup's filmstrip, and far more direct than arrow buttons.
  slidePreviewHost?.addEventListener("dragstart", (event) => {
    const card = event.target.closest("[data-map-slide-card]");
    if (!card) {
      return;
    }
    event.dataTransfer?.setData("text/plain", card.dataset.slideId || "");
    card.classList.add("is-dragging");
  });
  slidePreviewHost?.addEventListener("dragend", (event) => {
    event.target.closest("[data-map-slide-card]")?.classList.remove("is-dragging");
  });
  slidePreviewHost?.addEventListener("dragover", (event) => event.preventDefault());
  slidePreviewHost?.addEventListener("drop", (event) => {
    const card = event.target.closest("[data-map-slide-card]");
    if (!card) {
      return;
    }
    event.preventDefault();
    const movedId = event.dataTransfer?.getData("text/plain");
    const from = slides.findIndex((candidate) => candidate.id === movedId);
    const to = slides.findIndex((candidate) => candidate.id === card.dataset.slideId);
    if (from < 0 || to < 0 || from === to) {
      return;
    }
    slides.splice(to, 0, slides.splice(from, 1)[0]);
    renderSlideInputs();
    // renderSlideTabs refreshes the strip, and syncSlideCards re-orders it from `slides` - so the DOM
    // follows the model instead of being nudged into place by hand.
    renderSlideTabs();
  });
  renderSlidePreviews();
  filterTools();
  syncLayoutAvailability();
  renderGridGuides();

  selectMap();
}


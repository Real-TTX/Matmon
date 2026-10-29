// Matmon - the dashboard's live refresh and the counts on the menu.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Polls while there is something on the page that shows live numbers: the dashboard (its sensors, status chart and
// highlight strip) or a monitoring-tree node that carries a live value (data-series-key) need the full /api/dashboard
// snapshot; a page that only has the menu's alert status polls the light /api/alerts/summary (the same count names,
// none of the per-sensor graphs).
function initializeDashboardRefresh() {
  const needsSnapshot = document.querySelector("[data-series-key], [data-dashboard-status-chart], [data-dashboard-highlight-strip]") !== null;
  const navBadges = document.querySelectorAll("[data-navinfo], [data-nav-badge]");
  if (!needsSnapshot && navBadges.length === 0) {
    return;
  }

  const refresh = async () => {
    try {
      const response = await fetch(needsSnapshot ? "/api/dashboard" : "/api/alerts/summary", {
        headers: {
          Accept: "application/json"
        }
      });

      if (response.status === 401 || response.status === 403) {
        redirectToLogin();
        return;
      }

      if (!response.ok) {
        return;
      }

      const snapshot = await response.json();
      if (needsSnapshot) {
        renderDashboard(snapshot);
      } else {
        renderNavCounters(snapshot);
      }
    } catch {
      // The UI stays usable even if one refresh fails.
    }
  };

  refresh();
  window.setInterval(refresh, dashboardRefreshMs);
  // The connection is back (pwa.js): the current numbers now, not at the next tick.
  window.addEventListener("matmon:online", refresh);
}

function renderDashboard(snapshot) {
  const seriesByKey = new Map((snapshot.telemetrySeries ?? []).map((series) => [series.key, series]));
  const highlightedKeys = new Set((snapshot.highlightedTelemetrySeries ?? []).map((series) => series.key));
  const highlightStrip = document.querySelector("[data-dashboard-highlight-strip]");
  const numberFormatter = new Intl.NumberFormat(undefined, {
    maximumFractionDigits: 1,
    minimumFractionDigits: 0
  });

  renderNavCounters(snapshot);
  renderDashboardStatusChart(snapshot);

  // A highlight was added elsewhere: the strip is only drawn when there is one, so it is not on the page yet.
  if (!highlightStrip && highlightedKeys.size > 0 && document.querySelector("[data-dashboard-status-chart]")) {
    window.location.reload();
    return;
  }

  if (highlightStrip) {
    const currentHighlightedCards = Array.from(highlightStrip.querySelectorAll("[data-series-key]"));
    const currentHighlightedKeys = currentHighlightedCards
      .map((card) => card.dataset.seriesKey)
      .filter((key) => Boolean(key));
    const highlightedKeysMatch =
      currentHighlightedKeys.length === highlightedKeys.size &&
      currentHighlightedKeys.every((key) => highlightedKeys.has(key));

    if (!highlightedKeysMatch) {
      window.location.reload();
      return;
    }
  }

  // What each sensor said last - a tile shows it while the sensor has a problem.
  const messages = new Map((snapshot.nodes ?? []).map((node) => [node.id, node.stateMessage]));

  const seriesCards = document.querySelectorAll("[data-series-key]");
  seriesCards.forEach((card) => {
    const key = card.dataset.seriesKey;
    const series = seriesByKey.get(key);
    if (!series) {
      return;
    }

    const state = normalizeStateKey(series.stateKey ?? series.currentState);
    const lineColor = series.stateColor || series.lineColor || "";
    // The colour of a dashboard card follows its data-state (dashboard.css, on the MatUi tokens). A monitoring-tree
    // node that still carries a --series-color of its own keeps getting it updated.
    card.dataset.state = state;
    if (lineColor && card.style.getPropertyValue("--series-color")) {
      card.style.setProperty("--series-color", lineColor);
    }

    const valueElement = card.querySelector('[data-role="current-value"]');
    if (valueElement) {
      // A check that failed without a reading has no current value - its placeholder 0 is not one.
      const lastPoint = (series.points ?? [])[(series.points ?? []).length - 1];
      valueElement.textContent = series.currentValue == null || (lastPoint && lastPoint.hasReading === false) ? "-" : numberFormatter.format(series.currentValue);
    }

    const unitElement = card.querySelector('[data-role="current-unit"]');
    if (unitElement) {
      unitElement.textContent = series.unit ?? "";
    }

    const stateElement = card.querySelector('[data-role="state"]');
    if (stateElement) {
      stateElement.textContent = series.stateLabel || defaultStateLabel(state);
      if (stateElement.classList.contains("mat-pill")) {
        setStatePillTone(stateElement, state);
      }
    }

    const messageElement = card.querySelector('[data-role="message"]');
    if (messageElement) {
      const message = state === "error" || state === "warning" ? messages.get(series.sensorId) : null;
      messageElement.textContent = message ?? "";
      messageElement.title = message ?? "";
      messageElement.hidden = !message;
    }

    const trend = card.querySelector("[data-trend]");
    if (trend) {
      drawTrend(trend, series.points ?? [], series.unit ?? "");
      return;
    }

    const svg = card.querySelector("svg[data-sparkline], svg.sparkline");
    if (svg) {
      drawSparkline(svg, series.points ?? [], lineColor);
    }
  });
}
// ---- The dashboard trend (_DashboardTrend.cshtml) ----
// The readings are placed by TIME (a gap in the checks shows as a gap, not as an even step), a failed check has no
// reading - its point is a 0 in the series - so the line is broken there instead of diving to zero, and the state of
// every check is drawn as a band under the line: runs of the same state are one segment each.
const trendData = new WeakMap();

function drawTrend(trend, points, unit) {
  const svgNs = "http://www.w3.org/2000/svg";
  const line = trend.querySelector('[data-role="line"]');
  const area = trend.querySelector('[data-role="area"]');
  const band = trend.querySelector('[data-role="states"]');
  const dot = trend.querySelector('[data-role="dot"]');
  const empty = trend.querySelector('[data-role="empty"]');
  if (!line || !area || !band) {
    return;
  }

  const samples = points
    .map((point) => ({
      time: Date.parse(point.timestampUtc),
      value: Number(point.value),
      state: normalizeStateKey(point.state),
      reading: point.hasReading !== false
    }))
    .filter((sample) => Number.isFinite(sample.time))
    .sort((a, b) => a.time - b.time);
  trendData.set(trend, { samples, unit });

  band.replaceChildren();
  const readingCount = samples.filter((sample) => sample.reading && Number.isFinite(sample.value)).length;
  if (empty) {
    // Said in words rather than left as an empty box: no check yet, or checks that all failed.
    empty.textContent = samples.length === 0 ? "No checks yet" : readingCount === 0 ? "No readings - the checks failed" : "";
    empty.hidden = readingCount > 0;
  }
  if (samples.length === 0) {
    line.setAttribute("d", "");
    area.setAttribute("d", "");
    if (dot) {
      dot.hidden = true;
    }
    return;
  }

  const start = samples[0].time;
  const end = samples[samples.length - 1].time;
  const span = end - start;
  const xOf = (time) => (span > 0 ? ((time - start) / span) * 100 : 50);
  const hasReading = (sample) => sample.reading && Number.isFinite(sample.value);

  const readings = samples.filter(hasReading).map((sample) => sample.value);
  let min = readings.length ? Math.min(...readings) : 0;
  let max = readings.length ? Math.max(...readings) : 1;
  if (max === min) {
    // A flat series sits in the middle rather than on an edge.
    min -= 1;
    max += 1;
  }
  const height = 40;
  const pad = 3;
  const yOf = (value) => height - pad - ((value - min) / (max - min)) * (height - pad * 2);

  // The line: one segment per run of checks that have a reading.
  const segments = [];
  let current = [];
  samples.forEach((sample) => {
    if (!hasReading(sample)) {
      if (current.length) {
        segments.push(current);
      }
      current = [];
      return;
    }
    current.push({ x: xOf(sample.time), y: yOf(sample.value) });
  });
  if (current.length) {
    segments.push(current);
  }

  const lineParts = [];
  const areaParts = [];
  segments.forEach((segment) => {
    // A lone reading between two failures is drawn as a short dash, so it is still visible.
    const pts = segment.length === 1
      ? [{ x: Math.max(0, segment[0].x - 0.6), y: segment[0].y }, { x: Math.min(100, segment[0].x + 0.6), y: segment[0].y }]
      : segment;
    const path = pts.map((p, i) => (i === 0 ? "M" : "L") + p.x.toFixed(2) + " " + p.y.toFixed(2)).join(" ");
    lineParts.push(path);
    areaParts.push(path + " L" + pts[pts.length - 1].x.toFixed(2) + " " + height + " L" + pts[0].x.toFixed(2) + " " + height + " Z");
  });
  line.setAttribute("d", lineParts.join(" "));
  area.setAttribute("d", areaParts.join(" "));

  // The band: a check owns the stretch from halfway after the previous check to halfway before the next one.
  let runStart = 0;
  for (let index = 0; index < samples.length; index++) {
    const next = samples[index + 1];
    if (next && next.state === samples[index].state) {
      continue;
    }
    const from = runStart === 0 ? 0 : (xOf(samples[runStart - 1].time) + xOf(samples[runStart].time)) / 2;
    const to = next ? (xOf(samples[index].time) + xOf(next.time)) / 2 : 100;
    const rect = document.createElementNS(svgNs, "rect");
    rect.setAttribute("x", from.toFixed(3));
    rect.setAttribute("y", "0");
    rect.setAttribute("width", Math.max(0.4, to - from).toFixed(3));
    rect.setAttribute("height", "1");
    rect.setAttribute("data-state", samples[index].state);
    band.appendChild(rect);
    runStart = index + 1;
  }

  // The latest reading as a dot (HTML, so it stays round in a stretched plot).
  if (dot) {
    const last = samples[samples.length - 1];
    dot.hidden = !hasReading(last);
    if (hasReading(last)) {
      dot.style.setProperty("--mm-x", xOf(last.time) + "%");
      // A fraction of the plot's height; the stylesheet turns it into a position above the band.
      dot.style.setProperty("--mm-y", (yOf(last.value) / height).toFixed(4));
    }
  }
}

// Hovering a trend shows the check nearest the pointer: its time, its reading and its state. Delegated, so a trend
// drawn later works too; built with textContent only.
function initializeDashboardTrends() {
  if (!document.querySelector("[data-trend]")) {
    return;
  }

  const hide = (trend) => {
    trend.querySelectorAll('[data-role="cursor"], [data-role="tip"]').forEach((element) => {
      element.hidden = true;
    });
  };

  const show = (trend, clientX) => {
    const data = trendData.get(trend);
    const tip = trend.querySelector('[data-role="tip"]');
    const cursor = trend.querySelector('[data-role="cursor"]');
    if (!data || data.samples.length === 0 || !tip || !cursor) {
      return;
    }
    const rect = trend.getBoundingClientRect();
    if (rect.width <= 0) {
      return;
    }
    const share = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
    const start = data.samples[0].time;
    const span = data.samples[data.samples.length - 1].time - start;
    const target = start + share * span;
    let nearest = data.samples[0];
    data.samples.forEach((sample) => {
      if (Math.abs(sample.time - target) < Math.abs(nearest.time - target)) {
        nearest = sample;
      }
    });
    const x = span > 0 ? ((nearest.time - start) / span) * 100 : 50;

    const time = document.createElement("time");
    time.textContent = new Date(nearest.time).toLocaleString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    const reading = document.createElement("strong");
    const formatter = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
    const failed = !nearest.reading;
    reading.textContent = failed ? "Check failed" : formatter.format(nearest.value) + (data.unit ? " " + data.unit : "");
    const state = document.createElement("span");
    state.textContent = defaultStateLabel(nearest.state);
    tip.replaceChildren(time, reading, state);

    cursor.style.setProperty("--mm-x", x + "%");
    tip.style.setProperty("--mm-x", x + "%");
    // Keep the tip inside the tile at either end.
    tip.style.setProperty("--mm-shift", x < 20 ? "0%" : x > 80 ? "-100%" : "-50%");
    cursor.hidden = false;
    tip.hidden = false;
  };

  document.addEventListener("pointermove", (event) => {
    const trend = event.target instanceof Element ? event.target.closest("[data-trend]") : null;
    document.querySelectorAll("[data-trend]").forEach((other) => {
      if (other !== trend) {
        hide(other);
      }
    });
    if (trend) {
      show(trend, event.clientX);
    }
  });
}


// The tone of a MatUi state pill (as MatUi.PillClass draws it on the server): the word inside it is set by the caller.
const statePillTones = ["mat-pill--ok", "mat-pill--warn", "mat-pill--error", "mat-pill--info", "mat-pill--plain"];

function statePillTone(state) {
  switch (state) {
    case "ok":
      return "mat-pill--ok";
    case "warning":
      return "mat-pill--warn";
    case "error":
      return "mat-pill--error";
    case "ack":
      return "mat-pill--info";
    case "paused":
    case "disabled":
      return "mat-pill--plain";
    default:
      return null;
  }
}

function setStatePillTone(pill, state) {
  const tone = statePillTone(state);
  statePillTones.forEach((name) => pill.classList.toggle(name, name === tone));
}

// The sensor-status card: the total, one part of the stacked meter per state (OK, warning, acknowledged, error - paused
// and other are the rest of the track) and the pills with their numbers. The meter's parts are widths given as
// data-value; MatUi.refresh applies them.
function renderDashboardStatusChart(snapshot) {
  const charts = document.querySelectorAll("[data-dashboard-status-chart]");
  if (charts.length === 0) {
    return;
  }

  const counts = getDashboardStatusCounts(snapshot);
  const numberFormatter = new Intl.NumberFormat();

  charts.forEach((chart) => {
    setDashboardStatusText(chart, "status-total", numberFormatter.format(counts.total));
    const ok = counts.items.find((item) => item.key === "ok");
    setDashboardStatusText(chart, "status-ok-share", String(counts.total > 0 && ok ? Math.round((ok.count / counts.total) * 100) : 0));

    counts.items.forEach((item) => {
      setDashboardStatusText(chart, `status-${item.key}-count`, numberFormatter.format(item.count));
      setDashboardStatusText(chart, `status-${item.key}-percent`, formatDashboardStatusPercent(item.count, counts.total));

      const fill = chart.querySelector(`[data-status-fill="${item.key}"]`);
      if (fill) {
        fill.setAttribute("data-value", formatDashboardStatusShare(item.count, counts.total));
      }

      const row = chart.querySelector(`[data-dashboard-status-item="${item.key}"]`);
      if (row && (item.key === "other" || item.key === "paused")) {
        row.hidden = item.count <= 0;
      }
    });

    const meter = chart.querySelector('[data-role="status-meter"]');
    if (meter) {
      meter.setAttribute("aria-label", describeDashboardStatus(counts));
      if (window.MatUi && typeof window.MatUi.refresh === "function") {
        window.MatUi.refresh(meter);
      }
    }
  });
}

function getDashboardStatusCounts(snapshot) {
  const total = Math.max(0, Number(snapshot.sensorCount ?? 0));
  const warning = Math.max(0, Number(snapshot.warningSensorCount ?? 0));
  const ack = Math.max(0, Number(snapshot.acknowledgedSensorCount ?? snapshot.acknowledgedAlertCount ?? 0));
  const error = Math.max(0, Number(snapshot.errorSensorCount ?? 0));
  const paused = Math.max(0, Number(snapshot.pausedSensorCount ?? 0));
  const otherFromSnapshot = snapshot.otherSensorCount == null ? null : Math.max(0, Number(snapshot.otherSensorCount));
  const healthyFromSnapshot = snapshot.healthySensorCount == null ? null : Math.max(0, Number(snapshot.healthySensorCount));
  const other = otherFromSnapshot ?? Math.max(0, total - warning - ack - error - paused - (healthyFromSnapshot ?? 0));
  const healthy = healthyFromSnapshot ?? Math.max(0, total - warning - ack - error - paused - other);

  return {
    total,
    items: [
      { key: "ok", label: "OK", count: healthy },
      { key: "warning", label: "warning", count: warning },
      { key: "ack", label: "acknowledged", count: ack },
      { key: "error", label: "error", count: error },
      { key: "paused", label: "paused", count: paused },
      { key: "other", label: "other", count: other }
    ]
  };
}

function formatDashboardStatusPercent(count, total) {
  if (total <= 0) {
    return "0%";
  }

  return `${Math.round((count / total) * 100)}%`;
}

// The width of one part of the meter, unrounded so the parts add up to the whole bar.
function formatDashboardStatusShare(count, total) {
  if (total <= 0) {
    return "0";
  }

  return String(Math.round((count / total) * 100000) / 1000);
}

function describeDashboardStatus(counts) {
  const parts = counts.items.map((item) => `${item.count} ${item.label}`);
  return `${parts.join(", ")} - of ${counts.total} sensors`;
}

function setDashboardStatusText(chart, role, value) {
  chart.querySelectorAll(`[data-role="${role}"]`).forEach((element) => {
    element.textContent = value;
  });
}

// The alert status at the top of the menu (_NavInfo, as InstanceShellBuilder draws it): the big number is the open
// (unacknowledged) alerts, Err/Warn the active alerts by severity, Ack the acknowledged ones, Pause the paused sensors.
// Everything is alert-based so it agrees with the Alerts page.
function renderNavCounters(snapshot) {
  const count = (value) => Math.max(0, Number(value ?? 0));
  const errors = count(snapshot.errorAlertCount);
  const warnings = count(snapshot.warningAlertCount);
  const acknowledged = count(snapshot.acknowledgedAlertCount);
  const paused = count(snapshot.pausedSensorCount);
  const status = alertStatus(errors, warnings, acknowledged, paused);
  setNavInfo("alerts", count(snapshot.activeAlertCount), status.tone, status.state, { error: errors, warning: warnings, ack: acknowledged, paused });
}

// The same rule as InstanceShellBuilder.AlertStatus - the most severe thing first. Change both together.
function alertStatus(errors, warnings, acknowledged, paused) {
  if (errors > 0) return { tone: "error", state: "Error" };
  if (warnings > 0) return { tone: "warn", state: "Warning" };
  if (acknowledged > 0) return { tone: "info", state: "Ack" };
  if (paused > 0) return { tone: "plain", state: "Paused" };
  return { tone: "ok", state: "OK" };
}

// Writes one menu status block (.mm-navinfo[data-navinfo=key]): the number, the state word, the tone and each small
// count (a zero is drawn quietly - .is-zero).
function setNavInfo(key, value, tone, state, stats) {
  document.querySelectorAll(`[data-navinfo="${key}"]`).forEach((block) => {
    block.dataset.tone = tone;
    const valueElement = block.querySelector("[data-navinfo-value]");
    if (valueElement) {
      valueElement.textContent = String(value);
    }
    const stateElement = block.querySelector("[data-navinfo-state]");
    if (stateElement) {
      stateElement.textContent = state;
    }
    Object.entries(stats || {}).forEach(([statKey, statValue]) => {
      const stat = block.querySelector(`[data-navinfo-stat="${statKey}"]`);
      if (!stat) {
        return;
      }
      const number = stat.querySelector("strong");
      if (number) {
        number.textContent = String(statValue);
      }
      stat.classList.toggle("is-zero", !(statValue > 0));
    });
  });
}

const navBadgeTones = ["error", "warn", "plain", "ok", "info"];

function setNavBadge(key, count, tone) {
  document.querySelectorAll(`[data-nav-badge="${key}"]`).forEach((badge) => {
    // The number is the badge's first text node; the "open" / "paused" after it is for screen readers and stays.
    const first = badge.firstChild;
    if (first && first.nodeType === Node.TEXT_NODE) {
      first.nodeValue = String(count);
    } else {
      badge.insertBefore(document.createTextNode(String(count)), first);
    }

    navBadgeTones.forEach((name) => badge.classList.toggle(`mat-nav__badge--${name}`, name === tone));
    badge.hidden = count <= 0;
  });
}

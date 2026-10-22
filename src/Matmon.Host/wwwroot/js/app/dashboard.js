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
      valueElement.textContent = series.currentValue == null ? "-" : numberFormatter.format(series.currentValue);
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

    const svg = card.querySelector("svg[data-sparkline], svg.sparkline");
    if (svg) {
      drawSparkline(svg, series.points ?? [], lineColor);
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

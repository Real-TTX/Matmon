// Matmon - the shared constants, the one DOMContentLoaded that starts every feature, and helpers several features use.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Live refresh: 5s normally, but 30s when embedded in the Matmon.Cloud tunnel (each AJAX poll is
// proxied over the WebSocket tunnel, so keep that traffic light).
const dashboardRefreshMs = document.documentElement.dataset.embedded === "1" ? 30000 : 5000;
const monitoringTreeCollapsedStorageKey = "matmon-monitoring-tree-collapsed";
const monitoringTreeMoveStorageKey = "matmon-monitoring-tree-move";
const monitoringSizeStorageKey = "matmon-monitoring-size";

// The theme button, the menu and its drawer, the user menu, dialogs, data-confirm, tabs and copy buttons are MatUi's
// (matui.js, loaded before these files) - what starts here is only what Matmon does itself.
// Each feature starts on its own: one that throws is reported in the console and the others still start (one long
// chain used to stop at the first error, and everything after it - often the whole rest of the page - stayed dead).
const featureStarters = [
  "initializePwa", // first: it watches the connection, and the live refreshes below start fetching at once
  "initializeTabHash",
  "initializeStatusRefresh",
  "initializeMapNoc",
  "initializeMapEmbeds",
  "initializeMonitoringTree",
  "initializeTreeContextMenu",
  "initializeMonitoringSizeToggle",
  "initializeDashboardRefresh",
  "initializeDashboardTrends",
  "initializeSensorNameSuggestion",
  "initializeSensorTypePreview",
  "initializeSensorParameterVisibility",
  "initializeScriptEditors",
  "initializeTemplateScopeEditors",
  "initializeScheduleEditors",
  "initializeRowLinks",
  "initializeInteractiveCharts",
  "initializeThresholdEditors",
  "initializeCredentialEditors",
  "initializeNotificationKindEditors",
  "initializeWebhookPresets",
  "initializeNotificationFilter",
  "initializeBackupJobEditor",
  "initializeUpdateScreen",
  "initializeUpdateNotice",
  "initializeDiscoveryJobRefresh",
  "initializeDiscoveryResultTable",
  "initializeDiscoveryJobList",
  "initializeDiscoveryScanForm",
  "initializeMapStages",
  "initializeMapDesigner",
  "initializeMapCarousel",
  "initializeMapClocks",
  "initializeMapLiveData",
  "initializeMultiGraphTooltips",
  "initializeElementPickers",
  "initializeIconPicker",
  "initializeTagInputs",
  "initializeTagOverflow",
  "initializeAlertsTable",
  "initializeLogViewer",
  "initializeSyslogMessages",
  "initializeRemoteRunPreviews",
  "initializeSnmpWalkMibs",
  "initializeRunLoadingIndicator",
];

document.addEventListener("DOMContentLoaded", () => {
  for (const name of featureStarters) {
    const start = window[name];
    if (typeof start !== "function") {
      console.warn("Matmon: " + name + " is not loaded");
      continue;
    }
    try {
      start();
    } catch (error) {
      console.error("Matmon: " + name + " failed", error);
    }
  }
});

// Tabs that remember themselves in the address (data-tab-hash on a MatUi [data-tabs] group). MatUi switches the tabs;
// this keeps "#name" in the address so a reload or a shared link opens the same tab. The tab button is id="tab-<name>",
// its panel id="panel-<name>" (not "<name>", or the browser would jump to it on load).
function initializeTabHash() {
  const open = () => {
    const name = decodeURIComponent((location.hash || "").replace(/^#/, ""));
    if (!name) {
      return;
    }
    document.querySelectorAll("[data-tabs][data-tab-hash]").forEach((group) => {
      const tab = group.querySelector(`[role=tab]#tab-${CSS.escape(name)}`);
      if (tab && tab.getAttribute("aria-selected") !== "true") {
        tab.click();
      }
    });
  };
  document.addEventListener("click", (event) => {
    const tab = event.target.closest && event.target.closest("[data-tab-hash] [role=tab]");
    if (!tab || !tab.id.startsWith("tab-")) {
      return;
    }
    try {
      history.replaceState(null, "", "#" + tab.id.slice(4));
    } catch {
      // A sandboxed frame may refuse; the tab still switched.
    }
  });
  window.addEventListener("hashchange", open);
  open();

  // A form posted from a tabbed page comes back on the same tab: the open tab's "#name" rides along on the action, and a
  // browser keeps it across the redirect after a save. Only for a post back to this page - another page's tabs are not
  // this page's. (The old editors kept the tab in sessionStorage, one form at a time.)
  document.addEventListener("submit", (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || form.method.toLowerCase() !== "post") {
      return;
    }
    const group = form.querySelector("[data-tabs][data-tab-hash]") || form.closest("[data-tabs][data-tab-hash]");
    if (!group) {
      return;
    }
    // The tab that is open now - also one the server opened without "#name" in the address. The first tab is what the
    // page opens on anyway, so it adds nothing (and would ride along on the redirect to another page after a create).
    const tabs = Array.from(group.querySelectorAll('[role="tab"]'));
    const selected = tabs.find((tab) => tab.getAttribute("aria-selected") === "true");
    if (!selected || selected === tabs[0] || !selected.id.startsWith("tab-")) {
      return;
    }
    const hash = "#" + selected.id.slice(4);
    const submitter = event.submitter;
    const viaSubmitter = !!submitter && submitter.hasAttribute("formaction");
    const target = new URL(viaSubmitter ? submitter.formAction : form.action, location.href);
    if (target.origin !== location.origin || target.pathname.toLowerCase() !== location.pathname.toLowerCase()) {
      return;
    }
    target.hash = hash;
    if (viaSubmitter) {
      submitter.formAction = target.href;
    } else {
      form.action = target.href;
    }
  });
}

// Enter in a field submits the button marked data-default-submit rather than whichever submit button comes first in the
// form - on the element editor that was "Restore from template", which overwrites the element's own values. A field that
// handles Enter itself (a tag input, a picker's search) cancels the key first and is left alone.
document.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" || event.defaultPrevented || event.isComposing || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
    return;
  }
  const field = event.target;
  if (!(field instanceof HTMLInputElement) || ["button", "submit", "reset", "checkbox", "radio", "file", "image"].includes(field.type)) {
    return;
  }
  const button = field.form && field.form.querySelector("[data-default-submit]");
  if (!button || button.disabled) {
    return;
  }
  event.preventDefault();
  field.form.requestSubmit(button);
});

// Show a spinner on the button that triggered a run/test/discover. These POST synchronously (a remote run
// waits up to ~12s for the probe to report back), so without feedback the button looks dead. Matched by the
// handler in the submit target's formaction / the form action, so it covers Run now, Run sensor, Run subtree,
// Test and SNMP discover without marking each button. The spinner clears when the page reloads after the POST.
function initializeRunLoadingIndicator() {
  document.addEventListener("submit", (event) => {
    const button = event.submitter;
    if (!button || button.classList.contains("is-loading")) {
      return;
    }

    const action = button.getAttribute("formaction") || event.target.getAttribute("action") || "";
    if (!/[?&]handler=(Run|DiscoverSnmp)/i.test(action) && !button.hasAttribute("data-loading-button")) {
      return;
    }

    // Add the class after the submit is dispatched so the button's name/value is still sent with the form.
    button.classList.add("is-loading");
    button.setAttribute("aria-busy", "true");
  });
}

function redirectToLogin() {
  // When operated through the Matmon.Cloud Full Access tunnel, the instance UI is served under an
  // /instances/{id}/embed prefix on the CLOUD origin. window.location.origin is then the cloud, so a bare
  // "/login" would throw the iframe out to the cloud. The tunnel shim exposes the prefix; use it, and strip
  // it from the returnUrl so the instance's own post-login redirect stays instance-relative (the tunnel
  // re-adds the prefix). Outside the tunnel the prefix is empty and this behaves exactly as before.
  const prefix = window.__matmonEmbedPrefix || "";
  let currentPath = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  if (prefix && currentPath.indexOf(prefix) === 0) {
    currentPath = currentPath.slice(prefix.length) || "/";
  }
  const loginUrl = new URL(prefix + "/login", window.location.origin);
  loginUrl.searchParams.set("returnUrl", currentPath);
  window.location.assign(loginUrl.toString());
}

function drawSparkline(svg, points, lineColor) {
  const linePath = svg.querySelector('[data-role="line"]');
  const areaPath = svg.querySelector('[data-role="area"]');
  if (!linePath || !areaPath) {
    return;
  }

  if (points.length === 0) {
    linePath.setAttribute("d", "");
    areaPath.setAttribute("d", "");
    return;
  }

  const width = 100;
  const height = 40;
  const samples = points.map((point) => ({
    value: Number(point.value ?? 0),
    state: normalizeStateKey(point.state ?? point.currentState)
  }));
  const scaleValues = samples
    .filter((sample) => sample.state !== "error")
    .map((sample) => sample.value);
  const values = scaleValues.length > 0 ? scaleValues : samples.map((sample) => sample.value);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const step = points.length > 1 ? width / (points.length - 1) : 0;
  const padding = 3;

  const coords = samples.map((sample, index) => {
    const x = points.length > 1 ? index * step : width / 2;
    const normalized = sample.state === "error" ? 1 : (sample.value - min) / range;
    const clamped = Math.min(Math.max(normalized, 0), 1);
    const y = sample.state === "error"
      ? padding
      : height - padding - clamped * (height - padding * 2);
    return { x, y };
  });

  const line = coords
    .map((point, index) => `${index === 0 ? "M" : "L"} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`)
    .join(" ");

  const area = [
    `M 0 ${height}`,
    `L ${coords[0].x.toFixed(2)} ${coords[0].y.toFixed(2)}`,
    ...coords.slice(1).map((point) => `L ${point.x.toFixed(2)} ${point.y.toFixed(2)}`),
    `L ${width} ${height}`,
    "Z"
  ].join(" ");

  linePath.setAttribute("d", line);
  linePath.setAttribute("stroke", lineColor);
  areaPath.setAttribute("d", area);
  areaPath.setAttribute("fill", applyAlpha(lineColor, 0.14));
}

function applyAlpha(color, alpha) {
  if (color.startsWith("#")) {
    const hex = color.slice(1);
    const normalized = hex.length === 3
      ? hex.split("").map((part) => part + part).join("")
      : hex;

    if (normalized.length === 6) {
      const red = parseInt(normalized.slice(0, 2), 16);
      const green = parseInt(normalized.slice(2, 4), 16);
      const blue = parseInt(normalized.slice(4, 6), 16);
      return `rgba(${red}, ${green}, ${blue}, ${alpha})`;
    }
  }

  return color;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replace(/`/g, "&#96;");
}

function capitalize(value) {
  if (!value) {
    return "";
  }

  return value.charAt(0).toUpperCase() + value.slice(1);
}

function normalizeStateKey(value) {
  const state = String(value ?? "error").toLowerCase();

  if (state === "healthy") {
    return "ok";
  }

  if (state === "critical" || state === "disabled" || state === "unknown") {
    if (state === "unknown") {
      return "unknown";
    }

    if (state === "disabled") {
      return "disabled";
    }

    return "error";
  }

  if (state === "ok" || state === "warning" || state === "error" || state === "paused") {
    return state;
  }

  return state;
}

function defaultStateLabel(state) {
  if (state === "ok") {
    return "OK";
  }

  if (state === "warning") {
    return "Warning";
  }

  if (state === "error") {
    return "Error";
  }

  if (state === "unknown") {
    return "No data";
  }

  return capitalize(state);
}


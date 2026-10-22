// Matmon - network discovery: the scan form, the live job view and the result table.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).
// The result table is a MatUi table with one group per host (tbody.mat-group): the host is the heading row, its sensor
// suggestions are the rows under it. Opening and closing a host is this script's (data-discovery-toggle), not MatUi's
// data-group-toggle: the open state survives the live re-render and is not remembered across jobs.

function initializeDiscoveryScanForm() {
  const form = document.querySelector("[data-discovery-scan-form]");
  if (!form) {
    return;
  }

  const networkField = form.querySelector("[data-discovery-network-field]");
  const networkInput = form.querySelector("[data-discovery-network-input]");
  const scopeRadios = form.querySelectorAll('input[name="Input.ScanScope"]');

  const applyScope = () => {
    const selected = form.querySelector('input[name="Input.ScanScope"]:checked');
    const known = selected && selected.value === "known";
    if (networkField) {
      networkField.toggleAttribute("hidden", !!known);
    }
  };

  scopeRadios.forEach((radio) => radio.addEventListener("change", applyScope));
  applyScope();

  form.querySelectorAll("[data-discovery-subnet]").forEach((chip) => {
    chip.addEventListener("click", () => {
      if (networkInput) {
        networkInput.value = chip.getAttribute("data-discovery-subnet") || "";
        networkInput.focus();
      }

      const networkRadio = form.querySelector('input[name="Input.ScanScope"][value="network"]');
      if (networkRadio) {
        networkRadio.checked = true;
        applyScope();
      }
    });
  });
}

function initializeDiscoveryJobRefresh() {
  const panel = document.querySelector("[data-discovery-job-panel]");
  if (!panel) {
    return;
  }

  const jobId = panel.dataset.discoveryJobId;
  if (!jobId) {
    return;
  }

  const statusElement = panel.querySelector("[data-discovery-job-status]");
  const messageElement = panel.querySelector("[data-discovery-job-message]");
  const progressPercentElement = panel.querySelector("[data-discovery-progress-percent]");
  const progressScannedElement = panel.querySelector("[data-discovery-progress-scanned]");
  const progressTotalElement = panel.querySelector("[data-discovery-progress-total]");
  const progressMeterElement = panel.querySelector("[data-discovery-progress]");
  const progressBarElement = panel.querySelector("[data-discovery-progress-bar]");
  const countElement = document.querySelector("[data-discovery-result-count]");
  const resultList = document.querySelector("[data-discovery-result-list]");
  const tableElement = document.querySelector("[data-discovery-table]");
  const emptyState = document.querySelector("[data-discovery-empty]");
  const importButton = document.querySelector("[data-discovery-import-button]");
  const importLink = document.querySelector("[data-discovery-import-link]");
  const resultsPanel = document.querySelector("[data-discovery-results-panel]");
  const importMode = resultsPanel?.dataset.discoveryImportMode === "true";

  initializeDiscoveryAssistantActions();

  if (importMode) {
    // Import view = a completed job, rendered server-side and static. No streaming to poll, and we must
    // not overwrite the richly server-rendered tree with the client renderer. Just leave it as-is.
    return;
  }

  const refresh = async () => {
    try {
      const response = await fetch(`/api/discovery-jobs/${encodeURIComponent(jobId)}`, {
        headers: {
          Accept: "application/json"
        }
      });

      if (response.status === 401 || response.status === 403) {
        redirectToLogin();
        return true;
      }

      if (!response.ok) {
        return false;
      }

      const job = await response.json();
      renderDiscoveryJob(job, {
        statusElement,
        messageElement,
        progressPercentElement,
        progressScannedElement,
        progressTotalElement,
        progressMeterElement,
        progressBarElement,
        countElement,
        resultList,
        tableElement,
        emptyState,
        importButton,
        importLink,
        importMode
      });

      return Boolean(job.isComplete);
    } catch {
      return false;
    }
  };

  refresh();
  const interval = window.setInterval(async () => {
    if (await refresh()) {
      window.clearInterval(interval);
    }
  }, 1500);
}

function renderDiscoveryJob(job, elements) {
  const status = String(job.status ?? "Pending");
  const results = Array.isArray(job.results) ? job.results : [];

  if (elements.statusElement) {
    elements.statusElement.textContent = status;
    setDiscoveryStatusPill(elements.statusElement, status);
  }

  if (elements.messageElement) {
    elements.messageElement.textContent = job.message || (job.isComplete ? "Discovery completed." : "Discovery is running.");
  }

  const totalHosts = Number(job.totalHosts ?? 0);
  const scannedHosts = Number(job.scannedHosts ?? 0);
  const progressPercent = Number.isFinite(Number(job.progressPercent))
    ? Math.max(0, Math.min(100, Number(job.progressPercent)))
    : totalHosts > 0
      ? Math.max(0, Math.min(100, Math.floor((scannedHosts / totalHosts) * 100)))
      : job.isComplete
        ? 100
        : 0;

  if (elements.progressPercentElement) {
    elements.progressPercentElement.textContent = `${progressPercent}%`;
  }

  if (elements.progressScannedElement) {
    elements.progressScannedElement.textContent = String(Math.max(0, scannedHosts));
  }

  if (elements.progressTotalElement) {
    elements.progressTotalElement.textContent = String(Math.max(0, totalHosts));
  }

  // The MatUi meter: data-value is what the markup says, --mat-meter what MatUi draws (as its fillMeters does).
  if (elements.progressBarElement) {
    elements.progressBarElement.dataset.value = String(progressPercent);
    elements.progressBarElement.style.setProperty("--mat-meter", `${progressPercent}%`);
  }

  if (elements.progressMeterElement) {
    elements.progressMeterElement.setAttribute("aria-valuenow", String(progressPercent));
  }

  if (elements.countElement) {
    elements.countElement.textContent = String(results.length);
  }

  if (elements.emptyState) {
    elements.emptyState.hidden = results.length > 0;
  }

  if (elements.tableElement) {
    elements.tableElement.hidden = results.length === 0;
  }

  if (elements.importButton) {
    elements.importButton.disabled = results.length === 0;
  }

  if (elements.importLink) {
    elements.importLink.hidden = !(job.isComplete && status.toLowerCase() === "completed" && results.length > 0);
  }

  if (!elements.resultList) {
    return;
  }

  const selectedByAddress = new Map();
  const selectedSuggestions = new Map();
  const expandedByAddress = new Map();
  if (elements.importMode) {
    elements.resultList.querySelectorAll("[data-discovery-address]").forEach((row) => {
      const address = row.dataset.discoveryAddress || "";
      const checkbox = row.querySelector("input[type='checkbox'][name='SelectedHostAddresses']");
      if (address && checkbox) {
        selectedByAddress.set(address, checkbox.checked);
      }

      if (address) {
        expandedByAddress.set(address, row.dataset.discoveryExpanded === "true");
      }

    });

    elements.resultList.querySelectorAll("[data-discovery-suggestion-key]").forEach((suggestionRow) => {
      const suggestionKey = suggestionRow.dataset.discoverySuggestionKey || "";
      const suggestionCheckbox = suggestionRow.querySelector("input[type='checkbox'][name='SelectedSuggestionKeys']");
      if (suggestionKey && suggestionCheckbox) {
        selectedSuggestions.set(suggestionKey, suggestionCheckbox.checked);
      }
    });
  } else {
    elements.resultList.querySelectorAll("[data-discovery-address]").forEach((row) => {
      const address = row.dataset.discoveryAddress || "";
      if (address) {
        expandedByAddress.set(address, row.dataset.discoveryExpanded === "true");
      }
    });
  }

  // The list is the <table>: its host groups are replaced, its caption and head stay.
  elements.resultList.querySelectorAll(":scope > tbody").forEach((group) => group.remove());
  elements.resultList.insertAdjacentHTML("beforeend", results
    .map((result, index) => renderDiscoveryResultRow(result, index, selectedByAddress, selectedSuggestions, expandedByAddress, elements.importMode))
    .join(""));

  applyDiscoveryTableState(document.querySelector("[data-discovery-results-panel]"));
}

// A job's state as a MatUi pill: busy while it waits or runs, then what it came to (the word is the status itself).
const DISCOVERY_PILL_TONES = ["mat-pill--ok", "mat-pill--warn", "mat-pill--error", "mat-pill--info", "mat-pill--busy", "mat-pill--plain", "mat-pill--accent"];

function setDiscoveryStatusPill(element, status) {
  element.classList.remove(...DISCOVERY_PILL_TONES);
  element.classList.add("mat-pill", discoveryStatusPillClass(status));
}

function discoveryStatusPillClass(status) {
  switch (String(status || "").toLowerCase()) {
    case "completed":
      return "mat-pill--ok";
    case "failed":
      return "mat-pill--error";
    case "cancelled":
      return "mat-pill--plain";
    default:
      return "mat-pill--busy";
  }
}

// A suggestion at/above this confidence is "recommended" (default-selected + badged). Mirror in Discovery.cshtml.
const DISCOVERY_RECOMMENDED_MIN = 80;
// The icons of the MatUi sprite, as the server renders them (MatUi.Icon), so the streamed table matches the server's.
const DISCOVERY_HOST_ICON = '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-monitor"></use></svg>';
const DISCOVERY_SENSOR_ICON = '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-activity"></use></svg>';
const DISCOVERY_CHEVRON_ICON = '<svg class="mat-icon mat-group__chev" aria-hidden="true" focusable="false"><use href="#mat-i-chevron-down"></use></svg>';
const DISCOVERY_X_ICON = '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-x"></use></svg>';

function renderDiscoveryResultRow(result, index, selectedByAddress, selectedSuggestions, expandedByAddress, importMode) {
  const address = String(result.address ?? "");
  const hostName = String(result.hostName ?? "");
  const message = String(result.message ?? "");
  const pingAlive = Boolean(result.pingAlive);
  const pingMs = result.pingMs == null ? "" : Number(result.pingMs).toFixed(1).replace(/\.0$/, "");
  const openPorts = Array.isArray(result.openPorts) ? result.openPorts.filter((port) => Number(port) > 0) : [];
  const openPortsText = openPorts.join(", ");
  const snmpResponded = Boolean(result.snmpResponded);
  const snmpSummary = String(result.snmpSummary ?? "");
  const suggestedSensors = Array.isArray(result.suggestedSensors) ? result.suggestedSensors : [];
  const sensorCount = suggestedSensors.length;
  const anyRecommended = suggestedSensors.some((suggestion) => Number(suggestion.confidence ?? 0) >= DISCOVERY_RECOMMENDED_MIN);
  const selected = selectedByAddress.has(address) ? selectedByAddress.get(address) : anyRecommended;
  const expanded = expandedByAddress.has(address) ? expandedByAddress.get(address) : sensorCount > 0;
  const searchText = [
    address,
    hostName,
    openPortsText,
    snmpSummary,
    message,
    ...suggestedSensors.flatMap((suggestion) => [
      String(suggestion.sensorTypeKey ?? ""),
      String(suggestion.name ?? ""),
      String(suggestion.target ?? ""),
      String(suggestion.reason ?? "")
    ])
  ].join(" ");

  const noteRow = message
    ? `<tr class="mat-group__item discovery-node-note"><td colspan="4" data-label="Note">${escapeHtml(message)}</td></tr>`
    : "";
  const sensorRows = sensorCount === 0
    ? `<tr class="mat-group__item discovery-node-note"><td colspan="4" data-label="">No sensor suggestions.</td></tr>`
    : suggestedSensors.map((suggestion) => renderDiscoverySuggestionRow(address, suggestion, selectedSuggestions, importMode)).join("");

  const hostCheck = importMode
    ? `<span class="discovery-node-pick"><label class="mat-check"><input type="checkbox" name="SelectedHostAddresses" value="${escapeAttribute(address)}" data-discovery-host-check aria-label="Create the host ${escapeAttribute(address)}"${selected ? " checked" : ""} /></label></span>`
    : "";
  const groupClass = [
    "mat-group",
    "discovery-node",
    importMode ? (selected ? "is-selected" : "is-disabled") : "",
    expanded ? "" : "is-collapsed"
  ].filter(Boolean).join(" ");

  return `
    <tbody class="${groupClass}"
        data-discovery-address="${escapeAttribute(address)}"
        data-discovery-host="${escapeAttribute(hostName)}"
        data-discovery-text="${escapeAttribute(searchText)}"
        data-discovery-ping="${pingAlive ? "ok" : "none"}"
        data-discovery-ping-ms="${escapeAttribute(String(result.pingMs ?? ""))}"
        data-discovery-port-count="${openPorts.length}"
        data-discovery-snmp="${snmpResponded ? "true" : "false"}"
        data-discovery-sensor-count="${sensorCount}"
        data-discovery-expanded="${expanded ? "true" : "false"}">
      <tr class="mat-group__head">
        <td class="mat-group__cell" colspan="4">
          <div class="mat-group__bar">
            ${hostCheck}
            <button type="button" class="mat-group__toggle discovery-node-toggle" data-discovery-toggle aria-expanded="${expanded ? "true" : "false"}">
              ${DISCOVERY_CHEVRON_ICON}${DISCOVERY_HOST_ICON}
              <span class="mat-group__title"><span class="mat-mono">${escapeHtml(address)}</span>${hostName ? `<span class="discovery-node-sub">${escapeHtml(hostName)}</span>` : ""}</span>
            </button>
            <span class="discovery-node-facts">
              ${pingAlive ? `<span class="mat-tag mat-mono">ping ${escapeHtml(pingMs)} ms</span>` : ""}
              ${openPorts.length > 0 ? `<span class="mat-tag" title="${escapeAttribute(openPortsText)}">${openPorts.length} port${openPorts.length === 1 ? "" : "s"}</span>` : ""}
              ${snmpResponded ? `<span class="mat-tag">SNMP</span>` : ""}
              <span class="discovery-node-count">${sensorCount} sensor${sensorCount === 1 ? "" : "s"}</span>
            </span>
          </div>
        </td>
      </tr>
      ${noteRow}
      ${sensorRows}
    </tbody>
  `;
}

function renderDiscoverySuggestionRow(address, suggestion, selectedSuggestions, importMode) {
  const sensorTypeKey = String(suggestion.sensorTypeKey ?? "");
  const name = String(suggestion.name ?? sensorTypeKey);
  const target = String(suggestion.target ?? "");
  const reason = String(suggestion.reason ?? "");
  const confidence = Number.isFinite(Number(suggestion.confidence)) ? Number(suggestion.confidence) : 0;
  const recommended = confidence >= DISCOVERY_RECOMMENDED_MIN;
  const nameMarkup = `<span class="discovery-sensor-name" title="${escapeAttribute(reason)}">${DISCOVERY_SENSOR_ICON}<span>${escapeHtml(name)}</span></span>`;
  const otherCells = `
      <td data-label="Type"><span class="mat-tag mat-mono">${escapeHtml(sensorTypeKey)}</span></td>
      <td data-label="Target" class="mat-hide-sm mat-mono">${target ? escapeHtml(target) : "-"}</td>`;
  const pill = `<span class="mat-pill ${recommended ? "mat-pill--ok" : "mat-pill--warn"}">${confidence}%</span>`;

  if (!importMode) {
    return `<tr class="mat-group__item discovery-sensor is-readonly"><td data-label="Sensor">${nameMarkup}</td>${otherCells}<td data-label="Confidence">${pill}</td></tr>`;
  }

  const suggestionKey = buildDiscoverySuggestionKey(address, sensorTypeKey, target, name);
  const selected = selectedSuggestions.has(suggestionKey) ? selectedSuggestions.get(suggestionKey) : recommended;
  const badge = recommended ? `<span class="mat-pill mat-pill--accent">Recommended</span>` : "";
  return `
    <tr class="mat-group__item discovery-sensor" data-discovery-suggestion-key="${escapeAttribute(suggestionKey)}" data-discovery-recommended="${recommended ? "true" : "false"}">
      <td data-label="Sensor"><label class="mat-check"><input type="checkbox" name="SelectedSuggestionKeys" value="${escapeAttribute(suggestionKey)}" data-discovery-sensor-check${selected ? " checked" : ""} /><span class="mat-check__text">${nameMarkup}</span></label></td>
      ${otherCells}
      <td data-label="Confidence"><span class="discovery-sensor-state">${badge}${pill}</span></td>
    </tr>
  `;
}

function buildDiscoverySuggestionKey(address, sensorTypeKey, target, name) {
  return `${address}|${sensorTypeKey}|${target}|${name}`;
}

function initializeDiscoveryAssistantActions() {
  const form = document.querySelector("[data-discovery-results-form]");
  if (!form) {
    return;
  }

  // Reflect a host's checkbox onto its group: dim + disable its sensor checkboxes when it won't be imported.
  const syncNodeDisabled = (node) => {
    const hostCheck = node.querySelector("[data-discovery-host-check]");
    const selected = hostCheck ? hostCheck.checked : true; // host-scoped nodes have no toggle: always in.
    node.classList.toggle("is-selected", selected);
    node.classList.toggle("is-disabled", !selected);
    node.querySelectorAll("[data-discovery-sensor-check]").forEach((checkbox) => {
      checkbox.disabled = !selected;
    });
  };

  const applyPreset = (preset) => {
    form.querySelectorAll(".discovery-node").forEach((node) => {
      const hostCheck = node.querySelector("[data-discovery-host-check]");
      const sensorChecks = Array.from(node.querySelectorAll("[data-discovery-sensor-check]"));
      if (preset === "none") {
        if (hostCheck) { hostCheck.checked = false; }
        sensorChecks.forEach((checkbox) => { checkbox.checked = false; });
      } else if (preset === "all") {
        if (hostCheck) { hostCheck.checked = true; }
        sensorChecks.forEach((checkbox) => { checkbox.checked = true; });
        node.classList.add("is-showing-extras"); // everything is checked now - reveal the extras too
      } else {
        // recommended: only the recommended sensors (host iff it has one), extras collapsed again.
        sensorChecks.forEach((checkbox) => {
          const row = checkbox.closest(".discovery-sensor");
          checkbox.checked = row?.dataset.discoveryRecommended === "true";
        });
        if (hostCheck) { hostCheck.checked = sensorChecks.some((checkbox) => checkbox.checked); }
        node.classList.remove("is-showing-extras");
      }
      syncNodeDisabled(node);
    });
  };

  // The presets are a MatUi segmented control: the one in effect is aria-pressed.
  form.querySelectorAll("[data-discovery-preset]").forEach((button) => {
    button.addEventListener("click", () => {
      applyPreset(button.dataset.discoveryPreset || "recommended");
      form.querySelectorAll("[data-discovery-preset]").forEach((other) => {
        other.setAttribute("aria-pressed", other === button ? "true" : "false");
      });
    });
  });

  form.querySelectorAll("[data-discovery-host-check]").forEach((hostCheck) => {
    hostCheck.addEventListener("change", () => {
      const node = hostCheck.closest(".discovery-node");
      if (node) {
        syncNodeDisabled(node);
      }
    });
  });

  // Initial pass: keep the server-rendered checked state but make sure dim + sensor-disabled agree with it.
  form.querySelectorAll(".discovery-node").forEach(syncNodeDisabled);
}

function initializeDiscoveryResultTable() {
  const panel = document.querySelector("[data-discovery-results-panel]");
  if (!panel || panel.dataset.discoveryTableInitialized === "true") {
    return;
  }

  panel.dataset.discoveryTableInitialized = "true";

  const filter = panel.querySelector("[data-discovery-filter]");
  const serviceSelect = panel.querySelector("[data-discovery-service-filter]");
  filter?.addEventListener("input", () => applyDiscoveryTableState(panel));
  // The filter field sits in the import form: Enter filters, it must not submit the import.
  filter?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
    }
  });
  serviceSelect?.addEventListener("change", () => applyDiscoveryTableState(panel));
  wireDiscoveryFilterReset(panel, serviceSelect);

  panel.addEventListener("click", (event) => {
    const more = event.target.closest("[data-discovery-more]");
    if (more) {
      const moreNode = more.closest(".discovery-node");
      if (moreNode) {
        const showing = moreNode.classList.toggle("is-showing-extras");
        more.setAttribute("aria-expanded", showing ? "true" : "false");
      }
      return;
    }

    const toggle = event.target.closest("[data-discovery-toggle]");
    if (!toggle) {
      return;
    }

    const node = toggle.closest(".discovery-node");
    if (!node) {
      return;
    }

    node.dataset.discoveryExpanded = node.dataset.discoveryExpanded !== "true" ? "true" : "false";
    applyDiscoveryTableState(panel);
  });

  applyDiscoveryTableState(panel);
}

function initializeDiscoveryJobList() {
  const panel = document.querySelector("[data-discovery-jobs-panel]");
  if (!panel) {
    return;
  }

  const searchInput = panel.querySelector("[data-discovery-job-filter]");
  const statusSelect = panel.querySelector("[data-discovery-job-status-filter]");
  const visibleCount = panel.querySelector("[data-discovery-job-visible-count]");
  const table = panel.querySelector("[data-discovery-job-table]");
  const noMatch = panel.querySelector("[data-discovery-job-nomatch]");
  const rows = Array.from(panel.querySelectorAll("[data-discovery-job-row]"));
  const apply = () => {
    const search = String(searchInput?.value || "").trim().toLowerCase();
    const status = String(statusSelect?.value || "all").toLowerCase();
    let count = 0;

    rows.forEach((row) => {
      const rowText = String(row.dataset.discoveryJobText || "").toLowerCase();
      const rowStatus = String(row.dataset.discoveryJobStatus || "").toLowerCase();
      const visible = (!search || rowText.includes(search)) && (status === "all" || rowStatus === status);
      row.hidden = !visible;
      if (visible) {
        count++;
      }
    });

    if (visibleCount) {
      visibleCount.textContent = String(count);
    }

    // Nothing left: the empty state takes the table's place (LIST-11).
    const nothing = rows.length > 0 && count === 0;
    if (table) {
      table.hidden = nothing;
    }

    if (noMatch) {
      noMatch.hidden = !nothing;
    }

    syncDiscoveryFilterUi(statusSelect, panel.querySelector("[data-discovery-filter-button]"), panel.querySelector("[data-discovery-filter-chips]"), "Status");
  };

  searchInput?.addEventListener("input", apply);
  statusSelect?.addEventListener("change", apply);
  wireDiscoveryFilterReset(panel, statusSelect);
  apply();
}

function applyDiscoveryTableState(panel) {
  if (!panel) {
    return;
  }

  const body = panel.querySelector("[data-discovery-result-list]");
  if (!body) {
    return;
  }

  const search = String(panel.querySelector("[data-discovery-filter]")?.value || "").trim().toLowerCase();
  const serviceSelect = panel.querySelector("[data-discovery-service-filter]");
  const serviceFilter = String(serviceSelect?.value || "all");

  let visibleCount = 0;
  body.querySelectorAll(".discovery-node").forEach((node) => {
    const visible = discoveryRowMatches(node, search, serviceFilter);
    node.hidden = !visible;
    if (visible) {
      visibleCount++;
    }

    // An open host shows its suggestion rows; a closed one only its heading (MatUi's is-collapsed hides the rest).
    const expanded = node.dataset.discoveryExpanded === "true";
    node.classList.toggle("is-collapsed", !expanded);
    const toggle = node.querySelector("[data-discovery-toggle]");
    if (toggle) {
      toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
    }
  });

  const visibleCountElement = panel.querySelector("[data-discovery-visible-count]");
  if (visibleCountElement) {
    visibleCountElement.textContent = String(visibleCount);
  }

  syncDiscoveryFilterUi(serviceSelect, panel.querySelector("[data-discovery-filter-button]"), panel.querySelector("[data-discovery-filter-chips]"), "Service");
}

// The Filter button and the chip of a filter that applies in the page (LIST-03, LIST-04): the button is marked and counts
// the filter in effect, the chip names it and its X sets it back to "all". Rebuilt only when the value changed, so the
// live refresh does not throw away a focused chip.
function syncDiscoveryFilterUi(select, button, chips, label) {
  if (!select) {
    return;
  }

  const value = select.value || "all";
  const active = value !== "all";

  if (button) {
    button.classList.toggle("is-active", active);
    let badge = button.querySelector(".mat-badge");
    if (active && !badge) {
      badge = document.createElement("span");
      badge.className = "mat-badge";
      badge.textContent = "1";
      button.appendChild(badge);
    } else if (!active && badge) {
      badge.remove();
    }
  }

  if (!chips || chips.dataset.filterValue === value) {
    return;
  }

  chips.dataset.filterValue = value;
  chips.replaceChildren();
  chips.hidden = !active;
  if (!active) {
    return;
  }

  const option = select.options[select.selectedIndex];
  const chip = document.createElement("span");
  chip.className = "mat-chip";
  chip.textContent = `${label}: ${option ? option.text : value}`;
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "mat-chip__x";
  remove.setAttribute("aria-label", `Remove the filter ${label}`);
  remove.innerHTML = DISCOVERY_X_ICON;
  remove.addEventListener("click", () => resetDiscoveryFilter(select));
  chip.appendChild(remove);
  chips.appendChild(chip);
}

function wireDiscoveryFilterReset(panel, select) {
  if (!select) {
    return;
  }

  panel.querySelectorAll("[data-discovery-filter-reset]").forEach((button) => {
    button.addEventListener("click", () => resetDiscoveryFilter(select));
  });
}

function resetDiscoveryFilter(select) {
  select.value = "all";
  select.dispatchEvent(new Event("change", { bubbles: true }));
}

function discoveryRowMatches(row, search, serviceFilter) {
  const searchableText = String(row.dataset.discoveryText || "").toLowerCase();
  if (search && !searchableText.includes(search)) {
    return false;
  }

  switch (serviceFilter) {
    case "ping":
      return row.dataset.discoveryPing === "ok";
    case "ports":
      return Number(row.dataset.discoveryPortCount || "0") > 0;
    case "snmp":
      return row.dataset.discoverySnmp === "true";
    case "sensors":
      return Number(row.dataset.discoverySensorCount || "0") > 0;
    default:
      return true;
  }
}

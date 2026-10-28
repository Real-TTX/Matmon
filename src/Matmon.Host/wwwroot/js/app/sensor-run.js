// Matmon - on-demand runs on a remote probe (the result preview) and the SNMP walk's MIB labels.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// A "Test" or SNMP-discover that was routed to a remote probe runs asynchronously (the primary only
// queued a run job). Each preview card (_RemoteRunPreview) carries the job id; poll GET /api/run-jobs/{id} until it
// completes, then render a compact channel table / discovered-OID table into its [data-remote-run-body].
// Progressive enhancement and dependency-free; the local (in-process) path renders no card and is untouched.
function initializeRemoteRunPreviews() {
  const containers = document.querySelectorAll("[data-remote-run-job]");
  containers.forEach((container) => pollRemoteRunPreview(container));
}

function pollRemoteRunPreview(container) {
  const jobId = container.dataset.remoteRunJob;
  if (!jobId) {
    return;
  }

  const kind = container.dataset.remoteRunKind || "sensor";
  const probeName = container.dataset.remoteRunProbe || "the probe";
  const deadline = Date.now() + 30000; // give up after ~30s; the result still lands on the probe sync

  const tick = async () => {
    try {
      const response = await fetch(`/api/run-jobs/${encodeURIComponent(jobId)}`, {
        headers: { Accept: "application/json" }
      });

      if (response.status === 401 || response.status === 403) {
        redirectToLogin();
        return;
      }

      if (response.ok) {
        const job = await response.json();
        if (job.isComplete) {
          renderRemoteRunResult(container, kind, job);
          return;
        }
      }
    } catch {
      // Transient network error - keep polling until the deadline.
    }

    if (Date.now() >= deadline) {
      const statusElement = container.querySelector("[data-remote-run-status]");
      if (statusElement) {
        // The spinner goes with the old text: nothing is being waited for on this page any more.
        statusElement.textContent = `Still running on probe ${probeName}… the result will appear on the sensor once the probe syncs.`;
      }
      return;
    }

    window.setTimeout(tick, 1500);
  };

  window.setTimeout(tick, 1500);
}

// A MatUi table with a caption for a screen reader; every cell carries its data-label (TABLE-02). The rows are HTML the
// caller built from escaped values.
function remoteRunTable(caption, headers, rows) {
  const head = headers
    .map((header) => `<th scope="col"${header.className ? ` class="${header.className}"` : ""}>${escapeHtml(header.label)}</th>`)
    .join("");
  return `<div class="mat-table-wrap"><table class="mat-table mat-table--compact"><caption class="mat-sr-only">${escapeHtml(caption)}</caption>` +
    `<thead><tr>${head}</tr></thead><tbody>${rows.join("")}</tbody></table></div>`;
}

function renderRemoteRunResult(container, kind, job) {
  const body = container.querySelector("[data-remote-run-body]") || container;

  if (job.error) {
    body.innerHTML =
      `<div class="mat-alert mat-alert--danger"><svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-x-circle"></use></svg>` +
      `<div class="mat-alert__body">Run failed on probe: ${escapeHtml(job.error)}</div></div>`;
    return;
  }

  if (kind === "snmp") {
    const oids = Array.isArray(job.discoveredOids) ? job.discoveredOids : [];
    if (oids.length === 0) {
      body.innerHTML = `<p class="remote-run-preview-status" data-remote-run-status>No OIDs discovered.</p>`;
      return;
    }

    const rows = oids.map((item) =>
      `<tr data-oid="${escapeAttribute(item.oid || "")}" data-raw-value="${escapeAttribute(item.value || "")}">` +
      `<td data-label="Name"><strong data-snmp-walk-name hidden></strong> <span class="mat-mono" data-snmp-walk-oid>${escapeHtml(item.oid || "")}</span></td>` +
      `<td data-label="Value" data-snmp-walk-value>${escapeHtml(item.value || "")}</td>` +
      `<td data-label="Type" class="mat-mono mat-hide-sm">${escapeHtml(item.syntax || "")}</td></tr>`);
    body.innerHTML =
      `<p class="remote-run-preview-status" data-remote-run-status>Discovered ${oids.length} OID${oids.length === 1 ? "" : "s"} on the probe. Re-open the editor after saving to pick channels.</p>` +
      remoteRunTable("OIDs the probe discovered", [
        { label: "Name and OID" },
        { label: "Value" },
        { label: "Type", className: "mat-hide-sm" }
      ], rows);
    // The probe knows no MIBs - the instance names what it found.
    labelSnmpWalkRows(body.querySelectorAll("tr[data-oid]"));
    return;
  }

  const result = job.result;
  if (!result) {
    body.innerHTML = `<p class="remote-run-preview-status" data-remote-run-status>The probe reported no result.</p>`;
    return;
  }

  const state = escapeHtml(String(result.state || "Unknown"));
  const message = result.message ? ` - ${escapeHtml(result.message)}` : "";
  const channels = Array.isArray(result.channels) ? result.channels : [];
  const rows = channels.map((channel) => {
    const label = escapeHtml(channel.label || channel.key || "");
    const value = channel.value === null || channel.value === undefined ? "-" : escapeHtml(String(channel.value));
    const unit = channel.unit ? ` ${escapeHtml(channel.unit)}` : "";
    return `<tr><td data-label="Channel">${label}</td><td data-label="Value" class="mat-num mat-mono">${value}${unit}</td></tr>`;
  });

  body.innerHTML =
    `<p class="remote-run-preview-status" data-remote-run-status>Test on probe: <strong>${state}</strong>${message}</p>` +
    (rows.length > 0
      ? remoteRunTable("Channels the probe reported", [{ label: "Channel" }, { label: "Value", className: "mat-num" }], rows)
      : "");
}

// ---- SNMP walk + MIBs -------------------------------------------------------------------------------------
// The walk table (_SnmpWalkList, inside [data-snmp-walk]) is rendered with names from the instance's MIBs. Uploading a
// MIB from the walk relabels the rows in place through /api/mibs/translate, so nobody has to walk again. The tools above
// the list narrow a long walk (text, numbers only) and tick or untick what is shown in one go; nothing is ticked at first.
function initializeSnmpWalkMibs() {
  document.querySelectorAll("[data-snmp-walk-tools]").forEach((tools) => {
    const scope = tools.closest("[data-snmp-walk]");
    const list = scope?.querySelector("[data-snmp-walk-list]");
    if (!list) {
      return;
    }
    const filter = tools.querySelector("[data-snmp-walk-filter]");
    const numeric = tools.querySelector("[data-snmp-walk-numeric]");
    const count = tools.querySelector("[data-snmp-walk-count]");
    const rows = () => [...list.querySelectorAll("[data-snmp-walk-row]")];
    const boxOf = (row) => row.querySelector("input[type=checkbox]");

    const updateCount = () => {
      const all = rows();
      const ticked = all.filter((row) => boxOf(row)?.checked).length;
      if (count) {
        count.textContent = `${ticked} of ${all.length} ticked`;
      }
    };
    const applyFilter = () => {
      const needle = (filter?.value || "").trim().toLowerCase();
      const onlyNumbers = numeric?.checked === true;
      rows().forEach((row) => {
        const matches = needle.length === 0 || row.textContent.toLowerCase().includes(needle);
        // a ticked row stays visible, so nothing that will be saved is ever hidden
        row.hidden = !boxOf(row)?.checked && (!matches || (onlyNumbers && row.dataset.numeric !== "true"));
      });
    };

    filter?.addEventListener("input", applyFilter);
    numeric?.addEventListener("change", applyFilter);
    tools.querySelectorAll("[data-snmp-walk-tick]").forEach((button) => {
      button.addEventListener("click", () => {
        const on = button.dataset.snmpWalkTick === "on";
        rows().forEach((row) => {
          const box = boxOf(row);
          if (box && (!on || !row.hidden)) {
            box.checked = on;
          }
        });
        applyFilter();
        updateCount();
      });
    });
    list.addEventListener("change", (event) => {
      if (event.target instanceof HTMLInputElement && event.target.type === "checkbox") {
        updateCount();
      }
    });
    updateCount();
  });

  document.querySelectorAll("[data-snmp-mib-upload]").forEach((input) => {
    input.addEventListener("change", async () => {
      if (!input.files || input.files.length === 0) {
        return;
      }
      const bar = input.closest("[data-snmp-mib-bar]");
      const scope = input.closest("[data-snmp-walk]") || bar?.parentElement;
      const status = bar?.querySelector("[data-snmp-mib-status]");
      const form = input.closest("form");
      const token = form?.querySelector('input[name="__RequestVerificationToken"]')?.value;
      const body = new FormData();
      Array.from(input.files).forEach((file) => body.append("files", file));
      if (token) {
        body.append("__RequestVerificationToken", token);
      }
      if (status) {
        status.textContent = "Uploading…";
      }

      try {
        const response = await fetch(input.dataset.uploadUrl, { method: "POST", headers: { Accept: "application/json" }, body });
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }
        const result = await response.json();
        const parts = [];
        if (result.added?.length) {
          parts.push(`Loaded ${result.added.join(", ")}.`);
        }
        if (result.missing?.length) {
          parts.push(`Still missing: ${result.missing.join(", ")}.`);
        }
        if (result.rejected?.length) {
          parts.push(result.rejected.join(" "));
        }
        const rows = scope?.querySelectorAll("[data-snmp-walk-row]") ?? [];
        const named = await labelSnmpWalkRows(rows);
        const summary = bar?.querySelector("[data-snmp-mib-summary] span");
        if (summary) {
          summary.textContent = `${result.modules} MIBs loaded${rows.length ? ` · names for ${named} of ${rows.length} OIDs` : ""}`;
        }
        if (status) {
          status.textContent = parts.join(" ") || "Nothing loaded.";
        }
      } catch (error) {
        if (status) {
          status.textContent = `Upload failed: ${error.message}`;
        }
      } finally {
        input.value = "";
      }
    });
  });
}

/// Names walk rows (elements with data-oid + data-raw-value) from the instance's MIBs. Returns how many got a name.
async function labelSnmpWalkRows(rows) {
  const list = Array.from(rows);
  if (list.length === 0) {
    return 0;
  }
  const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
  const response = await fetch("/api/mibs/translate", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...(token ? { RequestVerificationToken: token } : {}) },
    body: JSON.stringify({ items: list.map((row) => ({ oid: row.dataset.oid, value: row.dataset.rawValue ?? "" })) })
  });
  if (!response.ok) {
    return 0;
  }
  const result = await response.json();
  let named = 0;
  (result.items ?? []).forEach((item, index) => {
    const row = list[index];
    if (!row || !item.name) {
      return;
    }
    named++;
    const name = row.querySelector("[data-snmp-walk-name]");
    if (name) {
      name.textContent = item.name;
      name.hidden = false;
    }
    row.querySelector("[data-snmp-walk-oid]")?.classList.add("is-secondary");
    const value = row.querySelector("[data-snmp-walk-value]");
    if (value) {
      value.textContent = item.units ? `${item.value} ${item.units}` : item.value;
    }
    const module = row.querySelector("[data-snmp-walk-module]");
    if (module) {
      module.textContent = item.module || "";
      module.hidden = !item.module;
    }
    if (item.description) {
      row.title = item.description;
    }
  });
  return named;
}

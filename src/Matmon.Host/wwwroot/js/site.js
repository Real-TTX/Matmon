// Live refresh: 5s normally, but 30s when embedded in the Matmon.Cloud tunnel (each AJAX poll is
// proxied over the WebSocket tunnel, so keep that traffic light).
const dashboardRefreshMs = document.documentElement.dataset.embedded === "1" ? 30000 : 5000;
const themeStorageKey = "matmon-theme";
const monitoringTreeCollapsedStorageKey = "matmon-monitoring-tree-collapsed";
const monitoringTreeMoveStorageKey = "matmon-monitoring-tree-move";
const monitoringSizeStorageKey = "matmon-monitoring-size";

document.addEventListener("DOMContentLoaded", () => {
  initializeThemeToggle();
  initializeMobileSidebarMenu();
  initializeWorkspaceSummaryPlacement();
  initializeClipboardButtons();
  initializeAccountMenu();
  initializeWorkspaceActionMenus();
  initializeMonitoringTree();
  initializeTreeContextMenu();
  initializeMonitoringSizeToggle();
  initializeDashboardRefresh();
  initializeSensorTabs();
  initializeSensorNameSuggestion();
  initializeSensorTypePreview();
  initializeSensorParameterVisibility();
  initializeScriptEditors();
  initializeTemplateScopeEditors();
  initializeScheduleEditors();
  initializeRowLinks();
  initializeInteractiveCharts();
  initializeThresholdEditors();
  initializeCredentialEditors();
  initializeNotificationKindEditors();
  initializeDiscoveryJobRefresh();
  initializeDiscoveryResultTable();
  initializeDiscoveryJobList();
  initializeDiscoveryScanForm();
  initializeMapStages();
  initializeMapDesigner();
  initializeMapCarousel();
  initializeMapClocks();
  initializeMapLiveData();
  initializeElementPickers();
  initializeIconPicker();
  initializeTagInputs();
  initializeTagOverflow();
  initializeAlertsTable();
  initializeRemoteRunPreviews();
  initializeRunLoadingIndicator();
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

// Alerts table: client-side filter tabs + search + paging + row selection, all over the full set
// of rows the server rendered (active + resolved history). Keeps it instant regardless of count;
// the only round-trips are acknowledging (single row button or "Acknowledge selected").
function initializeAlertsTable() {
  const root = document.querySelector("[data-alerts]");
  if (!root) {
    return;
  }

  let rows = Array.from(root.querySelectorAll("[data-alert-row]"));
  const originalOrder = rows.slice();
  const tbody = root.querySelector("[data-alerts-body]");
  const searchInput = root.querySelector("[data-alerts-search]");
  const sortSelect = root.querySelector("[data-alerts-sort]");
  const countEl = root.querySelector("[data-alerts-count]");
  const tabs = Array.from(root.querySelectorAll("[data-alert-filter]"));
  const selectAll = root.querySelector("[data-alerts-select-all]");
  const bulkbar = root.querySelector("[data-alerts-bulkbar]");
  const selectedCountEl = root.querySelector("[data-alerts-selected]");
  const emptyEl = root.querySelector("[data-alerts-empty]");
  const pager = root.querySelector("[data-alerts-pager]");
  const pagerInfo = root.querySelector("[data-alerts-pager-info]");
  const prevBtn = root.querySelector("[data-alerts-prev]");
  const nextBtn = root.querySelector("[data-alerts-next]");
  const pageSizeSelect = root.querySelector("[data-alerts-page-size]");
  const timeSelect = root.querySelector("[data-alerts-time]");

  let filter = root.dataset.initialFilter || "all";
  let query = "";
  let page = 1;
  let pageSize = pageSizeSelect ? Number(pageSizeSelect.value) || 50 : 50;
  let sortMode = sortSelect ? sortSelect.value : "default";
  let timeRange = timeSelect ? timeSelect.value : "all";

  // Sorting reorders the actual rows in the DOM (re-append) so the visible page shows them in order;
  // "default" restores the server's order (active first, newest last-seen) - which is also what the
  // server-rendered first page assumes, so the initial paint never reshuffles.
  const severityRank = (row) => row.dataset.active !== "true" ? 0 : (row.dataset.state === "error" ? 3 : row.dataset.state === "warning" ? 2 : 1);
  const comparatorFor = (mode) => {
    const num = (row, key) => Number(row.dataset[key] || 0);
    switch (mode) {
      case "last-desc": return (a, b) => num(b, "last") - num(a, "last");
      case "last-asc": return (a, b) => num(a, "last") - num(b, "last");
      case "first-desc": return (a, b) => num(b, "first") - num(a, "first");
      case "first-asc": return (a, b) => num(a, "first") - num(b, "first");
      case "severity": return (a, b) => severityRank(b) - severityRank(a) || num(b, "last") - num(a, "last");
      case "element": return (a, b) => (a.dataset.name || "").localeCompare(b.dataset.name || "");
      default: return null;
    }
  };
  const applySort = () => {
    const comparator = comparatorFor(sortMode);
    rows = comparator ? originalOrder.slice().sort(comparator) : originalOrder.slice();
    if (tbody) {
      // Reparent in one batch (a fragment) so the browser reflows once, not once per row.
      const fragment = document.createDocumentFragment();
      rows.forEach((row) => fragment.appendChild(row));
      tbody.appendChild(fragment);
    }
  };

  const matchesFilter = (row) => {
    const active = row.dataset.active === "true";
    const ack = row.dataset.ack === "true";
    const state = row.dataset.state;
    switch (filter) {
      case "open": return active && !ack;
      case "error": return active && !ack && state === "error";
      case "warning": return active && !ack && state === "warning";
      case "ack": return active && ack;
      case "recovered": return active && row.dataset.recovered === "true";
      case "paused": return active && state === "paused";
      case "history": return !active;
      default: return active; // "all" = every active alert
    }
  };

  const matchesSearch = (row) => !query || (row.dataset.search || "").includes(query);

  // Time filter on the alert's AGE (first seen), so "older than 7 days" catches long-standing/chronic
  // problems, not just stale last-activity. data-first is Unix ms, directly comparable to Date.now().
  const matchesTime = (row) => {
    if (timeRange === "all") { return true; }
    const first = Number(row.dataset.first || 0);
    if (!first) { return true; }
    const age = Date.now() - first;
    const day = 86400000;
    switch (timeRange) {
      case "24h": return age <= day;
      case "7d": return age <= 7 * day;
      case "30d": return age <= 30 * day;
      case "older-24h": return age > day;
      case "older-7d": return age > 7 * day;
      default: return true;
    }
  };

  const updateSelection = () => {
    const checked = root.querySelectorAll("[data-alerts-check]:checked").length;
    if (selectedCountEl) {
      selectedCountEl.textContent = String(checked);
    }
    if (bulkbar) {
      bulkbar.hidden = checked === 0;
    }
    if (selectAll) {
      const visible = Array.from(root.querySelectorAll("[data-alert-row]:not([hidden]) [data-alerts-check]"));
      selectAll.checked = visible.length > 0 && visible.every((cb) => cb.checked);
    }
  };

  const resetSelection = () => {
    root.querySelectorAll("[data-alerts-check]").forEach((cb) => { cb.checked = false; });
    if (selectAll) {
      selectAll.checked = false;
    }
    updateSelection();
  };

  const apply = () => {
    const matched = rows.filter((row) => matchesFilter(row) && matchesSearch(row) && matchesTime(row));
    const total = matched.length;
    const pageCount = Math.max(1, Math.ceil(total / pageSize));
    page = Math.min(Math.max(1, page), pageCount);
    const start = (page - 1) * pageSize;

    rows.forEach((row) => { row.hidden = true; });
    matched.slice(start, start + pageSize).forEach((row) => { row.hidden = false; });

    tabs.forEach((tab) => tab.classList.toggle("is-active", tab.dataset.alertFilter === filter));

    if (countEl) {
      countEl.textContent = String(total);
    }
    if (emptyEl) {
      emptyEl.hidden = total !== 0 || rows.length === 0;
    }
    if (pager) {
      pager.hidden = total === 0;
    }
    if (pagerInfo) {
      pagerInfo.textContent = `Page ${page} of ${pageCount} · ${total} alert${total === 1 ? "" : "s"}`;
    }
    if (prevBtn) {
      prevBtn.disabled = page <= 1;
    }
    if (nextBtn) {
      nextBtn.disabled = page >= pageCount;
    }

    resetSelection();
  };

  tabs.forEach((tab) => {
    tab.addEventListener("click", (event) => {
      event.preventDefault();
      filter = tab.dataset.alertFilter || "all";
      page = 1;
      try {
        const url = new URL(location.href);
        if (filter === "all") {
          url.searchParams.delete("alertFilter");
        } else {
          url.searchParams.set("alertFilter", filter);
        }
        history.replaceState(null, "", url);
      } catch (error) {
        /* URL may be unavailable; the tab still switches */
      }
      apply();
    });
  });

  if (searchInput) {
    // Debounce so a fast typist doesn't re-run apply() (which touches every rendered row) on
    // every keystroke.
    let searchTimer = null;
    searchInput.addEventListener("input", () => {
      window.clearTimeout(searchTimer);
      searchTimer = window.setTimeout(() => {
        query = searchInput.value.trim().toLowerCase();
        page = 1;
        apply();
      }, 140);
    });
  }

  if (pageSizeSelect) {
    pageSizeSelect.addEventListener("change", () => {
      pageSize = Number(pageSizeSelect.value) || 50;
      page = 1;
      apply();
    });
  }

  if (timeSelect) {
    timeSelect.addEventListener("change", () => {
      timeRange = timeSelect.value;
      page = 1;
      apply();
    });
  }

  if (sortSelect) {
    sortSelect.addEventListener("change", () => {
      sortMode = sortSelect.value;
      page = 1;
      applySort();
      apply();
    });
  }

  if (prevBtn) {
    prevBtn.addEventListener("click", () => { page -= 1; apply(); });
  }
  if (nextBtn) {
    nextBtn.addEventListener("click", () => { page += 1; apply(); });
  }

  if (selectAll) {
    selectAll.addEventListener("change", () => {
      root.querySelectorAll("[data-alert-row]:not([hidden]) [data-alerts-check]").forEach((cb) => {
        cb.checked = selectAll.checked;
      });
      updateSelection();
    });
  }

  root.querySelectorAll("[data-alerts-check]").forEach((cb) => {
    cb.addEventListener("change", updateSelection);
  });

  apply();
}

// Sensor-chip tags: show as many as fit on the meta line, collapse the rest into a "+N" chip
// whose title (mouseover) lists the hidden tags. A single ResizeObserver re-measures each strip
// on resize and when it first becomes visible (e.g. a tree node is expanded), so it's correct
// without any layout thrash on the server side.
let tagOverflowObserver = null;

function initializeTagOverflow(root) {
  const scope = root || document;
  const strips = scope.querySelectorAll("[data-tag-overflow]");
  if (strips.length === 0) {
    return;
  }

  if (!tagOverflowObserver && "ResizeObserver" in window) {
    tagOverflowObserver = new ResizeObserver((entries) => {
      entries.forEach((entry) => applyTagOverflow(entry.target));
    });
  }

  strips.forEach((strip) => {
    applyTagOverflow(strip);
    if (tagOverflowObserver) {
      tagOverflowObserver.observe(strip);
    }
  });
}

function applyTagOverflow(strip) {
  const chips = Array.from(strip.querySelectorAll("[data-tag-chip]"));
  if (chips.length === 0) {
    return;
  }

  let more = strip.querySelector("[data-tag-more]");
  if (!more) {
    more = document.createElement("span");
    more.className = "tree-tag is-mini tag-overflow-more";
    more.setAttribute("data-tag-more", "");
    strip.appendChild(more);
  }

  // Reset to "all visible" before measuring.
  chips.forEach((chip) => {
    chip.hidden = false;
  });
  more.hidden = true;

  if (strip.clientWidth === 0) {
    return; // not laid out yet - the observer will call us again once it is
  }

  const limit = strip.getBoundingClientRect().right + 1;
  const last = chips[chips.length - 1];
  if (last.getBoundingClientRect().right <= limit) {
    return; // everything fits
  }

  // Overflow: reveal the +N chip and hide tags from the end until it fits.
  more.hidden = false;
  const hidden = [];
  for (let i = chips.length - 1; i >= 0; i--) {
    chips[i].hidden = true;
    hidden.unshift(chips[i].textContent.trim());
    more.textContent = "+" + hidden.length;
    more.title = hidden.join(", ");
    if (more.getBoundingClientRect().right <= limit) {
      break;
    }
  }
}

// Tile size (S/M/L) is a live, client-only preference on <html> + localStorage. The
// Monitoring page also writes the attribute before first paint (inline script), so
// there is no flash and - crucially - no URL round-trip/redirect to switch size.
function initializeMonitoringSizeToggle() {
  const buttons = document.querySelectorAll("[data-monitoring-size-set]");
  if (buttons.length === 0) {
    return;
  }

  const normalize = (value) => {
    const v = String(value || "").trim().toLowerCase();
    return v === "s" || v === "l" ? v : "m";
  };

  const apply = (size, persist) => {
    const normalized = normalize(size);
    document.documentElement.dataset.monitoringSize = normalized;
    if (persist) {
      try {
        localStorage.setItem(monitoringSizeStorageKey, normalized);
      } catch {
        // Size preference still works for this visit even without storage.
      }
    }
    buttons.forEach((button) => {
      button.classList.toggle("is-active", button.dataset.monitoringSizeSet === normalized);
    });
  };

  apply(document.documentElement.dataset.monitoringSize, false);
  buttons.forEach((button) => {
    button.addEventListener("click", () => apply(button.dataset.monitoringSizeSet, true));
  });
}

// Right-click anywhere on a tree node opens that node's action menu AT THE CURSOR
// (the deepest node under the cursor wins, so right-clicking a child sensor opens the
// sensor's menu). We just record the cursor on the menu and open it; the shared menu
// code (positionMenu) reads _openAtPointer and places it there, hidden until ready -
// the same path the ⋯ button uses (which anchors to itself, with no _openAtPointer).
function initializeTreeContextMenu() {
  document.querySelectorAll("[data-monitoring-tree]").forEach((tree) => {
    tree.addEventListener("contextmenu", (event) => {
      const target = event.target instanceof Element ? event.target : null;
      // Only react when the cursor is actually over a sensor chip or a container row -
      // not the gaps/padding around them. Otherwise closest("[data-tree-node]") would
      // grab the nearest ancestor node and open a menu for an element you're not on.
      const hit = target?.closest(".monitoring-sensor-chip, .monitoring-tree-row");
      const details = hit?.querySelector("details.workspace-action-menu");
      if (!details) {
        return;
      }
      event.preventDefault();

      document.querySelectorAll("details.workspace-action-menu[open]").forEach((open) => {
        if (open !== details) {
          open.open = false;
        }
      });

      details._openAtPointer = { x: event.clientX, y: event.clientY };
      details.open = true;
      // Focus the summary so :focus-within keeps the chip's action cluster visible
      // while the menu is open (otherwise moving onto the menu would hide it).
      details.querySelector(":scope > summary")?.focus({ preventScroll: true });
    });
  });
}

function initializeTagInputs() {
  const splitTags = (value) => (value || "")
    .split(/[,\n;]+/)
    .map((part) => part.trim())
    .filter(Boolean);

  document.querySelectorAll("input[data-tag-input]").forEach((input) => {
    if (input.dataset.tagInputReady === "1") {
      return;
    }
    input.dataset.tagInputReady = "1";
    input.type = "hidden";

    let tags = splitTags(input.value);

    const wrap = document.createElement("div");
    wrap.className = "tag-input form-control workspace-input";
    const chipList = document.createElement("div");
    chipList.className = "tag-input-chips";
    const field = document.createElement("input");
    field.type = "text";
    field.className = "tag-input-field";
    field.autocomplete = "off";
    // Suggest existing tags (datalist rendered by _TagSuggestions) when available.
    if (document.getElementById("matmon-tag-suggestions")) {
      field.setAttribute("list", "matmon-tag-suggestions");
    }
    field.placeholder = tags.length ? "Add tag…" : (input.getAttribute("placeholder") || "Add tag…");
    wrap.appendChild(chipList);
    wrap.appendChild(field);
    input.parentNode.insertBefore(wrap, input.nextSibling);

    const commit = () => {
      input.value = tags.join(", ");
      field.placeholder = tags.length ? "Add tag…" : (input.getAttribute("placeholder") || "Add tag…");
    };

    const render = () => {
      chipList.replaceChildren();
      tags.forEach((tag, index) => {
        const chip = document.createElement("span");
        chip.className = "tag-input-chip";
        const label = document.createElement("span");
        label.textContent = tag;
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "tag-input-remove";
        remove.setAttribute("aria-label", `Remove ${tag}`);
        remove.textContent = "×";
        remove.addEventListener("click", () => {
          tags.splice(index, 1);
          commit();
          render();
          field.focus();
        });
        chip.appendChild(label);
        chip.appendChild(remove);
        chipList.appendChild(chip);
      });
    };

    const addFrom = (raw) => {
      splitTags(raw).forEach((tag) => {
        if (!tags.some((existing) => existing.toLowerCase() === tag.toLowerCase())) {
          tags.push(tag);
        }
      });
      field.value = "";
      commit();
      render();
    };

    field.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === ",") {
        event.preventDefault();
        if (field.value.trim()) {
          addFrom(field.value);
        }
      } else if (event.key === "Backspace" && field.value === "" && tags.length > 0) {
        tags.pop();
        commit();
        render();
      }
    });
    field.addEventListener("blur", () => {
      if (field.value.trim()) {
        addFrom(field.value);
      }
    });
    wrap.addEventListener("click", (event) => {
      if (event.target === wrap || event.target === chipList) {
        field.focus();
      }
    });

    commit();
    render();
  });
}

// Icon picker: one shared modal (visual glyph grid + search) opened by any [data-icon-picker-trigger].
// Trigger wiring is idempotent per element so it survives cloned map tiles; modal wiring runs once.
// The active field is held on the modal element so the (once-wired) modal handlers see the current target.
function initializeIconPicker() {
  const modal = document.querySelector("[data-icon-picker-modal]");
  if (!modal) {
    return;
  }

  const grid = modal.querySelector("[data-icon-picker-grid]");
  const searchInput = modal.querySelector("[data-icon-picker-search]");
  const empty = modal.querySelector("[data-icon-picker-empty]");
  const cells = grid ? [...grid.querySelectorAll(".icon-picker-cell")] : [];

  const applyFilter = () => {
    const query = (searchInput?.value || "").trim().toLowerCase();
    let shown = 0;
    cells.forEach((cell) => {
      const match = !query || (cell.dataset.iconSearch || "").includes(query);
      cell.hidden = !match;
      if (match) {
        shown += 1;
      }
    });
    if (empty) {
      empty.hidden = shown > 0;
    }
  };

  const close = () => {
    modal.hidden = true;
    modal._iconField = null;
  };

  const open = (field) => {
    modal._iconField = field;
    const current = field.querySelector("[data-icon-picker-value]")?.value || "";
    cells.forEach((cell) => cell.classList.toggle("is-active", (cell.dataset.iconKey || "") === current));
    if (searchInput) {
      searchInput.value = "";
    }
    applyFilter();
    modal.hidden = false;
    searchInput?.focus();
    grid?.querySelector(".icon-picker-cell.is-active")?.scrollIntoView({ block: "nearest" });
  };

  const choose = (key, cell) => {
    const field = modal._iconField;
    if (!field) {
      return;
    }
    const input = field.querySelector("[data-icon-picker-value]");
    const label = field.querySelector("[data-icon-picker-trigger-label]");
    const glyph = field.querySelector("[data-icon-picker-trigger-glyph]");
    const sourceGlyph = cell?.querySelector(".icon-picker-glyph")?.firstElementChild;
    if (input) {
      input.value = key;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
    if (label) {
      label.textContent = key || "Auto (by type)";
    }
    if (glyph && sourceGlyph) {
      glyph.replaceChildren(sourceGlyph.cloneNode(true));
    }
    // Live WYSIWYG: reflect a chosen icon on the tile itself (skip Auto - the tile keeps its kind icon).
    if (key && sourceGlyph) {
      const panel = field.closest("[data-map-property-panel]");
      const tileIndex = panel?.dataset.tileIndex;
      if (tileIndex !== undefined) {
        document.querySelector(`[data-map-tile][data-tile-index="${tileIndex}"] .map-tile-icon`)
          ?.replaceChildren(sourceGlyph.cloneNode(true));
      }
    }
    close();
  };

  if (modal.dataset.iconPickerReady !== "1") {
    modal.dataset.iconPickerReady = "1";
    searchInput?.addEventListener("input", applyFilter);
    searchInput?.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        close();
      }
    });
    modal.addEventListener("click", (event) => {
      if (event.target.closest("[data-icon-picker-close]")) {
        close();
        return;
      }
      const cell = event.target.closest(".icon-picker-cell");
      if (cell) {
        choose(cell.dataset.iconKey || "", cell);
      }
    });
  }

  document.querySelectorAll("[data-icon-picker-trigger]").forEach((trigger) => {
    if (trigger.dataset.iconPickerBound === "1") {
      return;
    }
    trigger.dataset.iconPickerBound = "1";
    trigger.addEventListener("click", (event) => {
      event.preventDefault();
      const field = trigger.closest(".icon-picker-field");
      if (field) {
        open(field);
      }
    });
  });
}

function initializeElementPickers() {
  document.querySelectorAll("[data-element-picker]").forEach((picker) => {
    if (picker.dataset.pickerReady === "1") {
      return;
    }
    picker.dataset.pickerReady = "1";
    const valueInput = picker.querySelector("[data-picker-value]");
    const trigger = picker.querySelector("[data-picker-open]");
    const label = picker.querySelector("[data-picker-label]");
    const triggerPath = picker.querySelector("[data-picker-trigger-path]");
    const backdrop = picker.querySelector("[data-picker-dialog]");
    const search = picker.querySelector("[data-picker-search]");
    const tagFilter = picker.querySelector("[data-picker-tag]");
    const list = picker.querySelector("[data-picker-list]");
    const tagList = backdrop.querySelector("[data-picker-tag-list]");
    const empty = picker.querySelector("[data-picker-empty]");
    const closeButton = picker.querySelector("[data-picker-close]");
    const modeButtons = Array.from(picker.querySelectorAll("[data-picker-mode]"));
    if (!valueInput || !trigger || !backdrop || !list) {
      return;
    }

    // Options from BOTH the element tree list and (when tags are allowed) the tag list.
    const options = Array.from(backdrop.querySelectorAll("[data-picker-option]"));

    const applyFilter = () => {
      const tagMode = picker.classList.contains("is-tag-mode");
      const term = (search?.value || "").trim().toLowerCase();
      const tag = (tagFilter?.value || "").trim().toLowerCase();
      // Tree (indented) when browsing; flat list with paths when filtering.
      list.classList.toggle("is-flat", term !== "" || tag !== "");
      let visible = 0;
      options.forEach((option) => {
        const inTagList = option.closest("[data-picker-tag-list]") !== null;
        // Only the active mode's list participates (the other is hidden by .is-tag-mode).
        if (tagMode !== inTagList) {
          option.hidden = true;
          return;
        }
        const isClear = option.classList.contains("element-picker-clear");
        const haystack = option.getAttribute("data-search") || "";
        const tags = option.getAttribute("data-tags") || "";
        const matchesText = term === "" || haystack.includes(term);
        const matchesTag = tag === "" || tags.split(" ").includes(tag);
        // The "none" row is hidden while filtering so it doesn't masquerade as a result.
        const show = isClear ? term === "" && tag === "" : matchesText && matchesTag;
        option.hidden = !show;
        if (show && !isClear) {
          visible += 1;
        }
      });
      if (empty) {
        empty.hidden = visible > 0;
      }
    };

    const setMode = (mode) => {
      picker.classList.toggle("is-tag-mode", mode === "tag");
      modeButtons.forEach((button) => button.classList.toggle("is-active", button.dataset.pickerMode === mode));
      if (tagFilter) {
        tagFilter.value = "";
      }
      applyFilter();
    };
    modeButtons.forEach((button) => button.addEventListener("click", () => setMode(button.dataset.pickerMode)));

    const open = () => {
      backdrop.hidden = false;
      backdrop.style.display = "";
      document.body.classList.add("element-picker-open");
      if (search) {
        search.value = "";
      }
      if (tagFilter) {
        tagFilter.value = "";
      }
      applyFilter();
      window.setTimeout(() => search?.focus(), 0);
    };

    const close = () => {
      backdrop.hidden = true;
      backdrop.style.display = "none";
      document.body.classList.remove("element-picker-open");
    };

    const choose = (option) => {
      const id = option.getAttribute("data-id") || "";
      const name = option.getAttribute("data-name") || "";
      const path = option.getAttribute("data-path") || "";
      const isTag = id.startsWith("tag:");
      valueInput.value = id;
      valueInput.dataset.selectedName = id ? name : "";
      if (label) {
        label.textContent = id ? (isTag ? `# ${name}` : name) : (trigger.getAttribute("data-placeholder") || label.textContent);
      }
      if (triggerPath) {
        triggerPath.textContent = path;
      }
      trigger.classList.toggle("is-empty", !id);
      options.forEach((candidate) => candidate.classList.toggle("is-selected", candidate === option && !!id));
      close();
      try {
        valueInput.dispatchEvent(new Event("change", { bubbles: true }));
      } catch (error) {
        console.error("element picker change handler failed", error);
      }
    };

    trigger.setAttribute("data-placeholder", label ? label.textContent : "");
    trigger.addEventListener("click", open);
    closeButton?.addEventListener("click", close);
    // When the picker sits inside a <label> (e.g. the map tile "Target" field), the
    // label forwards clicks on non-control descendants to its first labelable control -
    // the trigger button - which instantly re-opens the dialog after a selection/close.
    // Swallow the default action for clicks inside the dialog that aren't on a real
    // control so the label can't re-trigger the button.
    backdrop.addEventListener("click", (event) => {
      const target = event.target;
      if (target instanceof Element && !target.closest("input, select, textarea, button, a")) {
        event.preventDefault();
      }
    }, true);
    backdrop.addEventListener("click", (event) => {
      if (event.target === backdrop) {
        close();
      }
    });
    search?.addEventListener("input", applyFilter);
    tagFilter?.addEventListener("change", applyFilter);
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && !backdrop.hidden) {
        close();
      }
    });
    options.forEach((option) => {
      option.addEventListener("click", () => choose(option));
      option.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          choose(option);
        }
      });
    });
  });
}


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
    const items = tile.querySelectorAll(".map-tile-row");
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

  const patchTile = (tile, data) => {
    tile.dataset.state = data.stateKey || "unknown";
    const badge = tile.querySelector("[data-tile-state-label]");
    if (badge) {
      badge.dataset.state = data.stateKey || "unknown";
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
      const label = progress.querySelector("strong");
      if (label) {
        label.textContent = `${percent.toFixed(1)}%`;
      }
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

    const sla = tile.querySelector(".map-tile-sla");
    if (sla && data.sla) {
      setText(sla, "strong", data.sla.percent === null || data.sla.percent === undefined
        ? "-"
        : `${Number(data.sla.percent).toFixed(2).replace(/\.?0+$/, "")} %`);
      setText(sla, "small", data.sla.label || "");
    }

    patchRows(tile, data.rows);
    patchPins(tile, data.pins);
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
    const autoplay = carousel.hasAttribute("data-map-carousel-autoplay");
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
      dots.forEach((dot, i) => dot.classList.toggle("is-active", i === active));
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

function initializeWorkspaceSummaryPlacement() {
  const summaryStrip = document.querySelector(".workspace-summary-strip");
  if (!summaryStrip) {
    return;
  }

  const targetHeader = document.querySelector("main .page-header");
  if (!targetHeader) {
    // No header to host it - reveal it where it already is (the sidebar).
    summaryStrip.classList.add("is-placed");
    return;
  }

  const shouldSkipMove = targetHeader.matches(
    ".dashboard-header, .sensor-header, .probe-install-header"
  ) || targetHeader.querySelector(
    ".dashboard-header-summary, .page-header-summary, .probe-install-summary, .user-edit-summary"
  );

  if (shouldSkipMove) {
    // Left where it was rendered (in the sidebar) - just reveal it in place.
    summaryStrip.classList.add("is-placed");
    return;
  }

  targetHeader.classList.add("has-summary");
  targetHeader.appendChild(summaryStrip);
  summaryStrip.classList.add("is-placed");
}

function initializeThemeToggle() {
  const buttons = Array.from(document.querySelectorAll("[data-theme-toggle]"));
  if (buttons.length === 0) {
    return;
  }

  const media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
  const labelFor = { light: "Light", dark: "Dark", system: "System" };
  const nextMode = { light: "dark", dark: "system", system: "light" };

  const readMode = () => {
    try {
      const stored = localStorage.getItem(themeStorageKey);
      if (stored === "light" || stored === "dark" || stored === "system") {
        return stored;
      }
    } catch {
      // ignore
    }
    return "system"; // default: follow the OS
  };

  const apply = (mode) => {
    const dark = mode === "dark" || (mode === "system" && media && media.matches);
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    document.documentElement.dataset.themeMode = mode;

    buttons.forEach((button) => {
      const label = button.querySelector("[data-theme-label]");
      button.title = "Theme: " + labelFor[mode] + " - click to change";
      button.setAttribute("aria-label", button.title);
      if (label) {
        label.textContent = labelFor[mode];
      }
    });
  };

  apply(readMode());

  buttons.forEach((button) => {
    button.addEventListener("click", () => {
      const mode = nextMode[readMode()] || "light";
      try {
        localStorage.setItem(themeStorageKey, mode);
      } catch {
        // Theme selection stays functional even if storage is unavailable.
      }
      apply(mode);
    });
  });

  // Re-resolve when the OS theme changes while in "system" mode.
  if (media) {
    const onChange = () => { if (readMode() === "system") { apply("system"); } };
    if (media.addEventListener) { media.addEventListener("change", onChange); }
    else if (media.addListener) { media.addListener(onChange); }
  }
}

function initializeMobileSidebarMenu() {
  const shell = document.querySelector(".app-shell");
  const sidebar = document.querySelector(".app-sidebar");
  const toggle = document.querySelector("[data-sidebar-toggle]");
  const backdrop = document.querySelector("[data-sidebar-backdrop]");
  if (!shell || !sidebar || !toggle || !backdrop) {
    return;
  }

  const mobileQuery = window.matchMedia("(max-width: 991.98px), (hover: none) and (pointer: coarse)");

  const setOpen = (open) => {
    const shouldOpen = Boolean(open) && mobileQuery.matches;

    if (shouldOpen && window.scrollY > 0) {
      window.scrollTo(0, 0);
    }

    shell.classList.toggle("is-sidebar-open", shouldOpen);
    document.body.classList.toggle("is-sidebar-open", shouldOpen);
    toggle.setAttribute("aria-expanded", String(shouldOpen));
    toggle.setAttribute("aria-label", shouldOpen ? "Close navigation menu" : "Open navigation menu");
    backdrop.hidden = !shouldOpen;
  };

  const syncResponsiveState = () => {
    if (!mobileQuery.matches) {
      setOpen(false);
    } else {
      backdrop.hidden = !shell.classList.contains("is-sidebar-open");
    }
  };

  toggle.addEventListener("click", () => {
    setOpen(!shell.classList.contains("is-sidebar-open"));
  });

  backdrop.addEventListener("click", () => setOpen(false));

  sidebar.addEventListener("click", (event) => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target) {
      return;
    }

    const actionable = target.closest(
      "a.topnav-link, a.account-login-button, .sidebar-alert-status-main, .sidebar-icon-button, .account-menu-action, .account-menu-form button"
    );
    if (actionable && mobileQuery.matches) {
      setOpen(false);
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      setOpen(false);
    }
  });

  if (typeof mobileQuery.addEventListener === "function") {
    mobileQuery.addEventListener("change", syncResponsiveState);
  } else if (typeof mobileQuery.addListener === "function") {
    mobileQuery.addListener(syncResponsiveState);
  }

  syncResponsiveState();
}

function initializeAccountMenu() {
  const menus = Array.from(document.querySelectorAll("details.account-menu"));
  if (menus.length === 0) {
    return;
  }

  const closeMenus = (except = null) => {
    menus.forEach((menu) => {
      if (menu !== except) {
        menu.open = false;
      }
    });
  };

  menus.forEach((menu) => {
    menu.addEventListener("toggle", () => {
      if (menu.open) {
        closeMenus(menu);
      }
    });
  });

  document.addEventListener("click", (event) => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target || !target.closest("details.account-menu")) {
      closeMenus();
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeMenus();
    }
  });
}

function initializeWorkspaceActionMenus() {
  const menus = Array.from(document.querySelectorAll("details.workspace-action-menu"));
  if (menus.length === 0) {
    return;
  }

  const resetMenuPosition = (menu) => {
    menu._openAtPointer = null;
    const panel = menu.querySelector(":scope > .workspace-action-menu-panel");
    if (!panel) {
      return;
    }

    menu.classList.remove("is-floating");
    panel.style.position = "";
    panel.style.left = "";
    panel.style.top = "";
    panel.style.right = "";
    panel.style.insetInlineEnd = "";
    panel.style.maxHeight = "";
    panel.style.overflowY = "";
    panel.style.zIndex = "";
    panel.style.visibility = "";
  };

  const positionMenu = (menu) => {
    const summary = menu.querySelector(":scope > summary");
    const panel = menu.querySelector(":scope > .workspace-action-menu-panel");
    if (!panel || !menu.open) {
      return;
    }

    const margin = 8;
    const viewportWidth = document.documentElement.clientWidth;
    const viewportHeight = document.documentElement.clientHeight;

    menu.classList.add("is-floating");
    panel.style.position = "fixed";
    panel.style.right = "auto";
    panel.style.insetInlineEnd = "auto";
    panel.style.left = "0px";
    panel.style.top = "0px";
    panel.style.zIndex = "10000";
    panel.style.maxHeight = "";
    panel.style.overflowY = "";

    const panelRect = panel.getBoundingClientRect();
    const panelWidth = Math.min(panelRect.width, viewportWidth - margin * 2);
    const panelHeight = Math.min(panelRect.height, viewportHeight - margin * 2);

    // Right-click opens at the cursor; the ⋯ button anchors below itself.
    const pointer = menu._openAtPointer;
    if (pointer) {
      panel.style.left = `${Math.max(margin, Math.min(pointer.x, viewportWidth - panelWidth - margin))}px`;
      panel.style.top = `${Math.max(margin, Math.min(pointer.y, viewportHeight - panelHeight - margin))}px`;
      return;
    }

    if (!summary) {
      return;
    }

    const summaryRect = summary.getBoundingClientRect();
    let left = summaryRect.right - panelWidth;
    left = Math.max(margin, Math.min(left, viewportWidth - panelWidth - margin));

    let top = summaryRect.bottom + margin;
    if (top + panelHeight > viewportHeight - margin) {
      top = summaryRect.top - panelHeight - margin;
    }

    if (top < margin) {
      top = margin;
      panel.style.maxHeight = `${Math.max(160, viewportHeight - margin * 2)}px`;
      panel.style.overflowY = "auto";
    }

    panel.style.left = `${left}px`;
    panel.style.top = `${top}px`;
  };

  const closeMenus = (except = null) => {
    menus.forEach((menu) => {
      if (menu !== except) {
        menu.open = false;
        resetMenuPosition(menu);
      }
    });
  };

  menus.forEach((menu) => {
    menu.addEventListener("toggle", () => {
      if (menu.open) {
        closeMenus(menu);
        // The panel is kept hidden by CSS until .is-floating is added (below), so it
        // doesn't flash at its default spot before we place/flip it.
        window.requestAnimationFrame(() => positionMenu(menu));
      } else {
        resetMenuPosition(menu);
      }
    });

    menu.addEventListener("click", (event) => {
      const target = event.target instanceof Element ? event.target : null;
      if (!target || target.closest("summary")) {
        return;
      }

      const action = target.closest("a, button");
      if (action) {
        window.setTimeout(() => {
          menu.open = false;
        }, 0);
      }
    });
  });

  document.addEventListener("click", (event) => {
    const target = event.target instanceof Element ? event.target : null;
    if (!target || !target.closest("details.workspace-action-menu")) {
      closeMenus();
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeMenus();
    }
  });

  const repositionOpenMenus = () => {
    menus.forEach((menu) => {
      if (menu.open) {
        positionMenu(menu);
      }
    });
  };

  window.addEventListener("resize", repositionOpenMenus);
  document.addEventListener("scroll", repositionOpenMenus, { capture: true, passive: true });
}

function initializeClipboardButtons() {
  const buttons = Array.from(document.querySelectorAll("[data-copy-button]"));
  if (buttons.length === 0) {
    return;
  }

  const copyTextToClipboard = async (text) => {
    if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // Fall back to the legacy clipboard path below.
      }
    }

    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "readonly");
    textarea.style.position = "fixed";
    textarea.style.top = "-9999px";
    textarea.style.left = "-9999px";
    textarea.style.opacity = "0";
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    textarea.setSelectionRange(0, textarea.value.length);

    let copied = false;
    try {
      copied = document.execCommand("copy");
    } catch {
      copied = false;
    } finally {
      document.body.removeChild(textarea);
    }

    return copied;
  };

  buttons.forEach((button) => {
    const targetId = button.dataset.copyTarget || "";
    const label = button.querySelector("[data-copy-label]");
    const defaultLabel = label?.textContent?.trim() || "Copy";
    const defaultTitle = button.getAttribute("title") || defaultLabel;
    let resetHandle = null;

    const resetButtonState = () => {
      if (label) {
        label.textContent = defaultLabel;
      }
      button.classList.remove("is-copied");
      button.title = defaultTitle;
      button.setAttribute("aria-label", defaultTitle);
    };

    button.setAttribute("aria-label", defaultTitle);

    button.addEventListener("click", async () => {
      const target = targetId ? document.getElementById(targetId) : null;
      const text = (button.dataset.copyText || target?.textContent || "")
        .replace(/^(?:\r?\n)+/, "")
        .trimEnd();
      if (!text) {
        return;
      }

      const copied = await copyTextToClipboard(text);
      window.clearTimeout(resetHandle);

      if (copied) {
        if (label) {
          label.textContent = "Copied";
        }
        button.classList.add("is-copied");
        button.title = "Copied";
        button.setAttribute("aria-label", "Copied");
      } else {
        if (label) {
          label.textContent = "Copy failed";
        }
        button.classList.remove("is-copied");
        button.title = "Copy failed";
        button.setAttribute("aria-label", "Copy failed");
      }

      resetHandle = window.setTimeout(resetButtonState, 1400);
    });
  });
}

function initializeMonitoringTree() {
  const trees = document.querySelectorAll("[data-monitoring-tree]");
  let collapsedIds = new Set();
  try {
    const stored = JSON.parse(localStorage.getItem(monitoringTreeCollapsedStorageKey) || "[]");
    if (Array.isArray(stored)) {
      collapsedIds = new Set(stored.filter((value) => typeof value === "string"));
    }
  } catch {
    collapsedIds = new Set();
  }

  const allowedMoveTargets = new Map([
    ["probe", new Set(["probe"])],
    ["folder", new Set(["probe", "folder"])],
    ["host", new Set(["probe", "folder"])],
    ["sensor", new Set(["probe", "folder", "host"])]
  ]);

  let moveState = null;
  try {
    const storedMoveState = JSON.parse(localStorage.getItem(monitoringTreeMoveStorageKey) || "null");
    if (
      storedMoveState &&
      typeof storedMoveState === "object" &&
      typeof storedMoveState.id === "string" &&
      typeof storedMoveState.kind === "string" &&
      typeof storedMoveState.path === "string" &&
      storedMoveState.path.length > 0
    ) {
      moveState = {
        id: storedMoveState.id,
        kind: storedMoveState.kind,
        name: typeof storedMoveState.name === "string" ? storedMoveState.name : "",
        path: typeof storedMoveState.path === "string" ? storedMoveState.path : ""
      };
    }
  } catch {
    moveState = null;
  }

  const moveBanner = document.querySelector("[data-tree-move-banner]");
  const moveBannerLabel = moveBanner?.querySelector("[data-tree-move-label]");
  const moveCancelButton = document.querySelector("[data-tree-move-cancel]");
  const moveForm = document.querySelector("[data-tree-move-form]");
  const moveElementInput = moveForm?.querySelector("[data-tree-move-element]");
  const moveParentInput = moveForm?.querySelector("[data-tree-move-parent]");

  const persistCollapsedIds = () => {
    try {
      localStorage.setItem(monitoringTreeCollapsedStorageKey, JSON.stringify([...collapsedIds]));
    } catch {
      // Tree state is still usable without persistence.
    }
  };

  const getOwnTreeControl = (node, selector) =>
    Array.from(node.querySelectorAll(selector)).find((control) => control.closest("[data-tree-node]") === node);

  const setNodeState = (node, collapsed) => {
    node.classList.toggle("is-collapsed", collapsed);

    const toggle = getOwnTreeControl(node, "[data-tree-toggle]");
    if (toggle) {
      toggle.setAttribute("aria-expanded", String(!collapsed));
    }
  };

  // Bulk expand/collapse. A node is collapsible only when it has its own toggle
  // (i.e. it has children); sensor leaves are ignored.
  const collapsibleNodes = () => {
    const result = [];
    trees.forEach((tree) => {
      tree.querySelectorAll("[data-tree-node]").forEach((node) => {
        const nodeId = node.dataset.treeNodeId;
        if (nodeId && getOwnTreeControl(node, "[data-tree-toggle]")) {
          result.push({
            node,
            nodeId,
            depth: Number(node.dataset.treeDepth || 0),
            kind: (node.dataset.treeKind || "").toLowerCase()
          });
        }
      });
    });
    return result;
  };

  const applyBulkCollapse = (shouldCollapse) => {
    collapsedIds = new Set();
    collapsibleNodes().forEach((entry) => {
      const collapse = shouldCollapse(entry);
      setNodeState(entry.node, collapse);
      if (collapse) {
        collapsedIds.add(entry.nodeId);
      }
    });
    persistCollapsedIds();
  };

  document.querySelectorAll("[data-tree-expand-all]").forEach((button) =>
    button.addEventListener("click", () => applyBulkCollapse(() => false)));
  document.querySelectorAll("[data-tree-collapse-all]").forEach((button) =>
    button.addEventListener("click", () => applyBulkCollapse(() => true)));
  // "Probe level": keep every probe expanded (incl. secondary probes nested under
  // the root probe), collapse folders/hosts - so you always see the probes plus
  // their first level, nothing deeper. Depth can't be used because the secondary
  // probes sit one level deeper than the root probe.
  document.querySelectorAll("[data-tree-collapse-level]").forEach((button) =>
    button.addEventListener("click", () => applyBulkCollapse((entry) => entry.kind !== "probe")));

  const persistMoveState = () => {
    try {
      if (moveState) {
        localStorage.setItem(monitoringTreeMoveStorageKey, JSON.stringify(moveState));
      } else {
        localStorage.removeItem(monitoringTreeMoveStorageKey);
      }
    } catch {
      // Move selection is still usable without persistence.
    }
  };

  const isValidMoveTarget = (sourceState, node) => {
    if (!sourceState) {
      return false;
    }

    const nodeId = node.dataset.treeNodeId;
    const nodeKind = (node.dataset.treeKind || "").toLowerCase();
    const nodePath = node.dataset.treePath || "";
    const sourceKind = (sourceState.kind || "").toLowerCase();
    const sourcePath = sourceState.path || "";
    const allowedTargets = allowedMoveTargets.get(sourceKind);

    if (!nodeId || !nodeKind || !allowedTargets || !allowedTargets.has(nodeKind)) {
      return false;
    }

    if (nodeId === sourceState.id) {
      return false;
    }

    if (sourcePath && nodePath) {
      if (nodePath === sourcePath) {
        return false;
      }

      if (nodePath.startsWith(`${sourcePath} /`)) {
        return false;
      }
    }

    return true;
  };

  const updateMoveState = () => {
    const hasMoveState = Boolean(moveState);
    const moveKindLabel = moveState?.kind ? moveState.kind.charAt(0).toUpperCase() + moveState.kind.slice(1) : "Element";

    trees.forEach((tree) => {
      tree.classList.toggle("is-move-active", hasMoveState);

      tree.querySelectorAll("[data-tree-node]").forEach((node) => {
        const nodeId = node.dataset.treeNodeId || "";
        const nodeKind = (node.dataset.treeKind || "").toLowerCase();
        const isSource = hasMoveState && nodeId === moveState.id;
        const startButton = getOwnTreeControl(node, "[data-tree-move-start]");
        const targetButton = getOwnTreeControl(node, "[data-tree-move-target]");

        node.classList.toggle("is-move-source", isSource);

        if (startButton) {
          const actionLabel = nodeKind === "sensor" ? "Move sensor" : "Move element";
          startButton.setAttribute("aria-label", isSource ? "Cancel move" : actionLabel);
          startButton.setAttribute("title", isSource ? "Cancel move" : actionLabel);
          startButton.classList.toggle("is-active", isSource);
        }

        if (targetButton) {
          const validTarget = hasMoveState && isValidMoveTarget(moveState, node);
          targetButton.hidden = !validTarget;

          if (validTarget) {
            const sourceName = moveState.name ? ` "${moveState.name}"` : "";
            targetButton.setAttribute("aria-label", `Move${sourceName} here`);
            targetButton.setAttribute("title", `Move${sourceName} here`);
          }
        }
      });
    });

    if (moveBanner) {
      moveBanner.hidden = !hasMoveState;
    }

    if (moveBannerLabel) {
      moveBannerLabel.textContent = hasMoveState
        ? `Moving ${moveKindLabel}${moveState.name ? ` "${moveState.name}"` : ""}. Choose a target.`
        : "";
    }

    if (moveCancelButton) {
      moveCancelButton.hidden = !hasMoveState;
    }
  };

  const clearMoveState = () => {
    moveState = null;
    persistMoveState();
    updateMoveState();
  };

  const setMoveState = (node) => {
    const nodeId = node.dataset.treeNodeId;
    const nodeKind = (node.dataset.treeKind || "").toLowerCase();
    const nodeName = node.querySelector(".tree-name")?.textContent?.trim() || "";
    const nodePath = node.dataset.treePath || "";

    if (!nodeId || !nodeKind) {
      return;
    }

    moveState = {
      id: nodeId,
      kind: nodeKind,
      name: nodeName,
      path: nodePath
    };
    persistMoveState();
    updateMoveState();
  };

  trees.forEach((tree) => {
    tree.querySelectorAll("[data-tree-node]").forEach((node) => {
      const nodeId = node.dataset.treeNodeId;
      if (!nodeId) {
        return;
      }

      setNodeState(node, collapsedIds.has(nodeId));
    });

    // The is-collapsed classes now drive collapsing; drop the pre-paint stylesheet
    // (injected before the tree to avoid the expand→collapse flash) so toggling works.
    document.getElementById("matmon-precollapse")?.remove();

    tree.querySelectorAll("[data-tree-toggle]").forEach((toggle) => {
      toggle.addEventListener("click", () => {
        const node = toggle.closest("[data-tree-node]");
        const nodeId = node?.dataset.treeNodeId;
        if (!node || !nodeId) {
          return;
        }

        const shouldCollapse = !node.classList.contains("is-collapsed");
        setNodeState(node, shouldCollapse);

        if (shouldCollapse) {
          collapsedIds.add(nodeId);
        } else {
          collapsedIds.delete(nodeId);
        }

        persistCollapsedIds();
      });
    });

    tree.querySelectorAll("[data-tree-move-start]").forEach((button) => {
      button.addEventListener("click", () => {
        const node = button.closest("[data-tree-node]");
        if (!node) {
          return;
        }

        const nodeId = node.dataset.treeNodeId;
        if (!nodeId) {
          return;
        }

        if (moveState && moveState.id === nodeId) {
          clearMoveState();
          return;
        }

        setMoveState(node);
      });
    });

    tree.querySelectorAll("[data-tree-move-target]").forEach((button) => {
      button.addEventListener("click", () => {
        if (!moveState || !moveForm || !moveElementInput || !moveParentInput) {
          return;
        }

        const node = button.closest("[data-tree-node]");
        if (!node || !isValidMoveTarget(moveState, node)) {
          return;
        }

        const targetNodeId = node.dataset.treeNodeId;
        if (!targetNodeId) {
          return;
        }

        moveElementInput.value = moveState.id;
        moveParentInput.value = targetNodeId;
        clearMoveState();
        moveForm.submit();
      });
    });
  });

  if (moveCancelButton) {
    moveCancelButton.addEventListener("click", () => {
      clearMoveState();
    });
  }

  updateMoveState();
}

function initializeDashboardRefresh() {
  const graphCards = document.querySelectorAll("[data-series-key]");
  const navStatus = document.querySelector("[data-nav-alert-status]");
  if (graphCards.length === 0 && !navStatus) {
    return;
  }

  const refresh = async () => {
    try {
      const response = await fetch("/api/dashboard", {
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
      renderDashboard(snapshot);
    } catch {
      // The UI stays usable even if one refresh fails.
    }
  };

  refresh();
  window.setInterval(refresh, dashboardRefreshMs);
}

function initializeSensorNameSuggestion() {
  document.querySelectorAll("[data-sensor-name-input]").forEach((input) => {
    const form = input.closest("form");
    const autoField = form ? form.querySelector("[data-sensor-name-auto]") : null;
    if (!autoField) {
      return;
    }

    input.addEventListener("input", () => {
      autoField.value = "false";
    });
  });
}

function initializeSensorTypePreview() {
  document.querySelectorAll("[data-sensor-type-select], [data-sensor-template-select], [data-sensor-parent-select], [data-sensor-credential-select]").forEach((select) => {
    select.addEventListener("change", () => {
      const form = select.closest("form");
      if (!form) {
        return;
      }

      if (select.matches("[data-sensor-type-select]")) {
        const templateSelect = form.querySelector("[data-sensor-template-select]");
        if (templateSelect) {
          templateSelect.value = "";
        }
      }

      const previewButton = form.querySelector("[data-sensor-preview-submit]");
      if (!previewButton) {
        return;
      }

      if (typeof form.requestSubmit === "function") {
        form.requestSubmit(previewButton);
      } else {
        previewButton.click();
      }
    });
  });
}

function initializeSensorTabs() {
  document.querySelectorAll("[data-sensor-tabs]").forEach((tabBar) => {
    const scope = tabBar.closest("form") || document;
    const buttons = Array.from(tabBar.querySelectorAll("[data-sensor-tab-target]"));
    const panels = Array.from(scope.querySelectorAll("[data-sensor-tab]"));
    if (buttons.length === 0 || panels.length === 0) {
      return;
    }

    const names = buttons.map((button) => button.dataset.sensorTabTarget);
    const useHash = tabBar.dataset.tabHash === "true";
    // Remember the active tab per editor URL so a full-page re-post (e.g. clicking "Test")
    // returns you to the same tab instead of snapping back to the first one.
    const tabKey = "matmon-sensor-tab:" + location.pathname + location.search;

    const activate = (name) => {
      buttons.forEach((button) => {
        const isActive = button.dataset.sensorTabTarget === name;
        button.classList.toggle("is-active", isActive);
        button.setAttribute("aria-selected", isActive ? "true" : "false");
      });
      panels.forEach((panel) => {
        panel.hidden = panel.dataset.sensorTab !== name;
      });
      try {
        sessionStorage.setItem(tabKey, name);
      } catch (error) {
        /* sessionStorage may be unavailable */
      }
    };

    buttons.forEach((button) => {
      button.addEventListener("click", () => {
        const name = button.dataset.sensorTabTarget;
        activate(name);
        if (useHash) {
          try {
            history.replaceState(null, "", `#${name}`);
          } catch (error) {
            /* history may be unavailable; tab still switches */
          }
        }
      });
    });

    // Pick the initial tab: server intent (data-active-tab) wins, then the URL hash, then the
    // tab remembered for this page (survives a Test/Preview re-post), otherwise the first tab.
    const hashName = useHash ? (location.hash || "").replace(/^#/, "") : "";
    let storedName = null;
    try {
      storedName = sessionStorage.getItem(tabKey);
    } catch (error) {
      storedName = null;
    }
    const initial = (names.includes(tabBar.dataset.activeTab) ? tabBar.dataset.activeTab : null)
      || (names.includes(hashName) ? hashName : null)
      || (names.includes(storedName) ? storedName : null)
      || names[0];
    activate(initial);
  });
}

function initializeSensorParameterVisibility() {
  document.querySelectorAll("form").forEach((form) => {
    const fields = Array.from(form.querySelectorAll("[data-sensor-parameter-field]"));
    if (fields.length === 0) {
      return;
    }

    const readParameterValue = (key) => {
      const field = fields.find((candidate) => {
        return (candidate.dataset.parameterKey || "").toLowerCase() === key.toLowerCase();
      });
      if (!field) {
        return "";
      }

      const input = field.querySelector("select, textarea, input:not([type='hidden'])");
      const currentValue = (input?.value || "").trim();
      const effectiveValue = currentValue || field.dataset.inheritedValue || field.dataset.effectiveValue || "";
      return effectiveValue.trim().toLowerCase();
    };

    const refreshVisibility = () => {
      fields.forEach((field) => {
        const driverKey = field.dataset.visibleWhenKey || "";
        const allowedValues = (field.dataset.visibleWhenValues || "")
          .split("|")
          .map((value) => value.trim().toLowerCase())
          .filter(Boolean);

        if (!driverKey || allowedValues.length === 0) {
          field.hidden = false;
          return;
        }

        const driverValue = readParameterValue(driverKey);
        field.hidden = !allowedValues.includes(driverValue);
      });
    };

    fields.forEach((field) => {
      field.querySelectorAll("select, textarea, input:not([type='hidden'])").forEach((input) => {
        input.addEventListener("input", refreshVisibility);
        input.addEventListener("change", refreshVisibility);
      });
    });

    refreshVisibility();
  });
}

// Progressively enhances a [data-script-editor] textarea into a code editor: a transparent
// textarea over a syntax-highlighted <pre>, with tab insertion. Dependency-free.
function initializeScriptEditors() {
  document.querySelectorAll("textarea[data-script-editor]").forEach((textarea) => {
    if (textarea.dataset.scriptEditorReady === "1") {
      return;
    }
    textarea.dataset.scriptEditorReady = "1";

    const wrapper = document.createElement("div");
    wrapper.className = "code-editor";
    const pre = document.createElement("pre");
    pre.className = "code-editor-highlight";
    pre.setAttribute("aria-hidden", "true");
    const code = document.createElement("code");
    pre.appendChild(code);

    textarea.parentNode.insertBefore(wrapper, textarea);
    wrapper.appendChild(pre);
    wrapper.appendChild(textarea);
    textarea.classList.add("code-editor-input");
    textarea.setAttribute("spellcheck", "false");
    textarea.setAttribute("autocomplete", "off");
    textarea.setAttribute("autocapitalize", "off");

    const render = () => {
      // Trailing newline keeps the last line scrollable into view.
      code.innerHTML = highlightScript(textarea.value) + "\n";
    };
    const syncScroll = () => {
      pre.scrollTop = textarea.scrollTop;
      pre.scrollLeft = textarea.scrollLeft;
    };

    textarea.addEventListener("input", render);
    textarea.addEventListener("scroll", syncScroll);
    textarea.addEventListener("keydown", (event) => {
      if (event.key === "Tab") {
        event.preventDefault();
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        textarea.value = `${textarea.value.slice(0, start)}  ${textarea.value.slice(end)}`;
        textarea.selectionStart = textarea.selectionEnd = start + 2;
        render();
      }
    });

    render();
  });
}

// Tiny PowerShell/shell highlighter: comments, strings, $variables, numbers, keywords.
function highlightScript(source) {
  const token = /(#[^\n]*)|("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')|(\$\{?[A-Za-z_][\w:.]*\}?|\$[0-9]+|\$[@*?#!-])|(\b\d+(?:\.\d+)?\b)|(\b(?:if|else|elseif|then|fi|for|foreach|in|do|done|while|until|switch|function|return|param|begin|process|end|try|catch|finally|throw|break|continue|case|esac|echo|exit|local|export|set)\b)/g;
  let result = "";
  let last = 0;
  source.replace(token, (match, comment, str, variable, number, keyword, offset) => {
    result += escapeHtml(source.slice(last, offset));
    const cls = comment ? "tok-comment" : str ? "tok-string" : variable ? "tok-var" : number ? "tok-number" : "tok-keyword";
    result += `<span class="${cls}">${escapeHtml(match)}</span>`;
    last = offset + match.length;
    return match;
  });
  result += escapeHtml(source.slice(last));
  return result;
}

function initializeTemplateScopeEditors() {
  document.querySelectorAll("[data-template-scope-form]").forEach((form) => {
    const scopeSelect = form.querySelector("[data-template-scope-select]");
    if (!scopeSelect) {
      return;
    }

    const updateScopeFields = () => {
      const value = String(scopeSelect.value || "").toLowerCase();
      const isSensorScope = value === "4" || value === "sensor";

      form.querySelectorAll("[data-template-sensor-only]").forEach((field) => {
        field.hidden = !isSensorScope;
      });

      form.querySelectorAll("[data-template-nonsensor-only]").forEach((field) => {
        field.hidden = isSensorScope;
      });
    };

    scopeSelect.addEventListener("change", updateScopeFields);
    updateScopeFields();
  });
}

function initializeScheduleEditors() {
  const unitSeconds = { seconds: 1, minutes: 60, hours: 3600, days: 86400 };
  const dowIndex = (name) => {
    const days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    const i = days.indexOf(String(name || "").toLowerCase());
    return i < 0 ? 1 : i;
  };
  const formatRun = (d) =>
    d.toLocaleString(undefined, { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });

  document.querySelectorAll("[data-schedule-editor]").forEach((editor) => {
    const modeSelect = editor.querySelector("[data-schedule-mode]");
    if (!modeSelect) {
      return;
    }

    const everyGroup = editor.querySelector("[data-schedule-every]");
    const timeField = editor.querySelector("[data-schedule-time]");
    const weekdayField = editor.querySelector("[data-schedule-weekday]");
    const monthdayField = editor.querySelector("[data-schedule-monthday]");
    const valueInput = editor.querySelector("[data-schedule-every-value]");
    const unitInput = editor.querySelector("[data-schedule-every-unit]");
    const timeInput = editor.querySelector("[data-schedule-time-input]");
    const weekdayInputs = editor.querySelectorAll("[data-schedule-weekday-input]");
    const monthdayInput = editor.querySelector("[data-schedule-monthday-input]");
    const preview = editor.querySelector("[data-schedule-preview]");
    const previewTimes = editor.querySelector("[data-schedule-preview-times]");

    const setHidden = (el, hidden) => {
      if (el) {
        el.hidden = hidden;
      }
    };

    const parseTime = () => {
      const raw = (timeInput && timeInput.value) || "00:00";
      const parts = raw.split(":");
      const h = parseInt(parts[0], 10);
      const m = parseInt(parts[1], 10);
      return { h: isNaN(h) ? 0 : h, m: isNaN(m) ? 0 : m };
    };

    const buildMonthly = (year, month, dom, h, m) => {
      const daysInMonth = new Date(year, month + 1, 0).getDate();
      return new Date(year, month, Math.min(dom, daysInMonth), h, m, 0, 0);
    };

    const computeNextRuns = (mode) => {
      const now = new Date();
      const runs = [];

      if (mode === "every") {
        const value = Math.max(parseInt((valueInput && valueInput.value) || "0", 10) || 0, 1);
        const unit = (unitInput && unitInput.value) || "minutes";
        const stepMs = Math.max(value * (unitSeconds[unit] || 60), 5) * 1000;
        for (let i = 1; i <= 3; i++) {
          runs.push(new Date(now.getTime() + stepMs * i));
        }
        return runs;
      }

      const { h, m } = parseTime();

      if (mode === "daily") {
        const next = new Date(now);
        next.setHours(h, m, 0, 0);
        if (next <= now) {
          next.setDate(next.getDate() + 1);
        }
        for (let i = 0; i < 3; i++) {
          runs.push(new Date(next));
          next.setDate(next.getDate() + 1);
        }
      } else if (mode === "weekly") {
        const days = Array.from(weekdayInputs)
          .filter((c) => c.checked)
          .map((c) => dowIndex(c.value));
        if (days.length === 0) {
          days.push(1); // default Monday
        }
        const candidates = [];
        days.forEach((target) => {
          const d = new Date(now);
          d.setHours(h, m, 0, 0);
          d.setDate(d.getDate() + ((target - d.getDay() + 7) % 7));
          if (d <= now) {
            d.setDate(d.getDate() + 7);
          }
          for (let i = 0; i < 3; i++) {
            candidates.push(new Date(d));
            d.setDate(d.getDate() + 7);
          }
        });
        candidates.sort((a, b) => a - b);
        return candidates.slice(0, 3);
      } else if (mode === "monthly") {
        const dom = Math.min(Math.max(parseInt((monthdayInput && monthdayInput.value) || "1", 10) || 1, 1), 31);
        let cur = buildMonthly(now.getFullYear(), now.getMonth(), dom, h, m);
        if (cur <= now) {
          cur = buildMonthly(now.getFullYear(), now.getMonth() + 1, dom, h, m);
        }
        for (let i = 0; i < 3; i++) {
          runs.push(new Date(cur));
          cur = buildMonthly(cur.getFullYear(), cur.getMonth() + 1, dom, h, m);
        }
      }

      return runs;
    };

    const refresh = () => {
      const mode = String(modeSelect.value || "inherit").toLowerCase();
      const usesTime = mode === "daily" || mode === "weekly" || mode === "monthly";

      setHidden(everyGroup, mode !== "every");
      setHidden(timeField, !usesTime);
      setHidden(weekdayField, mode !== "weekly");
      setHidden(monthdayField, mode !== "monthly");

      if (mode === "inherit") {
        setHidden(preview, true);
        return;
      }

      const runs = computeNextRuns(mode);
      if (previewTimes) {
        previewTimes.innerHTML = "";
        runs.forEach((run) => {
          const span = document.createElement("span");
          span.className = "schedule-preview-time";
          span.textContent = formatRun(run);
          previewTimes.appendChild(span);
        });
      }
      setHidden(preview, runs.length === 0);
    };

    modeSelect.addEventListener("change", refresh);
    [valueInput, unitInput, timeInput, monthdayInput].forEach((el) => {
      if (el) {
        el.addEventListener("change", refresh);
        el.addEventListener("input", refresh);
      }
    });
    weekdayInputs.forEach((el) => el.addEventListener("change", refresh));

    editor.querySelectorAll("[data-chip-value]").forEach((chip) => {
      chip.addEventListener("click", () => {
        if (valueInput) {
          valueInput.value = chip.dataset.chipValue;
        }
        if (unitInput) {
          unitInput.value = chip.dataset.chipUnit;
        }
        if (modeSelect.value !== "every") {
          modeSelect.value = "every";
        }
        refresh();
      });
    });

    refresh();
  });
}

function initializeRowLinks() {
  document.querySelectorAll("tr[data-href]").forEach((row) => {
    row.addEventListener("click", (event) => {
      if (event.target.closest("a, button, input, select, textarea")) {
        return;
      }
      const href = row.dataset.href;
      if (href) {
        window.location.href = href;
      }
    });
  });
}

function initializeInteractiveCharts() {
  document.querySelectorAll('.sensor-chart-wrap[data-chart="line"]').forEach(initializeLineChartHover);
  document.querySelectorAll('.sensor-chart-wrap[data-chart="bars"]').forEach(initializeBarChartHover);
}

function createChartTooltip(wrap) {
  const tip = document.createElement("div");
  tip.className = "sensor-chart-tooltip";
  tip.hidden = true;
  wrap.appendChild(tip);
  return tip;
}

function positionChartTooltip(tip, wrap, x, y) {
  const width = wrap.clientWidth || 1;
  tip.style.left = `${Math.min(Math.max(x, 6), width - 6)}px`;
  tip.style.top = `${Math.max(y, 0)}px`;
}

function formatChartNumber(value) {
  return (Math.round(value * 1000) / 1000).toLocaleString();
}

function initializeLineChartHover(wrap) {
  let points;
  try {
    points = JSON.parse(wrap.dataset.chartPoints || "[]");
  } catch (error) {
    points = [];
  }
  if (!Array.isArray(points) || points.length === 0) {
    return;
  }

  const min = parseFloat(wrap.dataset.chartMin);
  const max = parseFloat(wrap.dataset.chartMax);
  const from = parseInt(wrap.dataset.chartFrom, 10);
  const to = parseInt(wrap.dataset.chartTo, 10);
  const unit = wrap.dataset.chartUnit || "";
  const range = (max - min) || 1;
  const span = (to - from) || 1;
  // Mirror the server path geometry (viewBox 0 0 100 40, padding 3).
  const vbHeight = 40;
  const vbPadding = 3;

  const crosshair = document.createElement("div");
  crosshair.className = "sensor-chart-crosshair";
  crosshair.hidden = true;
  const dot = document.createElement("div");
  dot.className = "sensor-chart-dot";
  dot.hidden = true;
  const tip = createChartTooltip(wrap);
  wrap.append(crosshair, dot);

  const fractionOf = (timestamp) => (timestamp - from) / span;

  const onMove = (event) => {
    const rect = wrap.getBoundingClientRect();
    const fraction = Math.min(Math.max((event.clientX - rect.left) / rect.width, 0), 1);
    let nearest = points[0];
    let nearestDistance = Infinity;
    for (const point of points) {
      const distance = Math.abs(fractionOf(point[0]) - fraction);
      if (distance < nearestDistance) {
        nearestDistance = distance;
        nearest = point;
      }
    }

    const px = Math.min(Math.max(fractionOf(nearest[0]), 0), 1) * rect.width;
    const normalized = Math.min(Math.max((nearest[1] - min) / range, 0), 1);
    const vbY = vbHeight - vbPadding - normalized * (vbHeight - vbPadding * 2);
    const py = (vbY / vbHeight) * rect.height;

    crosshair.style.left = `${px}px`;
    crosshair.hidden = false;
    dot.style.left = `${px}px`;
    dot.style.top = `${py}px`;
    dot.hidden = false;

    const when = new Date(nearest[0]).toLocaleString([], {
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit"
    });
    tip.innerHTML = `<strong>${formatChartNumber(nearest[1])}${unit ? ` ${unit}` : ""}</strong><span>${when}</span>`;
    positionChartTooltip(tip, wrap, px, py);
    tip.hidden = false;
  };

  const onLeave = () => {
    crosshair.hidden = true;
    dot.hidden = true;
    tip.hidden = true;
  };

  wrap.addEventListener("pointermove", onMove);
  wrap.addEventListener("pointerleave", onLeave);
}

function initializeBarChartHover(wrap) {
  const bars = wrap.querySelectorAll("[data-bar-tip]");
  if (bars.length === 0) {
    return;
  }

  const tip = createChartTooltip(wrap);
  bars.forEach((bar) => {
    bar.addEventListener("pointerenter", () => {
      bar.classList.add("is-hover");
      tip.textContent = bar.dataset.barTip || "";
      tip.hidden = false;
    });
    bar.addEventListener("pointermove", (event) => {
      const rect = wrap.getBoundingClientRect();
      positionChartTooltip(tip, wrap, event.clientX - rect.left, event.clientY - rect.top - 6);
    });
    bar.addEventListener("pointerleave", () => {
      bar.classList.remove("is-hover");
    });
  });

  wrap.addEventListener("pointerleave", () => {
    tip.hidden = true;
  });
}

function initializeThresholdEditors() {
  document.querySelectorAll("[data-threshold-section]").forEach((section) => {
    const refreshDefaultState = () => {
      const rows = Array.from(section.querySelectorAll("[data-threshold-row]")).filter((row) => !row.hidden && row.dataset.thresholdDeleted !== "true");
      let defaultRow = rows.find((row) => {
        const defaultInput = row.querySelector("input[name$='.IsDefault']");
        return defaultInput?.value === "true";
      }) || null;

      if (!defaultRow && rows.length > 0) {
        defaultRow = rows[0];
        const defaultInput = defaultRow.querySelector("input[name$='.IsDefault']");
        if (defaultInput) {
          defaultInput.value = "true";
        }
      }

      rows.forEach((row) => {
        const isDefault = row === defaultRow;
        row.dataset.thresholdDefault = String(isDefault);

        const defaultInput = row.querySelector("input[name$='.IsDefault']");
        if (defaultInput) {
          defaultInput.value = String(isDefault);
        }

        const defaultButton = row.querySelector("[data-threshold-default]");
        if (defaultButton) {
          defaultButton.classList.toggle("is-active", isDefault);
          defaultButton.setAttribute("aria-pressed", String(isDefault));
          const label = defaultButton.querySelector("[data-threshold-default-label]");
          if (label) {
            label.textContent = isDefault ? "Primary" : "Set primary";
          }
        }
      });
    };

    section.querySelectorAll("[data-threshold-row][data-threshold-deleted='true']").forEach((row) => {
      row.hidden = true;
    });

    refreshDefaultState();

    // Per-rule enable/disable (Warn if / Alarm if): the toggle is on iff a value is set. Turning it off clears the
    // value + disables the inputs (a disabled/empty field posts nothing -> the server stores "no threshold"), so
    // enabling/disabling a condition is explicit instead of the unintuitive "leave the value blank".
    const syncRule = (rule) => {
      const toggle = rule.querySelector("[data-threshold-enable]");
      const value = rule.querySelector("[data-threshold-value]");
      const select = rule.querySelector("select");
      if (!toggle || !value) {
        return;
      }
      const on = toggle.checked;
      value.disabled = !on;
      if (select) {
        select.disabled = !on;
      }
      rule.classList.toggle("is-enabled", on);
      rule.classList.toggle("is-disabled", !on);
    };
    section.querySelectorAll("[data-threshold-rule]").forEach((rule) => {
      const toggle = rule.querySelector("[data-threshold-enable]");
      const value = rule.querySelector("[data-threshold-value]");
      if (!toggle || !value) {
        return;
      }
      toggle.checked = value.value.trim() !== "";
      syncRule(rule);
      toggle.addEventListener("change", () => {
        if (!toggle.checked) {
          value.value = "";
        }
        syncRule(rule);
        if (toggle.checked && typeof value.focus === "function") {
          value.focus();
        }
      });
    });

    section.querySelectorAll("[data-threshold-add]").forEach((button) => {
      button.addEventListener("click", () => {
        const hiddenRow = section.querySelector("[data-threshold-row][hidden]:not([data-threshold-deleted='true'])");
        if (!hiddenRow) {
          return;
        }

        hiddenRow.hidden = false;
        const focusTarget = hiddenRow.querySelector("input:not([type='hidden']), select, textarea");
        if (focusTarget && typeof focusTarget.focus === "function") {
          focusTarget.focus();
        }

        refreshDefaultState();
      });
    });

    section.querySelectorAll("[data-threshold-delete]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-threshold-row]");
        if (!row) {
          return;
        }

        const deletedInput = row.querySelector("input[name$='.IsDeleted']");
        if (deletedInput) {
          deletedInput.value = "true";
        }

        row.dataset.thresholdDeleted = "true";
        row.hidden = true;
        refreshDefaultState();
      });
    });

    section.querySelectorAll("[data-threshold-default]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-threshold-row]");
        if (!row) {
          return;
        }

        const rows = Array.from(section.querySelectorAll("[data-threshold-row]")).filter((candidate) => !candidate.hidden && candidate.dataset.thresholdDeleted !== "true");
        rows.forEach((candidate) => {
          const defaultInput = candidate.querySelector("input[name$='.IsDefault']");
          if (defaultInput) {
            defaultInput.value = candidate === row ? "true" : "false";
          }
        });

        refreshDefaultState();
      });
    });
  });
}

function initializeCredentialEditors() {
  document.querySelectorAll("[data-credential-section]").forEach((section) => {
    const updateCredentialPanels = (row) => {
      const select = row.querySelector("[data-credential-kind-select]");
      if (!select) {
        return;
      }

      const kind = (select.value || "").toLowerCase();
      // Every credential kind now has its own explicit field panel (the raw key=value
      // "Advanced values" editor was removed app-wide).
      row.querySelectorAll("[data-credential-kind-group]").forEach((panel) => {
        const panelKind = (panel.dataset.credentialKindGroup || "").toLowerCase();
        const matches =
          panelKind === kind ||
          (panelKind === "ssh" && (kind === "linux" || kind === "ssh"));

        panel.hidden = !matches;
      });
    };

    // List + modal UI: the row shows a compact summary; editing happens in an overlay dialog.
    const syncSummary = (row) => {
      const nameInput = row.querySelector("input[name$='.Name']");
      const nameLabel = row.querySelector("[data-credential-name-label]");
      if (nameInput && nameLabel) {
        nameLabel.textContent = nameInput.value.trim() || "New credential";
      }
      const kindSelect = row.querySelector("[data-credential-kind-select]");
      const kindLabel = row.querySelector("[data-credential-kind-label]");
      if (kindSelect && kindLabel) {
        kindLabel.textContent = kindSelect.value;
      }
    };
    const openModal = (row) => {
      const modal = row.querySelector("[data-credential-modal]");
      if (!modal) {
        return;
      }
      modal.hidden = false;
      document.body.classList.add("credential-modal-open");
      const focusTarget = modal.querySelector("input:not([type='hidden']), select, textarea");
      if (focusTarget && typeof focusTarget.focus === "function") {
        focusTarget.focus();
      }
    };
    const updateEmptyState = () => {
      const empty = section.querySelector("[data-credential-empty]");
      if (!empty) {
        return;
      }
      const anyVisible = !!section.querySelector(
        "[data-credential-list] [data-credential-row]:not([hidden]):not([data-credential-deleted='true'])"
      );
      empty.hidden = anyVisible;
    };
    const hideModal = (row) => {
      const modal = row.querySelector("[data-credential-modal]");
      if (modal) {
        modal.hidden = true;
      }
      if (!section.querySelector("[data-credential-modal]:not([hidden])")) {
        document.body.classList.remove("credential-modal-open");
      }
    };
    // Reset every visible field in the row (used when a newly-added credential is cancelled), so the pre-rendered
    // slot goes back to "empty" and is reusable for the next Add - and its blank Name makes the server ignore it.
    const clearRow = (row) => {
      row.querySelectorAll("input, select, textarea").forEach((el) => {
        if (el.type === "hidden") {
          return; // keep structural hidden inputs (index, IsDeleted)
        }
        if (el.type === "checkbox" || el.type === "radio") {
          el.checked = false;
        } else if (el.tagName === "SELECT") {
          el.selectedIndex = 0;
        } else {
          el.value = "";
        }
      });
      updateCredentialPanels(row);
    };
    // Cancel a NEW (pending) credential: discard it entirely - clear + collapse the row so no record is created.
    const discardRow = (row) => {
      clearRow(row);
      row.hidden = true;
      delete row.dataset.credentialPending;
      hideModal(row);
      syncSummary(row);
      updateEmptyState();
    };
    // Save/Done: keep the row (unless it's a still-blank new one, which is discarded so Add+Done makes no ghost).
    const commitRow = (row) => {
      const nameInput = row.querySelector("input[name$='.Name']");
      if (row.dataset.credentialPending === "true" && nameInput && nameInput.value.trim() === "") {
        discardRow(row);
        return;
      }
      delete row.dataset.credentialPending;
      hideModal(row);
      syncSummary(row);
      updateEmptyState();
    };
    // X / backdrop = cancel: a pending (new) row is discarded; editing an existing one just closes (edits kept).
    const cancelModal = (row) => {
      if (row.dataset.credentialPending === "true") {
        discardRow(row);
        return;
      }
      hideModal(row);
      syncSummary(row);
      updateEmptyState();
    };

    section.querySelectorAll("[data-credential-row]").forEach((row) => updateCredentialPanels(row));
    updateEmptyState();

    section.querySelectorAll("[data-credential-row][data-credential-deleted='true']").forEach((row) => {
      row.hidden = true;
    });

    section.querySelectorAll("[data-credential-add]").forEach((button) => {
      button.addEventListener("click", () => {
        const hiddenRow = section.querySelector("[data-credential-row][hidden]:not([data-credential-deleted='true'])");
        if (!hiddenRow) {
          return;
        }

        // Newly added = "pending": until the user hits Done it stays a discardable draft (Cancel/X removes it),
        // so pressing Add alone never leaves a record behind.
        hiddenRow.dataset.credentialPending = "true";
        hiddenRow.hidden = false;
        updateCredentialPanels(hiddenRow);
        syncSummary(hiddenRow);
        updateEmptyState();
        openModal(hiddenRow);
      });
    });

    section.querySelectorAll("[data-credential-kind-select]").forEach((select) => {
      select.addEventListener("change", () => {
        const row = select.closest("[data-credential-row]");
        if (!row) {
          return;
        }

        updateCredentialPanels(row);
        syncSummary(row);
      });
    });

    section.querySelectorAll("[data-credential-edit]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row) {
          openModal(row);
        }
      });
    });

    // X (top-right) = cancel: discards a pending new credential, otherwise just closes.
    section.querySelectorAll("[data-credential-modal-close]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row) {
          cancelModal(row);
        }
      });
    });

    // Done (bottom) = save/keep the credential.
    section.querySelectorAll("[data-credential-modal-save]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row) {
          commitRow(row);
        }
      });
    });

    section.querySelectorAll("[data-credential-modal]").forEach((modal) => {
      modal.addEventListener("click", (event) => {
        if (event.target !== modal) {
          return;
        }
        const row = modal.closest("[data-credential-row]");
        if (row) {
          cancelModal(row);
        }
      });
    });

    section.querySelectorAll("[data-credential-row]").forEach((row) => {
      const nameInput = row.querySelector("input[name$='.Name']");
      if (nameInput) {
        nameInput.addEventListener("input", () => syncSummary(row));
      }
    });

    section.querySelectorAll("[data-credential-delete]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (!row) {
          return;
        }

        const deletedInput = row.querySelector("input[name$='.IsDeleted']");
        if (deletedInput) {
          deletedInput.value = "true";
        }

        row.dataset.credentialDeleted = "true";
        row.hidden = true;
        updateEmptyState();
      });
    });
  });
}

function initializeNotificationKindEditors() {
  document.querySelectorAll("[data-notification-kind-select]").forEach((select) => {
    const panels = Array.from(select.closest("form")?.querySelectorAll("[data-notification-kind-panel]") ?? []);
    if (panels.length === 0) {
      return;
    }

    const updatePanels = () => {
      const raw = (select.value || "email").toLowerCase();
      const kind = raw === "webhook" ? "webhook" : raw === "cloud" ? "cloud" : "email";
      panels.forEach((panel) => {
        panel.hidden = (panel.dataset.notificationKindPanel || "").toLowerCase() !== kind;
      });
    };

    select.addEventListener("change", updatePanels);
    updatePanels();
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
  const progressBarElement = panel.querySelector("[data-discovery-progress-bar]");
  const countElement = document.querySelector("[data-discovery-result-count]");
  const resultList = document.querySelector("[data-discovery-result-list]");
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
        progressBarElement,
        countElement,
        resultList,
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
    elements.statusElement.dataset.state = discoveryStatusTone(status);
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

  if (elements.progressBarElement) {
    elements.progressBarElement.style.width = `${progressPercent}%`;
  }

  if (elements.countElement) {
    elements.countElement.textContent = String(results.length);
  }

  if (elements.emptyState) {
    elements.emptyState.hidden = results.length > 0;
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

  elements.resultList.innerHTML = results
    .map((result, index) => renderDiscoveryResultRow(result, index, selectedByAddress, selectedSuggestions, expandedByAddress, elements.importMode))
    .join("");

  applyDiscoveryTableState(document.querySelector("[data-discovery-results-panel]"));
}

// A suggestion at/above this confidence is "recommended" (default-selected + badged). Mirror in Discovery.cshtml.
const DISCOVERY_RECOMMENDED_MIN = 80;
// Inline copies of the host/sensor glyphs (MatmonIcons) so the client-streamed tree matches the server render.
const DISCOVERY_HOST_ICON = '<svg class="ui-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="3.2" width="11" height="7.1" /><path d="M6 13.1h4" /><path d="M8 10.3v2.8" /></svg>';
const DISCOVERY_SENSOR_ICON = '<svg class="ui-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="8" cy="8" r="4.8" /><circle cx="8" cy="8" r="1.5" /><path d="M8 1.5v2" /><path d="M8 12.5v2" /><path d="M1.5 8h2" /><path d="M12.5 8h2" /></svg>';

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

  const sensorsMarkup = sensorCount === 0
    ? `<div class="discovery-node-empty">No sensor suggestions.</div>`
    : `<div class="discovery-node-sensors">${suggestedSensors.map((suggestion) => renderDiscoverySuggestionRow(address, suggestion, selectedSuggestions, importMode)).join("")}</div>`;

  const hostCheck = importMode
    ? `<input type="checkbox" class="discovery-node-check" name="SelectedHostAddresses" value="${escapeAttribute(address)}" data-discovery-host-check ${selected ? "checked" : ""} />`
    : "";
  const nodeClass = importMode ? `discovery-node ${selected ? "is-selected" : "is-disabled"}` : "discovery-node";

  return `
    <div class="${nodeClass}"
        data-discovery-address="${escapeAttribute(address)}"
        data-discovery-host="${escapeAttribute(hostName)}"
        data-discovery-text="${escapeAttribute(searchText)}"
        data-discovery-ping="${pingAlive ? "ok" : "none"}"
        data-discovery-ping-ms="${escapeAttribute(String(result.pingMs ?? ""))}"
        data-discovery-port-count="${openPorts.length}"
        data-discovery-snmp="${snmpResponded ? "true" : "false"}"
        data-discovery-sensor-count="${sensorCount}"
        data-discovery-expanded="${expanded ? "true" : "false"}">
      <div class="discovery-node-head">
        ${hostCheck}
        <button type="button" class="discovery-node-toggle" data-discovery-toggle aria-expanded="${expanded ? "true" : "false"}">${expanded ? "▾" : "▸"}</button>
        <span class="tree-kind" data-kind="host">${DISCOVERY_HOST_ICON}</span>
        <span class="discovery-node-title">
          <strong>${escapeHtml(address)}</strong>
          ${hostName ? `<span class="discovery-node-sub">${escapeHtml(hostName)}</span>` : ""}
        </span>
        <span class="discovery-node-facts">
          ${pingAlive ? `<span class="discovery-fact" data-ok>${escapeHtml(pingMs)} ms</span>` : ""}
          ${openPorts.length > 0 ? `<span class="discovery-fact" title="${escapeAttribute(openPortsText)}">${openPorts.length} port${openPorts.length === 1 ? "" : "s"}</span>` : ""}
          ${snmpResponded ? `<span class="discovery-fact">SNMP</span>` : ""}
        </span>
        <span class="discovery-node-count">${sensorCount} sensor${sensorCount === 1 ? "" : "s"}</span>
      </div>
      <div class="discovery-node-body"${expanded ? "" : " hidden"}>
        ${message ? `<div class="discovery-node-message">${escapeHtml(message)}</div>` : ""}
        ${sensorsMarkup}
      </div>
    </div>
  `;
}

function renderDiscoverySuggestionRow(address, suggestion, selectedSuggestions, importMode) {
  const sensorTypeKey = String(suggestion.sensorTypeKey ?? "");
  const name = String(suggestion.name ?? sensorTypeKey);
  const target = String(suggestion.target ?? "");
  const reason = String(suggestion.reason ?? "");
  const confidence = Number.isFinite(Number(suggestion.confidence)) ? Number(suggestion.confidence) : 0;
  const recommended = confidence >= DISCOVERY_RECOMMENDED_MIN;
  const pillState = recommended ? "ok" : "warning";
  const targetMarkup = target ? `<span class="discovery-sensor-target">${escapeHtml(target)}</span>` : "";
  const iconAndName = `
      <span class="tree-kind" data-kind="sensor">${DISCOVERY_SENSOR_ICON}</span>
      <span class="sensor-chip">${escapeHtml(sensorTypeKey)}</span>
      <span class="discovery-sensor-name" title="${escapeAttribute(reason)}">${escapeHtml(name)}</span>
      ${targetMarkup}
      <span class="discovery-sensor-spacer"></span>`;
  const pill = `<span class="state-pill" data-state="${pillState}">${confidence}%</span>`;

  if (!importMode) {
    return `<div class="discovery-sensor is-readonly">${iconAndName}${pill}</div>`;
  }

  const suggestionKey = buildDiscoverySuggestionKey(address, sensorTypeKey, target, name);
  const selected = selectedSuggestions.has(suggestionKey) ? selectedSuggestions.get(suggestionKey) : recommended;
  const badge = recommended ? `<span class="discovery-sensor-badge">Recommended</span>` : "";
  return `
    <label class="discovery-sensor" data-discovery-suggestion-key="${escapeAttribute(suggestionKey)}" data-discovery-recommended="${recommended ? "true" : "false"}">
      <input type="checkbox" name="SelectedSuggestionKeys" value="${escapeAttribute(suggestionKey)}" data-discovery-sensor-check ${selected ? "checked" : ""} />${iconAndName}${badge}${pill}
    </label>
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

  // Reflect a host's checkbox onto its card: dim + disable its sensor checkboxes when it won't be imported.
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

  form.querySelectorAll("[data-discovery-preset]").forEach((button) => {
    button.addEventListener("click", () => {
      applyPreset(button.dataset.discoveryPreset || "recommended");
      form.querySelectorAll("[data-discovery-preset]").forEach((other) => {
        other.classList.toggle("is-active", other === button);
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

  panel.querySelector("[data-discovery-filter]")?.addEventListener("input", () => applyDiscoveryTableState(panel));
  panel.querySelector("[data-discovery-service-filter]")?.addEventListener("change", () => applyDiscoveryTableState(panel));

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
  };

  searchInput?.addEventListener("input", apply);
  statusSelect?.addEventListener("change", apply);
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
  const serviceFilter = String(panel.querySelector("[data-discovery-service-filter]")?.value || "all");

  let visibleCount = 0;
  body.querySelectorAll(".discovery-node").forEach((node) => {
    const visible = discoveryRowMatches(node, search, serviceFilter);
    node.hidden = !visible;
    if (visible) {
      visibleCount++;
    }

    const expanded = node.dataset.discoveryExpanded === "true";
    const toggle = node.querySelector("[data-discovery-toggle]");
    if (toggle) {
      toggle.textContent = expanded ? "▾" : "▸";
      toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
    }

    const bodyElement = node.querySelector(".discovery-node-body");
    if (bodyElement) {
      bodyElement.hidden = !expanded;
    }
  });

  const visibleCountElement = panel.querySelector("[data-discovery-visible-count]");
  if (visibleCountElement) {
    visibleCountElement.textContent = String(visibleCount);
  }
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

function readDiscoveryNumber(value, fallback) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function compareNumber(left, right) {
  return left === right ? 0 : left < right ? -1 : 1;
}

function compareText(left, right) {
  return String(left || "").localeCompare(String(right || ""), undefined, { sensitivity: "base", numeric: true });
}

function compareDiscoveryAddress(left, right) {
  const leftParts = String(left).split(".").map((part) => Number(part));
  const rightParts = String(right).split(".").map((part) => Number(part));
  if (leftParts.length === 4 && rightParts.length === 4 && leftParts.every(Number.isFinite) && rightParts.every(Number.isFinite)) {
    for (let index = 0; index < 4; index++) {
      if (leftParts[index] !== rightParts[index]) {
        return leftParts[index] - rightParts[index];
      }
    }

    return 0;
  }

  return compareText(left, right);
}

function discoveryStatusTone(status) {
  const normalized = String(status || "").toLowerCase();
  if (normalized === "completed") {
    return "ok";
  }

  if (normalized === "failed") {
    return "error";
  }

  if (normalized === "cancelled") {
    return "warning";
  }

  return "warning";
}

// Below this stage width (CSS px), a slide switches to the single-column Auto-Stack layout instead of the
// scaled canvas - based on the STAGE's own rendered width, not the viewport, so it is also correct inside an
// embedded/tunneled console whose iframe may be narrower than the outer window.
const MapStackBreakpoint = 640;

// Computes the uniform scale (+ centering offset) that fits each [data-map-stage]'s fixed logical canvas
// (--map-w x --map-h, in px) into whatever box the stage actually renders at, and writes it back as
// --map-kx/--map-ky/--map-ox/--map-oy - which .map-slide's `transform: translate(...) scale(...)` (site.css)
// reads. This is THE mechanism that makes the editor, /Maps and /Maps/Public render identically: the same
// .map-stage > .map-slide > .map-tile markup, only k differs per screen. Below MapStackBreakpoint, a slide
// instead gets `data-layout="stack"` (site.css turns it into a single-column flex list) and this function
// skips its transform math entirely - see the Auto-Stack CSS next to .map-slide[hidden].
function fitMapStages() {
  document.querySelectorAll("[data-map-stage]").forEach((stage) => {
    const style = getComputedStyle(stage);
    const mapWidth = parseFloat(style.getPropertyValue("--map-w")) || 1920;
    const mapHeight = parseFloat(style.getPropertyValue("--map-h")) || 1080;
    const rect = stage.getBoundingClientRect();
    const containerWidth = rect.width;
    const containerHeight = rect.height;
    const slides = stage.querySelectorAll(".map-slide");
    // Never stack the DESIGNER canvas. Auto-Stack is a reading layout for narrow VIEWERS; applying it to the
    // editor would show a single-column list while the user is placing widgets on a grid, which is the exact
    // opposite of WYSIWYG - and it silently kicks in on any window where the canvas column lands under 640px.
    const isDesigner = stage.querySelector("[data-map-designer]") !== null;
    const shouldStack = !isDesigner && containerWidth > 0 && containerWidth < MapStackBreakpoint;
    slides.forEach((slide) => {
      if (shouldStack) {
        slide.dataset.layout = "stack";
      } else {
        delete slide.dataset.layout;
      }
    });

    if (shouldStack || !containerWidth || !containerHeight || !mapWidth || !mapHeight) {
      return;
    }

    const fit = (stage.dataset.mapFit || "fit").toLowerCase();
    let kx;
    let ky;
    if (fit === "stretch") {
      kx = containerWidth / mapWidth;
      ky = containerHeight / mapHeight;
    } else {
      const k = Math.min(containerWidth / mapWidth, containerHeight / mapHeight);
      kx = k;
      ky = k;
    }

    const offsetX = (containerWidth - mapWidth * kx) / 2;
    const offsetY = (containerHeight - mapHeight * ky) / 2;

    stage.style.setProperty("--map-kx", String(kx));
    stage.style.setProperty("--map-ky", String(ky));
    stage.style.setProperty("--map-ox", `${offsetX}px`);
    stage.style.setProperty("--map-oy", `${offsetY}px`);
    // Exposed for the designer's pointer math (drag/resize) - avoids re-deriving k from a possibly-stale
    // getComputedStyle read on every pointermove.
    stage._mapScale = { kx, ky, offsetX, offsetY, mapWidth, mapHeight };
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
  const slideStrip = form?.querySelector("[data-map-slide-strip]");
  const slideTabsHost = slideStrip?.querySelector("[data-map-slide-tabs]");
  const slideInputsHost = slideStrip?.querySelector("[data-map-slide-inputs]");
  const slideAddButton = slideStrip?.querySelector("[data-map-slide-add]");
  const slideRenameButton = slideStrip?.querySelector("[data-map-slide-rename]");
  const slideDeleteButton = slideStrip?.querySelector("[data-map-slide-delete]");
  let slides = [];
  let activeSlideId = "";
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
    "11": "GeoMap"
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
    GeoMap: "World map"
  };
  const kindHints = {
    SensorList: "Lists the sensors under the target, ordered by state or by a channel value.",
    AlertFeed: "Newest open alerts. With no target it shows the whole workspace.",
    Sla: "Uptime over a window, from the statistics buckets - not live state.",
    Clock: "Shows the board timezone (Map properties > Wallboard).",
    Heading: "A section title. Use Text for the sub-line.",
    Image: "Upload a picture, then place status pins on it.",
    GeoMap: "The shipped offline world map - pins are placed by latitude/longitude.",
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

  // The logical canvas is always 1920px wide; only the height varies with the aspect ratio - mirrors
  // MonitoringMap.LogicalSizeFor exactly so the designer never disagrees with what the store will save.
  const readLogicalSize = () => {
    const aspectWidth = Math.max(1, Math.round(Number(aspectWidthInput?.value) || 16));
    const aspectHeight = Math.max(1, Math.round(Number(aspectHeightInput?.value) || 9));
    const logicalWidth = 1920;
    const logicalHeight = Math.max(1, Math.round((logicalWidth * aspectHeight) / aspectWidth));
    return { aspectWidth, aspectHeight, logicalWidth, logicalHeight };
  };

  // The SAME cell<->px math as MonitoringMapGeometry (Matmon.Core), mirrored here because JS cannot reference
  // Core directly. columns/rows/tilePadding/outerMargin are read from the hidden round-trip inputs above.
  const readGrid = () => {
    const { logicalWidth, logicalHeight } = readLogicalSize();
    const columns = Math.max(1, Math.round(Number(columnsInput?.value) || 12));
    const rows = Math.max(1, Math.round(Number(rowsInput?.value) || 6));
    const tilePadding = Math.max(0, Number(tilePaddingInput?.value) || 16);
    const outerMargin = Math.max(0, Number(outerMarginInput?.value) || 24);
    const cellWidth = (logicalWidth - 2 * outerMargin - (columns - 1) * tilePadding) / columns;
    const cellHeight = (logicalHeight - 2 * outerMargin - (rows - 1) * tilePadding) / rows;
    return { columns, rows, tilePadding, outerMargin, logicalWidth, logicalHeight, cellWidth, cellHeight };
  };

  // Cell rect (1-based column/row, span in cells) -> logical-px rect - mirrors MonitoringMapGeometry.PixelRect.
  const cellRectToPx = (grid, column, row, columnSpan, rowSpan) => ({
    x: grid.outerMargin + (column - 1) * (grid.cellWidth + grid.tilePadding),
    y: grid.outerMargin + (row - 1) * (grid.cellHeight + grid.tilePadding),
    w: columnSpan * grid.cellWidth + (columnSpan - 1) * grid.tilePadding,
    h: rowSpan * grid.cellHeight + (rowSpan - 1) * grid.tilePadding
  });

  // Logical-px point -> nearest 1-based cell - mirrors MonitoringMapGeometry.CellRectFromPixels' column/row half.
  const pxPointToCell = (grid, x, y) => ({
    column: Math.round((x - grid.outerMargin) / (grid.cellWidth + grid.tilePadding)) + 1,
    row: Math.round((y - grid.outerMargin) / (grid.cellHeight + grid.tilePadding)) + 1
  });

  // Logical-px size -> nearest cell span - mirrors MonitoringMapGeometry.CellRectFromPixels' span half.
  const pxSizeToSpan = (grid, width, height) => ({
    columnSpan: Math.round((width + grid.tilePadding) / (grid.cellWidth + grid.tilePadding)),
    rowSpan: Math.round((height + grid.tilePadding) / (grid.cellHeight + grid.tilePadding))
  });

  const syncMapSummary = () => {
    const name = mapNameInput?.value?.trim() || "New Map";
    const description = mapDescriptionInput?.value?.trim() || "No description";
    const { aspectWidth, aspectHeight } = readLogicalSize();
    if (mapTitlePreview) {
      mapTitlePreview.textContent = name;
    }
    if (mapDescriptionPreview) {
      mapDescriptionPreview.textContent = description;
    }
    if (mapGridPreview) {
      mapGridPreview.textContent = `${aspectWidth}:${aspectHeight} canvas`;
    }
  };

  // Recomputes the logical canvas size from the aspect-ratio fields, applies it to the stage + slide (both
  // carry their own --map-w/--map-h - the slide's server-rendered inline style would otherwise shadow the
  // stage's via CSS inheritance), zooms the stage per the slider (a percentage of the workbench width -
  // fitMapStages then measures the resulting box and computes kx/ky, so there is no separate zoom math here),
  // and finally re-clamps every tile into the (possibly resized) canvas.
  // The guide lines are server-rendered from MapTileRender.GridCells for the first paint. Once the user edits
  // columns/rows/gap/margin they have to be rebuilt from the same formula here, or the guides would keep
  // showing the old grid while the widgets already snap to the new one.
  const renderGridGuides = () => {
    if (!gridGuides) {
      return;
    }
    const grid = readGrid();
    const cells = [];
    for (let row = 1; row <= grid.rows; row += 1) {
      for (let column = 1; column <= grid.columns; column += 1) {
        const rect = cellRectToPx(grid, column, row, 1, 1);
        const style = "left:" + rect.x + "px;top:" + rect.y + "px;width:" + rect.w + "px;height:" + rect.h + "px;";
        cells.push('<span class="map-grid-cell" style="' + style + '"></span>');
      }
    }
    gridGuides.innerHTML = cells.join("");
  };

  const syncCanvas = () => {
    const { logicalWidth, logicalHeight } = readLogicalSize();
    stage.style.setProperty("--map-w", String(logicalWidth));
    stage.style.setProperty("--map-h", String(logicalHeight));
    canvas.style.setProperty("--map-w", String(logicalWidth));
    canvas.style.setProperty("--map-h", String(logicalHeight));

    const scale = readScale();
    stage.style.width = `${Math.round(scale * 100)}%`;
    if (scaleOutput) {
      scaleOutput.textContent = `${Math.round(scale * 100)}%`;
    }

    syncMapSummary();
    renderGridGuides();
    fitMapStages();
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => applyTilePosition(tile));
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
      column: tile.querySelector("[data-map-tile-column]"),
      row: tile.querySelector("[data-map-tile-row]"),
      columnSpan: panel?.querySelector("[data-map-tile-column-span]"),
      rowSpan: panel?.querySelector("[data-map-tile-row-span]")
    };
  };

  const applyTilePosition = (tile) => {
    const { column, row, columnSpan, rowSpan } = getTileControls(tile);
    const panel = getPanel(tile.dataset.tileIndex || "");
    const kind = normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || tile.dataset.kind);
    const limits = getLimits(kind);
    const grid = readGrid();
    const nextColumnSpan = clamp(Math.round(Number(columnSpan?.value || limits.defaultColumns)), limits.minColumns, grid.columns);
    const nextRowSpan = clamp(Math.round(Number(rowSpan?.value || limits.defaultRows)), limits.minRows, grid.rows);
    const nextColumn = clamp(Math.round(Number(column?.value ?? 1)), 1, Math.max(1, grid.columns - nextColumnSpan + 1));
    const nextRow = clamp(Math.round(Number(row?.value ?? 1)), 1, Math.max(1, grid.rows - nextRowSpan + 1));
    if (columnSpan) {
      columnSpan.value = String(nextColumnSpan);
    }
    if (rowSpan) {
      rowSpan.value = String(nextRowSpan);
    }
    if (column) {
      column.value = String(nextColumn);
    }
    if (row) {
      row.value = String(nextRow);
    }

    const rect = cellRectToPx(grid, nextColumn, nextRow, nextColumnSpan, nextRowSpan);
    tile.style.setProperty("--tile-x", String(Math.round(rect.x)));
    tile.style.setProperty("--tile-y", String(Math.round(rect.y)));
    tile.style.setProperty("--tile-w", String(Math.round(rect.w)));
    tile.style.setProperty("--tile-h", String(Math.round(rect.h)));
    tile.style.setProperty("--stack-min-h", `${nextRowSpan * 72}px`);
    const readout = panel?.querySelector("[data-map-property-size]");
    if (readout) {
      readout.textContent = `Size ${nextColumnSpan} x ${nextRowSpan} cells · Min ${limits.minColumns} x ${limits.minRows} cells`;
    }
    // Live size badge on the tile itself (shown while dragging/resizing) - "you see the tile taking shape".
    const badge = tile.querySelector("[data-map-tile-size-badge]");
    if (badge) {
      badge.textContent = `${nextColumnSpan} × ${nextRowSpan}`;
    }
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

  // --- Image upload + pin editor ----------------------------------------------------------------------
  // Pins are held as JSON in one hidden field per tile (see MapTileInput.PinsJson): they are an unbounded
  // per-tile collection placed by clicking a picture, and the designer clones whole tiles by rewriting field
  // names - index-bound nested fields would have to be re-indexed on every clone and delete.
  const pinTemplate = form?.querySelector("template[data-map-pin-template]");

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

  const writePins = (panel, pins) => {
    const field = panel?.querySelector("[data-map-property-pins]");
    if (field) {
      field.value = pins.length === 0 ? "" : JSON.stringify(pins);
      field.dispatchEvent(new Event("change", { bubbles: true }));
    }
  };

  const isGeoPanel = (panel) =>
    normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value) === "GeoMap";

  const renderPinRows = (panel) => {
    const host = panel?.querySelector("[data-map-pin-editor]");
    if (!host || !pinTemplate) {
      return;
    }

    const pins = readPins(panel);
    const geo = isGeoPanel(panel);
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
        entry.targetToken = row.querySelector("[data-pin-field-target]")?.value || null;
        if (geo) {
          const lat = Number(row.querySelector('[data-pin-field="latitude"]').value);
          const lon = Number(row.querySelector('[data-pin-field="longitude"]').value);
          entry.latitude = Number.isFinite(lat) ? lat : null;
          entry.longitude = Number.isFinite(lon) ? lon : null;
        }
        writePins(panel, current);
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
      });

      host.appendChild(row);
    });

    const hint = panel.querySelector("[data-map-pin-hint]");
    if (hint) {
      hint.textContent = geo
        ? "Enter latitude/longitude - the world map is equirectangular, so a pin lands exactly on its coordinates."
        : 'Use "Place on picture" and then click the tile where the pin belongs.';
    }
    const placeButton = panel.querySelector("[data-map-pin-place]");
    if (placeButton) {
      placeButton.hidden = geo;
    }
    initializeElementPickers();
  };

  const addPin = (panel, x, y) => {
    const pins = readPins(panel);
    pins.push({
      id: createId(),
      label: "",
      showLabel: true,
      style: "Dot",
      targetToken: null,
      x: x ?? 0.5,
      y: y ?? 0.5,
      latitude: null,
      longitude: null
    });
    writePins(panel, pins);
    renderPinRows(panel);
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
      window.alert("Upload a picture first.");
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

  propertyHost?.addEventListener("click", (event) => {
    const panel = event.target.closest("[data-map-property-panel]");
    if (!panel) {
      return;
    }
    if (event.target.closest("[data-map-pin-add]")) {
      addPin(panel, 0.5, 0.5);
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
    const isList = kind === "SensorList";
    const isRows = isList || kind === "AlertFeed";
    const isSla = kind === "Sla";
    const isImage = kind === "Image";
    const isPinned = isImage || kind === "GeoMap";
    // A clock has no target at all; a heading and a text tile carry their own copy instead of one.
    // An image / world map has no single target of its own - each PIN carries one.
    const isTargetless = kind === "Clock" || kind === "Text" || kind === "Heading"
      || kind === "Image" || kind === "GeoMap";
    const targetField = panel.querySelector("[data-map-property-target]");
    const visualField = panel.querySelector("[data-map-property-visual]");
    const textField = panel.querySelector("[data-map-property-text-only]");
    const graphField = panel.querySelector("[data-map-property-graph-only]");
    const hint = panel.querySelector("[data-map-property-hint]");
    if (targetField) {
      targetField.hidden = isTargetless;
    }
    panel.querySelectorAll("[data-map-property-list-only]").forEach((field) => { field.hidden = !isList; });
    panel.querySelectorAll("[data-map-property-rows-only]").forEach((field) => { field.hidden = !isRows; });
    panel.querySelectorAll("[data-map-property-sla-only]").forEach((field) => { field.hidden = !isSla; });
    panel.querySelectorAll("[data-map-property-image-only]").forEach((field) => { field.hidden = !isImage; });
    panel.querySelectorAll("[data-map-property-pins-only]").forEach((field) => { field.hidden = !isPinned; });
    if (isPinned) {
      renderPinRows(panel);
    }
    if (visualField) {
      visualField.hidden = isText || isGraph || isRows || isSla || isPinned || kind === "Clock";
    }
    if (textField) {
      textField.hidden = !isText;
    }
    if (graphField) {
      graphField.hidden = !isGraph;
    }
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
        stateLabelEl.dataset.state = data.stateKey;
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
    if (subtitle) {
      const isText = kind === "Text";
      // The target is now an element picker: its name lives on the hidden value
      // input's data-selected-name (set when chosen / server-rendered). A resolved live preview (once the
      // TilePreview fetch below lands) overwrites this with the real subtitle via applyLivePreview.
      const selectedText = (elementSelect?.dataset.selectedName || "").trim();
      subtitle.textContent = isText
        ? (text.trim() || "Text tile")
        : (selectedText || "No target selected");
    }

    const showCard = panel.querySelector("[data-map-property-show-card]")?.checked ?? true;
    tile.classList.toggle("is-plain", !showCard);

    syncPanelVisibility(panel);
    applyTileAppearance(tile, panel);
    applyTilePosition(tile);
    refreshTilePreview(tile, panel);
  };

  // Which General/Data/Display group of the SELECTED tile panel is visible. The bar is shared by all tile
  // panels (see MapEditor.cshtml), so this is designer state, not per-panel state.
  const applyPropertyTab = (panel) => {
    panel?.querySelectorAll("[data-map-prop-group]").forEach((group) => {
      group.hidden = group.dataset.mapPropGroup !== activePropertyTab;
    });
    propertyTabs?.querySelectorAll("[data-map-property-tab]").forEach((button) => {
      const isActive = button.dataset.mapPropertyTab === activePropertyTab;
      button.classList.toggle("is-active", isActive);
      button.setAttribute("aria-selected", isActive ? "true" : "false");
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
  const selectSlide = () => {
    const slide = slides.find((candidate) => candidate.id === activeSlideId);
    if (!slide || !slidePanel) {
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

    slidePanel.querySelectorAll("[data-map-slide-field]").forEach((field) => {
      const key = field.dataset.mapSlideField;
      if (field.type === "checkbox") {
        field.checked = slide[key] !== false;
      } else {
        field.value = slide[key] ?? "";
      }
    });

    // The panel lives in the Slides TAB now, so "edit this slide" has to take you there.
    form?.querySelector('[data-sensor-tab-target="slides"]')?.click();
  };

  // Converts a pointer event to logical-px coordinates on the canvas, transform-aware: the canvas
  // (.map-slide) is scaled via CSS transform (see fitMapStages), so its OWN getBoundingClientRect() already
  // reflects that scale - dividing by (renderedSize / logicalSize) recovers the pre-scale logical position
  // without needing to read the --map-kx/ky custom properties back out.
  const pointerToLogical = (event) => {
    const rect = canvas.getBoundingClientRect();
    const { logicalWidth, logicalHeight } = readLogicalSize();
    const kx = rect.width / logicalWidth || 1;
    const ky = rect.height / logicalHeight || 1;
    return {
      x: (event.clientX - rect.left) / kx,
      y: (event.clientY - rect.top) / ky
    };
  };

  const setupTile = (tile) => {
    applyTilePosition(tile);
    tile.addEventListener("click", () => selectTile(tile.dataset.tileIndex || ""));

    const panel = getPanel(tile.dataset.tileIndex || "");
    panel?.querySelectorAll("[data-map-tile-column-span], [data-map-tile-row-span]").forEach((input) => {
      input.addEventListener("input", () => applyTilePosition(tile));
    });
    panel?.querySelectorAll("[data-map-property-title], [data-map-property-kind], [data-map-property-visual-type], [data-map-property-element], [data-map-property-text], [data-map-property-graph-type], [data-map-property-background], [data-map-property-accent], [data-map-property-text-color], [data-map-property-show-title], [data-map-property-show-badge], [data-map-property-show-card]").forEach((input) => {
      input.addEventListener("input", () => syncTileFromPanel(panel));
      input.addEventListener("change", () => syncTileFromPanel(panel));
    });
    // The server already rendered each saved tile's real preview, so seed the cache to skip an
    // identical refetch on load; live fetches then only fire when the user actually changes the target.
    if (panel) {
      previewKeyCache.set(tile, previewKeyFor(panel).key);
    }
    syncTileFromPanel(panel);

    tile.querySelector("[data-map-remove-tile]")?.addEventListener("click", (event) => {
      event.stopPropagation();
      const deleted = tile.querySelector("[data-map-tile-deleted]");
      if (deleted) {
        deleted.value = "true";
      }

      tile.hidden = true;
      panel?.setAttribute("hidden", "hidden");
      selectMap();
    });

    // The WHOLE tile is the drag surface (except its interactive controls and the resize grip). It used to
    // be a thin header strip - barely 15% of a tile's height, and proportionally thinner the smaller the
    // tile or the zoom - so most of a tile simply did not react to dragging at all.
    tile.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) {
        return;
      }

      if (event.target instanceof HTMLElement
        && event.target.closest("input, select, textarea, button, a, [data-map-resize-handle]")) {
        return;
      }

      event.preventDefault();
      selectTile(tile.dataset.tileIndex || "");
      try {
        tile.setPointerCapture(event.pointerId);
      } catch {
        // Capture is a nicety here; the window-level listeners below are what actually carry the drag.
      }
      tile.classList.add("is-dragging");

      // Remember WHERE INSIDE the tile (in logical px) the pointer grabbed it, so that spot stays under the
      // cursor for the whole drag - expressed as a px offset from the tile's current cell rect, since the
      // pointer itself moves continuously while cells only exist at discrete steps. Without this the tile's
      // top-left corner was slammed onto the pointer, so grabbing a tile anywhere but its exact corner made
      // it jump the instant you started moving.
      const grabControls = getTileControls(tile);
      const grabGrid = readGrid();
      const grabRect = cellRectToPx(
        grabGrid,
        Number(grabControls.column?.value || 1),
        Number(grabControls.row?.value || 1),
        Number(grabControls.columnSpan?.value || 2),
        Number(grabControls.rowSpan?.value || 2));
      const grabPoint = pointerToLogical(event);
      const grabOffsetX = grabPoint.x - grabRect.x;
      const grabOffsetY = grabPoint.y - grabRect.y;

      const move = (moveEvent) => {
        const controls = getTileControls(tile);
        const grid = readGrid();
        const point = pointerToLogical(moveEvent);
        const cell = pxPointToCell(grid, point.x - grabOffsetX, point.y - grabOffsetY);
        if (controls.column) {
          controls.column.value = String(cell.column);
        }
        if (controls.row) {
          controls.row.value = String(cell.row);
        }

        applyTilePosition(tile);
      };

      const up = () => {
        tile.classList.remove("is-dragging");
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
      const panel = getPanel(tile.dataset.tileIndex || "");
      const kind = normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || tile.dataset.kind);
      const limits = getLimits(kind);
      const grid = readGrid();
      const startColumnSpan = Math.max(limits.minColumns, Number(controls.columnSpan?.value || limits.defaultColumns));
      const startRowSpan = Math.max(limits.minRows, Number(controls.rowSpan?.value || limits.defaultRows));
      const startRect = cellRectToPx(grid, Number(controls.column?.value || 1), Number(controls.row?.value || 1), startColumnSpan, startRowSpan);
      const startX = Number(event.clientX);
      const startY = Number(event.clientY);
      // k at drag-start (screen px per logical px) - a pointer delta in screen px divided by k is the
      // equivalent delta in logical px, same maths as pointerToLogical but for a DELTA instead of a point.
      const rect = canvas.getBoundingClientRect();
      const { logicalWidth, logicalHeight } = readLogicalSize();
      const kx = rect.width / logicalWidth || 1;
      const ky = rect.height / logicalHeight || 1;

      const move = (moveEvent) => {
        const deltaWidth = (moveEvent.clientX - startX) / kx;
        const deltaHeight = (moveEvent.clientY - startY) / ky;
        const span = pxSizeToSpan(grid, startRect.w + deltaWidth, startRect.h + deltaHeight);
        if (controls.columnSpan) {
          controls.columnSpan.value = String(Math.max(limits.minColumns, span.columnSpan));
        }
        if (controls.rowSpan) {
          controls.rowSpan.value = String(Math.max(limits.minRows, span.rowSpan));
        }

        applyTilePosition(tile);
      };

      const up = () => {
        tile.classList.remove("is-resizing");
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        window.removeEventListener("pointercancel", up);
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
    const columnSpan = clamp(placement?.columnSpan ?? limits.defaultColumns, limits.minColumns, grid.columns);
    const rowSpan = clamp(placement?.rowSpan ?? limits.defaultRows, limits.minRows, grid.rows);
    // A layout template hands in an explicit cell. A DROP instead centers the new tile on the cursor rather
    // than hanging it off the pointer by its top-left corner, so where you release is where the tile appears -
    // computed in px then converted to the nearest cell, since the drop point is a continuous position.
    let targetColumn = placement?.column;
    let targetRow = placement?.row;
    if (targetColumn === undefined || targetRow === undefined) {
      const rectW = columnSpan * grid.cellWidth + (columnSpan - 1) * grid.tilePadding;
      const rectH = rowSpan * grid.cellHeight + (rowSpan - 1) * grid.tilePadding;
      const dropCell = pxPointToCell(grid, (position?.x ?? grid.outerMargin) - rectW / 2, (position?.y ?? grid.outerMargin) - rectH / 2);
      targetColumn = dropCell.column;
      targetRow = dropCell.row;
    }
    const column = clamp(targetColumn, 1, Math.max(1, grid.columns - columnSpan + 1));
    const row = clamp(targetRow, 1, Math.max(1, grid.rows - rowSpan + 1));
    const html = template.innerHTML
      .replaceAll("__index__", String(index))
      .replaceAll("__id__", createId())
      .replaceAll("__slideId__", activeSlideId || "")
      .replaceAll("__kindLower__", kind.toLowerCase())
      .replaceAll("__kind__", kind)
      .replaceAll("__visual__", (tool.visual || "card").toLowerCase())
      .replaceAll("__kindLabel__", getKindLabel(kind))
      .replaceAll("__title__", title)
      .replaceAll("__column__", String(column))
      .replaceAll("__row__", String(row))
      .replaceAll("__colSpan__", String(columnSpan))
      .replaceAll("__rowSpan__", String(rowSpan));
    const fragment = document.createRange().createContextualFragment(html);
    const tile = fragment.querySelector("[data-map-tile]");
    const panel = fragment.querySelector("[data-map-property-panel]");
    if (!tile || !panel) {
      return;
    }

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
    // Initialize the freshly cloned tile's element + icon pickers (guarded so existing
    // ones aren't re-wired).
    initializeElementPickers();
    initializeIconPicker();
    selectTile(index);
  };

  mapSelectButton?.addEventListener("click", selectMap);
  mapNameInput?.addEventListener("input", () => syncMapSummary());
  mapDescriptionInput?.addEventListener("input", () => syncMapSummary());
  // Changing the aspect ratio changes the logical canvas HEIGHT (width is always 1920) - re-clamping every
  // tile (inside syncCanvas) is the "rescale" step here; unlike the old grid-cell scheme there is no separate
  // proportional-rescale pass because a tile's px size/position simply keeps meaning the same thing.
  aspectWidthInput?.addEventListener("input", () => syncCanvas());
  aspectHeightInput?.addEventListener("input", () => syncCanvas());
  form?.querySelectorAll("[data-map-aspect-preset]").forEach((button) => {
    button.addEventListener("click", () => {
      if (aspectWidthInput) { aspectWidthInput.value = button.dataset.aspectW || "16"; }
      if (aspectHeightInput) { aspectHeightInput.value = button.dataset.aspectH || "9"; }
      form?.querySelectorAll("[data-map-aspect-preset]").forEach((other) => {
        other.classList.toggle("is-active", other === button);
      });
      syncCanvas();
    });
  });
  scaleInput?.addEventListener("input", () => syncCanvas());

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

  const renderSlideTabs = () => {
    if (!slideTabsHost) {
      return;
    }
    slideTabsHost.replaceChildren();
    slides.forEach((slide) => {
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = "map-slide-tab" + (slide.id === activeSlideId ? " is-active" : "");
      tab.dataset.slideId = slide.id;
      tab.setAttribute("data-map-slide-tab", "");
      tab.textContent = slide.name;
      tab.addEventListener("click", () => setActiveSlide(slide.id));
      slideTabsHost.appendChild(tab);
    });
  };

  const applySlideFilter = () => {
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      const deleted = tile.querySelector("[data-map-tile-deleted]")?.value === "true";
      const onActiveSlide = (tile.dataset.slideId || "") === activeSlideId;
      tile.hidden = deleted || !onActiveSlide;
    });
  };

  const setActiveSlide = (id) => {
    if (!id) {
      return;
    }
    activeSlideId = id;
    slideTabsHost?.querySelectorAll("[data-map-slide-tab]").forEach((tab) => {
      tab.classList.toggle("is-active", tab.dataset.slideId === activeSlideId);
    });
    applySlideFilter();
    selectMap();
  };

  const addSlide = () => {
    const id = createId();
    slides.push({ id, name: `Slide ${slides.length + 1}`, showHeader: true });
    renderSlideInputs();
    renderSlideTabs();
    setActiveSlide(id);
  };

  const renameSlide = () => {
    const slide = slides.find((candidate) => candidate.id === activeSlideId);
    if (!slide) {
      return;
    }
    const name = window.prompt("Slide name", slide.name);
    if (name === null) {
      return;
    }
    slide.name = name.trim() || slide.name;
    renderSlideInputs();
    renderSlideTabs();
  };

  const deleteSlide = () => {
    if (slides.length <= 1) {
      window.alert("A map needs at least one slide.");
      return;
    }
    if (!window.confirm("Delete this slide and all its tiles?")) {
      return;
    }
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      if ((tile.dataset.slideId || "") === activeSlideId) {
        const deleted = tile.querySelector("[data-map-tile-deleted]");
        if (deleted) {
          deleted.value = "true";
        }
        tile.hidden = true;
      }
    });
    slides = slides.filter((candidate) => candidate.id !== activeSlideId);
    renderSlideInputs();
    renderSlideTabs();
    setActiveSlide(slides[0].id);
  };

  slides = slideTabsHost
    ? Array.from(slideTabsHost.querySelectorAll("[data-map-slide-tab]")).map((tab) => ({
        id: tab.dataset.slideId,
        name: tab.textContent.trim(),
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
  slideRenameButton?.addEventListener("click", renameSlide);
  slideDeleteButton?.addEventListener("click", deleteSlide);

  canvas.querySelectorAll("[data-map-tile]").forEach(setupTile);
  applySlideFilter();
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

  canvas.addEventListener("drop", (event) => {
    event.preventDefault();
    const rawPayload = event.dataTransfer?.getData("application/x-matmon-map-tool");
    if (!rawPayload) {
      return;
    }

    try {
      const payload = JSON.parse(rawPayload);
      addTile(payload, pointerToLogical(event));
    } catch {
      addTile({ kind: "1", title: "Tile" });
    }
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

  // --- Layout templates -------------------------------------------------------------------------------
  // A template is authored against a minimum grid; on a smaller one its slots would be clamped and pile up,
  // so the button is disabled instead of quietly producing a mess.
  const syncLayoutAvailability = () => {
    const grid = readGrid();
    let anyHidden = false;
    form?.querySelectorAll("[data-map-layout]").forEach((button) => {
      const tooSmall = grid.columns < Number(button.dataset.layoutMinColumns || 1)
        || grid.rows < Number(button.dataset.layoutMinRows || 1);
      button.disabled = tooSmall;
      button.classList.toggle("is-disabled", tooSmall);
      anyHidden = anyHidden || tooSmall;
    });
    const warn = form?.querySelector("[data-map-layout-too-small]");
    if (warn) {
      warn.hidden = !anyHidden;
    }
  };

  const applyLayoutTemplate = (key) => {
    const template = layoutTemplates.find((candidate) => candidate.key === key);
    if (!template) {
      return;
    }
    const existing = Array.from(canvas.querySelectorAll("[data-map-tile]")).filter((tile) =>
      (tile.dataset.slideId || "") === activeSlideId
      && tile.querySelector("[data-map-tile-deleted]")?.value !== "true");
    if (existing.length > 0 && !window.confirm(`Replace the ${existing.length} widget(s) on this slide with the "${template.label}" layout?`)) {
      return;
    }
    existing.forEach((tile) => {
      const deleted = tile.querySelector("[data-map-tile-deleted]");
      if (deleted) {
        deleted.value = "true";
      }
      tile.hidden = true;
    });

    (template.slots || []).forEach((slot) => {
      const widget = widgetCatalog[slot.widget];
      if (!widget) {
        return;
      }
      addTile(widget, null, slot);
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
  form?.querySelector("[data-map-slide-settings]")?.addEventListener("click", selectSlide);

  // --- Slide order + duplicate ------------------------------------------------------------------------
  form?.querySelectorAll("[data-map-slide-move]").forEach((button) => {
    button.addEventListener("click", () => {
      const delta = Number(button.dataset.mapSlideMove || 0);
      const from = slides.findIndex((candidate) => candidate.id === activeSlideId);
      const to = from + delta;
      if (from < 0 || to < 0 || to >= slides.length) {
        return;
      }
      slides.splice(to, 0, slides.splice(from, 1)[0]);
      renderSlideInputs();
      renderSlideTabs();
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

  form?.querySelector("[data-map-slide-duplicate]")?.addEventListener("click", () => {
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
      const rewrite = (html) => html
        .replaceAll(`Input.Tiles[${oldIndex}]`, `Input.Tiles[${index}]`)
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
  });


  // --- Slide previews ---------------------------------------------------------------------------------
  // A real scaled clone of the slide, not a hand-drawn thumbnail: a preview that can disagree with the board
  // is worse than no preview at all. Cloned from the live canvas so it reflects unsaved edits too.
  const slidePreviewHost = form?.querySelector("[data-map-slide-previews]");

  const renderSlidePreviews = () => {
    if (!slidePreviewHost) {
      return;
    }
    const { logicalWidth, logicalHeight } = readLogicalSize();
    slidePreviewHost.style.setProperty("--map-preview-ratio", `${logicalWidth} / ${logicalHeight}`);

    slidePreviewHost.querySelectorAll("[data-map-slide-card]").forEach((card) => {
      const frame = card.querySelector("[data-map-slide-preview]");
      if (!frame) {
        return;
      }
      frame.style.setProperty("--map-preview-ratio", `${logicalWidth} / ${logicalHeight}`);
      const slideId = card.dataset.slideId || "";
      const board = document.createElement("div");
      board.className = "map-slide map-slide-preview-board";
      board.style.cssText = `position:absolute;inset:0;width:${logicalWidth}px;height:${logicalHeight}px;`
        + "transform-origin:top left;";

      canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
        if ((tile.dataset.slideId || "") !== slideId
          || tile.querySelector("[data-map-tile-deleted]")?.value === "true") {
          return;
        }
        const dot = document.createElement("span");
        dot.className = "map-slide-preview-tile";
        dot.dataset.state = tile.dataset.state || "unknown";
        dot.style.cssText = `position:absolute;left:${tile.style.getPropertyValue("--tile-x")}px;`
          + `top:${tile.style.getPropertyValue("--tile-y")}px;`
          + `width:${tile.style.getPropertyValue("--tile-w")}px;`
          + `height:${tile.style.getPropertyValue("--tile-h")}px;`;
        board.appendChild(dot);
      });

      frame.replaceChildren(board);
      // Scale AFTER insertion: the frame has no size until it is in the document.
      const rect = frame.getBoundingClientRect();
      if (rect.width > 0) {
        board.style.transform = `scale(${rect.width / logicalWidth})`;
      }
    });
  };

  form?.querySelectorAll("[data-map-slide-card]").forEach((card) => {
    card.addEventListener("click", () => {
      setActiveSlide(card.dataset.slideId || "");
      slidePreviewHost?.querySelectorAll("[data-map-slide-card]").forEach((other) => {
        other.classList.toggle("is-active", other === card);
      });
      selectSlide();
    });

    // Reorder by dragging a card - the mockup's filmstrip, and far more direct than the arrow buttons.
    card.addEventListener("dragstart", (event) => {
      event.dataTransfer?.setData("text/plain", card.dataset.slideId || "");
      card.classList.add("is-dragging");
    });
    card.addEventListener("dragend", () => card.classList.remove("is-dragging"));
    card.addEventListener("dragover", (event) => event.preventDefault());
    card.addEventListener("drop", (event) => {
      event.preventDefault();
      const movedId = event.dataTransfer?.getData("text/plain");
      const from = slides.findIndex((candidate) => candidate.id === movedId);
      const to = slides.findIndex((candidate) => candidate.id === card.dataset.slideId);
      if (from < 0 || to < 0 || from === to) {
        return;
      }
      slides.splice(to, 0, slides.splice(from, 1)[0]);
      renderSlideInputs();
      renderSlideTabs();
      window.location.hash = "";
      // The cards are server-rendered, so reordering them in the DOM keeps the strip honest without a reload.
      const cards = Array.from(slidePreviewHost.querySelectorAll("[data-map-slide-card]"));
      const moved = cards.find((candidate) => candidate.dataset.slideId === movedId);
      if (moved) {
        slidePreviewHost.insertBefore(moved, from < to ? card.nextSibling : card);
      }
      renderSlidePreviews();
    });
  });
  renderSlidePreviews();
  filterTools();
  syncLayoutAvailability();
  renderGridGuides();

  selectMap();
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
    card.dataset.state = state;
    card.style.setProperty("--series-color", series.stateColor || series.lineColor || "var(--matmon-accent)");

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
    }

    const svg = card.querySelector("svg.sparkline");
    if (svg) {
      drawSparkline(svg, series.points ?? [], series.stateColor || series.lineColor || "var(--matmon-accent)");
    }
  });
}

function renderDashboardStatusChart(snapshot) {
  const charts = document.querySelectorAll("[data-dashboard-status-chart]");
  if (charts.length === 0) {
    return;
  }

  const counts = getDashboardStatusCounts(snapshot);
  const gradient = buildDashboardStatusGradient(counts);
  const numberFormatter = new Intl.NumberFormat();

  charts.forEach((chart) => {
    const donut = chart.querySelector('[data-role="status-donut"]');
    if (donut) {
      donut.style.background = gradient;
    }

    setDashboardStatusText(chart, "status-total", numberFormatter.format(counts.total));

    counts.items.forEach((item) => {
      setDashboardStatusText(chart, `status-${item.key}-count`, numberFormatter.format(item.count));
      setDashboardStatusText(chart, `status-${item.key}-percent`, formatDashboardStatusPercent(item.count, counts.total));

      const row = chart.querySelector(`[data-dashboard-status-item="${item.key}"]`);
      if (row && (item.key === "other" || item.key === "paused")) {
        row.classList.toggle("is-hidden", item.count <= 0);
      }
    });
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
      { key: "ok", count: healthy, color: "#78d5c8" },
      { key: "warning", count: warning, color: "#f3b36b" },
      { key: "ack", count: ack, color: "#5f8dff" },
      { key: "error", count: error, color: "#ff7f93" },
      { key: "paused", count: paused, color: "#6b8caf" },
      { key: "other", count: other, color: "#7c8eab" }
    ]
  };
}

function buildDashboardStatusGradient(counts) {
  const visibleItems = counts.items.filter((item) => item.count > 0);
  const total = visibleItems.reduce((sum, item) => sum + item.count, 0);
  if (total <= 0) {
    return "conic-gradient(rgba(124, 142, 171, 0.22) 0% 100%)";
  }

  let cursor = 0;
  const segments = visibleItems.map((item) => {
    const next = cursor + (item.count / total) * 100;
    const segment = `${item.color} ${cursor.toFixed(3)}% ${next.toFixed(3)}%`;
    cursor = next;
    return segment;
  });

  return `conic-gradient(${segments.join(", ")})`;
}

function formatDashboardStatusPercent(count, total) {
  if (total <= 0) {
    return "0%";
  }

  return `${Math.round((count / total) * 100)}%`;
}

function setDashboardStatusText(chart, role, value) {
  chart.querySelectorAll(`[data-role="${role}"]`).forEach((element) => {
    element.textContent = value;
  });
}

function renderNavCounters(snapshot) {
  // Everything here is alert-based so the sidebar badge agrees with the Alerts page. The big
  // number is open (unacknowledged) alerts; the Err/Warn tiles are active alerts by severity
  // (which can outlive sensor recovery, Alerta-style - that's why sensor states diverged before).
  const openAlerts = Number(snapshot.activeAlertCount ?? 0);
  const acknowledgedAlerts = Number(snapshot.acknowledgedAlertCount ?? 0);
  const errorAlerts = Number(snapshot.errorAlertCount ?? 0);
  const warningAlerts = Number(snapshot.warningAlertCount ?? 0);
  const pausedSensors = Number(snapshot.pausedSensorCount ?? 0);
  const alertStatus = document.querySelector("[data-nav-alert-status]");
  const hasErrors = errorAlerts > 0;
  const hasWarnings = warningAlerts > 0 || acknowledgedAlerts > 0 || pausedSensors > 0;

  const alertTone = hasErrors
    ? "error"
    : hasWarnings
      ? "warning"
      : "ok";

  if (alertStatus) {
    alertStatus.dataset.tone = alertTone;

    const stateElement = alertStatus.querySelector("[data-nav-alert-state]");
    if (stateElement) {
      let stateLabel = "OK";
      if (errorAlerts > 0) {
        stateLabel = "Error";
      } else if (warningAlerts > 0) {
        stateLabel = "Warning";
      } else if (acknowledgedAlerts > 0) {
        stateLabel = "Ack";
      } else if (pausedSensors > 0) {
        stateLabel = "Paused";
      }

      stateElement.textContent = stateLabel;
    }

    const hintElement = alertStatus.querySelector("[data-nav-alert-hint]");
    if (hintElement) {
      const parts = [];
      if (errorAlerts > 0) {
        parts.push(`${errorAlerts} error`);
      }
      if (warningAlerts > 0) {
        parts.push(`${warningAlerts} warning`);
      }
      if (acknowledgedAlerts > 0) {
        parts.push(`${acknowledgedAlerts} ack`);
      }
      if (pausedSensors > 0) {
        parts.push(`${pausedSensors} paused`);
      }

      hintElement.textContent = parts.length > 0 ? parts.join(" / ") : "All clear";
    }
  }

  setNavBadge("[data-nav-alert-count]", openAlerts, alertTone, true);
  setNavCounterText("[data-nav-error-count]", errorAlerts);
  setNavCounterText("[data-nav-warning-count]", warningAlerts);
  setNavCounterText("[data-nav-ack-count]", acknowledgedAlerts);
  setNavCounterText("[data-nav-paused-inline-count]", pausedSensors);
}

function setNavCounterText(selector, count) {
  const counter = document.querySelector(selector);
  if (counter) {
    counter.textContent = String(count);
  }
}

function setNavBadge(selector, count, tone, showZero = false) {
  const badge = document.querySelector(selector);
  if (!badge) {
    return;
  }

  if (count > 0 || showZero) {
    badge.hidden = false;
    badge.textContent = String(count);
    badge.dataset.tone = tone;
  } else {
    badge.hidden = true;
    badge.textContent = "";
    delete badge.dataset.tone;
  }
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

// A "Test" or SNMP-discover that was routed to a remote probe runs asynchronously (the primary only
// queued a run job). Each preview container carries the job id; poll GET /api/run-jobs/{id} until it
// completes, then render a compact channel preview / discovered-OID list. Progressive enhancement and
// dependency-free; the local (in-process) path renders no container and is untouched.
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
  const statusElement = container.querySelector(".remote-run-preview-status");
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
      if (statusElement) {
        statusElement.textContent = `Still running on probe ${probeName}… the result will appear on the sensor once the probe syncs.`;
      }
      return;
    }

    window.setTimeout(tick, 1500);
  };

  window.setTimeout(tick, 1500);
}

function renderRemoteRunResult(container, kind, job) {
  if (job.error) {
    container.innerHTML = `<span class="remote-run-preview-status">Run failed on probe: ${escapeHtml(job.error)}</span>`;
    return;
  }

  if (kind === "snmp") {
    const oids = Array.isArray(job.discoveredOids) ? job.discoveredOids : [];
    if (oids.length === 0) {
      container.innerHTML = `<span class="remote-run-preview-status">No OIDs discovered.</span>`;
      return;
    }

    const rows = oids
      .map((item) => `<li><code>${escapeHtml(item.oid || "")}</code> <span>${escapeHtml(item.value || "")}</span> <em>${escapeHtml(item.syntax || "")}</em></li>`)
      .join("");
    container.innerHTML =
      `<div class="remote-run-preview-status">Discovered ${oids.length} OID${oids.length === 1 ? "" : "s"} on the probe. Re-open the editor after saving to pick channels.</div>` +
      `<ul class="remote-run-preview-oids">${rows}</ul>`;
    return;
  }

  const result = job.result;
  if (!result) {
    container.innerHTML = `<span class="remote-run-preview-status">The probe reported no result.</span>`;
    return;
  }

  const state = escapeHtml(String(result.state || "Unknown"));
  const message = result.message ? ` - ${escapeHtml(result.message)}` : "";
  const channels = Array.isArray(result.channels) ? result.channels : [];
  const channelRows = channels
    .map((channel) => {
      const label = escapeHtml(channel.label || channel.key || "");
      const value = channel.value === null || channel.value === undefined ? "-" : escapeHtml(String(channel.value));
      const unit = channel.unit ? ` ${escapeHtml(channel.unit)}` : "";
      return `<li><span>${label}</span><strong>${value}${unit}</strong></li>`;
    })
    .join("");

  container.innerHTML =
    `<div class="remote-run-preview-status">Test on probe: <strong>${state}</strong>${message}</div>` +
    (channelRows ? `<ul class="remote-run-preview-channels">${channelRows}</ul>` : "");
}

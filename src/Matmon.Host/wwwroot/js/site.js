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
  initializeMapDesigner();
  initializeMapCarousel();
  initializeElementPickers();
  initializeTagInputs();
  initializeTagOverflow();
  initializeAlertsTable();
});

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

  let filter = root.dataset.initialFilter || "all";
  let query = "";
  let page = 1;
  let pageSize = pageSizeSelect ? Number(pageSizeSelect.value) || 50 : 50;
  let sortMode = sortSelect ? sortSelect.value : "default";

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
      case "paused": return active && state === "paused";
      case "history": return !active;
      default: return active; // "all" = every active alert
    }
  };

  const matchesSearch = (row) => !query || (row.dataset.search || "").includes(query);

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
    const matched = rows.filter((row) => matchesFilter(row) && matchesSearch(row));
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

function initializeMapCarousel() {
  document.querySelectorAll("[data-map-carousel]").forEach((carousel) => {
    const slides = Array.from(carousel.querySelectorAll("[data-map-slide]"));
    if (slides.length <= 1) {
      return;
    }

    const scope = carousel.parentElement || carousel;
    const dots = Array.from(scope.querySelectorAll("[data-map-carousel-dot]"));
    const prev = scope.querySelector("[data-map-carousel-prev]");
    const next = scope.querySelector("[data-map-carousel-next]");
    const autoplay = carousel.hasAttribute("data-map-carousel-autoplay");
    const intervalSeconds = parseInt(carousel.dataset.mapCarouselInterval || "", 10);
    const intervalMs = Math.max(3, Number.isFinite(intervalSeconds) ? intervalSeconds : 12) * 1000;
    let active = 0;
    let timer = null;

    // "Overlay - on mouse-move / change": reveal the page indicator on activity, then fade it.
    const stage = carousel.closest("[data-map-pagination]");
    const autoHideNav = stage?.dataset.mapPagination === "overlayonactivity";
    let activityTimer = null;
    const pingActivity = () => {
      if (!autoHideNav || !stage) {
        return;
      }
      stage.classList.add("is-active");
      if (activityTimer) {
        clearTimeout(activityTimer);
      }
      activityTimer = setTimeout(() => stage.classList.remove("is-active"), 2600);
    };

    const show = (index) => {
      active = (index + slides.length) % slides.length;
      slides.forEach((slide, i) => {
        slide.hidden = i !== active;
      });
      dots.forEach((dot, i) => dot.classList.toggle("is-active", i === active));
      pingActivity();
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
    const closeModal = (row) => {
      const modal = row.querySelector("[data-credential-modal]");
      if (modal) {
        modal.hidden = true;
      }
      syncSummary(row);
      // A row with no name is treated as "no entry" - collapse it back out of the list.
      const nameInput = row.querySelector("input[name$='.Name']");
      if (nameInput && nameInput.value.trim() === "") {
        row.hidden = true;
      }
      updateEmptyState();
      if (!section.querySelector("[data-credential-modal]:not([hidden])")) {
        document.body.classList.remove("credential-modal-open");
      }
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

    section.querySelectorAll("[data-credential-modal-close]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row) {
          closeModal(row);
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
          closeModal(row);
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

function compareDiscoveryRows(left, right, sortKey, direction) {
  const multiplier = direction === "desc" ? -1 : 1;
  let result = 0;

  switch (sortKey) {
    case "host":
      result = compareText(left.dataset.discoveryHost || "", right.dataset.discoveryHost || "");
      break;
    case "ping":
      result = compareNumber(readDiscoveryNumber(left.dataset.discoveryPingMs, Number.POSITIVE_INFINITY), readDiscoveryNumber(right.dataset.discoveryPingMs, Number.POSITIVE_INFINITY));
      break;
    case "ports":
      result = compareNumber(Number(left.dataset.discoveryPortCount || "0"), Number(right.dataset.discoveryPortCount || "0"));
      break;
    case "sensors":
      result = compareNumber(Number(left.dataset.discoverySensorCount || "0"), Number(right.dataset.discoverySensorCount || "0"));
      break;
    default:
      result = compareDiscoveryAddress(left.dataset.discoveryAddress || "", right.dataset.discoveryAddress || "");
      break;
  }

  if (result === 0) {
    result = compareDiscoveryAddress(left.dataset.discoveryAddress || "", right.dataset.discoveryAddress || "");
  }

  return result * multiplier;
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

function initializeMapDesigner() {
  const canvas = document.querySelector("[data-map-designer]");
  if (!canvas) {
    return;
  }

  const form = document.querySelector("[data-map-designer-form]");
  const columnInput = form?.querySelector("[data-map-columns]");
  const rowInput = form?.querySelector("[data-map-rows]");
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
  const mapPanel = form?.querySelector("[data-map-property-map-panel]");
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
  const numericKindMap = {
    "0": "Text",
    "1": "Element",
    "2": "Status",
    "3": "Value",
    "4": "Graph"
  };
  const kindLabels = {
    "0": "Text",
    "1": "State",
    "2": "Summary",
    "3": "Value",
    "4": "Graph",
    Text: "Text",
    Element: "State",
    Status: "Summary",
    Value: "Value",
    Graph: "Graph"
  };
  // One consistent rule (mirrors Core MonitoringMapTileConstraints): a per-kind readability floor, and a
  // maximum of the whole board for every kind (the big maxWidth/maxHeight are capped to the live grid in
  // getSizeLimits). A graph needs >=3x2; an aggregate/summary >=2x1; text/value/element can be a single cell.
  const sizeLimits = {
    Text: { minWidth: 1, minHeight: 1, maxWidth: 24, maxHeight: 16, defaultWidth: 4, defaultHeight: 1 },
    Element: { minWidth: 1, minHeight: 1, maxWidth: 24, maxHeight: 16, defaultWidth: 3, defaultHeight: 2 },
    Status: { minWidth: 2, minHeight: 1, maxWidth: 24, maxHeight: 16, defaultWidth: 4, defaultHeight: 2 },
    Value: { minWidth: 1, minHeight: 1, maxWidth: 24, maxHeight: 16, defaultWidth: 2, defaultHeight: 2 },
    Graph: { minWidth: 3, minHeight: 2, maxWidth: 24, maxHeight: 16, defaultWidth: 5, defaultHeight: 3 }
  };
  const kindHints = {
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
  const presetOptions = {
    FullHd1080: { width: 1920, height: 1080, label: "Optimized for Full HD" },
    Qhd1440: { width: 2560, height: 1440, label: "Optimized for QHD" },
    Uhd2160: { width: 3840, height: 2160, label: "Optimized for 4K UHD" },
    Ultrawide3440x1440: { width: 3440, height: 1440, label: "Optimized for ultrawide" }
  };

  const readGridValue = (value, fallback, min, max) => {
    const numeric = Number(value);
    const rounded = Number.isFinite(numeric) ? Math.round(numeric) : fallback;
    return Math.min(max, Math.max(min, rounded));
  };

  const readGrid = () => ({
    columns: readGridValue(columnInput?.value, 12, 4, 24),
    rows: readGridValue(rowInput?.value, 8, 3, 16)
  });

  // The grid the user last committed, so a column/row change can scale the tiles relative to it.
  let lastCommittedGrid = readGrid();

  const syncMapSummary = (grid = readGrid()) => {
    const name = mapNameInput?.value?.trim() || "New Map";
    const description = mapDescriptionInput?.value?.trim() || "No description";
    const preset = readDisplayPreset();
    if (mapTitlePreview) {
      mapTitlePreview.textContent = name;
    }
    if (mapDescriptionPreview) {
      mapDescriptionPreview.textContent = description;
    }
    if (mapGridPreview) {
      mapGridPreview.textContent = `${preset.label} · ${grid.columns} x ${grid.rows} grid`;
    }
  };

  const readDisplayPreset = () => {
    // Only the aspect ratio matters now - the board scales to fill whatever screen it is shown on.
    const width = Math.max(1, Math.round(Number(aspectWidthInput?.value) || 16));
    const height = Math.max(1, Math.round(Number(aspectHeightInput?.value) || 9));
    return {
      width,
      height,
      label: `${width}:${height}`
    };
  };

  const syncGrid = (commit = false) => {
    const grid = readGrid();
    const preset = readDisplayPreset();
    if (commit && columnInput) {
      columnInput.value = String(grid.columns);
    }
    if (commit && rowInput) {
      rowInput.value = String(grid.rows);
    }

    canvas.style.setProperty("--map-columns", String(grid.columns));
    canvas.style.setProperty("--map-rows", String(grid.rows));
    canvas.style.setProperty("--map-display-width", String(preset.width));
    canvas.style.setProperty("--map-display-height", String(preset.height));
    const scale = readScale();
    // Only the aspect ratio matters (preset.width/height are ratio numbers now, e.g. 16/9). The board fills
    // the workbench width at 100% and derives its height from the ratio; the CSS `zoom` property then scales
    // the WHOLE board uniformly (workbench scrolls above 100%, shrinks below). getBoundingClientRect reports
    // zoomed coords, so drag/resize stays correct.
    const workbench = canvas.closest(".map-designer-workbench");
    const baseWidth = workbench ? Math.max(320, workbench.clientWidth - 14) : 960;
    canvas.style.width = `${baseWidth}px`;
    canvas.style.minWidth = "";
    canvas.style.minHeight = "";
    canvas.style.aspectRatio = `${preset.width} / ${preset.height}`;
    canvas.style.zoom = String(scale);
    if (scaleOutput) {
      scaleOutput.textContent = `${Math.round(scale * 100)}%`;
    }
    if (mapSelectButton) {
      mapSelectButton.style.minWidth = `${baseWidth}px`;
    }
    syncMapSummary(grid);
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => applyTilePosition(tile));
  };

  const getPanel = (index) => propertyHost?.querySelector(`[data-map-property-panel][data-tile-index="${index}"]`);
  const getTile = (index) => canvas.querySelector(`[data-map-tile][data-tile-index="${index}"]`);
  const normalizeKind = (kind) => numericKindMap[String(kind)] || String(kind || "Element");
  const getKindLabel = (kind) => kindLabels[String(kind)] || kindLabels[normalizeKind(kind)] || "Tile";
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const getSizeLimits = (kind) => {
    const grid = readGrid();
    const limits = sizeLimits[normalizeKind(kind)] || sizeLimits.Element;
    const maxWidth = clamp(limits.maxWidth, limits.minWidth, grid.columns);
    const maxHeight = clamp(limits.maxHeight, limits.minHeight, grid.rows);
    return {
      ...limits,
      maxWidth,
      maxHeight,
      defaultWidth: clamp(limits.defaultWidth, limits.minWidth, maxWidth),
      defaultHeight: clamp(limits.defaultHeight, limits.minHeight, maxHeight)
    };
  };
  const createId = () => {
    if (window.crypto?.randomUUID) {
      return window.crypto.randomUUID();
    }

    return `00000000-0000-4000-8000-${Date.now().toString(16).padStart(12, "0").slice(-12)}`;
  };

  const getTileControls = (tile) => {
    const panel = getPanel(tile.dataset.tileIndex || "");
    return {
      x: tile.querySelector("[data-map-tile-x]"),
      y: tile.querySelector("[data-map-tile-y]"),
      width: panel?.querySelector("[data-map-tile-width]"),
      height: panel?.querySelector("[data-map-tile-height]")
    };
  };

  const applyTilePosition = (tile) => {
    const { x, y, width, height } = getTileControls(tile);
    const panel = getPanel(tile.dataset.tileIndex || "");
    const kind = normalizeKind(panel?.querySelector("[data-map-property-kind]")?.value || tile.dataset.kind);
    const grid = readGrid();
    const limits = getSizeLimits(kind);
    const nextWidth = clamp(Math.round(Number(width?.value || limits.defaultWidth)), limits.minWidth, limits.maxWidth);
    const nextHeight = clamp(Math.round(Number(height?.value || limits.defaultHeight)), limits.minHeight, limits.maxHeight);
    const nextX = clamp(Math.round(Number(x?.value || 1)), 1, Math.max(1, grid.columns - nextWidth + 1));
    const nextY = clamp(Math.round(Number(y?.value || 1)), 1, Math.max(1, grid.rows - nextHeight + 1));
    if (width) {
      width.value = String(nextWidth);
    }
    if (height) {
      height.value = String(nextHeight);
    }
    if (x) {
      x.value = String(nextX);
    }
    if (y) {
      y.value = String(nextY);
    }

    tile.style.setProperty("--tile-x", String(nextX));
    tile.style.setProperty("--tile-y", String(nextY));
    tile.style.setProperty("--tile-w", String(nextWidth));
    tile.style.setProperty("--tile-h", String(nextHeight));
    const readout = panel?.querySelector("[data-map-property-size]");
    if (readout) {
      readout.textContent = `Size ${nextWidth} x ${nextHeight} · Min ${limits.minWidth} x ${limits.minHeight} · Max ${limits.maxWidth} x ${limits.maxHeight}`;
    }
    // Live size badge on the tile itself (shown while dragging/resizing) - "you see the tile taking shape".
    const badge = tile.querySelector("[data-map-tile-size-badge]");
    if (badge) {
      badge.textContent = `${nextWidth} × ${nextHeight}`;
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

  const syncPanelVisibility = (panel) => {
    if (!panel) {
      return;
    }

    const kind = normalizeKind(panel.querySelector("[data-map-property-kind]")?.value || "Element");
    const isText = kind === "Text";
    const isGraph = kind === "Graph";
    const targetField = panel.querySelector("[data-map-property-target]");
    const visualField = panel.querySelector("[data-map-property-visual]");
    const textField = panel.querySelector("[data-map-property-text-only]");
    const graphField = panel.querySelector("[data-map-property-graph-only]");
    const hint = panel.querySelector("[data-map-property-hint]");
    if (targetField) {
      targetField.hidden = isText;
    }
    if (visualField) {
      visualField.hidden = isText || isGraph;
    }
    if (textField) {
      textField.hidden = !isText;
    }
    if (graphField) {
      graphField.hidden = !isGraph;
    }
    if (hint) {
      const limits = getSizeLimits(kind);
      hint.textContent = `${kindHints[kind] || "Select a target and place the tile on the grid."} Resize from the bottom-right corner. Allowed size: ${limits.minWidth}x${limits.minHeight} to ${limits.maxWidth}x${limits.maxHeight}.`;
    }
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
    const preview = tile.querySelector("[data-map-tile-preview]");
    const titleElement = tile.querySelector("[data-map-tile-title]");
    const showTitle = panel.querySelector("[data-map-property-show-title]")?.checked ?? true;
    tile.dataset.kind = kind;
    if (titleElement) {
      titleElement.hidden = !showTitle;
      titleElement.replaceChildren(document.createTextNode(title));
    }
    tile.querySelector("[data-map-tile-kind-label]")?.replaceChildren(document.createTextNode(getKindLabel(kind)));
    if (preview) {
      const isText = kind === "Text";
      // The target is now an element picker: its name lives on the hidden value
      // input's data-selected-name (set when chosen / server-rendered).
      const selectedText = (elementSelect?.dataset.selectedName || "").trim();
      preview.textContent = isText
        ? (text.trim() || "Text tile")
        : (selectedText || "No target selected");
    }

    const showCard = panel.querySelector("[data-map-property-show-card]")?.checked ?? true;
    tile.classList.toggle("is-plain", !showCard);

    // Design-mode realistic preview: pick the mock (value / gauge / progress / graph / text) from kind + visual.
    const visual = (panel.querySelector("[data-map-property-visual-type]")?.value || "").trim();
    tile.dataset.preview = kind === "Text" ? "text"
      : kind === "Graph" ? "graph"
      : visual === "Gauge" ? "gauge"
      : visual === "ProgressBar" ? "progress"
      : "value";

    syncPanelVisibility(panel);
    applyTileAppearance(tile, panel);
    applyTilePosition(tile);
  };

  const selectTile = (index) => {
    if (mapPanel) {
      mapPanel.hidden = true;
    }
    mapSelectButton?.classList.remove("is-selected");

    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      tile.classList.toggle("is-selected", tile.dataset.tileIndex === String(index) && !tile.hidden);
    });

    let hasPanel = false;
    propertyHost?.querySelectorAll("[data-map-property-panel]").forEach((panel) => {
      const isActive = panel.dataset.tileIndex === String(index);
      panel.hidden = !isActive;
      if (isActive) {
        hasPanel = true;
        syncPanelVisibility(panel);
      }
    });

    if (propertyEmpty) {
      propertyEmpty.hidden = hasPanel;
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
      propertyEmpty.hidden = true;
    }
    if (mapPanel) {
      mapPanel.hidden = false;
    }

    mapSelectButton?.classList.add("is-selected");
    syncMapSummary();
  };

  const pointerToGrid = (event, width, height) => {
    const rect = canvas.getBoundingClientRect();
    const grid = readGrid();
    const cellWidth = rect.width / grid.columns;
    const cellHeight = rect.height / grid.rows;
    return {
      x: Math.max(1, Math.min(grid.columns - width + 1, Math.floor((event.clientX - rect.left) / cellWidth) + 1)),
      y: Math.max(1, Math.min(grid.rows - height + 1, Math.floor((event.clientY - rect.top) / cellHeight) + 1))
    };
  };

  const setupTile = (tile) => {
    applyTilePosition(tile);
    tile.addEventListener("click", () => selectTile(tile.dataset.tileIndex || ""));

    const panel = getPanel(tile.dataset.tileIndex || "");
    panel?.querySelectorAll("[data-map-tile-width], [data-map-tile-height]").forEach((input) => {
      input.addEventListener("input", () => applyTilePosition(tile));
    });
    panel?.querySelectorAll("[data-map-property-title], [data-map-property-kind], [data-map-property-visual-type], [data-map-property-element], [data-map-property-text], [data-map-property-graph-type], [data-map-property-background], [data-map-property-accent], [data-map-property-text-color], [data-map-property-show-title], [data-map-property-show-badge], [data-map-property-show-card]").forEach((input) => {
      input.addEventListener("input", () => syncTileFromPanel(panel));
      input.addEventListener("change", () => syncTileFromPanel(panel));
    });
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

    const handle = tile.querySelector("[data-map-drag-handle]");
    if (!handle) {
      return;
    }

    handle.addEventListener("pointerdown", (event) => {
      if (event.target instanceof HTMLElement && event.target.closest("input, select, textarea, button")) {
        return;
      }

      event.preventDefault();
      selectTile(tile.dataset.tileIndex || "");
      handle.setPointerCapture(event.pointerId);
      tile.classList.add("is-dragging");

      const move = (moveEvent) => {
        const controls = getTileControls(tile);
        const tileWidth = Math.max(1, Number(controls.width?.value || 3));
        const tileHeight = Math.max(1, Number(controls.height?.value || 2));
        const next = pointerToGrid(moveEvent, tileWidth, tileHeight);
        if (controls.x) {
          controls.x.value = String(next.x);
        }
        if (controls.y) {
          controls.y.value = String(next.y);
        }

        applyTilePosition(tile);
      };

      const up = () => {
        tile.classList.remove("is-dragging");
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", up);
        handle.removeEventListener("pointercancel", up);
      };

      handle.addEventListener("pointermove", move);
      handle.addEventListener("pointerup", up);
      handle.addEventListener("pointercancel", up);
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
      const startWidth = Math.max(1, Number(controls.width?.value || 3));
      const startHeight = Math.max(1, Number(controls.height?.value || 2));
      const startX = Number(event.clientX);
      const startY = Number(event.clientY);

      const move = (moveEvent) => {
        const rect = canvas.getBoundingClientRect();
        const grid = readGrid();
        const cellWidth = rect.width / grid.columns;
        const cellHeight = rect.height / grid.rows;
        const x = Math.max(1, Number(controls.x?.value || 1));
        const y = Math.max(1, Number(controls.y?.value || 1));
        const limits = getSizeLimits(kind);
        const deltaWidth = Math.round((moveEvent.clientX - startX) / cellWidth);
        const deltaHeight = Math.round((moveEvent.clientY - startY) / cellHeight);
        const maxWidthAtPosition = Math.min(limits.maxWidth, grid.columns - x + 1);
        const maxHeightAtPosition = Math.min(limits.maxHeight, grid.rows - y + 1);
        if (controls.width) {
          controls.width.value = String(clamp(startWidth + deltaWidth, limits.minWidth, maxWidthAtPosition));
        }
        if (controls.height) {
          controls.height.value = String(clamp(startHeight + deltaHeight, limits.minHeight, maxHeightAtPosition));
        }

        applyTilePosition(tile);
      };

      const up = () => {
        tile.classList.remove("is-resizing");
        resizeHandle.removeEventListener("pointermove", move);
        resizeHandle.removeEventListener("pointerup", up);
        resizeHandle.removeEventListener("pointercancel", up);
      };

      resizeHandle.addEventListener("pointermove", move);
      resizeHandle.addEventListener("pointerup", up);
      resizeHandle.addEventListener("pointercancel", up);
    });
  };

  const addTile = (tool, position) => {
    if (!template || !propertyHost) {
      return;
    }

    const index = Number(canvas.dataset.nextTileIndex || 0);
    const kind = normalizeKind(tool.kind || "Element");
    const baseTitle = tool.title || getKindLabel(kind);
    const title = `${baseTitle} ${index + 1}`;
    const limits = getSizeLimits(kind);
    const width = clamp(Math.max(1, Number(tool.width || limits.defaultWidth)), limits.minWidth, limits.maxWidth);
    const height = clamp(Math.max(1, Number(tool.height || limits.defaultHeight)), limits.minHeight, limits.maxHeight);
    const grid = readGrid();
    const x = Math.max(1, Math.min(grid.columns - width + 1, position?.x || 1));
    const y = Math.max(1, Math.min(grid.rows - height + 1, position?.y || 1));
    const html = template.innerHTML
      .replaceAll("__index__", String(index))
      .replaceAll("__id__", createId())
      .replaceAll("__slideId__", activeSlideId || "")
      .replaceAll("__kind__", kind)
      .replaceAll("__kindLabel__", getKindLabel(kind))
      .replaceAll("__title__", title)
      .replaceAll("__x__", String(x))
      .replaceAll("__y__", String(y))
      .replaceAll("__w__", String(width))
      .replaceAll("__h__", String(height));
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
    // Initialize the freshly cloned tile's element picker (guarded so existing
    // pickers aren't re-wired).
    initializeElementPickers();
    selectTile(index);
  };

  mapSelectButton?.addEventListener("click", selectMap);
  mapNameInput?.addEventListener("input", () => syncMapSummary());
  mapDescriptionInput?.addEventListener("input", () => syncMapSummary());
  aspectWidthInput?.addEventListener("input", () => syncGrid());
  aspectHeightInput?.addEventListener("input", () => syncGrid());
  form?.querySelectorAll("[data-map-aspect-preset]").forEach((button) => {
    button.addEventListener("click", () => {
      if (aspectWidthInput) { aspectWidthInput.value = button.dataset.aspectW || "16"; }
      if (aspectHeightInput) { aspectHeightInput.value = button.dataset.aspectH || "9"; }
      form?.querySelectorAll("[data-map-aspect-preset]").forEach((other) => {
        other.classList.toggle("is-active", other === button);
      });
      syncGrid();
    });
  });
  // Scale existing tiles proportionally when the column/row count changes, so the visual layout is
  // preserved (a finer grid keeps tiles the same size, occupying more cells) instead of leaving them
  // the same cell-span - which shrank + clustered them to the top-left and squished the graphs.
  // applyTilePosition (via syncGrid) then clamps everything into the new bounds. Runs on "change"
  // (commit) only - the per-keystroke "input" reflow is dropped so a half-typed number (which clamps
  // to the min) can't destroy the tile sizes before the scale is applied.
  const rescaleTilesToGrid = (oldGrid, newGrid) => {
    if (!oldGrid || (oldGrid.columns === newGrid.columns && oldGrid.rows === newGrid.rows)) {
      return;
    }
    const colRatio = newGrid.columns / oldGrid.columns;
    const rowRatio = newGrid.rows / oldGrid.rows;
    const scalePos = (value, ratio) => Math.max(1, Math.round((Number(value || 1) - 1) * ratio) + 1);
    const scaleSpan = (value, ratio) => Math.max(1, Math.round(Number(value || 1) * ratio));
    canvas.querySelectorAll("[data-map-tile]").forEach((tile) => {
      const { x, y, width, height } = getTileControls(tile);
      if (width) { width.value = String(scaleSpan(width.value, colRatio)); }
      if (height) { height.value = String(scaleSpan(height.value, rowRatio)); }
      if (x) { x.value = String(scalePos(x.value, colRatio)); }
      if (y) { y.value = String(scalePos(y.value, rowRatio)); }
    });
  };
  const commitGridChange = () => {
    const nextGrid = readGrid();
    rescaleTilesToGrid(lastCommittedGrid, nextGrid);
    lastCommittedGrid = nextGrid;
    syncGrid(true);
  };
  columnInput?.addEventListener("change", commitGridChange);
  rowInput?.addEventListener("change", commitGridChange);
  scaleInput?.addEventListener("input", () => syncGrid());
  // Recompute the board's fit-to-workbench base width when the window resizes.
  window.addEventListener("resize", () => syncGrid());

  const renderSlideInputs = () => {
    if (!slideInputsHost) {
      return;
    }
    slideInputsHost.replaceChildren();
    slides.forEach((slide, index) => {
      const idInput = document.createElement("input");
      idInput.type = "hidden";
      idInput.name = `Input.Slides[${index}].Id`;
      idInput.value = slide.id;
      const nameInput = document.createElement("input");
      nameInput.type = "hidden";
      nameInput.name = `Input.Slides[${index}].Name`;
      nameInput.value = slide.name;
      slideInputsHost.append(idInput, nameInput);
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
    slides.push({ id, name: `Slide ${slides.length + 1}` });
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
        name: tab.textContent.trim()
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
  syncGrid(true);

  document.querySelectorAll("[data-map-tool-kind]").forEach((tool) => {
    const payload = {
      kind: tool.getAttribute("data-map-tool-kind"),
      title: tool.getAttribute("data-map-tool-title"),
      width: tool.getAttribute("data-map-tool-width"),
      height: tool.getAttribute("data-map-tool-height"),
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
      const width = Math.max(1, Number(payload.width || 3));
      const height = Math.max(1, Number(payload.height || 2));
      addTile(payload, pointerToGrid(event, width, height));
    } catch {
      addTile({ kind: "1", title: "Tile", width: 3, height: 2 });
    }
  });

  canvas.addEventListener("click", (event) => {
    if (event.target === canvas) {
      selectMap();
    }
  });

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
      if (row && item.key === "other") {
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
  const otherFromSnapshot = snapshot.otherSensorCount == null ? null : Math.max(0, Number(snapshot.otherSensorCount));
  const healthyFromSnapshot = snapshot.healthySensorCount == null ? null : Math.max(0, Number(snapshot.healthySensorCount));
  const other = otherFromSnapshot ?? Math.max(0, total - warning - ack - error - (healthyFromSnapshot ?? 0));
  const healthy = healthyFromSnapshot ?? Math.max(0, total - warning - ack - error - other);

  return {
    total,
    items: [
      { key: "ok", count: healthy, color: "#78d5c8" },
      { key: "warning", count: warning, color: "#f3b36b" },
      { key: "ack", count: ack, color: "#5f8dff" },
      { key: "error", count: error, color: "#ff7f93" },
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

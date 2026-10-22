// Matmon - the Alerts page: the table's views, search, time filter, sort, paging and selection.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Alerts table: client-side view tabs + search + time filter + sort + paging + row selection, all over the full set
// of rows the server rendered (active + resolved history). Keeps it instant regardless of count; the only round-trips
// are acknowledging and muting (a row's buttons, or "Acknowledge selected" in the bulk bar).
// The bulk bar and its count are MatUi's (data-selectable / data-select-row / data-bulk-bar); the check in the header is
// this page's own, because it ticks only the rows of the page on screen - the hidden ones must never be acknowledged
// along with them.
function initializeAlertsTable() {
  const root = document.querySelector("[data-alerts]");
  if (!root) {
    return;
  }

  let rows = Array.from(root.querySelectorAll("[data-alert-row]"));
  const originalOrder = rows.slice();
  const tbody = root.querySelector("[data-alerts-body]");
  const searchInput = root.querySelector("[data-alerts-search]");
  const countEl = root.querySelector("[data-alerts-count]");
  const tabs = Array.from(root.querySelectorAll("[data-alert-filter]"));
  const selectAll = root.querySelector("[data-alerts-select-all]");
  const emptyEl = root.querySelector("[data-alerts-empty]");
  const pager = root.querySelector("[data-alerts-pager]");
  const pagerInfo = root.querySelector("[data-alerts-pager-info]");
  const prevBtn = root.querySelector("[data-alerts-prev]");
  const nextBtn = root.querySelector("[data-alerts-next]");
  const pageSizeSelect = root.querySelector("[data-alerts-page-size]");
  const chips = root.querySelector("[data-alerts-chips]");
  const timeLabel = root.querySelector("[data-alerts-time-label]");
  const filterButton = root.querySelector("[data-alerts-filter-button]");
  const filterBadge = root.querySelector("[data-alerts-filter-badge]");
  const sortButton = root.querySelector("[data-alerts-sort-button]");
  const sortLabel = root.querySelector("[data-alerts-sort-label]");

  // The time filter and the sort are radios in the Filter and Sort dialogs; the checked one is the choice (a browser
  // that restores the form state on Back restores it too).
  const checkedRadio = (attribute) => root.querySelector(`input[${attribute}]:checked`);
  const checkedValue = (attribute, fallback) => {
    const radio = checkedRadio(attribute);
    return radio ? radio.value : fallback;
  };
  const checkedLabel = (attribute) => {
    const radio = checkedRadio(attribute);
    const label = radio && radio.closest("label");
    return label ? label.textContent.trim() : "";
  };

  let filter = root.dataset.initialFilter || "all";
  let query = "";
  let page = 1;
  let pageSize = pageSizeSelect ? Number(pageSizeSelect.value) || 50 : 50;
  let sortMode = checkedValue("data-alerts-sort", "default");
  let timeRange = checkedValue("data-alerts-time", "all");

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

  // MatUi counts the ticked rows and shows the bulk bar when it hears a change on one of them; a check set from here
  // fires none, so tell it.
  const syncSelectionBar = () => {
    const anyRow = root.querySelector("input[data-select-row]");
    if (anyRow) {
      anyRow.dispatchEvent(new Event("change", { bubbles: true }));
    }
  };

  const updateSelectAll = () => {
    if (!selectAll) {
      return;
    }
    const visible = Array.from(root.querySelectorAll("[data-alert-row]:not([hidden]) [data-alerts-check]"));
    const checked = visible.filter((cb) => cb.checked).length;
    selectAll.checked = visible.length > 0 && checked === visible.length;
    selectAll.indeterminate = checked > 0 && checked < visible.length;
  };

  const resetSelection = () => {
    root.querySelectorAll("[data-alerts-check]").forEach((cb) => { cb.checked = false; });
    if (selectAll) {
      selectAll.checked = false;
      selectAll.indeterminate = false;
    }
    syncSelectionBar();
  };

  // The tools say what is in effect: a chip and a badge on Filter for the time filter (LIST-04), the order on Sort.
  const renderTools = () => {
    const timeActive = timeRange !== "all";
    if (chips) {
      chips.hidden = !timeActive;
    }
    if (timeLabel) {
      timeLabel.textContent = checkedLabel("data-alerts-time");
    }
    if (filterButton) {
      filterButton.classList.toggle("is-active", timeActive);
    }
    if (filterBadge) {
      filterBadge.hidden = !timeActive;
    }
    if (sortButton) {
      sortButton.classList.toggle("is-active", sortMode !== "default");
    }
    if (sortLabel) {
      sortLabel.textContent = sortMode === "default" ? "Default" : checkedLabel("data-alerts-sort");
    }
  };

  const setPagerButton = (button, disabled) => {
    if (!button) {
      return;
    }
    button.disabled = disabled;
    button.classList.toggle("is-disabled", disabled);
  };

  const apply = () => {
    const matched = rows.filter((row) => matchesFilter(row) && matchesSearch(row) && matchesTime(row));
    const total = matched.length;
    const pageCount = Math.max(1, Math.ceil(total / pageSize));
    page = Math.min(Math.max(1, page), pageCount);
    const start = (page - 1) * pageSize;

    rows.forEach((row) => { row.hidden = true; });
    matched.slice(start, start + pageSize).forEach((row) => { row.hidden = false; });

    tabs.forEach((tab) => {
      const on = tab.dataset.alertFilter === filter;
      tab.classList.toggle("is-active", on);
      if (on) {
        tab.setAttribute("aria-current", "page");
      } else {
        tab.removeAttribute("aria-current");
      }
    });

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
    setPagerButton(prevBtn, page <= 1);
    setPagerButton(nextBtn, page >= pageCount);

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

  root.addEventListener("change", (event) => {
    const target = event.target;
    if (!target.matches) {
      return;
    }

    if (target.matches("input[data-alerts-time]")) {
      timeRange = target.value || "all";
      page = 1;
      renderTools();
      apply();
    } else if (target.matches("input[data-alerts-sort]")) {
      sortMode = target.value || "default";
      page = 1;
      applySort();
      renderTools();
      apply();
    } else if (target.matches("[data-alerts-page-size]")) {
      pageSize = Number(target.value) || 50;
      page = 1;
      apply();
    } else if (target.matches("[data-alerts-select-all]")) {
      root.querySelectorAll("[data-alert-row]:not([hidden]) [data-alerts-check]").forEach((cb) => {
        cb.checked = target.checked;
      });
      syncSelectionBar();
    } else if (target.matches("[data-alerts-check]")) {
      updateSelectAll();
    }
  });

  root.addEventListener("click", (event) => {
    const target = event.target;
    if (!target.closest) {
      return;
    }

    if (target.closest("[data-alerts-time-clear]")) {
      const anyTime = root.querySelector('input[data-alerts-time][value="all"]');
      if (anyTime) {
        anyTime.checked = true;
      }
      timeRange = "all";
      page = 1;
      renderTools();
      apply();
    } else if (target.closest("[data-alerts-prev]")) {
      page -= 1;
      apply();
    } else if (target.closest("[data-alerts-next]")) {
      page += 1;
      apply();
    }
  });

  if (sortMode !== "default") {
    applySort();
  }
  renderTools();
  apply();
}

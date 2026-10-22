// Matmon - the two log views: the app's own log (the Logs page) and the network's (the Syslog tab of Events).
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).
// Both draw their rows here from JSON, and every value of a line - a log message, a device's syslog text - goes onto
// the page with textContent, never as markup: anything on the network can send a syslog line.

// ---- shared ---------------------------------------------------------------------------------------------------------

// A cell of a MatUi table: data-label is what a phone shows in front of it (TABLE-02).
function logViewCell(label, className) {
  const td = document.createElement("td");
  td.setAttribute("data-label", label);
  if (className) {
    td.className = className;
  }
  return td;
}

// An icon of the MatUi sprite, built as DOM (no markup string).
function logViewIcon(name, extraClass) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", "mat-icon" + (extraClass ? " " + extraClass : ""));
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
  use.setAttribute("href", "#mat-i-" + name);
  svg.appendChild(use);
  return svg;
}

function logViewPill(text, tone, title) {
  const pill = document.createElement("span");
  pill.className = "mat-pill" + (tone ? " " + tone : "");
  pill.textContent = text || "";
  if (title) {
    pill.title = title;
  }
  return pill;
}

// ---- Logs: the in-memory log of this process -------------------------------------------------------------------------
// The rows come from the page's Data handler, so the first load and the live refresh share one path. The search, the level
// (radios in the Level dialog) and Refresh load at once; while Live is on the view reloads every 4 s. A failed request
// keeps the last view. The same view as Matmon.Cloud's Admin > Logs.
function initializeLogViewer() {
  const root = document.querySelector("[data-log]");
  if (!root) {
    return;
  }

  const rows = root.querySelector("[data-log-rows]");
  const table = root.querySelector("[data-log-table]");
  const empty = root.querySelector("[data-log-empty]");
  const count = root.querySelector("[data-log-count]");
  const search = root.querySelector("[data-log-search]");
  const live = root.querySelector("[data-log-live]");
  const levelButton = root.querySelector("[data-log-level-button]");
  const levelLabel = root.querySelector("[data-log-level-label]");
  const defaultLevel = "Information";
  let timer = null;
  let searchDebounce = null;

  // The level the view shows; with nothing chosen, everything (as the old select did with no option selected).
  const currentLevel = () => {
    const checked = document.querySelector("input[data-log-level]:checked");
    return checked ? checked.value : "Trace";
  };

  const shortSource = (category) => {
    if (!category) {
      return "";
    }
    const i = category.lastIndexOf(".");
    return i >= 0 ? category.slice(i + 1) : category;
  };

  const formatTime = (iso) => {
    const d = new Date(iso);
    if (isNaN(d)) {
      return iso;
    }
    return d.toLocaleTimeString(undefined, { hour12: false }) + "." + String(d.getMilliseconds()).padStart(3, "0");
  };

  const levelTone = (level) => {
    switch ((level || "").toLowerCase()) {
      case "information": return "mat-pill--info";
      case "warning": return "mat-pill--warn";
      case "error":
      case "critical": return "mat-pill--error";
      default: return "";
    }
  };

  const render = (items) => {
    rows.textContent = "";
    count.textContent = String(items.length);
    const any = items.length > 0;
    table.hidden = !any;
    empty.hidden = any;
    if (!any) {
      return;
    }

    const fragment = document.createDocumentFragment();
    items.forEach((entry) => {
      const tr = document.createElement("tr");

      const time = logViewCell("Time", "mat-mono mat-text-muted mat-nowrap");
      time.textContent = formatTime(entry.ts);
      time.title = entry.ts || "";

      const level = logViewCell("Level");
      level.appendChild(logViewPill(entry.level, levelTone(entry.level)));

      const source = logViewCell("Source", "mat-hide-sm mat-mono mat-text-muted");
      source.textContent = shortSource(entry.category);
      source.title = entry.category || "";

      const message = logViewCell("Message", "mat-text-break");
      const text = document.createElement("div");
      text.className = "mm-prewrap";
      text.textContent = entry.message || "";
      message.appendChild(text);
      if (entry.exception) {
        const details = document.createElement("details");
        details.className = "mat-collapse";
        const summary = document.createElement("summary");
        summary.appendChild(document.createTextNode("Exception"));
        summary.appendChild(logViewIcon("chevron-down", "mat-accordion__chev"));
        const body = document.createElement("div");
        body.className = "mat-accordion__body";
        const pre = document.createElement("pre");
        pre.className = "mat-code";
        const code = document.createElement("code");
        code.textContent = entry.exception;
        pre.appendChild(code);
        body.appendChild(pre);
        details.appendChild(summary);
        details.appendChild(body);
        message.appendChild(details);
      }

      tr.appendChild(time);
      tr.appendChild(level);
      tr.appendChild(source);
      tr.appendChild(message);
      fragment.appendChild(tr);
    });
    rows.appendChild(fragment);
  };

  const load = () => {
    const params = new URLSearchParams({ handler: "Data", level: currentLevel(), q: search ? search.value : "" });
    fetch("?" + params.toString(), { headers: { "X-Requested-With": "fetch" } })
      .then((response) => (response.ok ? response.json() : { items: [] }))
      .then((data) => { render((data && data.items) || []); })
      .catch(() => { /* keep the last view on a transient error */ });
  };

  const schedule = () => {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
    if (live && live.checked) {
      timer = setInterval(load, 4000);
    }
  };

  // The Level button says which level is shown, and is marked while it is not the default.
  const showLevel = () => {
    const checked = document.querySelector("input[data-log-level]:checked");
    const label = checked && checked.closest("label");
    if (levelLabel) {
      levelLabel.textContent = label ? label.textContent.trim() : "All";
    }
    if (levelButton) {
      levelButton.classList.toggle("is-active", currentLevel() !== defaultLevel);
    }
  };

  document.addEventListener("change", (event) => {
    const target = event.target;
    if (!target.matches) {
      return;
    }
    if (target.matches("input[data-log-level]")) {
      showLevel();
      load();
    } else if (target.matches("[data-log-live]")) {
      schedule();
    }
  });

  document.addEventListener("input", (event) => {
    if (!event.target.matches || !event.target.matches("[data-log-search]")) {
      return;
    }
    clearTimeout(searchDebounce);
    searchDebounce = setTimeout(load, 250);
  });

  document.addEventListener("click", (event) => {
    if (event.target.closest && event.target.closest("[data-log-refresh]")) {
      load();
    }
  });

  load();
  schedule();
}

// ---- Syslog: the Syslog tab of the Events page ---------------------------------------------------------------------
// Newest first, one page at a time (paging by the id of the oldest row shown), new rows put on top while Live is on, the
// totals only when the filter changes and after new rows arrived, the sender suggestions only when someone starts to
// type a sender. The tab loads when it is first shown, and its live poll stops while it is hidden.
function initializeSyslogMessages() {
  const root = document.getElementById("syslog-messages");
  if (!root) {
    return;
  }

  const byId = (id) => document.getElementById(id);
  const els = {
    window: byId("slWindow"),
    source: byId("slSource"),
    senders: byId("slSenders"),
    severity: byId("slSeverity"),
    search: byId("slSearch"),
    regex: byId("slRegex"),
    live: byId("slLive"),
    refresh: byId("slRefresh"),
    reset: byId("slReset"),
    rows: byId("slRows"),
    table: byId("slTable"),
    emptyBox: byId("slEmptyBox"),
    emptyTitle: byId("slEmptyTitle"),
    empty: byId("slEmpty"),
    more: byId("slMore"),
    moreRow: byId("slMoreRow"),
    summary: byId("slSummary"),
    errorBox: byId("slErrorBox"),
    error: byId("slError"),
    chips: byId("slChips"),
    filterButton: byId("slFilterButton"),
    filterBadge: byId("slFilterBadge")
  };
  if (!els.window || !els.rows) {
    return;
  }

  const PAGE = Number(root.dataset.pageSize) || 200;
  const MAX_ROWS = 5000;
  const DEFAULT_WINDOW = root.dataset.defaultWindow || "60";
  const hasMessages = root.dataset.hasMessages === "true";
  const enabled = root.dataset.enabled === "true";

  let rows = [];      // what is on screen, newest first
  let hasMore = false;
  let busy = false;
  let timer = null;
  let started = false;
  let sendersLoaded = false;
  let generation = 0; // bumps when the filter changes, so a slow answer for an old filter is dropped
  let debounce = null;

  const params = (extra) => {
    const p = new URLSearchParams({
      window: els.window.value,
      source: els.source.value,
      severity: els.severity.value,
      q: els.search.value,
      regex: els.regex.checked ? "true" : "false"
    });
    Object.keys(extra || {}).forEach((key) => { p.set(key, extra[key]); });
    return p;
  };

  const get = (handler, extra) => {
    const p = params(extra);
    p.set("handler", handler);
    return fetch("?" + p.toString(), { headers: { "X-Requested-With": "fetch" }, credentials: "same-origin" })
      .then((response) => {
        if (!response.ok) {
          throw new Error("HTTP " + response.status);
        }
        return response.json();
      });
  };

  const severityTone = (sevClass) => {
    switch (sevClass) {
      case "critical":
      case "error": return "mat-pill--error";
      case "warning": return "mat-pill--warn";
      case "information": return "mat-pill--info";
      default: return "";
    }
  };

  const small = (text) => {
    const el = document.createElement("small");
    el.className = "mat-d-block mat-text-muted";
    el.textContent = text;
    return el;
  };

  // One message as a row - every value with textContent.
  const buildRow = (m) => {
    const tr = document.createElement("tr");

    const received = logViewCell("Received", "mat-mono mat-text-muted mat-nowrap");
    received.textContent = m.time || "";
    received.title = (m.ts || "") + (m.deviceTime ? "  (the device's own clock said: " + m.deviceTime + ")" : "");
    tr.appendChild(received);

    const severity = logViewCell("Severity");
    severity.appendChild(logViewPill(m.sevName, severityTone(m.sevClass), "severity " + m.sev + ", facility " + m.facility));
    tr.appendChild(severity);

    const sender = logViewCell("Sender", "mat-mono");
    sender.textContent = m.source || "";
    sender.title = m.source || "";
    if (m.host && m.host !== m.source) {
      sender.appendChild(small(m.host));
    }
    tr.appendChild(sender);

    const app = logViewCell("Program", "mat-hide-sm mat-mono mat-text-muted");
    app.textContent = m.app || "";
    app.title = m.procId && m.app ? m.app + "[" + m.procId + "]" : (m.app || "");
    if (m.msgId && m.msgId !== m.app) {
      app.appendChild(small(m.msgId));
    }
    tr.appendChild(app);

    const message = logViewCell("Message", "mat-text-break");
    const text = document.createElement("div");
    text.className = "mm-prewrap";
    text.textContent = m.text || "";
    message.appendChild(text);
    tr.appendChild(message);
    return tr;
  };

  const setMore = (visible) => {
    if (els.moreRow) {
      els.moreRow.hidden = !visible;
    }
  };

  const syncEmpty = () => {
    const any = rows.length > 0;
    if (els.table) {
      els.table.hidden = !any;
    }
    if (els.emptyBox) {
      els.emptyBox.hidden = any;
    }
    if (any) {
      return;
    }

    let title = "No message matches this filter";
    let text = "Change the search or the filters, or reset them.";
    if (!hasMessages) {
      title = enabled ? "Nothing has arrived yet" : "The receiver is off";
      text = enabled
        ? "Point a device's syslog setting at this machine and its messages appear here within seconds."
        : "Nothing has been collected. An admin turns it on under System > Syslog.";
    }
    if (els.emptyTitle) {
      els.emptyTitle.textContent = title;
    }
    if (els.empty) {
      els.empty.textContent = text;
    }
  };

  const renderAll = () => {
    els.rows.textContent = "";
    const fragment = document.createDocumentFragment();
    rows.forEach((m) => { fragment.appendChild(buildRow(m)); });
    els.rows.appendChild(fragment);
    setMore(hasMore);
    syncEmpty();
  };

  const showError = (message) => {
    if (els.errorBox) {
      els.errorBox.hidden = !message;
    }
    if (els.error) {
      els.error.textContent = message || "";
    }
  };

  // The totals in the toolbar: errors and warnings in their colour while there are any, then all messages.
  const showCounts = (counts) => {
    if (!counts || !els.summary) {
      return;
    }
    const part = (n, one, many, tone) => {
      const span = document.createElement("span");
      if (tone && n > 0) {
        span.className = tone;
      }
      span.textContent = Number(n).toLocaleString() + " " + (n === 1 ? one : many);
      return span;
    };
    els.summary.textContent = "";
    els.summary.append(
      part(counts.errors, "error", "errors", "mat-text-danger"),
      " · ",
      part(counts.warnings, "warning", "warnings", "mat-text-warning"),
      " · ",
      part(counts.total, "message", "messages", null));
  };

  // What is in effect, as chips (LIST-04) and as the number on the Filter button.
  const selectedText = (select) => {
    const option = select.selectedOptions && select.selectedOptions[0];
    return option ? option.textContent.trim() : select.value;
  };

  const renderTools = () => {
    const active = [];
    if (els.window.value !== DEFAULT_WINDOW) {
      active.push({ label: "Time: " + selectedText(els.window), name: "Time", clear: () => { els.window.value = DEFAULT_WINDOW; } });
    }
    const source = els.source.value.trim();
    if (source) {
      active.push({ label: "Sender: " + source, name: "Sender", clear: () => { els.source.value = ""; } });
    }
    if (els.severity.value) {
      active.push({ label: "Severity: " + selectedText(els.severity), name: "Severity", clear: () => { els.severity.value = ""; } });
    }
    if (els.regex.checked) {
      active.push({ label: "Search as a regular expression", name: "Regular expression", clear: () => { els.regex.checked = false; } });
    }

    if (els.chips) {
      els.chips.textContent = "";
      active.forEach((filter) => {
        const chip = document.createElement("span");
        chip.className = "mat-chip";
        chip.appendChild(document.createTextNode(filter.label));
        const x = document.createElement("button");
        x.type = "button";
        x.className = "mat-chip__x";
        x.setAttribute("aria-label", "Remove the filter " + filter.name);
        x.appendChild(logViewIcon("x"));
        x.addEventListener("click", () => {
          filter.clear();
          renderTools();
          reload();
        });
        chip.appendChild(x);
        els.chips.appendChild(chip);
      });
      els.chips.hidden = active.length === 0;
    }
    if (els.filterButton) {
      els.filterButton.classList.toggle("is-active", active.length > 0);
    }
    if (els.filterBadge) {
      els.filterBadge.textContent = String(active.length);
      els.filterBadge.hidden = active.length === 0;
    }
  };

  const reload = () => {
    const mine = ++generation;
    busy = true;
    get("SyslogData", { counts: "true" })
      .then((d) => {
        if (mine !== generation) {
          return;
        }
        showError(d.error);
        rows = d.items || [];
        hasMore = !!d.hasMore;
        renderAll();
        if (d.error) {
          if (els.summary) {
            els.summary.textContent = "";
          }
        } else {
          showCounts(d.counts);
        }
      })
      .catch(() => { /* keep the last view on a transient error */ })
      .then(() => { busy = false; });
  };

  const refreshCounts = (mine) => {
    get("SyslogCounts").then((d) => {
      if (mine === generation) {
        showCounts(d.counts);
      }
    }).catch(() => { });
  };

  const tick = () => {
    // Only while the tab is the one on screen: a hidden panel must not keep polling the log.
    if (busy || document.hidden || root.closest("[hidden]")) {
      return;
    }
    const mine = generation;
    const topId = rows.length ? rows[0].id : 0;
    busy = true;
    get("SyslogData", { counts: "false" })
      .then((d) => {
        if (mine !== generation) {
          return;
        }
        showError(d.error);
        const items = d.items || [];
        const fresh = items.filter((m) => m.id > topId);
        if (fresh.length === 0) {
          return;
        }

        if (fresh.length === items.length && items.length >= PAGE) {
          // A whole page of news: there may be more between it and what is shown. Start over.
          rows = items;
          hasMore = !!d.hasMore;
          renderAll();
        } else {
          const fragment = document.createDocumentFragment();
          fresh.forEach((m) => { fragment.appendChild(buildRow(m)); });
          els.rows.insertBefore(fragment, els.rows.firstChild);
          rows = fresh.concat(rows);
          if (rows.length > MAX_ROWS) {
            rows.length = MAX_ROWS;
            while (els.rows.children.length > MAX_ROWS) {
              els.rows.removeChild(els.rows.lastChild);
            }
            hasMore = true;
            setMore(true);
          }
          syncEmpty();
        }
        refreshCounts(mine);
      })
      .catch(() => { })
      .then(() => { busy = false; });
  };

  const schedule = () => {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
    if (els.live && els.live.checked) {
      timer = setInterval(tick, 4000);
    }
  };

  // The sender suggestions are an aggregate over the log - asked for once, when someone starts to type, not on every
  // visit to the Events page.
  const loadSenders = () => {
    if (sendersLoaded || !els.senders) {
      return;
    }
    sendersLoaded = true;
    get("SyslogSenders").then((d) => {
      (d.names || []).forEach((name) => {
        const option = document.createElement("option");
        option.value = name;
        els.senders.appendChild(option);
      });
    }).catch(() => { sendersLoaded = false; });
  };

  if (els.more) {
    els.more.addEventListener("click", () => {
      if (busy || rows.length === 0) {
        return;
      }
      const mine = generation;
      busy = true;
      els.more.disabled = true;
      get("SyslogData", { before: rows[rows.length - 1].id, counts: "false" })
        .then((d) => {
          if (mine !== generation) {
            return;
          }
          const items = d.items || [];
          const fragment = document.createDocumentFragment();
          items.forEach((m) => { fragment.appendChild(buildRow(m)); });
          els.rows.appendChild(fragment);
          rows = rows.concat(items);
          hasMore = !!d.hasMore && rows.length < MAX_ROWS;
          setMore(hasMore);
        })
        .catch(() => { })
        .then(() => { busy = false; els.more.disabled = false; });
    });
  }

  [els.window, els.severity, els.regex, els.source].forEach((input) => {
    input.addEventListener("change", () => {
      renderTools();
      reload();
    });
  });
  [els.search, els.source].forEach((input) => {
    input.addEventListener("input", () => {
      clearTimeout(debounce);
      debounce = setTimeout(() => {
        renderTools();
        reload();
      }, 300);
    });
  });
  els.source.addEventListener("focus", loadSenders);
  if (els.refresh) {
    els.refresh.addEventListener("click", reload);
  }
  if (els.live) {
    els.live.addEventListener("change", schedule);
  }
  if (els.reset) {
    els.reset.addEventListener("click", () => {
      els.window.value = DEFAULT_WINDOW;
      els.source.value = "";
      els.severity.value = "";
      els.regex.checked = false;
      renderTools();
      reload();
    });
  }

  renderTools();

  // The log is loaded when its tab is first shown (or at once when the page opens on it), not on every visit to the
  // Events page - most visits are about the Matmon events next to it.
  const start = () => {
    if (started) {
      return;
    }
    started = true;
    reload();
    schedule();
  };

  const shown = () => !root.closest("[hidden]");

  // The tabs show and hide the panel after this script has run - by a click, by the #syslog in a link, by a hash change -
  // so watch the panel itself instead of guessing which of those happened (MatUi's tabs toggle its hidden attribute).
  const panel = root.closest('[role="tabpanel"]');
  if (!panel || shown()) {
    start();
  } else {
    new MutationObserver(() => {
      if (shown()) {
        start();
      }
    }).observe(panel, { attributes: true, attributeFilter: ["hidden"] });
  }
}

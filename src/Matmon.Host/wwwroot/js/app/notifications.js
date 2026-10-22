// Matmon - notifications: the list's filter (Notifications page) and the sender/receiver editors.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// The kind select of a sender/receiver editor shows the panel of the chosen kind (e-mail, webhook, cloud). Deliberately
// NOT MatUi's data-show-if: that disables the fields of a hidden panel, and the editors rely on every field being posted
// (a missing SSL switch or timeout would be saved as its default).
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

// The Notifications list (trigger rules + the scheduled report) filters in the page: the search matches a row's
// data-notif-name, the Filter dialog picks a data-notif-type. The type in effect shows as a chip and a badge on the Filter
// button, the count says how many rows are shown, and "Nothing matches" replaces the table when no row is left.
function initializeNotificationFilter() {
  const root = document.querySelector("[data-notif-filter]");
  if (!root) {
    return;
  }

  const rows = Array.from(root.querySelectorAll("[data-notif-row]"));
  const search = root.querySelector("[data-notif-search]");
  const dialog = document.querySelector("[data-notif-filter-dialog]");
  const typeSelect = dialog?.querySelector("[data-notif-type-filter]") ?? null;
  const filterButton = root.querySelector("[data-notif-filter-button]");
  const filterBadge = root.querySelector("[data-notif-filter-badge]");
  const chips = root.querySelector("[data-notif-chips]");
  const chipLabel = root.querySelector("[data-notif-chip-label]");
  const count = root.querySelector("[data-notif-count]");
  const table = root.querySelector("[data-notif-table]");
  const empty = root.querySelector("[data-notif-empty]");
  let activeType = "all";

  const typeLabel = (value) => {
    const option = typeSelect ? Array.from(typeSelect.options).find((candidate) => candidate.value === value) : null;
    return option ? option.textContent.trim() : value;
  };

  const apply = () => {
    const query = ((search && search.value) || "").trim().toLowerCase();
    let shown = 0;
    rows.forEach((row) => {
      const type = row.getAttribute("data-notif-type");
      const name = (row.getAttribute("data-notif-name") || "").toLowerCase();
      const show = (activeType === "all" || activeType === type) && (query === "" || name.indexOf(query) >= 0);
      row.hidden = !show;
      if (show) {
        shown += 1;
      }
    });

    const filtered = activeType !== "all";
    if (filterButton) {
      filterButton.classList.toggle("is-active", filtered);
    }
    if (filterBadge) {
      filterBadge.hidden = !filtered;
    }
    if (chips) {
      chips.hidden = !filtered;
    }
    if (chipLabel) {
      chipLabel.textContent = filtered ? typeLabel(activeType) : "";
    }
    if (count) {
      const total = rows.length;
      const noun = total === 1 ? "notification" : "notifications";
      count.textContent = shown === total ? `${total} ${noun}` : `${shown} of ${total} ${noun}`;
    }
    if (table) {
      table.hidden = shown === 0;
    }
    if (empty) {
      empty.hidden = shown !== 0;
    }
  };

  const setType = (value) => {
    activeType = value || "all";
    if (typeSelect) {
      typeSelect.value = activeType;
    }
    apply();
  };

  if (search) {
    search.addEventListener("input", apply);
  }

  // Apply takes the dialog's choice and closes it; Cancel or Escape leave the filter as it was, so the select goes back
  // to the type in effect.
  dialog?.querySelector("[data-notif-apply]")?.addEventListener("click", () => {
    setType(typeSelect ? typeSelect.value : "all");
    if (window.MatUi && window.MatUi.dialog) {
      window.MatUi.dialog.close(dialog);
    } else if (dialog.open) {
      dialog.close();
    }
  });
  dialog?.addEventListener("close", () => {
    if (typeSelect) {
      typeSelect.value = activeType;
    }
  });

  root.querySelector("[data-notif-type-clear]")?.addEventListener("click", () => setType("all"));

  document.querySelectorAll("[data-notif-reset]").forEach((button) => {
    button.addEventListener("click", () => {
      if (search) {
        search.value = "";
      }
      setType("all");
    });
  });

  apply();
}

// A webhook sender's preset select fills the fields below it from the chosen preset (the options carry the preset in
// data-* attributes, rendered from WebhookPresets on the server - one source). Only on a CHANGE of the select: opening
// a saved sender must show what was saved, not the preset it once came from. The address is kept when the preset has
// none (Teams, Slack, Discord: the address is the part only the user knows).
function initializeWebhookPresets() {
  document.querySelectorAll("[data-webhook-editor]").forEach((editor) => {
    const select = editor.querySelector("[data-webhook-preset]");
    if (!select) {
      return;
    }

    const field = (selector) => editor.querySelector(selector);
    const hint = field("[data-webhook-hint]");
    select.addEventListener("change", () => {
      const option = select.selectedOptions[0];
      if (!option) {
        return;
      }

      const preset = option.dataset;
      const url = field("[data-webhook-url]");
      if (url && preset.url) {
        url.value = preset.url;
      }
      const set = (selector, value) => {
        const element = field(selector);
        if (element && value !== undefined) {
          element.value = value;
          element.dispatchEvent(new Event("change", { bubbles: true }));
        }
      };
      set("[data-webhook-method]", preset.method);
      set("[data-webhook-content-type]", preset.contentType);
      set("[data-webhook-headers]", preset.headers);
      set("[data-webhook-body]", preset.body);
      if (hint) {
        hint.textContent = preset.hint || "";
      }
    });
  });
}

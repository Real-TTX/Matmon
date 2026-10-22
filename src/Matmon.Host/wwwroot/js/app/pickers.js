// Matmon - the icon picker and the element picker.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Opens / closes a MatUi dialog (<dialog class="mat-dialog">): MatUi keeps the focus, the scroll lock, Escape and the
// full-screen sheet on a phone. A plain element (an older markup) is shown and hidden instead.
function openPickerDialog(dialog, opener) {
  if (dialog.tagName === "DIALOG") {
    // A dialog must not carry the hidden attribute: MatUi's [hidden] rule would keep it invisible while it is open.
    dialog.removeAttribute("hidden");
    if (window.MatUi && MatUi.dialog) {
      MatUi.dialog.open(dialog, opener);
    } else if (!dialog.open) {
      dialog.showModal();
    }
    return;
  }
  dialog.hidden = false;
}

function closePickerDialog(dialog) {
  if (dialog.tagName === "DIALOG") {
    if (window.MatUi && MatUi.dialog) {
      MatUi.dialog.close(dialog);
    } else if (dialog.open) {
      dialog.close();
    }
    return;
  }
  dialog.hidden = true;
}

function isPickerDialogOpen(dialog) {
  return dialog.tagName === "DIALOG" ? dialog.open : !dialog.hidden;
}

// Icon picker: one shared modal (visual glyph grid + search) opened by any [data-icon-picker-trigger].
// Trigger wiring is idempotent per element so it survives cloned map tiles; modal wiring runs once.
// The active field is held on the modal element so the (once-wired) modal handlers see the current target.
// The modal ([data-icon-picker-modal]) is a MatUi dialog in the map designer; a hidden element still works.
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
    closePickerDialog(modal);
    modal._iconField = null;
  };

  const open = (field, opener) => {
    modal._iconField = field;
    const current = field.querySelector("[data-icon-picker-value]")?.value || "";
    cells.forEach((cell) => {
      const active = (cell.dataset.iconKey || "") === current;
      cell.classList.toggle("is-active", active);
      cell.setAttribute("aria-pressed", active ? "true" : "false");
    });
    if (searchInput) {
      searchInput.value = "";
    }
    applyFilter();
    openPickerDialog(modal, opener);
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
    if (modal.tagName !== "DIALOG") {
      // A dialog closes on Escape by itself.
      searchInput?.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
          close();
        }
      });
    } else {
      modal.addEventListener("close", () => {
        modal._iconField = null;
      });
    }
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
        open(field, trigger);
      }
    });
  });
}

// Element picker (_ElementPicker.cshtml): a select-like trigger that opens a MatUi dialog with the topology - an
// indented tree while browsing, a flat list with paths while searching or filtering by tag. Choosing writes the id (or
// "tag:<name>") into the hidden [data-picker-value] input and fires "change" on it, which is what the host pages hook
// (data-sensor-parent-select, data-map-property-element, ...). Wiring is idempotent per picker, so a map tile cloned by
// the designer can call this again. "Wired" is remembered per element object, not in an attribute: a clone made from the
// HTML of a wired picker is a new element and must be wired too.
const wiredElementPickers = new WeakSet();

function initializeElementPickers() {
  document.querySelectorAll("[data-element-picker]").forEach((picker) => {
    if (wiredElementPickers.has(picker)) {
      return;
    }
    wiredElementPickers.add(picker);
    const valueInput = picker.querySelector("[data-picker-value]");
    const trigger = picker.querySelector("[data-picker-open]");
    const label = picker.querySelector("[data-picker-label]");
    const triggerPath = picker.querySelector("[data-picker-trigger-path]");
    const dialog = picker.querySelector("[data-picker-dialog]");
    if (!valueInput || !trigger || !dialog) {
      return;
    }
    const search = dialog.querySelector("[data-picker-search]");
    const tagFilter = dialog.querySelector("[data-picker-tag]");
    const list = dialog.querySelector("[data-picker-list]");
    const empty = dialog.querySelector("[data-picker-empty]");
    const closeButton = dialog.querySelector("[data-picker-close]");
    const modeButtons = Array.from(dialog.querySelectorAll("[data-picker-mode]"));
    if (!list) {
      return;
    }

    // Options from BOTH the element tree list and (when tags are allowed) the tag list.
    const options = Array.from(dialog.querySelectorAll("[data-picker-option]"));

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
      modeButtons.forEach((button) => {
        const active = button.dataset.pickerMode === mode;
        button.classList.toggle("is-active", active);
        button.setAttribute("aria-pressed", active ? "true" : "false");
      });
      if (tagFilter) {
        tagFilter.value = "";
      }
      applyFilter();
    };
    modeButtons.forEach((button) => button.addEventListener("click", () => setMode(button.dataset.pickerMode)));

    const open = () => {
      if (search) {
        search.value = "";
      }
      if (tagFilter) {
        tagFilter.value = "";
      }
      applyFilter();
      openPickerDialog(dialog, trigger);
      window.setTimeout(() => search?.focus(), 0);
    };

    const close = () => {
      closePickerDialog(dialog);
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
      options.forEach((candidate) => {
        const selected = candidate === option && !!id;
        candidate.classList.toggle("is-selected", selected);
        candidate.setAttribute("aria-selected", selected ? "true" : "false");
      });
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
    // When the picker sits inside a <label> (e.g. the map tile "Target" field), the label forwards clicks on
    // non-control descendants to its first labelable control - the trigger button - which instantly re-opens the
    // dialog after a selection/close. Swallow the default action for clicks inside the dialog that aren't on a real
    // control so the label can't re-trigger the button.
    dialog.addEventListener("click", (event) => {
      const target = event.target;
      if (target instanceof Element && !target.closest("input, select, textarea, button, a")) {
        event.preventDefault();
      }
    }, true);
    if (dialog.tagName !== "DIALOG") {
      // A plain overlay (older markup): close on a click on its backdrop and on Escape. A dialog does both itself.
      dialog.addEventListener("click", (event) => {
        if (event.target === dialog) {
          close();
        }
      });
      document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && isPickerDialogOpen(dialog)) {
          close();
        }
      });
    }
    search?.addEventListener("input", applyFilter);
    // Enter in the search field chooses the only result instead of submitting the form around the picker.
    search?.addEventListener("keydown", (event) => {
      if (event.key !== "Enter") {
        return;
      }
      event.preventDefault();
      const shown = options.filter((option) => !option.hidden && !option.classList.contains("element-picker-clear"));
      if (shown.length === 1) {
        choose(shown[0]);
      }
    });
    tagFilter?.addEventListener("change", applyFilter);
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

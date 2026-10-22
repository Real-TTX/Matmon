// Matmon - the sensor editors: name suggestion, type preview, the tab carried over a re-post, parameter visibility,
// script editor, template scope, schedule, thresholds, credentials.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

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
    // The editor draws the field itself (sensor.css): the toolkit's textarea look would fight the overlay.
    textarea.classList.remove("mat-textarea");
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

// Tiny PowerShell/shell highlighter: comments, strings, $variables, numbers, keywords. The parts carry MatUi's code
// colours (mat-code__*); a $variable has no colour of its own there and keeps a feature class (sensor.css).
function highlightScript(source) {
  const token = /(#[^\n]*)|("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')|(\$\{?[A-Za-z_][\w:.]*\}?|\$[0-9]+|\$[@*?#!-])|(\b\d+(?:\.\d+)?\b)|(\b(?:if|else|elseif|then|fi|for|foreach|in|do|done|while|until|switch|function|return|param|begin|process|end|try|catch|finally|throw|break|continue|case|esac|echo|exit|local|export|set)\b)/g;
  let result = "";
  let last = 0;
  source.replace(token, (match, comment, str, variable, number, keyword, offset) => {
    result += escapeHtml(source.slice(last, offset));
    const cls = comment ? "mat-code__com" : str ? "mat-code__str" : variable ? "code-editor-var" : number ? "mat-code__num" : "mat-code__kw";
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
        previewTimes.replaceChildren();
        runs.forEach((run) => {
          const span = document.createElement("span");
          span.className = "mat-tag mat-mono";
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

    // Per-rule enable/disable (Warn if / Alarm if): the switch is on iff a value is set. Turning it off clears the
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

// The credential bundles of a container / template (ElementEditor, TemplateEditor): a compact list of rows, each with
// its editor in a MatUi dialog ([data-credential-modal] = <dialog class="mat-dialog">, never with a hidden attribute -
// MatUi's [hidden] rule would hide it even while open). Edit opens it (data-dialog-open or [data-credential-edit]),
// Done ([data-credential-modal-save]) keeps it, X / Escape / the backdrop cancel it: a NEW credential that was never
// finished is then discarded, so Add + Cancel leaves no record behind.
function initializeCredentialEditors() {
  document.querySelectorAll("[data-credential-section]").forEach((section) => {
    const updateCredentialPanels = (row) => {
      const select = row.querySelector("[data-credential-kind-select]");
      if (!select) {
        return;
      }

      const kind = (select.value || "").toLowerCase();
      // Every credential kind has its own explicit field panel (the raw key=value "Advanced values" editor was
      // removed app-wide).
      row.querySelectorAll("[data-credential-kind-group]").forEach((panel) => {
        const panelKind = (panel.dataset.credentialKindGroup || "").toLowerCase();
        const matches =
          panelKind === kind ||
          (panelKind === "ssh" && (kind === "linux" || kind === "ssh"));

        panel.hidden = !matches;
      });
    };

    // The row shows a compact summary; editing happens in the dialog.
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
    const modalOf = (row) => row.querySelector("[data-credential-modal]");
    const isDialog = (modal) => modal && modal.tagName === "DIALOG";
    const openModal = (row, opener) => {
      const modal = modalOf(row);
      if (!modal) {
        return;
      }
      if (isDialog(modal)) {
        modal.removeAttribute("hidden"); // MatUi's [hidden] rule would keep an open dialog invisible
        if (window.MatUi && MatUi.dialog) {
          MatUi.dialog.open(modal, opener);
        } else if (!modal.open) {
          modal.showModal();
        }
        return;
      }
      // An overlay of the older markup.
      modal.hidden = false;
      const focusTarget = modal.querySelector("input:not([type='hidden']), select, textarea");
      if (focusTarget && typeof focusTarget.focus === "function") {
        focusTarget.focus();
      }
    };
    const hideModal = (row) => {
      const modal = modalOf(row);
      if (!modal) {
        return;
      }
      if (isDialog(modal)) {
        if (window.MatUi && MatUi.dialog) {
          MatUi.dialog.close(modal);
        } else if (modal.open) {
          modal.close();
        }
        return;
      }
      modal.hidden = true;
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
      delete row.dataset.credentialPending;
      clearRow(row);
      row.hidden = true;
      hideModal(row);
      syncSummary(row);
      updateEmptyState();
    };
    // Done: keep the row (unless it's a still-blank new one, which is discarded so Add + Done makes no ghost).
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
    // Cancel: a pending (new) row is discarded; editing an existing one just closes (edits kept).
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

        // Newly added = "pending": until the user presses Done it stays a discardable draft (Cancel/X/Escape removes
        // it), so pressing Add alone never leaves a record behind.
        hiddenRow.dataset.credentialPending = "true";
        hiddenRow.hidden = false;
        updateCredentialPanels(hiddenRow);
        syncSummary(hiddenRow);
        updateEmptyState();
        openModal(hiddenRow, button);
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
          openModal(row, button);
        }
      });
    });

    // X (top right) = cancel. With a dialog, the close event below does the work, whoever closed it.
    section.querySelectorAll("[data-credential-modal-close]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row && !isDialog(modalOf(row))) {
          cancelModal(row);
        }
      });
    });

    // Done (bottom) = keep the credential. It runs before MatUi's data-dialog-close on the same button, so the
    // pending flag is gone by the time the dialog's close event asks for it.
    section.querySelectorAll("[data-credential-modal-save]").forEach((button) => {
      button.addEventListener("click", () => {
        const row = button.closest("[data-credential-row]");
        if (row) {
          commitRow(row);
        }
      });
    });

    section.querySelectorAll("[data-credential-modal]").forEach((modal) => {
      const row = modal.closest("[data-credential-row]");
      if (!row) {
        return;
      }
      if (isDialog(modal)) {
        // Escape, the backdrop, X or a script: a new credential that was not finished with Done is discarded.
        modal.addEventListener("close", () => {
          if (row.dataset.credentialPending === "true") {
            discardRow(row);
            return;
          }
          syncSummary(row);
          updateEmptyState();
        });
        return;
      }
      // The older overlay: a click on its backdrop cancels.
      modal.addEventListener("click", (event) => {
        if (event.target === modal) {
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

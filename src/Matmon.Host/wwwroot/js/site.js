const dashboardRefreshMs = 5000;
const themeStorageKey = "matmon-theme";
const monitoringTreeCollapsedStorageKey = "matmon-monitoring-tree-collapsed";
const monitoringTreeMoveStorageKey = "matmon-monitoring-tree-move";

document.addEventListener("DOMContentLoaded", () => {
  initializeThemeToggle();
  initializeWorkspaceActionMenus();
  initializeMonitoringTree();
  initializeDashboardRefresh();
  initializeSensorNameSuggestion();
  initializeSensorTypePreview();
  initializeSensorParameterVisibility();
  initializeTemplateScopeEditors();
  initializeScheduleEditors();
  initializeThresholdEditors();
  initializeCredentialEditors();
  initializeNotificationKindEditors();
  initializeDiscoveryJobRefresh();
});

function initializeThemeToggle() {
  const button = document.querySelector("[data-theme-toggle]");
  if (!button) {
    return;
  }

  const label = button.querySelector("[data-theme-label]");

  const applyTheme = (theme) => {
    const normalizedTheme = theme === "light" ? "light" : "dark";
    document.documentElement.dataset.theme = normalizedTheme;
    button.dataset.theme = normalizedTheme;

    if (label) {
      label.textContent = normalizedTheme === "dark" ? "Bright" : "Dark";
    }

    button.setAttribute(
      "aria-label",
      normalizedTheme === "dark" ? "Switch to bright mode" : "Switch to dark mode"
    );
  };

  applyTheme(document.documentElement.dataset.theme || "dark");

  button.addEventListener("click", () => {
    const currentTheme = document.documentElement.dataset.theme === "light" ? "light" : "dark";
    const nextTheme = currentTheme === "dark" ? "light" : "dark";

    try {
      localStorage.setItem(themeStorageKey, nextTheme);
    } catch {
      // Theme selection stays functional even if storage is unavailable.
    }

    applyTheme(nextTheme);
  });
}

function initializeWorkspaceActionMenus() {
  const menus = Array.from(document.querySelectorAll("details.workspace-action-menu"));
  if (menus.length === 0) {
    return;
  }

  const resetMenuPosition = (menu) => {
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
  };

  const positionMenu = (menu) => {
    const summary = menu.querySelector(":scope > summary");
    const panel = menu.querySelector(":scope > .workspace-action-menu-panel");
    if (!summary || !panel || !menu.open) {
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

    const summaryRect = summary.getBoundingClientRect();
    const panelRect = panel.getBoundingClientRect();
    const panelWidth = Math.min(panelRect.width, viewportWidth - margin * 2);
    const panelHeight = Math.min(panelRect.height, viewportHeight - margin * 2);

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
  document.querySelectorAll("[data-schedule-editor]").forEach((editor) => {
    const presetSelect = editor.querySelector("[data-schedule-preset]");
    if (!presetSelect) {
      return;
    }

    const refreshFields = () => {
      const preset = String(presetSelect.value || "inherit").toLowerCase();
      const usesTime = preset === "daily" || preset === "weekly" || preset === "monthly";

      editor.querySelectorAll("[data-schedule-custom]").forEach((field) => {
        field.hidden = preset !== "custom";
      });

      editor.querySelectorAll("[data-schedule-time]").forEach((field) => {
        field.hidden = !usesTime;
      });

      editor.querySelectorAll("[data-schedule-weekday]").forEach((field) => {
        field.hidden = preset !== "weekly";
      });

      editor.querySelectorAll("[data-schedule-monthday]").forEach((field) => {
        field.hidden = preset !== "monthly";
      });
    };

    presetSelect.addEventListener("change", refreshFields);
    refreshFields();
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
            label.textContent = isDefault ? "Graph" : "Set graph";
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
      row.querySelectorAll("[data-credential-kind-group]").forEach((panel) => {
        const panelKind = (panel.dataset.credentialKindGroup || "").toLowerCase();
        const matches =
          panelKind === kind ||
          (panelKind === "ssh" && kind === "linux") ||
          (panelKind === "ssh" && kind === "ssh");

        panel.hidden = !matches;
      });
    };

    section.querySelectorAll("[data-credential-row]").forEach((row) => updateCredentialPanels(row));

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
        const focusTarget = hiddenRow.querySelector("input:not([type='hidden']), select, textarea");
        if (focusTarget && typeof focusTarget.focus === "function") {
          focusTarget.focus();
        }
      });
    });

    section.querySelectorAll("[data-credential-kind-select]").forEach((select) => {
      select.addEventListener("change", () => {
        const row = select.closest("[data-credential-row]");
        if (!row) {
          return;
        }

        updateCredentialPanels(row);
      });
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
      const kind = select.value === "Webhook" || select.value === "webhook" ? "webhook" : "email";
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
  const countElement = document.querySelector("[data-discovery-result-count]");
  const resultList = document.querySelector("[data-discovery-result-list]");
  const emptyState = document.querySelector("[data-discovery-empty]");
  const importButton = document.querySelector("[data-discovery-import-button]");
  const importLink = document.querySelector("[data-discovery-import-link]");
  const resultsPanel = document.querySelector("[data-discovery-results-panel]");
  const importMode = resultsPanel?.dataset.discoveryImportMode === "true";

  initializeDiscoveryAssistantActions();

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
  if (elements.importMode) {
    elements.resultList.querySelectorAll("[data-discovery-address]").forEach((row) => {
      const address = row.dataset.discoveryAddress || "";
      const checkbox = row.querySelector("input[type='checkbox'][name$='.Selected']");
      if (address && checkbox) {
        selectedByAddress.set(address, checkbox.checked);
      }

      row.querySelectorAll("[data-discovery-suggestion-key]").forEach((suggestionRow) => {
        const suggestionKey = suggestionRow.dataset.discoverySuggestionKey || "";
        const suggestionCheckbox = suggestionRow.querySelector("input[type='checkbox'][name$='.Selected']");
        if (suggestionKey && suggestionCheckbox) {
          selectedSuggestions.set(suggestionKey, suggestionCheckbox.checked);
        }
      });
    });
  }

  elements.resultList.innerHTML = results
    .map((result, index) => renderDiscoveryResultRow(result, index, selectedByAddress, selectedSuggestions, elements.importMode))
    .join("");
}

function renderDiscoveryResultRow(result, index, selectedByAddress, selectedSuggestions, importMode) {
  const address = String(result.address ?? "");
  const hostName = String(result.hostName ?? "");
  const message = String(result.message ?? "");
  const pingAlive = Boolean(result.pingAlive);
  const pingMs = result.pingMs == null ? "" : Number(result.pingMs).toFixed(1).replace(/\.0$/, "");
  const openPorts = Array.isArray(result.openPorts) ? result.openPorts.filter((port) => Number(port) > 0) : [];
  const openPortsText = openPorts.join(", ");
  const snmpResponded = Boolean(result.snmpResponded);
  const snmpSummary = String(result.snmpSummary ?? "");
  const selected = selectedByAddress.has(address) ? selectedByAddress.get(address) : true;
  const suggestedSensors = Array.isArray(result.suggestedSensors) ? result.suggestedSensors : [];
  const suggestionRows = suggestedSensors
    .map((suggestion, sensorIndex) => renderDiscoverySuggestionRow(address, suggestion, index, sensorIndex, selectedSuggestions, importMode))
    .join("");
  const hostControl = importMode
    ? `
        <label class="discovery-select">
          <input type="checkbox" name="Results[${index}].Selected" value="true" ${selected ? "checked" : ""} />
          <input type="hidden" name="Results[${index}].Selected" value="false" />
          <span class="tree-kind" data-kind="host"><span>Host</span></span>
        </label>
      `
    : `<span class="tree-kind" data-kind="host"><span>Host</span></span>`;
  const hiddenFields = importMode
    ? `
        <input type="hidden" name="Results[${index}].Address" value="${escapeAttribute(address)}" />
        <input type="hidden" name="Results[${index}].HostName" value="${escapeAttribute(hostName)}" />
        <input type="hidden" name="Results[${index}].PingAlive" value="${pingAlive}" />
        <input type="hidden" name="Results[${index}].PingMs" value="${escapeAttribute(String(result.pingMs ?? ""))}" />
        <input type="hidden" name="Results[${index}].OpenPortsText" value="${escapeAttribute(openPortsText)}" />
        <input type="hidden" name="Results[${index}].SnmpResponded" value="${snmpResponded}" />
        <input type="hidden" name="Results[${index}].SnmpSummary" value="${escapeAttribute(snmpSummary)}" />
        <input type="hidden" name="Results[${index}].Message" value="${escapeAttribute(message)}" />
      `
    : "";

  return `
    <article class="event-row discovery-result-row" data-state="ok" data-discovery-address="${escapeAttribute(address)}">
      <div class="event-row-summary">
        ${hostControl}
        <div class="event-row-main">
          <span class="event-row-name">${escapeHtml(address)}</span>
          ${hostName ? `<span class="event-row-separator">&middot;</span><span class="event-row-path">${escapeHtml(hostName)}</span>` : ""}
          <span class="event-row-separator">&middot;</span>
          <span class="event-row-message">${escapeHtml(message)}</span>
        </div>
        <div class="event-row-side">
          ${pingAlive ? `<span class="state-pill" data-state="ok">Ping ${escapeHtml(pingMs)} ms</span>` : ""}
          ${openPortsText ? `<span class="state-pill" data-state="warning">Ports ${escapeHtml(openPortsText)}</span>` : ""}
          ${snmpResponded ? `<span class="state-pill" data-state="ok">SNMP</span>` : ""}
        </div>
      </div>

      ${suggestionRows ? `<div class="discovery-suggestion-list">${suggestionRows}</div>` : ""}

      ${hiddenFields}
    </article>
  `;
}

function renderDiscoverySuggestionRow(address, suggestion, resultIndex, sensorIndex, selectedSuggestions, importMode) {
  const sensorTypeKey = String(suggestion.sensorTypeKey ?? "");
  const name = String(suggestion.name ?? sensorTypeKey);
  const target = String(suggestion.target ?? "");
  const reason = String(suggestion.reason ?? "");
  const confidence = Number(suggestion.confidence ?? 0);
  const settingsJson = JSON.stringify(suggestion.settings ?? {});
  const suggestionKey = buildDiscoverySuggestionKey(address, sensorTypeKey, target, name);
  const selected = selectedSuggestions.has(suggestionKey) ? selectedSuggestions.get(suggestionKey) : true;

  if (!importMode) {
    return `
      <div class="discovery-suggestion-row discovery-suggestion-row-readonly">
        <span class="sensor-chip">${escapeHtml(sensorTypeKey)}</span>
        <span class="discovery-suggestion-name">${escapeHtml(name)}</span>
        ${target ? `<span class="event-row-path">${escapeHtml(target)}</span>` : ""}
        ${reason ? `<span class="event-row-message">${escapeHtml(reason)}</span>` : ""}
        <span class="state-pill" data-state="ok">${Number.isFinite(confidence) ? confidence : 0}%</span>
      </div>
    `;
  }

  return `
    <label class="discovery-suggestion-row" data-discovery-suggestion-key="${escapeAttribute(suggestionKey)}">
      <input type="checkbox" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Selected" value="true" ${selected ? "checked" : ""} />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Selected" value="false" />
      <span class="sensor-chip">${escapeHtml(sensorTypeKey)}</span>
      <span class="discovery-suggestion-name">${escapeHtml(name)}</span>
      ${target ? `<span class="event-row-path">${escapeHtml(target)}</span>` : ""}
      ${reason ? `<span class="event-row-message">${escapeHtml(reason)}</span>` : ""}
      <span class="state-pill" data-state="ok">${Number.isFinite(confidence) ? confidence : 0}%</span>

      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].SensorTypeKey" value="${escapeAttribute(sensorTypeKey)}" />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Name" value="${escapeAttribute(name)}" />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Target" value="${escapeAttribute(target)}" />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Reason" value="${escapeAttribute(reason)}" />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].Confidence" value="${escapeAttribute(String(confidence))}" />
      <input type="hidden" name="Results[${resultIndex}].SuggestedSensors[${sensorIndex}].SettingsJson" value="${escapeAttribute(settingsJson)}" />
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

  const setChecked = (checked) => {
    form.querySelectorAll(".discovery-suggestion-row input[type='checkbox'], .discovery-select input[type='checkbox']").forEach((checkbox) => {
      checkbox.checked = checked;
    });
  };

  document.querySelector("[data-discovery-select-all]")?.addEventListener("click", () => setChecked(true));
  document.querySelector("[data-discovery-select-none]")?.addEventListener("click", () => setChecked(false));
}

function discoveryStatusTone(status) {
  const normalized = String(status || "").toLowerCase();
  if (normalized === "completed") {
    return "ok";
  }

  if (normalized === "failed") {
    return "error";
  }

  return "warning";
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

function renderNavCounters(snapshot) {
  const openAlerts = Number(snapshot.activeAlertCount ?? 0);
  const acknowledgedAlerts = Number(snapshot.acknowledgedAlertCount ?? 0);
  const pausedSensors = Number(snapshot.pausedSensorCount ?? 0);
  const warningSensors = Number(snapshot.warningSensorCount ?? 0);
  const errorSensors = Number(snapshot.errorSensorCount ?? 0);
  const alertStatus = document.querySelector("[data-nav-alert-status]");
  const hasErrors = openAlerts > 0 || errorSensors > 0;
  const hasWarnings = warningSensors > 0 || acknowledgedAlerts > 0 || pausedSensors > 0;

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
      if (hasErrors) {
        stateLabel = "Error";
      } else if (warningSensors > 0) {
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
      if (openAlerts > 0) {
        parts.push(`${openAlerts} open`);
      }
      if (errorSensors > 0) {
        parts.push(`${errorSensors} error`);
      }
      if (warningSensors > 0) {
        parts.push(`${warningSensors} warning`);
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
  setNavCounterText("[data-nav-error-count]", errorSensors);
  setNavCounterText("[data-nav-warning-count]", warningSensors);
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
  const currentPath = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  const loginUrl = new URL("/login", window.location.origin);
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

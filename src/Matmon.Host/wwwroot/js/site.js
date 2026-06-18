const dashboardRefreshMs = 5000;
const themeStorageKey = "matmon-theme";
const monitoringTreeCollapsedStorageKey = "matmon-monitoring-tree-collapsed";
const monitoringTreeMoveStorageKey = "matmon-monitoring-tree-move";
const monitoringViewStorageKey = "matmon-monitoring-view";
const monitoringTreeSizeStorageKey = "matmon-monitoring-tree-size";
const monitoringListSizeStorageKey = "matmon-monitoring-list-size";

document.addEventListener("DOMContentLoaded", () => {
  initializeThemeToggle();
  initializeMobileSidebarMenu();
  initializeWorkspaceSummaryPlacement();
  initializeClipboardButtons();
  if (initializeMonitoringPreferences()) {
    return;
  }
  initializeAccountMenu();
  initializeWorkspaceActionMenus();
  initializeMonitoringTree();
  initializeDashboardRefresh();
  initializeSensorTabs();
  initializeSensorNameSuggestion();
  initializeSensorTypePreview();
  initializeSensorParameterVisibility();
  initializeTemplateScopeEditors();
  initializeScheduleEditors();
  initializeStatisticsFilters();
  initializeThresholdEditors();
  initializeCredentialEditors();
  initializeNotificationKindEditors();
  initializeDiscoveryJobRefresh();
  initializeDiscoveryResultTable();
  initializeDiscoveryJobList();
  initializeDiscoveryScanForm();
  initializeMapDesigner();
  initializeMapCarousel();
});

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

    const show = (index) => {
      active = (index + slides.length) % slides.length;
      slides.forEach((slide, i) => {
        slide.hidden = i !== active;
      });
      dots.forEach((dot, i) => dot.classList.toggle("is-active", i === active));
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
    return;
  }

  const shouldSkipMove = targetHeader.matches(
    ".dashboard-header, .sensor-header, .probe-install-header"
  ) || targetHeader.querySelector(
    ".dashboard-header-summary, .page-header-summary, .probe-install-summary, .user-edit-summary"
  );

  if (shouldSkipMove) {
    return;
  }

  targetHeader.classList.add("has-summary");
  targetHeader.appendChild(summaryStrip);
}

function initializeThemeToggle() {
  const buttons = Array.from(document.querySelectorAll("[data-theme-toggle]"));
  if (buttons.length === 0) {
    return;
  }

  const applyTheme = (theme) => {
    const normalizedTheme = theme === "light" ? "light" : "dark";
    document.documentElement.dataset.theme = normalizedTheme;

    buttons.forEach((button) => {
      const label = button.querySelector("[data-theme-label]");
      button.dataset.theme = normalizedTheme;
      button.title = normalizedTheme === "dark" ? "Switch to bright mode" : "Switch to dark mode";
      button.setAttribute("aria-label", button.title);

      if (label) {
        label.textContent = normalizedTheme === "dark" ? "Bright" : "Dark";
      }
    });
  };

  applyTheme(document.documentElement.dataset.theme || "dark");

  buttons.forEach((button) => {
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
  });
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

function initializeMonitoringPreferences() {
  const shell = document.querySelector("[data-monitoring-shell]");
  if (!shell) {
    return false;
  }

  const normalizeView = (value) => String(value || "").trim().toLowerCase() === "list" ? "list" : "tree";
  const normalizeTreeSize = (value) => {
    switch (String(value || "").trim().toLowerCase()) {
      case "s":
        return "s";
      case "l":
        return "l";
      default:
        return "m";
    }
  };
  const normalizeListSize = (value) => String(value || "").trim().toLowerCase() === "s" ? "s" : "l";

  const readStorageValue = (key) => {
    try {
      return localStorage.getItem(key) || "";
    } catch {
      return "";
    }
  };

  const writeStorageValue = (key, value) => {
    try {
      if (value) {
        localStorage.setItem(key, value);
      } else {
        localStorage.removeItem(key);
      }
    } catch {
      // The preference still works for the current visit even if persistence fails.
    }
  };

  const currentView = normalizeView(shell.dataset.monitoringView);
  const currentSize = currentView === "list" ? normalizeListSize(shell.dataset.monitoringSize) : normalizeTreeSize(shell.dataset.monitoringSize);
  const storedView = normalizeView(readStorageValue(monitoringViewStorageKey) || currentView);
  const storedTreeSize = normalizeTreeSize(readStorageValue(monitoringTreeSizeStorageKey));
  const storedListSize = normalizeListSize(readStorageValue(monitoringListSizeStorageKey));

  const url = new URL(window.location.href);
  const hasMonitoringView = url.searchParams.has("monitoringView");
  const hasMonitoringSize = url.searchParams.has("monitoringSize");
  if (!hasMonitoringView || !hasMonitoringSize) {
    const targetUrl = new URL(window.location.href);
    const targetView = storedView;
    const targetSize = targetView === "list" ? storedListSize : storedTreeSize;

    targetUrl.searchParams.set("monitoringView", targetView);
    targetUrl.searchParams.set("monitoringSize", targetSize);
    window.location.replace(targetUrl.toString());
    return true;
  }

  writeStorageValue(monitoringViewStorageKey, currentView);
  writeStorageValue(currentView === "list" ? monitoringListSizeStorageKey : monitoringTreeSizeStorageKey, currentSize);

  const buildUrl = (baseHref, view, size) => {
    const url = new URL(baseHref || window.location.href, window.location.origin);
    url.searchParams.set("monitoringView", view);
    url.searchParams.set("monitoringSize", size);
    return url;
  };

  shell.querySelectorAll("[data-monitoring-view-link]").forEach((link) => {
    const targetView = normalizeView(link.dataset.monitoringViewLink);
    const targetSize = targetView === "list" ? storedListSize : storedTreeSize;
    link.href = buildUrl(link.getAttribute("href"), targetView, targetSize).toString();
  });

  shell.querySelectorAll("[data-monitoring-size-link]").forEach((link) => {
    const targetSize = currentView === "list"
      ? normalizeListSize(link.dataset.monitoringSizeLink)
      : normalizeTreeSize(link.dataset.monitoringSizeLink);
    link.href = buildUrl(link.getAttribute("href"), currentView, targetSize).toString();
  });

  return false;
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

function initializeSensorTabs() {
  document.querySelectorAll("[data-sensor-tabs]").forEach((tabBar) => {
    const scope = tabBar.closest("form") || document;
    const buttons = Array.from(tabBar.querySelectorAll("[data-sensor-tab-target]"));
    const panels = Array.from(scope.querySelectorAll("[data-sensor-tab]"));
    if (buttons.length === 0 || panels.length === 0) {
      return;
    }

    const activate = (name) => {
      buttons.forEach((button) => {
        button.classList.toggle("is-active", button.dataset.sensorTabTarget === name);
        button.setAttribute("aria-selected", button.dataset.sensorTabTarget === name ? "true" : "false");
      });
      panels.forEach((panel) => {
        panel.hidden = panel.dataset.sensorTab !== name;
      });
    };

    buttons.forEach((button) => {
      button.addEventListener("click", () => activate(button.dataset.sensorTabTarget));
    });

    activate(buttons[0].dataset.sensorTabTarget);
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
    const weekdayInput = editor.querySelector("[data-schedule-weekday-input]");
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
        const target = dowIndex(weekdayInput && weekdayInput.value);
        const d = new Date(now);
        d.setHours(h, m, 0, 0);
        d.setDate(d.getDate() + ((target - d.getDay() + 7) % 7));
        if (d <= now) {
          d.setDate(d.getDate() + 7);
        }
        for (let i = 0; i < 3; i++) {
          runs.push(new Date(d));
          d.setDate(d.getDate() + 7);
        }
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
    [valueInput, unitInput, timeInput, weekdayInput, monthdayInput].forEach((el) => {
      if (el) {
        el.addEventListener("change", refresh);
        el.addEventListener("input", refresh);
      }
    });

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

function initializeStatisticsFilters() {
  document.querySelectorAll("[data-statistics-section]").forEach((section) => {
    const filter = section.querySelector("[data-statistics-filter]");
    const rows = Array.from(section.querySelectorAll("tbody tr[data-period-ms]"));
    const empty = section.querySelector("[data-statistics-empty]");
    const count = section.querySelector("[data-statistics-count]");
    if (!filter || rows.length === 0) {
      return;
    }

    const baseCountText = count ? count.textContent : "";

    const apply = () => {
      const days = parseInt(filter.value, 10) || 0;
      const cutoff = days > 0 ? Date.now() - days * 86400000 : 0;
      let visible = 0;
      rows.forEach((row) => {
        const ts = parseInt(row.dataset.periodMs, 10) || 0;
        const show = days === 0 || ts >= cutoff;
        row.hidden = !show;
        if (show) {
          visible += 1;
        }
      });

      if (empty) {
        empty.hidden = visible > 0;
      }
      if (count) {
        count.textContent = days === 0 ? baseCountText : `${visible} period${visible === 1 ? "" : "s"} shown`;
      }
    };

    filter.addEventListener("change", apply);
    apply();
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
  const selected = selectedByAddress.has(address) ? selectedByAddress.get(address) : true;
  const suggestedSensors = Array.isArray(result.suggestedSensors) ? result.suggestedSensors : [];
  const expanded = expandedByAddress.has(address) ? expandedByAddress.get(address) : false;
  const sensorCount = suggestedSensors.length;
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
  const suggestionRows = suggestedSensors
    .map((suggestion, sensorIndex) => renderDiscoverySuggestionRow(address, suggestion, index, sensorIndex, selectedSuggestions, importMode))
    .join("");
  const hostControl = importMode
    ? `
        <input type="checkbox" name="SelectedHostAddresses" value="${escapeAttribute(address)}" ${selected ? "checked" : ""} />
      `
    : "";

  return `
    <tr class="discovery-host-row"
        data-discovery-address="${escapeAttribute(address)}"
        data-discovery-host="${escapeAttribute(hostName)}"
        data-discovery-text="${escapeAttribute(searchText)}"
        data-discovery-ping="${pingAlive ? "ok" : "none"}"
        data-discovery-ping-ms="${escapeAttribute(String(result.pingMs ?? ""))}"
        data-discovery-port-count="${openPorts.length}"
        data-discovery-snmp="${snmpResponded ? "true" : "false"}"
        data-discovery-sensor-count="${sensorCount}"
        data-discovery-expanded="${expanded ? "true" : "false"}">
      <td class="discovery-table-select">
        <button type="button" class="discovery-row-toggle" data-discovery-toggle aria-expanded="${expanded ? "true" : "false"}">${expanded ? "-" : "+"}</button>
        ${hostControl}
      </td>
      <td class="discovery-address-cell"><span class="tree-kind" data-kind="host"><span>Host</span></span><strong>${escapeHtml(address)}</strong></td>
      <td>${hostName ? escapeHtml(hostName) : "-"}</td>
      <td>${pingAlive ? `${escapeHtml(pingMs)} ms` : "-"}</td>
      <td title="${escapeAttribute(openPortsText)}">${openPorts.length > 0 ? `${openPorts.length} open` : "-"}</td>
      <td>${snmpResponded ? "yes" : "-"}</td>
      <td><span class="state-pill" data-state="${sensorCount > 0 ? "ok" : "warning"}">${sensorCount} service${sensorCount === 1 ? "" : "s"}</span></td>
    </tr>
    <tr class="discovery-suggestion-panel" data-discovery-parent-address="${escapeAttribute(address)}" ${expanded ? "" : "hidden"}>
      <td colspan="7">
        <div class="discovery-host-message">${escapeHtml(message || "No summary.")}</div>
        ${suggestionRows ? `<div class="discovery-suggestion-list">${suggestionRows}</div>` : `<div class="empty-state">No sensor suggestions.</div>`}
      </td>
    </tr>
  `;
}

function renderDiscoverySuggestionRow(address, suggestion, resultIndex, sensorIndex, selectedSuggestions, importMode) {
  const sensorTypeKey = String(suggestion.sensorTypeKey ?? "");
  const name = String(suggestion.name ?? sensorTypeKey);
  const target = String(suggestion.target ?? "");
  const reason = String(suggestion.reason ?? "");
  const confidence = Number(suggestion.confidence ?? 0);
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
      <input type="checkbox" name="SelectedSuggestionKeys" value="${escapeAttribute(suggestionKey)}" ${selected ? "checked" : ""} />
      <span class="sensor-chip">${escapeHtml(sensorTypeKey)}</span>
      <span class="discovery-suggestion-name">${escapeHtml(name)}</span>
      ${target ? `<span class="event-row-path">${escapeHtml(target)}</span>` : ""}
      ${reason ? `<span class="event-row-message">${escapeHtml(reason)}</span>` : ""}
      <span class="state-pill" data-state="ok">${Number.isFinite(confidence) ? confidence : 0}%</span>
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
    form.querySelectorAll(".discovery-suggestion-row input[type='checkbox'], .discovery-host-row input[type='checkbox']").forEach((checkbox) => {
      checkbox.checked = checked;
    });
  };

  document.querySelector("[data-discovery-select-all]")?.addEventListener("click", () => setChecked(true));
  document.querySelector("[data-discovery-select-none]")?.addEventListener("click", () => setChecked(false));
}

function initializeDiscoveryResultTable() {
  const panel = document.querySelector("[data-discovery-results-panel]");
  if (!panel || panel.dataset.discoveryTableInitialized === "true") {
    return;
  }

  panel.dataset.discoveryTableInitialized = "true";
  panel.dataset.discoverySort = panel.dataset.discoverySort || "address";
  panel.dataset.discoverySortDirection = panel.dataset.discoverySortDirection || "asc";

  panel.querySelector("[data-discovery-filter]")?.addEventListener("input", () => applyDiscoveryTableState(panel));
  panel.querySelector("[data-discovery-service-filter]")?.addEventListener("change", () => applyDiscoveryTableState(panel));

  panel.querySelectorAll("[data-discovery-sort]").forEach((button) => {
    button.addEventListener("click", () => {
      const sortKey = button.dataset.discoverySort || "address";
      const currentSort = panel.dataset.discoverySort || "address";
      const currentDirection = panel.dataset.discoverySortDirection || "asc";
      panel.dataset.discoverySort = sortKey;
      panel.dataset.discoverySortDirection = currentSort === sortKey && currentDirection === "asc" ? "desc" : "asc";
      applyDiscoveryTableState(panel);
    });
  });

  panel.addEventListener("click", (event) => {
    const toggle = event.target.closest("[data-discovery-toggle]");
    if (!toggle) {
      return;
    }

    const row = toggle.closest(".discovery-host-row");
    if (!row) {
      return;
    }

    const expanded = row.dataset.discoveryExpanded !== "true";
    row.dataset.discoveryExpanded = expanded ? "true" : "false";
    toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
    toggle.textContent = expanded ? "-" : "+";
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

  const sortKey = panel.dataset.discoverySort || "address";
  const sortDirection = panel.dataset.discoverySortDirection === "desc" ? "desc" : "asc";
  const search = String(panel.querySelector("[data-discovery-filter]")?.value || "").trim().toLowerCase();
  const serviceFilter = String(panel.querySelector("[data-discovery-service-filter]")?.value || "all");
  const pairs = Array.from(body.querySelectorAll(".discovery-host-row")).map((row) => ({
    row,
    details: row.nextElementSibling?.classList.contains("discovery-suggestion-panel") ? row.nextElementSibling : null
  }));

  pairs.sort((left, right) => compareDiscoveryRows(left.row, right.row, sortKey, sortDirection));
  pairs.forEach((pair) => {
    body.appendChild(pair.row);
    if (pair.details) {
      body.appendChild(pair.details);
    }
  });

  let visibleCount = 0;
  pairs.forEach((pair) => {
    const visible = discoveryRowMatches(pair.row, search, serviceFilter);
    pair.row.hidden = !visible;
    if (visible) {
      visibleCount++;
    }

    const expanded = visible && pair.row.dataset.discoveryExpanded === "true";
    const toggle = pair.row.querySelector("[data-discovery-toggle]");
    if (toggle) {
      toggle.textContent = expanded ? "-" : "+";
      toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
    }

    if (pair.details) {
      pair.details.hidden = !expanded;
    }
  });

  panel.querySelectorAll("[data-discovery-sort]").forEach((button) => {
    const active = button.dataset.discoverySort === sortKey;
    button.classList.toggle("is-active", active);
    button.dataset.direction = active ? sortDirection : "";
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
  const mapNameInput = form?.querySelector("[data-map-name]");
  const mapDescriptionInput = form?.querySelector("[data-map-description]");
  const displayPresetInput = form?.querySelector("[data-map-display-preset]");
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
  const sizeLimits = {
    Text: { minWidth: 2, minHeight: 1, maxWidth: 12, maxHeight: 6, defaultWidth: 4, defaultHeight: 2 },
    Element: { minWidth: 2, minHeight: 1, maxWidth: 8, maxHeight: 6, defaultWidth: 3, defaultHeight: 2 },
    Status: { minWidth: 3, minHeight: 2, maxWidth: 12, maxHeight: 8, defaultWidth: 4, defaultHeight: 2 },
    Value: { minWidth: 2, minHeight: 2, maxWidth: 8, maxHeight: 6, defaultWidth: 3, defaultHeight: 2 },
    Graph: { minWidth: 4, minHeight: 3, maxWidth: 12, maxHeight: 10, defaultWidth: 5, defaultHeight: 3 }
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
    const selected = displayPresetInput?.selectedOptions?.[0];
    const key = String(displayPresetInput?.value || "").trim();
    const preset = presetOptions[key] || presetOptions.FullHd1080;
    const width = Number(selected?.dataset.displayWidth || preset.width || 1920);
    const height = Number(selected?.dataset.displayHeight || preset.height || 1080);
    const label = selected?.textContent?.trim() || preset.label;
    return {
      width: Number.isFinite(width) && width > 0 ? width : 1920,
      height: Number.isFinite(height) && height > 0 ? height : 1080,
      label
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
    canvas.style.width = `min(100%, ${preset.width}px)`;
    canvas.style.aspectRatio = `${preset.width} / ${preset.height}`;
    const canvasWidth = Math.max(720, grid.columns * 72);
    canvas.style.minWidth = `${canvasWidth}px`;
    canvas.style.minHeight = `${Math.max(560, grid.rows * 72)}px`;
    if (mapSelectButton) {
      mapSelectButton.style.minWidth = `${canvasWidth}px`;
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
      const selectedText = elementSelect?.selectedOptions?.[0]?.textContent?.trim();
      preview.textContent = isText
        ? (text.trim() || "Text tile")
        : (selectedText && selectedText !== "No element" ? selectedText : "No target selected");
    }

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
    panel?.querySelectorAll("[data-map-property-title], [data-map-property-kind], [data-map-property-visual-type], [data-map-property-element], [data-map-property-text], [data-map-property-graph-type], [data-map-property-background], [data-map-property-accent], [data-map-property-text-color], [data-map-property-show-title], [data-map-property-show-badge]").forEach((input) => {
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
    selectTile(index);
  };

  mapSelectButton?.addEventListener("click", selectMap);
  mapNameInput?.addEventListener("input", () => syncMapSummary());
  mapDescriptionInput?.addEventListener("input", () => syncMapSummary());
  displayPresetInput?.addEventListener("change", () => syncGrid(true));
  displayPresetInput?.addEventListener("input", () => syncGrid(true));
  columnInput?.addEventListener("input", () => syncGrid());
  columnInput?.addEventListener("change", () => syncGrid(true));
  rowInput?.addEventListener("input", () => syncGrid());
  rowInput?.addEventListener("change", () => syncGrid(true));

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

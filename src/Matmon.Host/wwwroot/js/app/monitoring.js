// Matmon - the monitoring tree and list: expand/collapse, move, the context menu, the size toggle, row links.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).
// monitoring-prepaint.js (in <head>, Sensors page only) applies the size and the collapsed branches before the first paint.

// Tile size (S/M/L) is a live, client-only preference on <html> + localStorage, switched by the mat-seg buttons of the
// toolbar (aria-pressed). There is no URL round-trip and no redirect to switch size.
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
      button.setAttribute("aria-pressed", String(button.dataset.monitoringSizeSet === normalized));
    });
  };

  apply(document.documentElement.dataset.monitoringSize, false);
  buttons.forEach((button) => {
    button.addEventListener("click", () => apply(button.dataset.monitoringSizeSet, true));
  });
}

// Right-click on a tree row or a sensor chip opens THAT element's action menu at the pointer (the deepest element under
// the pointer wins, so right-clicking a child sensor opens the sensor's menu). The menu is a MatUi menu: a click on its
// toggle opens it (MatUi places it next to the toggle and moves the focus into it), then it is moved to the pointer. A
// context menu from the keyboard (Shift+F10, the menu key) has no pointer position and stays next to the toggle.
function initializeTreeContextMenu() {
  // The element's menu for the chip or row under a point - only when the point is really over a sensor chip or a
  // container row, not the gaps around them, where closest("[data-tree-node]") would grab an ancestor and open a menu
  // for an element the user is not on. Inside an open menu the browser keeps its own context menu (copy a link ...).
  const menuAt = (target) => {
    if (!(target instanceof Element) || target.closest(".mat-menu")) {
      return null;
    }
    const hit = target.closest(".monitoring-sensor-chip, .monitoring-tree-row");
    const toggle = hit?.querySelector("[data-menu-toggle]");
    const menu = toggle ? document.getElementById(toggle.getAttribute("data-menu-toggle") || "") : null;
    return toggle && menu ? { toggle, menu } : null;
  };

  const open = ({ toggle, menu }, x, y) => {
    if (toggle.getAttribute("aria-expanded") !== "true") {
      toggle.click();
    }
    if (!menu.hidden && (x !== 0 || y !== 0)) {
      placeTreeMenuAtPointer(menu, x, y);
    }
  };

  // A long press on a touch screen opens the same menu as a right click. Android fires a contextmenu of its own for
  // a long press, iOS does not (it shows its link preview instead - the stylesheet switches that off in the tree), so
  // the press is timed here and the browser's own event is ignored for a moment after it.
  const longPressMs = 500;
  const slop = 10;
  let press = null;
  let openedByPressAt = 0;

  document.querySelectorAll("[data-monitoring-tree]").forEach((tree) => {
    tree.addEventListener("contextmenu", (event) => {
      const found = menuAt(event.target);
      if (!found) {
        return;
      }
      event.preventDefault();
      if (press) {
        window.clearTimeout(press.timer);
        press = null;
      }
      if (Date.now() - openedByPressAt < 1000) {
        return;
      }
      open(found, event.clientX, event.clientY);
    });

    tree.addEventListener("pointerdown", (event) => {
      if (event.pointerType === "mouse" || !event.isPrimary) {
        return;
      }
      const found = menuAt(event.target);
      if (!found) {
        return;
      }
      const x = event.clientX;
      const y = event.clientY;
      press = {
        x,
        y,
        timer: window.setTimeout(() => {
          press = null;
          if (navigator.vibrate) {
            navigator.vibrate(10);
          }
          open(found, x, y);
          // Stamped AFTER opening: the menu opens through a click on its toggle, which the guard below would eat.
          openedByPressAt = Date.now();
        }, longPressMs)
      };
    });

    const cancel = () => {
      if (press) {
        window.clearTimeout(press.timer);
        press = null;
      }
    };
    tree.addEventListener("pointermove", (event) => {
      if (press && Math.hypot(event.clientX - press.x, event.clientY - press.y) > slop) {
        cancel();
      }
    });
    tree.addEventListener("pointerup", cancel);
    tree.addEventListener("pointercancel", cancel);

    // The finger lifting after a long press is not a tap: the link under it must not open.
    tree.addEventListener("click", (event) => {
      if (Date.now() - openedByPressAt < 1000 && !(event.target instanceof Element && event.target.closest(".mat-menu"))) {
        event.preventDefault();
        event.stopPropagation();
        openedByPressAt = 0;
      }
    }, true);
  });
}

// The MatUi menu is position: fixed; keep it inside the window like MatUi's own placement does.
function placeTreeMenuAtPointer(menu, x, y) {
  const margin = 8;
  const width = menu.offsetWidth;
  const height = menu.offsetHeight;
  const left = Math.max(margin, Math.min(x, window.innerWidth - width - margin));
  const below = y + height + margin <= window.innerHeight;
  const top = below ? y : Math.max(margin, y - height);
  menu.style.left = `${left}px`;
  menu.style.top = `${top}px`;
  menu.style.bottom = "auto";
  menu.style.transformOrigin = `${below ? "top" : "bottom"} left`;
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
      const label = toggle.dataset.treeLabel || "";
      toggle.setAttribute("aria-label", `${collapsed ? "Expand" : "Collapse"} ${label}`.trim());
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
        const isSource = hasMoveState && nodeId === moveState.id;
        const startButton = getOwnTreeControl(node, "[data-tree-move-start]");
        const targetButton = getOwnTreeControl(node, "[data-tree-move-target]");

        node.classList.toggle("is-move-source", isSource);

        // The Move item of the element's menu becomes "Cancel move" while that element is the one being moved
        // (the visible text is its name: no aria-label that would say something else).
        if (startButton) {
          const text = startButton.querySelector("[data-tree-move-start-text]");
          if (text) {
            text.textContent = isSource ? "Cancel move" : "Move";
          }
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
    // (monitoring-prepaint.js, added before the tree to avoid the expand->collapse flash) so toggling works.
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

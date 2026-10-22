// Matmon - the Sensors page before its first paint. Loaded in <head> by Monitoring.cshtml (its Styles section), not by
// _AppScripts: the tile size and the collapsed branches are client-only preferences in localStorage, and applying them
// here means the page never draws the default size or a fully expanded tree and then jumps (the old M->S flash and the
// tree that visibly "built up"). There is no URL round-trip and no redirect for either. monitoring.js takes over on load:
// it toggles the size live and turns the collapsed branches into is-collapsed classes, then removes the style added here.
// The keys are the ones core.js names (monitoringSizeStorageKey, monitoringTreeCollapsedStorageKey); core.js is not loaded yet.
(function () {
  var root = document.documentElement;

  try {
    var size = String(localStorage.getItem("matmon-monitoring-size") || "m").trim().toLowerCase();
    root.dataset.monitoringSize = size === "s" || size === "l" ? size : "m";
  } catch (e) {
    root.dataset.monitoringSize = "m";
  }

  // Hide the remembered collapsed subtrees (and turn their chevrons back) with a stylesheet BEFORE the tree is parsed.
  try {
    var collapsed = JSON.parse(localStorage.getItem("matmon-monitoring-tree-collapsed") || "[]");
    if (!Array.isArray(collapsed) || collapsed.length === 0) {
      return;
    }

    var rules = collapsed
      .filter(function (id) { return typeof id === "string"; })
      .map(function (id) { return id.replace(/[^a-zA-Z0-9-]/g, ""); })
      .filter(function (id) { return id.length > 0; })
      .map(function (id) {
        var node = '.monitoring-tree-node[data-tree-node-id="' + id + '"]';
        return node + " > .monitoring-tree-children{display:none!important}" +
          node + " > .monitoring-tree-row > .monitoring-tree-toggle{rotate:none!important}";
      })
      .join("");

    if (rules) {
      var style = document.createElement("style");
      style.id = "matmon-precollapse";
      style.textContent = rules;
      document.head.appendChild(style);
    }
  } catch (e) {
    // The collapse before the first paint is best-effort; monitoring.js applies it on load either way.
  }
})();

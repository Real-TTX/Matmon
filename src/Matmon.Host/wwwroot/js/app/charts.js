// Matmon - the hover tooltips of the line and bar charts (.sensor-chart-wrap[data-chart="line|bars"], charts.css).
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).
// The tooltip, crosshair and dot are placed with left/top from the pointer at run time - geometry, not a look.

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
    // The unit is the sensor's text: written with textContent, never as markup.
    const value = document.createElement("strong");
    value.textContent = `${formatChartNumber(nearest[1])}${unit ? ` ${unit}` : ""}`;
    const time = document.createElement("span");
    time.textContent = when;
    tip.replaceChildren(value, time);
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


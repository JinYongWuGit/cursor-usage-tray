(function () {
  "use strict";

  const vscode = acquireVsCodeApi();
  const DAY_MS = 86_400_000;

  const ui = {
    tabUsage: document.getElementById("tab-usage"),
    tabCursorBench: document.getElementById("tab-cursorbench"),
    panelUsage: document.getElementById("tab-panel-usage"),
    panelCursorBench: document.getElementById("tab-panel-cursorbench"),
    summaryCards: document.getElementById("summary-cards"),
    rangeSelector: document.getElementById("range-selector"),
    usageFilter: document.getElementById("usage-filter"),
    metricFilter: document.getElementById("metric-filter"),
    canvas: document.getElementById("usage-chart"),
    chartNote: document.getElementById("chart-note"),
    tableBody: document.querySelector("#events-table tbody"),
    tableHead: document.querySelector("#events-table thead"),
    breakdownBody: document.querySelector("#breakdown-table tbody"),
    breakdownHead: document.querySelector("#breakdown-table thead"),
    breakdownRangeLabel: document.getElementById("breakdown-range-label"),
    pagination: document.getElementById("pagination"),
    refreshBtn: document.getElementById("refresh-btn"),
    usageOpenSource: document.getElementById("usage-open-source"),
    exportBtn: document.getElementById("export-csv"),
    lastUpdated: document.getElementById("last-updated"),
    errorBanner: document.getElementById("error-banner"),

    cursorBenchMeta: document.getElementById("cursorbench-meta"),
    cursorBenchRefresh: document.getElementById("cursorbench-refresh"),
    cursorBenchOpenSource: document.getElementById("cursorbench-open-source"),
    cursorBenchXMetric: document.getElementById("cursorbench-xmetric"),
    cursorBenchCanvas: document.getElementById("cursorbench-chart"),
    cursorBenchNote: document.getElementById("cursorbench-note"),
    cursorBenchTableBody: document.querySelector("#cursorbench-table tbody"),
    cursorBenchTableHead: document.querySelector("#cursorbench-table thead"),
  };

  const persisted = vscode.getState() || {};
  const local = {
    range: persisted.range || "billingCycle",
    usageFilter: persisted.usageFilter || "all",
    metric: persisted.metric || "tokens",
    sortKey: persisted.sortKey || "timestamp",
    sortOrder: persisted.sortOrder || "desc",
    breakdownSortKey: persisted.breakdownSortKey || "totalTokens",
    breakdownSortOrder: persisted.breakdownSortOrder || "desc",
    activeTab: persisted.activeTab || "usage",
    cursorBenchSortKey: persisted.cursorBenchSortKey || "score",
    cursorBenchSortOrder: persisted.cursorBenchSortOrder || "desc",
    cursorBenchXMetric: persisted.cursorBenchXMetric || "costPerTask",
    sectionOpen: {
      usage: persisted.sectionOpen?.usage !== false,
      breakdown: persisted.sectionOpen?.breakdown !== false,
      events: persisted.sectionOpen?.events !== false,
    },
  };

  let state = null;
  let chart = null;
  let cursorBench = null;
  let cursorBenchChart = null;

  // Soft pastel palette that pairs with the shadcn dark surface — gentle, low-saturation
  // hues with enough contrast against the card background. Ordered so adjacent series
  // never use neighboring hues.
  const PALETTE = [
    "#9ec5fe", // sky blue
    "#b6e3c1", // mint
    "#f7c5a0", // peach
    "#d3b9f2", // lavender
    "#f5b8c5", // rose
    "#a7e0e0", // aqua
    "#f0d99b", // butter
    "#c9d4f0", // periwinkle
  ];

  function persistLocal() {
    vscode.setState({
      range: local.range,
      usageFilter: local.usageFilter,
      metric: local.metric,
      sortKey: local.sortKey,
      sortOrder: local.sortOrder,
      breakdownSortKey: local.breakdownSortKey,
      breakdownSortOrder: local.breakdownSortOrder,
      activeTab: local.activeTab,
      cursorBenchSortKey: local.cursorBenchSortKey,
      cursorBenchSortOrder: local.cursorBenchSortOrder,
      cursorBenchXMetric: local.cursorBenchXMetric,
      sectionOpen: local.sectionOpen,
    });
  }

  function setActiveTab(tab) {
    local.activeTab = tab === "cursorbench" ? "cursorbench" : "usage";
    persistLocal();

    if (ui.panelUsage) ui.panelUsage.classList.toggle("hidden", local.activeTab !== "usage");
    if (ui.panelCursorBench) ui.panelCursorBench.classList.toggle("hidden", local.activeTab !== "cursorbench");

    if (ui.tabUsage) {
      ui.tabUsage.classList.toggle("active", local.activeTab === "usage");
      ui.tabUsage.setAttribute("aria-selected", local.activeTab === "usage" ? "true" : "false");
    }
    if (ui.tabCursorBench) {
      ui.tabCursorBench.classList.toggle("active", local.activeTab === "cursorbench");
      ui.tabCursorBench.setAttribute("aria-selected", local.activeTab === "cursorbench" ? "true" : "false");
    }

    // Chart.js needs a redraw when switching into the tab.
    if (local.activeTab === "usage" && state) {
      renderChart();
    }
    if (local.activeTab === "cursorbench" && cursorBench) {
      renderCursorBench();
    }
  }

  function setSectionCollapsed(section, isOpen) {
    const sectionEl = document.querySelector('.collapsible-section[data-section="' + section + '"]');
    const bodyEl = document.getElementById("section-body-" + section);
    const toggleEl = document.querySelector('.section-toggle[data-toggle-section="' + section + '"]');
    if (!sectionEl || !bodyEl || !toggleEl) return;
    sectionEl.classList.toggle("collapsed", !isOpen);
    bodyEl.classList.toggle("hidden", !isOpen);
    toggleEl.setAttribute("aria-expanded", isOpen ? "true" : "false");
  }

  function applySectionState() {
    setSectionCollapsed("usage", local.sectionOpen.usage);
    setSectionCollapsed("breakdown", local.sectionOpen.breakdown);
    setSectionCollapsed("events", local.sectionOpen.events);
  }

  function startOfUtcDay(ts) {
    const d = new Date(ts);
    return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  }

  function getDurationCutoff(range, resetAtIso, now) {
    if (range === "billingCycle") {
      if (!resetAtIso) return now - 31 * DAY_MS;
      const reset = new Date(resetAtIso);
      if (Number.isNaN(reset.getTime())) return now - 31 * DAY_MS;
      reset.setMonth(reset.getMonth() - 1);
      return reset.getTime();
    }
    const map = { "1d": 1, "7d": 7, "30d": 30 };
    return now - (map[range] || 30) * DAY_MS;
  }

  function matchesUsageFilter(event, filter) {
    if (filter === "all") return true;
    if (filter === "included") return event.kind === "Included";
    return event.kind === "On-Demand";
  }

  function formatTokens(n) {
    const trim = (v) => {
      const s = v.toFixed(1);
      return s.endsWith(".0") ? s.slice(0, -2) : s;
    };
    if (n >= 1e9) return trim(n / 1e9) + "B";
    if (n >= 1e6) return trim(n / 1e6) + "M";
    if (n >= 1e3) return trim(n / 1e3) + "K";
    return String(Math.round(n));
  }

  function formatDollars(n) {
    return "$" + (n || 0).toFixed(2);
  }

  function toMillis(ts) {
    if (typeof ts === "number") return ts;
    if (typeof ts === "string" && ts !== "") {
      const n = Number(ts);
      return Number.isFinite(n) ? n : NaN;
    }
    return NaN;
  }

  function formatDateTime(ts) {
    const d = new Date(toMillis(ts));
    if (Number.isNaN(d.getTime())) return "—";
    return d.toLocaleString("en-US", {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  function formatRequests(n) {
    if (!Number.isFinite(n)) return "0";
    if (Number.isInteger(n)) return n.toLocaleString();
    return n.toFixed(1);
  }

  function isOnDemandEvent(event) {
    return event.kind === "On-Demand";
  }

  function isIncludedEvent(event) {
    return event.kind === "Included";
  }

  function eventSpendDollars(event) {
    return (event.spendCents || 0) / 100;
  }

  function eventRequestsText(event) {
    if (state && state.quotaAwareEventDisplay && !isIncludedEvent(event)) return "—";
    return formatRequests(event.requests || 0);
  }

  function eventSpendText(event) {
    return formatDollars(eventSpendDollars(event));
  }

  function formatDayLabel(dayMs) {
    return new Date(dayMs).toLocaleDateString("en-US", {
      month: "short",
      day: "numeric",
      timeZone: "UTC",
    });
  }

  function formatResetCountdown(iso) {
    if (!iso) return "";
    const reset = new Date(iso);
    const days = Math.max(0, Math.ceil((reset.getTime() - Date.now()) / DAY_MS));
    const formatted = reset.toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" });
    return "Resets in " + days + " day" + (days === 1 ? "" : "s") + " on " + formatted;
  }

  function setActiveRangeButton() {
    ui.rangeSelector.querySelectorAll("button").forEach((btn) => {
      btn.classList.toggle("active", btn.dataset.range === local.range);
    });
  }

  function renderSummaryCards() {
    if (!state || !state.data) {
      ui.summaryCards.innerHTML = '<div class="card"><div class="card-label">No data yet</div></div>';
      return;
    }
    const { includedRequests, onDemand, includedSpend } = state.data;
    const hasSpend = includedSpend && includedSpend.totalDollars > 0;
    const onDemandSpend = hasSpend ? Math.max(0, includedSpend.totalDollars - includedSpend.includedDollars) : 0;
    const ratio = hasSpend
      ? Math.min(1, onDemandSpend / includedSpend.totalDollars)
      : (includedRequests.limit > 0 ? Math.min(1, includedRequests.used / includedRequests.limit) : 0);
    const pct = Math.round(ratio * 100);

    const parts = [];
    parts.push(
      '<div class="card">' +
        '<div class="card-label">' + (hasSpend ? "On-demand spend" : "Included-Request Usage") + "</div>" +
        '<div class="card-value">' + (hasSpend ? formatDollars(onDemandSpend) : (includedRequests.used + " / " + includedRequests.limit)) + "</div>" +
        '<div class="progress"><div style="width:' + (pct) + '%"></div></div>' +
        '<div class="card-footer">' + formatResetCountdown(state.resetsAt) + "</div>" +
      "</div>"
    );

    if (onDemand.state !== "disabled") {
      let valText, footerText, ratio;
      const labelText = onDemand.label || "On-Demand Usage";
      if (onDemand.state === "unlimited") {
        valText = formatDollars(onDemand.spendDollars);
        footerText = "Unlimited";
        ratio = 0;
      } else {
        valText = formatDollars(onDemand.spendDollars) + " / " + formatDollars(onDemand.limitDollars || 0);
        ratio = onDemand.limitDollars > 0 ? Math.min(1, onDemand.spendDollars / onDemand.limitDollars) : 0;
        footerText = onDemand.footer || "Pay for extra usage beyond your plan limits";
      }
      parts.push(
        '<div class="card">' +
          '<div class="card-label">' + labelText + "</div>" +
          '<div class="card-value">' + valText + "</div>" +
          '<div class="progress"><div style="width:' + Math.round(ratio * 100) + '%"></div></div>' +
          '<div class="card-footer">' + footerText + "</div>" +
        "</div>"
      );
    }
    ui.summaryCards.innerHTML = parts.join("");
  }

  function buildChartSeries() {
    const cutoff = getDurationCutoff(local.range, state.resetsAt, state.generatedAt);
    const start = startOfUtcDay(cutoff);
    // For the billing cycle range, extend the x-axis to the end of the current cycle
    // (the day before the next reset) so empty future days are visible.
    let end = startOfUtcDay(state.generatedAt);
    if (local.range === "billingCycle" && state.resetsAt) {
      const reset = new Date(state.resetsAt);
      if (!Number.isNaN(reset.getTime())) {
        // Show through the last day of the cycle (day before reset).
        const cycleEnd = startOfUtcDay(reset.getTime() - DAY_MS);
        if (cycleEnd > end) end = cycleEnd;
      }
    }
    const days = [];
    for (let d = start; d <= end; d += DAY_MS) days.push(d);
    if (days.length === 0) days.push(end);
    const dayIndex = new Map(days.map((d, i) => [d, i]));

    const perModel = new Map();
    const perModelSpend = new Map();
    const ensureArr = (map, m) => {
      let arr = map.get(m);
      if (!arr) {
        arr = new Array(days.length).fill(0);
        map.set(m, arr);
      }
      return arr;
    };

    for (const e of state.events) {
      const ts = toMillis(e.timestamp);
      if (!Number.isFinite(ts) || ts < cutoff) continue;
      if (!matchesUsageFilter(e, local.usageFilter)) continue;
      const day = startOfUtcDay(ts);
      const idx = dayIndex.get(day);
      if (idx === undefined) continue;
      const value =
        local.metric === "tokens" ? (e.totalTokens || 0) :
        local.metric === "requests" ? (e.requests || 0) :
        eventSpendDollars(e);
      ensureArr(perModel, e.model)[idx] += value;
      ensureArr(perModelSpend, e.model)[idx] += eventSpendDollars(e);
    }

    const datasets = [];
    for (const [model, arr] of perModel.entries()) {
      datasets.push({
        model,
        data: arr.slice(),
        spendByDay: (perModelSpend.get(model) || new Array(days.length).fill(0)).slice(),
        total: arr.reduce((a, b) => a + b, 0),
      });
    }
    // Sort by total over the range, descending — biggest contributor first.
    datasets.sort((a, b) => b.total - a.total);

    return { labels: days.map(formatDayLabel), datasets };
  }

  // Shared model→color map. Built from the current chart series so the table
  // and the chart always agree on which color belongs to which model.
  let modelColorMap = new Map();

  function rebuildModelColorMap(series) {
    modelColorMap = new Map();
    series.datasets.forEach((d, i) => {
      modelColorMap.set(d.model, PALETTE[i % PALETTE.length]);
    });
  }

  function colorForModel(model) {
    return modelColorMap.get(model) || "rgba(255,255,255,0.4)";
  }

  function tintColor(color, alpha) {
    if (!color) return "rgba(255,255,255," + alpha + ")";
    if (color.startsWith("#")) {
      const hex = color.slice(1);
      const full = hex.length === 3
        ? hex.split("").map((c) => c + c).join("")
        : hex;
      const r = parseInt(full.slice(0, 2), 16);
      const g = parseInt(full.slice(2, 4), 16);
      const b = parseInt(full.slice(4, 6), 16);
      return "rgba(" + r + "," + g + "," + b + "," + alpha + ")";
    }
    if (color.startsWith("rgb(")) {
      return color.replace("rgb(", "rgba(").replace(")", "," + alpha + ")");
    }
    if (color.startsWith("rgba(")) {
      return color.replace(/rgba\(([^,]+),([^,]+),([^,]+),[^)]+\)/, "rgba($1,$2,$3," + alpha + ")");
    }
    return color;
  }

  function getOrCreateTooltipEl() {
    let el = document.getElementById("chart-tooltip");
    if (!el) {
      el = document.createElement("div");
      el.id = "chart-tooltip";
      el.className = "chart-tooltip";
      document.body.appendChild(el);
    }
    return el;
  }

  function renderExternalTooltip(context, opts) {
    const { chart, tooltip } = context;
    const el = getOrCreateTooltipEl();
    if (tooltip.opacity === 0) {
      el.style.opacity = "0";
      return;
    }

    const dataPoints = (tooltip.dataPoints || [])
      .filter((dp) => (dp.parsed.y || 0) > 0)
      .sort((a, b) => (b.parsed.y || 0) - (a.parsed.y || 0));

    const title = (tooltip.title && tooltip.title[0]) || "";
    const isSpend = opts.isSpend;
    const metricLabel = isSpend ? "Spend" : opts.metric === "tokens" ? "Tokens" : "Requests";

    const formatMetric = (v) =>
      isSpend ? formatDollars(v) : opts.metric === "tokens" ? formatTokens(v) : formatRequests(v);

    const rows = dataPoints.map((dp) => {
      const ds = dp.dataset;
      const v = dp.parsed.y || 0;
      const spend = isSpend ? v : (ds.spendByDay ? (ds.spendByDay[dp.dataIndex] || 0) : 0);
      const color = ds.backgroundColor || colorForModel(ds.label);
      return (
        '<tr>' +
          '<td><span class="t-dot" style="background:' + color + '"></span>' + escapeHtml(ds.label) + '</td>' +
          '<td class="num">' + formatMetric(v) + '</td>' +
          (isSpend ? "" : '<td class="num">' + formatDollars(spend) + '</td>') +
        '</tr>'
      );
    }).join("");

    const headerCols = isSpend
      ? '<th>Model</th><th class="num">' + metricLabel + '</th>'
      : '<th>Model</th><th class="num">' + metricLabel + '</th><th class="num">Spend</th>';

    el.innerHTML =
      '<div class="t-title">' + escapeHtml(title) + '</div>' +
      '<table class="t-table"><thead><tr>' + headerCols + '</tr></thead><tbody>' + rows + '</tbody></table>';

    // Position tooltip relative to the canvas, keeping it inside the chart bounds.
    const canvasRect = chart.canvas.getBoundingClientRect();
    const tooltipWidth = el.offsetWidth;
    const tooltipHeight = el.offsetHeight;
    const padding = 12;

    let left = canvasRect.left + window.scrollX + tooltip.caretX + padding;
    let top = canvasRect.top + window.scrollY + tooltip.caretY - tooltipHeight / 2;

    // Flip to the left of the cursor if it would overflow the right edge.
    if (left + tooltipWidth > canvasRect.right + window.scrollX) {
      left = canvasRect.left + window.scrollX + tooltip.caretX - tooltipWidth - padding;
    }
    // Clamp vertically.
    const minTop = canvasRect.top + window.scrollY + 4;
    const maxTop = canvasRect.bottom + window.scrollY - tooltipHeight - 4;
    if (top < minTop) top = minTop;
    if (top > maxTop) top = maxTop;

    el.style.left = left + "px";
    el.style.top = top + "px";
    el.style.opacity = "1";
  }

  function renderChart() {
    const series = buildChartSeries();
    rebuildModelColorMap(series);
    const isSpend = local.metric === "spend";
    const yLabel = isSpend ? "Spend" : local.metric === "tokens" ? "Tokens" : "Requests";

    // For each x (day), find the topmost non-zero dataset so we only round
    // that segment's top corners. Datasets stack in order, so the "top" is
    // the LAST non-zero dataset at that x.
    const numX = series.labels.length;
    const topDatasetForX = new Array(numX).fill(-1);
    for (let i = 0; i < series.datasets.length; i++) {
      const data = series.datasets[i].data;
      for (let x = 0; x < numX; x++) {
        if ((data[x] || 0) > 0) topDatasetForX[x] = i;
      }
    }

    const RADIUS = 4;
    const chartData = {
      labels: series.labels,
      datasets: series.datasets.map((d, i) => ({
        label: d.model,
        data: d.data,
        spendByDay: d.spendByDay,
        backgroundColor: PALETTE[i % PALETTE.length],
        borderColor: PALETTE[i % PALETTE.length],
        borderWidth: 0,
        borderSkipped: false,
        borderRadius: (ctx) => {
          const x = ctx.dataIndex;
          const v = ctx.parsed && typeof ctx.parsed.y === "number" ? ctx.parsed.y : 0;
          if (v <= 0) return 0;
          if (topDatasetForX[x] !== i) return 0;
          // Round only the top corners of the top segment.
          return { topLeft: RADIUS, topRight: RADIUS, bottomLeft: 0, bottomRight: 0 };
        },
        categoryPercentage: 0.7,
        barPercentage: 0.85,
      })),
    };

    const styles = getComputedStyle(document.body);
    const muted = styles.getPropertyValue("--muted").trim() || "rgba(255,255,255,0.55)";
    const grid = styles.getPropertyValue("--border").trim() || "rgba(255,255,255,0.06)";

    const opts = {
      responsive: true,
      maintainAspectRatio: false,
      interaction: { mode: "index", intersect: false },
      layout: { padding: { top: 8, right: 4, bottom: 0, left: 0 } },
      plugins: {
        legend: {
          position: "bottom",
          align: "center",
          labels: {
            color: muted,
            boxWidth: 8,
            boxHeight: 8,
            usePointStyle: true,
            pointStyle: "circle",
            font: { size: 11 },
            padding: 12,
          },
        },
        tooltip: {
          enabled: false,
          external: (context) => renderExternalTooltip(context, { isSpend, metric: local.metric }),
        },
      },
      scales: {
        x: {
          stacked: true,
          ticks: { color: muted, font: { size: 10 }, maxRotation: 0, autoSkipPadding: 12 },
          grid: { display: false, drawBorder: false },
          border: { display: false },
        },
        y: {
          stacked: true,
          beginAtZero: true,
          ticks: {
            color: muted,
            font: { size: 10 },
            callback: (v) => isSpend ? formatDollars(v) : local.metric === "tokens" ? formatTokens(v) : v.toLocaleString(),
          },
          grid: { color: grid, drawBorder: false, drawTicks: false },
          border: { display: false },
          title: { display: false, text: yLabel },
        },
      },
    };

    // Recreate chart when switching type isn't supported in-place.
    if (chart) {
      chart.destroy();
      chart = null;
    }
    chart = new Chart(ui.canvas.getContext("2d"), { type: "bar", data: chartData, options: opts });

    ui.chartNote.textContent = "";
  }

  function getFilteredEvents() {
    if (!state) return [];
    const cutoff = getDurationCutoff(local.range, state.resetsAt, state.generatedAt);
    return state.events.filter((e) => {
      const ts = toMillis(e.timestamp);
      return Number.isFinite(ts) && ts >= cutoff && matchesUsageFilter(e, local.usageFilter);
    });
  }

  function getSortedEvents() {
    const events = getFilteredEvents();
    const dir = local.sortOrder === "asc" ? 1 : -1;
    const key = local.sortKey;
    return events.slice().sort((a, b) => {
      let av = a[key], bv = b[key];
      if (key === "timestamp") { av = toMillis(av); bv = toMillis(bv); }
      const an = typeof av === "number" ? av : Number(av);
      const bn = typeof bv === "number" ? bv : Number(bv);
      if (Number.isFinite(an) && Number.isFinite(bn)) return (an - bn) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
  }

  function renderTable() {
    const events = getSortedEvents();

    if (events.length === 0) {
      ui.tableBody.innerHTML = '<tr><td colspan="6" style="text-align:center; padding:24px;" class="muted">No events in this range</td></tr>';
    } else {
      ui.tableBody.innerHTML = events.map((e) => {
        const maxBadge = e.maxMode ? ' <span class="max-badge">MAX</span>' : "";
        const color = colorForModel(e.model);
        // Tint the row with a low-alpha derivative of the model color and add a
        // brighter left border for clearer association with the chart.
        const rowStyle = 'background:' + tintColor(color, 0.10) + ';box-shadow:inset 3px 0 0 ' + color + ';';
        return '<tr style="' + rowStyle + '">' +
          "<td>" + formatDateTime(e.timestamp) + "</td>" +
          '<td><span class="kind-badge kind-' + e.kind.replace(/[^A-Za-z]/g, "") + '">' + e.kind + "</span></td>" +
          "<td>" + escapeHtml(e.model) + maxBadge + "</td>" +
          '<td class="num">' + formatTokens(e.totalTokens || 0) + "</td>" +
          '<td class="num">' + eventRequestsText(e) + "</td>" +
          '<td class="num">' + eventSpendText(e) + "</td>" +
        "</tr>";
      }).join("");
    }

    ui.tableHead.querySelectorAll("th.sortable").forEach((th) => {
      th.classList.remove("sorted-asc", "sorted-desc");
      if (th.dataset.sort === local.sortKey) {
        th.classList.add(local.sortOrder === "asc" ? "sorted-asc" : "sorted-desc");
      }
    });

    if (events.length > 0) {
      ui.pagination.innerHTML = '<span class="muted">' + events.length + ' event' + (events.length === 1 ? '' : 's') + '</span>';
    } else {
      ui.pagination.innerHTML = "";
    }
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  }

  function rangeLabel() {
    if (local.range === "1d") return "Last 24 hours";
    if (local.range === "7d") return "Last 7 days";
    if (local.range === "30d") return "Last 30 days";
    return "Current Billing Cycle";
  }

  function aggregateModelBreakdown() {
    if (!state) return [];
    const cutoff = getDurationCutoff(local.range, state.resetsAt, state.generatedAt);
    const map = new Map();
    const spendByModel = new Map();
    if (Array.isArray(state.dailySpend)) {
      for (const row of state.dailySpend) {
        const day = toMillis(row.day);
        if (!Number.isFinite(day) || day < cutoff) continue;
        const model = row.category || "unknown";
        const cents = Number(row.spendCents || 0);
        if (!Number.isFinite(cents)) continue;
        spendByModel.set(model, (spendByModel.get(model) || 0) + cents);
      }
    }
    for (const e of state.events) {
      const ts = toMillis(e.timestamp);
      if (!Number.isFinite(ts) || ts < cutoff) continue;
      if (!matchesUsageFilter(e, local.usageFilter)) continue;
      const entry = map.get(e.model) || { model: e.model, requests: 0, totalTokens: 0, spendCents: 0 };
      entry.requests += e.requests || 0;
      entry.totalTokens += e.totalTokens || 0;
      // Spend in the model breakdown should match Cursor's dashboard "Cost" attribution
      // (includes included usage), which we source from dailySpend rows.
      entry.spendCents = spendByModel.get(e.model) || 0;
      map.set(e.model, entry);
    }
    const rows = Array.from(map.values());
    const dir = local.breakdownSortOrder === "asc" ? 1 : -1;
    const key = local.breakdownSortKey;
    rows.sort((a, b) => {
      const av = a[key], bv = b[key];
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * dir;
      return String(av).localeCompare(String(bv)) * dir;
    });
    return rows;
  }

  function renderBreakdown() {
    if (ui.breakdownRangeLabel) ui.breakdownRangeLabel.textContent = "(" + rangeLabel() + ")";
    const rows = aggregateModelBreakdown();

    if (ui.breakdownHead) {
      ui.breakdownHead.querySelectorAll("th.sortable").forEach((th) => {
        th.classList.remove("sorted-asc", "sorted-desc");
        if (th.dataset.sort === local.breakdownSortKey) {
          th.classList.add(local.breakdownSortOrder === "asc" ? "sorted-asc" : "sorted-desc");
        }
      });
    }

    if (rows.length === 0) {
      ui.breakdownBody.innerHTML = '<tr><td colspan="4" style="text-align:center; padding:24px;" class="muted">No usage in this range</td></tr>';
      return;
    }
    ui.breakdownBody.innerHTML = rows.map((r) => {
      const color = colorForModel(r.model);
      const rowStyle = 'background:' + tintColor(color, 0.10) + ';box-shadow:inset 3px 0 0 ' + color + ';';
      return '<tr style="' + rowStyle + '">' +
        '<td>' + escapeHtml(r.model) + '</td>' +
        '<td class="num">' + formatRequests(r.requests) + '</td>' +
        '<td class="num">' + formatTokens(r.totalTokens) + '</td>' +
        '<td class="num">' + formatDollars(r.spendCents / 100) + '</td>' +
      '</tr>';
    }).join("");
  }

  function exportCsv() {
    const events = getSortedEvents();
    const header = ["Date", "Type", "Model", "MaxMode", "Tokens", "Requests", "SpendUSD"];
    const lines = [header.join(",")];
    for (const e of events) {
      const ts = toMillis(e.timestamp);
      const dateStr = Number.isFinite(ts) ? new Date(ts).toISOString() : "";
      const row = [
        dateStr,
        e.kind,
        e.model,
        e.maxMode ? "true" : "false",
        e.totalTokens || 0,
        state && state.quotaAwareEventDisplay && !isIncludedEvent(e) ? "" : (e.requests || 0),
        state && state.quotaAwareEventDisplay && !isOnDemandEvent(e) ? "" : eventSpendDollars(e).toFixed(4),
      ].map(csvCell).join(",");
      lines.push(row);
    }
    const csv = lines.join("\n");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "cursor-usage-" + local.range + "-" + new Date().toISOString().slice(0, 10) + ".csv";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }

  function csvCell(v) {
    const s = String(v);
    const safe = /^\s*[=+\-@]/.test(s) ? "'" + s : s;
    if (/[",\n]/.test(safe)) return '"' + safe.replace(/"/g, '""') + '"';
    return safe;
  }

  function applyTeamMemberConstraints() {
    // Spend now derives from per-event chargedCents and is available for both solo and team users.
    const spendOpt = ui.metricFilter.querySelector('option[value="spend"]');
    if (spendOpt) spendOpt.disabled = false;
  }

  function showError(msg) {
    if (msg) {
      ui.errorBanner.textContent = msg;
      ui.errorBanner.classList.remove("hidden");
    } else {
      ui.errorBanner.classList.add("hidden");
    }
  }

  function renderAll() {
    if (!state) return;
    applySectionState();
    setActiveRangeButton();
    ui.usageFilter.value = local.usageFilter;
    ui.metricFilter.value = local.metric;
    applyTeamMemberConstraints();
    renderSummaryCards();
    if (local.activeTab === "usage") {
      renderChart();
      renderBreakdown();
      renderTable();
    }
    showError(state.error);
    ui.lastUpdated.textContent = "Updated " + new Date(state.generatedAt).toLocaleTimeString();
  }

  function fmtScorePct(s) {
    if (!Number.isFinite(s)) return "—";
    return (Math.round(s * 1000) / 10).toFixed(1) + "%";
  }

  function fmtUsd(n) {
    if (!Number.isFinite(n)) return "—";
    return "$" + n.toFixed(2);
  }

  function safeNumber(v) {
    const n = typeof v === "number" ? v : Number(v);
    return Number.isFinite(n) ? n : 0;
  }

  function formatCursorBenchDate(capturedAt) {
    if (!capturedAt) return "";
    const d = new Date(capturedAt);
    if (!Number.isFinite(d.getTime())) return String(capturedAt).slice(0, 10);
    return d.toISOString().slice(0, 10);
  }

  function formatCursorBenchProvenance(snapshot) {
    if (!snapshot) return "";
    if (snapshot.provenance === "live") return "live";
    if (snapshot.provenance === "bundled") return "bundled fallback";
    if (snapshot.stale) return "cached (stale)";
    return "cached";
  }

  function computeAxisRange(values, { pad = 0.05, floor = 0, ceiling = null } = {}) {
    const nums = values.filter((v) => Number.isFinite(v));
    if (nums.length === 0) return { min: floor, max: 1 };
    const rawMin = Math.min(...nums);
    const rawMax = Math.max(...nums);
    const min = Math.max(floor, rawMin - pad);
    const max = ceiling === null ? rawMax + pad : Math.min(ceiling, rawMax + pad);
    return { min, max: max <= min ? min + pad : max };
  }

  function cursorBenchBaseModel(name) {
    // Groups variants like "Opus 4.8 Max" / "Opus 4.8 High" together.
    // Keep this conservative; if a model has no tier suffix, it groups to itself.
    return String(name || "")
      .replace(/\s+(Max|Extra High|High|Medium|Low)\s*$/i, "")
      .trim();
  }

  function cursorBenchColorForBase(base) {
    // Stable palette mapping (sorted keys) so colors don't jump around.
    if (!cursorBench || !Array.isArray(cursorBench.rows)) return PALETTE[0];
    const bases = Array.from(
      new Set(cursorBench.rows.map((r) => cursorBenchBaseModel(r.model))),
    ).sort((a, b) => a.localeCompare(b));
    const idx = bases.indexOf(base);
    return PALETTE[(idx >= 0 ? idx : 0) % PALETTE.length];
  }

  function getCursorBenchSortedRows() {
    if (!cursorBench || !Array.isArray(cursorBench.rows)) return [];
    const dir = local.cursorBenchSortOrder === "asc" ? 1 : -1;
    const key = local.cursorBenchSortKey;
    return cursorBench.rows.slice().sort((a, b) => {
      const av = a[key];
      const bv = b[key];
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * dir;
      if (key === "model") return String(av).localeCompare(String(bv)) * dir;
      return (safeNumber(av) - safeNumber(bv)) * dir;
    });
  }

  function renderCursorBenchTable() {
    if (!ui.cursorBenchTableBody) return;
    const rows = getCursorBenchSortedRows();
    if (rows.length === 0) {
      ui.cursorBenchTableBody.innerHTML =
        '<tr><td colspan="5" style="text-align:center; padding:24px;" class="muted">No snapshot data</td></tr>';
      return;
    }

    ui.cursorBenchTableBody.innerHTML = rows
      .map((r) => {
        const base = cursorBenchBaseModel(r.model);
        const color = cursorBenchColorForBase(base);
        return (
          "<tr>" +
          "<td>" +
          '<span class="model-cell"><span class="model-dot" style="background:' + color + '"></span>' +
          escapeHtml(r.model) +
          "</span>" +
          "</td>" +
          '<td class="num">' +
          fmtScorePct(r.score) +
          "</td>" +
          '<td class="num">' +
          fmtUsd(r.costPerTask) +
          "</td>" +
          '<td class="num">' +
          formatTokens(r.tokensPerTask) +
          "</td>" +
          '<td class="num">' +
          formatRequests(r.stepsPerTask) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");

    if (ui.cursorBenchTableHead) {
      ui.cursorBenchTableHead.querySelectorAll("th.sortable").forEach((th) => {
        th.classList.remove("sorted-asc", "sorted-desc");
        if (th.dataset.sort === local.cursorBenchSortKey) {
          th.classList.add(local.cursorBenchSortOrder === "asc" ? "sorted-asc" : "sorted-desc");
        }
      });
    }
  }

  function renderCursorBenchChart() {
    if (!ui.cursorBenchCanvas) return;
    const rows = getCursorBenchSortedRows();
    if (rows.length === 0) return;

    const xKey = local.cursorBenchXMetric || "costPerTask";
    const xLabel =
      xKey === "tokensPerTask" ? "Tokens / task" :
      xKey === "stepsPerTask" ? "Steps / task" :
      "Cost / task ($)";

    const grouped = new Map();
    for (const r of rows) {
      const base = cursorBenchBaseModel(r.model);
      const arr = grouped.get(base) || [];
      arr.push({ x: r[xKey], y: r.score, model: r.model, base });
      grouped.set(base, arr);
    }

    // Within each base-model dataset, sort by x so the connecting line is tidy.
    const datasets = Array.from(grouped.entries())
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([base, pts]) => {
        pts.sort((a, b) => (a.x - b.x));
        const color = cursorBenchColorForBase(base);
        return {
          label: base,
          data: pts,
          showLine: true,
          borderColor: color,
          backgroundColor: color,
          pointBackgroundColor: color,
          pointBorderColor: color,
          borderWidth: 1.5,
          pointRadius: 3,
          pointHoverRadius: 5,
          tension: 0,
        };
      });

    const styles = getComputedStyle(document.body);
    const muted = styles.getPropertyValue("--muted").trim() || "rgba(255,255,255,0.55)";
    const grid = styles.getPropertyValue("--border").trim() || "rgba(255,255,255,0.06)";

    const xValues = rows.map((r) => r[xKey]);
    const yValues = rows.map((r) => r.score);
    const xRange = computeAxisRange(xValues, { pad: Math.max(...xValues, 1) * 0.05, floor: 0 });
    const yRange = computeAxisRange(yValues, { pad: 0.03, floor: 0, ceiling: 1 });

    if (cursorBenchChart) {
      cursorBenchChart.destroy();
      cursorBenchChart = null;
    }

    cursorBenchChart = new Chart(ui.cursorBenchCanvas.getContext("2d"), {
      type: "scatter",
      data: {
        datasets,
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          legend: {
            display: true,
            position: "bottom",
            labels: {
              color: muted,
              boxWidth: 8,
              boxHeight: 8,
              usePointStyle: true,
              pointStyle: "circle",
              font: { size: 11 },
              padding: 12,
            },
          },
          tooltip: {
            callbacks: {
              label: (ctx) => {
                const raw = ctx.raw || {};
                const model = raw.model || "";
                const score = Number.isFinite(raw.y) ? fmtScorePct(raw.y) : "—";
                const xText =
                  xKey === "costPerTask" ? (Number.isFinite(raw.x) ? fmtUsd(raw.x) : "—") :
                  xKey === "tokensPerTask" ? (Number.isFinite(raw.x) ? formatTokens(raw.x) : "—") :
                  (Number.isFinite(raw.x) ? formatRequests(raw.x) : "—");
                const xPrefix = xKey === "costPerTask" ? "cost" : xKey === "tokensPerTask" ? "tokens" : "steps";
                return model + " • score " + score + " • " + xPrefix + " " + xText;
              },
            },
          },
        },
        scales: {
          x: {
            type: "linear",
            min: xRange.min,
            max: xRange.max,
            beginAtZero: true,
            reverse: true,
            ticks: { color: muted, font: { size: 10 } },
            grid: { color: grid, drawBorder: false, drawTicks: false },
            border: { display: false },
            title: { display: true, text: xLabel, color: muted, font: { size: 11, weight: "500" } },
          },
          y: {
            type: "linear",
            min: yRange.min,
            max: yRange.max,
            ticks: {
              color: muted,
              font: { size: 10 },
              callback: (v) => Math.round(v * 100) + "%",
            },
            grid: { color: grid, drawBorder: false, drawTicks: false },
            border: { display: false },
            title: { display: true, text: "CursorBench score", color: muted, font: { size: 11, weight: "500" } },
          },
        },
      },
    });
  }

  function renderCursorBench() {
    if (!cursorBench) return;
    if (ui.cursorBenchMeta) {
      const version = cursorBench.version || "";
      const date = formatCursorBenchDate(cursorBench.capturedAt);
      const provenance = formatCursorBenchProvenance(cursorBench);
      ui.cursorBenchMeta.textContent = "v" + version + " • " + date + " • " + provenance;
    }
    if (ui.cursorBenchXMetric) ui.cursorBenchXMetric.value = local.cursorBenchXMetric;
    renderCursorBenchChart();
    renderCursorBenchTable();
    if (ui.cursorBenchNote) {
      ui.cursorBenchNote.textContent = cursorBench.sourceUrl ? "Source: " + cursorBench.sourceUrl : "";
    }
  }

  // Event wiring
  ui.rangeSelector.addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-range]");
    if (!btn) return;
    local.range = btn.dataset.range;
    persistLocal();
    renderAll();
  });

  ui.usageFilter.addEventListener("change", () => {
    local.usageFilter = ui.usageFilter.value;
    persistLocal();
    renderChart();
    renderBreakdown();
    renderTable();
  });

  ui.metricFilter.addEventListener("change", () => {
    local.metric = ui.metricFilter.value;
    persistLocal();
    renderChart();
  });

  ui.tableHead.addEventListener("click", (e) => {
    const th = e.target.closest("th.sortable");
    if (!th) return;
    const key = th.dataset.sort;
    if (local.sortKey === key) {
      local.sortOrder = local.sortOrder === "asc" ? "desc" : "asc";
    } else {
      local.sortKey = key;
      local.sortOrder = key === "model" || key === "kind" ? "asc" : "desc";
    }
    persistLocal();
    renderTable();
  });

  if (ui.breakdownHead) {
    ui.breakdownHead.addEventListener("click", (e) => {
      const th = e.target.closest("th.sortable");
      if (!th) return;
      const key = th.dataset.sort;
      if (local.breakdownSortKey === key) {
        local.breakdownSortOrder = local.breakdownSortOrder === "asc" ? "desc" : "asc";
      } else {
        local.breakdownSortKey = key;
        local.breakdownSortOrder = key === "model" ? "asc" : "desc";
      }
      persistLocal();
      renderBreakdown();
    });
  }

  ui.refreshBtn.addEventListener("click", () => {
    vscode.postMessage({ type: "refresh" });
  });

  ui.exportBtn.addEventListener("click", exportCsv);

  document.querySelectorAll(".section-title-row[data-toggle-section]").forEach((row) => {
    row.addEventListener("click", () => {
      const section = row.dataset.toggleSection;
      if (!section || !Object.prototype.hasOwnProperty.call(local.sectionOpen, section)) return;
      local.sectionOpen[section] = !local.sectionOpen[section];
      persistLocal();
      applySectionState();
      // Chart.js needs a redraw when the canvas becomes visible again.
      if (section === "usage" && local.sectionOpen.usage && state) {
        renderChart();
      }
    });
  });

  window.addEventListener("message", (event) => {
    const msg = event.data;
    if (!msg || typeof msg !== "object") return;
    if (msg.type === "state") {
      state = msg.state;
      renderAll();
    } else if (msg.type === "loading") {
      ui.refreshBtn.disabled = !!msg.on;
      ui.refreshBtn.textContent = msg.on ? "Refreshing…" : "Refresh";
    } else if (msg.type === "cursorbench") {
      cursorBench = msg.snapshot || null;
      if (ui.cursorBenchRefresh) ui.cursorBenchRefresh.disabled = false;
      if (local.activeTab === "cursorbench") renderCursorBench();
    } else if (msg.type === "cursorbenchRefreshing") {
      if (ui.cursorBenchRefresh) ui.cursorBenchRefresh.disabled = Boolean(msg.on);
      if (ui.cursorBenchMeta && msg.on) ui.cursorBenchMeta.textContent = "Refreshing…";
    } else if (msg.type === "selectTab") {
      setActiveTab(msg.tab);
    }
  });

  if (ui.tabUsage) ui.tabUsage.addEventListener("click", () => setActiveTab("usage"));
  if (ui.tabCursorBench) ui.tabCursorBench.addEventListener("click", () => setActiveTab("cursorbench"));

  if (ui.usageOpenSource) {
    ui.usageOpenSource.addEventListener("click", () => {
      vscode.postMessage({ type: "openExternal", url: "https://cursor.com/dashboard/usage" });
    });
  }

  if (ui.cursorBenchRefresh) {
    ui.cursorBenchRefresh.addEventListener("click", () => {
      ui.cursorBenchRefresh.disabled = true;
      if (ui.cursorBenchMeta) ui.cursorBenchMeta.textContent = "Refreshing…";
      vscode.postMessage({ type: "refreshCursorBench" });
    });
  }

  if (ui.cursorBenchOpenSource) {
    ui.cursorBenchOpenSource.addEventListener("click", () => {
      const url = cursorBench && cursorBench.sourceUrl ? cursorBench.sourceUrl : "https://cursor.com/cursorbench";
      vscode.postMessage({ type: "openExternal", url });
    });
  }

  if (ui.cursorBenchTableHead) {
    ui.cursorBenchTableHead.addEventListener("click", (e) => {
      const th = e.target.closest("th.sortable");
      if (!th) return;
      const key = th.dataset.sort;
      if (local.cursorBenchSortKey === key) {
        local.cursorBenchSortOrder = local.cursorBenchSortOrder === "asc" ? "desc" : "asc";
      } else {
        local.cursorBenchSortKey = key;
        local.cursorBenchSortOrder = key === "model" ? "asc" : "desc";
      }
      persistLocal();
      renderCursorBenchTable();
    });
  }

  if (ui.cursorBenchXMetric) {
    ui.cursorBenchXMetric.addEventListener("change", () => {
      local.cursorBenchXMetric = ui.cursorBenchXMetric.value;
      persistLocal();
      if (local.activeTab === "cursorbench") renderCursorBenchChart();
    });
  }

  applySectionState();
  setActiveTab(local.activeTab);
  // Tell host we're ready
  vscode.postMessage({ type: "ready" });
})();

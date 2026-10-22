# Matmon UI conventions - MatUi

Matmon is drawn with **MatUi**, the toolkit of the Mat app family (repository `../MatDesign`, `src/MatUi`; rulebook,
components and live examples on the MatDesign site). The toolkit is **copied** into `src/Matmon.Host/wwwroot/matui`
(rule CODE-03: never loaded from another site - the instance works offline and in air-gapped networks) by
`scripts/sync-matui.ps1`; `scripts/check-matui.ps1` says whether MatDesign has something newer and
`scripts/matui-synced.txt` records which version the copy is. Read `wwwroot/matui/CHANGELOG.md` from the old version to
the new one before committing an update: a *Breaking* section says what the app has to change. Matmon.Cloud uses the same
toolkit and the same conventions (`../Matmon.Cloud/docs/ui-conventions.md`), so the two look like one product.

The aim is not a look but **less code**: the menu, dialogs, toolbar, buttons, colours and the dark mode are MatUi's, and
what is left in Matmon is what only a monitoring tool has (the tree, charts, the map designer and wallboards, the script
editor ...). Rule ids below (`LIST-03`, `CODE-02` ...) are MatDesign's.

## What the app adds

| File | What |
|---|---|
| `Ui/MatUi.cs` | `MatUi.Icon("name")` (an icon of the inline sprite - the names are in `wwwroot/matui/matui-icons.svg`), `MatUi.Initials`, `MatUi.PillClass(toneOrStateKey)`, `MatUi.StatePill(SensorState)`; `MatUiToolkit` (the sprite, inlined once per page, and the version hash for the file addresses). |
| `Ui/InstanceShell.cs` | `InstanceShellBuilder`: the brand (Matmon or a managing partner's white label: product name, logo, accent via `data-custom-accent`, favicon), the menu with its live counts, the user and the version. `_Layout.cshtml` only draws it. |
| `Pages/Shared/_AppStyles.cshtml`, `_AppScripts.cshtml` | The app's own stylesheets and scripts, in one place for both layouts and the public wallboard. |
| `wwwroot/css/app.css` | The few shared app styles (embedded mode, the stacked partner brand, `mm-state-dot`, small helpers). |
| `wwwroot/css/features/*.css` | One file per feature (charts, dashboard, monitoring, sensor, editors, probes, notifications, system, maps, auth): only what MatUi does not have. |
| `wwwroot/js/app/*.js` | One classic script per feature, sharing one global scope; `core.js` first (its one DOMContentLoaded starts every feature, plus `initializeTabHash` and shared helpers). Theme, menu, drawer, user menu, dialogs, `data-confirm`, tabs, copy, pickers and validation are `matui.js`. |
| `wwwroot/js/matmon.boot.js` | Carries an old theme choice (`matmon-theme`) over to MatUi's `mat-theme`, once. |

## The page

The layout draws the shell and the page head. A page sets:

```csharp
ViewData["Title"] = "Sensors";                     // the h1, the last crumb and the window title
ViewData["PageSub"] = "One line under the title."; // optional
ViewData["Width"] = "narrow";                     // optional: "narrow" (forms, 780 px) or "wide" (the map designer)
ViewData["Breadcrumbs"] = new[] { new BreadcrumbItem("Sensors", "/Monitoring"), new BreadcrumbItem("Ping") }; // a custom trail
ViewData["OwnHead"] = true;                       // only when the page draws its own <div class="mat-page__head">
```

`@section TopbarActions { <div class="mat-topbar__actions">…</div> }` puts at most four icon buttons at the right end of
the header bar (SHELL-17). Exactly one `<h1>` per page. Back navigation is the breadcrumb. Pages without the menu (sign-in,
the second factor, setup, the setup assistant, errors) use `Layout = "_AnonymousLayout"` and draw one
`<div class="mat-auth__card">` with a `mat-auth__brand`. Inside the Matmon.Cloud console (`X-Matmon-Embed: 1`) the layout
renders no menu and no header bar - nothing for a page to do.

## Building blocks (old Matmon class -> MatUi)

| Old | MatUi |
|---|---|
| `.panel` / `.workspace-panel` / `.panel-header` / `.section-title` | `<section class="mat-card">` with `<div class="mat-card__head">@MatUi.Icon("x")<h2 class="mat-card__title">…</h2></div>`, `<div class="mat-card__body">`, optional `mat-card__foot`. Several cards: `mat-stack` or `mat-grid`. |
| `.panel-hint`, `.workspace-inline-note`, `.workspace-hint` | `<p class="mat-muted">` / `<span class="mat-text-muted">`; a field's help is `<span class="mat-hint">` |
| `.page-header`, `.page-title`, `.workspace-summary-strip` | the layout's head (the summary strip is gone - the dashboard has the counts) |
| `.workspace-banner(-success/-error/-warning)` | `<div class="mat-alert mat-alert--success|danger|warning|info" role="status|alert">@MatUi.Icon("check-circle|x-circle|alert|info")<div class="mat-alert__body">…</div></div>` |
| `.workspace-submit` | `mat-btn mat-btn--primary` |
| `.workspace-secondary-submit` | `mat-btn mat-btn--secondary` |
| `.workspace-danger-submit` | `mat-btn mat-btn--danger` (+ `data-confirm`) |
| a link-style action in a row | `mat-btn mat-btn--ghost mat-btn--sm`, or an icon button `mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm` with `aria-label` + `data-tip` |
| `.workspace-form-actions` (+ spacer) | `<div class="mat-btn-row">` primary, secondary, `<span class="mat-btn-row__spacer"></span>`, danger - the toolkit enforces the order (FORM-01) |
| `details.workspace-action-menu` (⋯ menus) | `<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm" data-menu-toggle="id" data-menu-place="bottom" aria-haspopup="menu" aria-expanded="false" aria-label="Actions for X">@MatUi.Icon("more-horizontal")</button>` + `<div class="mat-menu" id="id" role="menu" hidden>` with `mat-menu__item` (`a` or `button`; in a form: `<form method="post"><button type="submit" class="mat-menu__item" role="menuitem">`), `mat-menu__sep`, `mat-menu__item--danger` |
| `.workspace-form` / `.workspace-field` / `<label><span>…</span><input>` | `<form class="mat-form" novalidate>`, `<div class="mat-field"><label class="mat-label" for="Id">…</label><input class="mat-input" …><span class="mat-hint">…</span><span class="mat-error" aria-live="polite"></span></div>`; side by side: `<div class="mat-form__grid">` (`mat-field--full` spans); sections: `mat-form__section` + `mat-form__section-head` |
| `.workspace-input`, `form-control` | `mat-input` (`mat-mono` for ids, addresses, OIDs, tokens) |
| `.workspace-select`, `form-select` | `mat-select` |
| textarea | `mat-textarea` |
| checkbox | `<label class="mat-check"><input type="checkbox" …><span class="mat-check__text">…</span></label>`; on/off: `mat-switch` (`<span class="mat-switch__track"></span>`) or a `mat-setting` row |
| `.workspace-table`, `table` | `<div class="mat-table-wrap"><table class="mat-table"><caption class="mat-sr-only">…</caption>` - EVERY `<td>` has `data-label` (TABLE-02); the cell that names the row and links to it has `mat-table__primary`; numbers `mat-num`, ids/dates/IPs `mat-mono` (TABLE-01); columns a phone can do without: `mat-hide-sm` on `<th>` and `<td>`; row actions in a last `<td class="mat-table__cell-actions"><div class="mat-table__actions">…</div></td>` |
| `.state-pill[data-state=…]` | `<span class="@MatUi.PillClass(MonitoringStatePresentation.Key(state))">@MonitoringStatePresentation.Label(state)</span>` (or `MatUi.StatePill(state)`) - always a word (TABLE-03, COLOR-04). A compact dot: `<span class="mm-state-dot" data-state="ok|warning|error|paused|disabled|unknown|ack"></span>` next to the word. |
| `.metric-card`, `.dashboard-count-grid` | `<div class="mat-grid mat-grid--4">` of `<div class="mat-card"><div class="mat-card__body mat-stat"><span class="mat-stat__label">…</span><span class="mat-stat__value">…</span><span class="mat-stat__delta">…</span></div></div>` (`<a class="mat-card">` when it links) |
| `.meter` / progress bars | `<div class="mat-meter" role="progressbar" aria-valuenow=… aria-valuemin="0" aria-valuemax="100" aria-label="…"><div class="mat-meter__fill" data-value="63" data-tone="ok|warn|error"></div></div>`; a split: `mat-meter--stack` with several fills |
| `.code-block`, `pre` | `<pre class="mat-code"><code>…</code></pre>`; a value to copy: `<div class="mat-input-group"><input class="mat-input mat-mono" readonly id="x"><button type="button" class="mat-btn mat-btn--secondary" data-copy="#x">…Copy</button></div>`; the old `data-copy-button` is gone |
| key/value tables | `<dl class="mat-kv"><dt>Host</dt><dd class="mat-mono">…</dd>…</dl>` |
| `.sensor-tabs` / `data-sensor-tab(-target)` | see *Tabs* |
| `_SystemTabs` (link tabs) | `<nav class="mat-tabs" aria-label="System"><a class="mat-tab is-active" aria-current="page" href="…">…</a>…</nav>` |
| `<details>` disclosure | `<details class="mat-collapse"><summary><span>Title</span>@MatUi.Icon("chevron-down", "mat-accordion__chev")</summary><div class="mat-accordion__body">…</div></details>` - the text in a `<span>`: Razor reads `text@MatUi` without a space as an e-mail address and prints it literally |
| an empty list | `<div class="mat-empty">` with `mat-empty__icon`, `mat-empty__title`, a sentence and the action that fills it (LIST-11); "nothing yet" is `mat-empty--first` |
| a home-made modal / overlay (credential editor, element picker …) | `<dialog class="mat-dialog" id="x" aria-labelledby="x-title"><div class="mat-dialog__form">` with `mat-dialog__head` (icon, `h2.mat-dialog__title`, close button `data-dialog-close`), `mat-dialog__body`, `mat-dialog__foot > .mat-btn-row.mat-btn-row--plain`; opened by `data-dialog-open="x"` or `MatUi.dialog.open(el)`. On a phone it is full screen (DIALOG-03). |
| tag chips | `<span class="mat-tag">…</span>` in `<span class="mat-chips">` |
| a filter bar with `<select>`s above a list | `<div class="mat-list">` + `<div class="mat-toolbar">` with the search as a field and the filters behind ONE Filter button that opens a dialog (LIST-03); filters in effect as `mat-chip`s (LIST-04); the count `mat-toolbar__info` (LIST-12). See `../Matmon.Cloud/src/Matmon.Cloud.Api/Pages/Shared/_ServerFilterBar.cshtml` for the exact markup. |

## Tabs

```cshtml
<section data-tabs data-tab-hash>   @* data-tab-hash: core.js keeps "#name" in the address *@
    <div class="mat-tabs" role="tablist" aria-label="Sections">
        <button type="button" class="mat-tab" role="tab" id="tab-general" aria-controls="panel-general" aria-selected="true">General</button>
        <button type="button" class="mat-tab" role="tab" id="tab-thresholds" aria-controls="panel-thresholds" aria-selected="false" tabindex="-1">Thresholds</button>
    </div>
    <div class="mat-tabpanel" role="tabpanel" id="panel-general" aria-labelledby="tab-general">…</div>
    <div class="mat-tabpanel" role="tabpanel" id="panel-thresholds" aria-labelledby="tab-thresholds" hidden>…</div>
</section>
```

The tab is `id="tab-<name>"`, its panel `id="panel-<name>"`; `#<name>` in the address opens it. A form posted back to the
same page carries the open tab's `#name` on its action (core.js; not the first tab, which the page opens on anyway), so Save, Test or a validation error come back on the tab
the user was on - nothing to do per page. A tab the server wants open regardless gets `aria-selected="true"` and its panel
loses `hidden`. A partial that brings its own tab panel (the sensor editors' thresholds, schedule, credentials) is a
`mat-tabpanel` with the matching ids.

## Forms with several submit buttons

Enter in a field submits the first submit button of the form - which on a big editor can be a secondary action ("Restore
from template" overwrote the element's values). Mark the button Enter should press with `data-default-submit` (core.js
presses it; a field that handles Enter itself - a tag input, a picker's search - cancels the key first and is left alone).

## Scripts

`core.js` runs the list `featureStarters` on DOMContentLoaded, each starter in its own `try/catch`: a feature that throws
is reported in the console and the others still start. A new feature = a `function initializeX()` in its file (a function
declaration, so it is on `window`) + its name in that list.


## The alert status at the top of the menu (`_NavInfo`)

MatUi's menu only has count badges (SHELL-14). The alert status is an app component above the menu items -
`Ui/NavInfo.cs` (record) + `Pages/Shared/_NavInfo.cshtml` (markup) + `.mm-navinfo` (app.css), the SAME three
pieces in the instance and in Matmon.Cloud: a titled link with one big number and its state word (Error / Warning / Ack /
Paused / OK - the most severe first), and a row of small linked counts (Err / Warn / Ack / Pause); a count that is not zero
takes its tone, a zero stays quiet (`.is-zero`). It IS the menu's entry to the alerts - there is no separate Alerts item.
`InstanceShellBuilder` builds it, `dashboard.js` (`renderNavCounters` -> `setNavInfo`) keeps it current from `/api/dashboard`. The tone/state
rule exists twice (C# `AlertStatus` and the script's `alertStatus`) - change both. Written generically (key, title, icon,
value, tone, state, stats) so another summary can use it, and so it can move into MatUi as a menu component one day.

⚠️ Razor drops an ordinary attribute whose value is null, but writes a **data-* attribute as `data-x=""`**, so
`data-x="@(cond ? "" : null)"` is always present. Give such an attribute a value ("true"/"false"), use a class, or emit
it with `@(cond ? Html.Raw("data-x") : HtmlString.Empty)`. `MatUiMarkupContractTests` fails on the pattern.

## Confirmations and messages (DIALOG-*)

`data-confirm="Delete this sensor? Its history goes with it."` + `data-confirm-ok="Delete"` on the submit button (or the
form) - MatUi asks in its own dialog. Never `onsubmit="return confirm(…)"`, never `window.confirm/alert/prompt` (in script:
`await MatUi.confirm(text, { ok: "Delete" })`).

## Feature styles (porting from the old site.css)

The old `wwwroot/css/site.css` stays in the repository until the move is complete, as the reference for what a feature
looked like - it is no longer linked. A feature's own components (the tree, charts, the map designer, wallboard tiles, the
script editor, discovery results, the schedule editor …) keep their feature class names (`map-*`, `sensor-chart-*`,
`discovery-*`, `tree-*` …) and get their CSS in their feature file, rewritten on MatUi tokens:

| old variable | MatUi token |
|---|---|
| `--matmon-foreground`, `--matmon-text` | `--mat-text` |
| `--matmon-muted` | `--mat-text-muted` (`--mat-text-faint` for placeholders) |
| `--matmon-border`, `--matmon-border-soft` | `--mat-border` (`--mat-border-strong` for a control's edge) |
| `--matmon-surface` | `--mat-surface` |
| `--matmon-surface-soft`, `--matmon-surface-strong`, `--matmon-surface-2` | `--mat-surface-2` / `--mat-surface-3` |
| `--matmon-bg`, `--matmon-page-bg` | `--mat-bg` |
| `--matmon-accent` (`-2`) | `--mat-accent` (`--mat-accent-soft` for a tint, `--mat-accent-text` for text); no second accent |
| `--matmon-success`, `--matmon-ok` | `--mat-success` (`--mat-success-bg`, `--mat-success-text`) |
| `--matmon-warning` | `--mat-warning` (`-bg`, `-text`) |
| `--matmon-danger`, `--matmon-critical` | `--mat-danger` (`-bg`, `-text`) |
| `--matmon-ack` | `--mat-info` (`-bg`, `-text`) |
| `--matmon-mono-font`, `--matmon-mono` | `--mat-mono` |
| `--matmon-shadow*` | none on cards; `--mat-shadow-2` only for a popover, `--mat-shadow-3` for a dialog (SPACE-03) |
| `--matmon-radius-*` | `--mat-radius-sm` / `--mat-radius` / `--mat-radius-lg` |
| spacing in px/rem | `--mat-space-1..12` (4 px scale, SPACE-01) |

Drop gradients, glows, lifted cards, tinted icon tiles next to titles and upper-case spaced labels (COLOR-06, SPACE-02/03,
SHELL-09). A gradient is only allowed for data (a colour ramp). Data colours that the user chose go into `data-colour`
(MatUi draws them as a mark, COLOR-10). **The one exception to "no inline style" is geometry that is data**: a map tile's
cell and span, a chart point, a meter-like width the toolkit has no attribute for - passed as CSS custom properties ONLY
(`style="--tile-col: 3; --tile-span: 4"`), never a property itself (`style="width: …"`, `style="color: …"` stay
forbidden). The feature CSS decides what the variable does. The public wallboard is the one place with its own
full-screen look - still on tokens.

## Rules for every page

- **No inline style** (`style="…"`), **no inline script**, **no `onclick`/`oninput`/`onsubmit`/`onchange`** (CODE-02). SVG
  geometry attributes (`d`, `x`, `width`, `viewBox`) are data, not style - fine; so is a style that sets nothing but CSS
  custom properties for geometry (see *Feature styles*).
- No literal colours (COLOR-01), no gradients (COLOR-06), no web fonts (TYPE-01).
- Icons: `@MatUi.Icon("name")` only, names from the sprite; an app glyph MatUi lacks (a probe, a host, a sensor kind) may
  keep `MatmonIcons.Render(...)` but rendered as `class="mat-icon"`-sized (see `Ui/MatmonIcons.cs`). No emoji as icons.
- Forms: `novalidate` + `required` / `type` / `pattern` / `min` / `max` and `data-msg-*` for MatUi's messages (FORM-05);
  server errors render into the field's `mat-error` (`<span asp-validation-for="X" class="mat-error"></span>`) or as a
  `mat-alert` at the top. A dependent field shows with `data-show-if="#id"` + `data-show-value` (FORM-04).
- Every page is checked in light and dark and at 390 px (MOBILE-01).

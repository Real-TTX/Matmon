// Matmon - tags: the overflow of tag chips and the tag input.
// Part of what was one site.js; the parts are classic scripts sharing one global scope (core.js first).

// Sensor-chip tags: show as many as fit on the meta line, collapse the rest into a "+N" tag
// whose title (mouseover) lists the hidden tags. A single ResizeObserver re-measures each strip
// on resize and when it first becomes visible (e.g. a tree node is expanded, or the tile size
// changes), so it's correct without any layout thrash on the server side.
let tagOverflowObserver = null;

function initializeTagOverflow(root) {
  const scope = root || document;
  const strips = scope.querySelectorAll("[data-tag-overflow]");
  if (strips.length === 0) {
    return;
  }

  if (!tagOverflowObserver && "ResizeObserver" in window) {
    tagOverflowObserver = new ResizeObserver((entries) => {
      entries.forEach((entry) => applyTagOverflow(entry.target));
    });
  }

  strips.forEach((strip) => {
    applyTagOverflow(strip);
    if (tagOverflowObserver) {
      tagOverflowObserver.observe(strip);
    }
  });
}

function applyTagOverflow(strip) {
  const chips = Array.from(strip.querySelectorAll("[data-tag-chip]"));
  if (chips.length === 0) {
    return;
  }

  let more = strip.querySelector("[data-tag-more]");
  if (!more) {
    more = document.createElement("span");
    more.className = "mat-tag tag-overflow-more";
    more.setAttribute("data-tag-more", "");
    strip.appendChild(more);
  }

  // Reset to "all visible" before measuring.
  chips.forEach((chip) => {
    chip.hidden = false;
  });
  more.hidden = true;

  if (strip.clientWidth === 0) {
    return; // not laid out yet - the observer will call us again once it is
  }

  const limit = strip.getBoundingClientRect().right + 1;
  const last = chips[chips.length - 1];
  if (last.getBoundingClientRect().right <= limit) {
    return; // everything fits
  }

  // Overflow: reveal the +N tag and hide tags from the end until it fits.
  more.hidden = false;
  const hidden = [];
  for (let i = chips.length - 1; i >= 0; i--) {
    chips[i].hidden = true;
    hidden.unshift(chips[i].textContent.trim());
    more.textContent = "+" + hidden.length;
    more.title = hidden.join(", ");
    if (more.getBoundingClientRect().right <= limit) {
      break;
    }
  }
}

// The tag field of the editors (ElementEditor, TemplateEditor, SensorCreate, SensorAssistant): a text input marked
// data-tag-input that holds the tags comma-joined ("prod, network"). It becomes MatUi's tag input (data-picker="tags"):
// the input is wrapped in a .mat-picker and stays the field that is posted (same name, same comma-joined value - the
// server splits at commas); MatUi draws the chips and handles Enter / comma / Backspace, pasting and the suggestions.
// The suggestions are the tags that already exist: the <datalist id="matmon-tag-suggestions"> of _TagSuggestions.
// An input that an editor already put into a .mat-picker itself is left to MatUi.
function initializeTagInputs() {
  const inputs = Array.from(document.querySelectorAll("input[data-tag-input]"))
    .filter((input) => input.dataset.tagInputReady !== "1" && !input.closest(".mat-picker"));
  if (inputs.length === 0) {
    return;
  }

  const suggestionList = document.getElementById("matmon-tag-suggestions");
  const suggestions = suggestionList
    ? Array.from(suggestionList.querySelectorAll("option")).map((option) => option.value.trim()).filter(Boolean)
    : [];

  inputs.forEach((input) => {
    input.dataset.tagInputReady = "1";

    const wrap = document.createElement("div");
    wrap.className = "mat-picker";
    wrap.setAttribute("data-picker", "tags");
    wrap.setAttribute("data-placeholder", input.getAttribute("placeholder") || "Add a tag");
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);

    if (input.type === "hidden") {
      input.type = "text";
    }
    input.classList.remove("mat-input", "form-control", "workspace-input");
    input.classList.add("mat-picker__input");
    input.removeAttribute("list");

    if (suggestions.length > 0) {
      const options = document.createElement("div");
      options.className = "mat-picker__options";
      options.hidden = true;
      suggestions.forEach((tag) => {
        const option = document.createElement("button");
        option.type = "button";
        option.className = "mat-picker__option";
        option.setAttribute("data-value", tag);
        option.textContent = tag;
        options.appendChild(option);
      });
      wrap.appendChild(options);
    }

    // matui.js set up the pickers that were on the page when it started; this one is new.
    if (window.MatUi && window.MatUi.picker) {
      window.MatUi.picker.init(wrap.parentNode);
    }
  });
}

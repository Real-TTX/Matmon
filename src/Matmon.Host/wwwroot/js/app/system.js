// Matmon - system (System section, backup job editor). Part of the shared global scope of wwwroot/js/app (core.js first).

// The backup job editor: a Cloud job stores configuration only, so its sections and retention do not apply and its
// passphrase does. The local-only parts are HIDDEN, not disabled (which MatUi's data-show-if would do): they keep their
// values, so a job that goes back to Local disk still has the sections it had - and the bool checkboxes' "false"
// partners (written at the end of the form) never post without their checkbox. The server draws the first state.
function initializeBackupJobEditor() {
  document.querySelectorAll("[data-backup-job-editor]").forEach((form) => {
    const destination = form.querySelector("[data-backup-destination]");
    if (!destination) {
      return;
    }

    const localOnly = form.querySelectorAll("[data-local-only]");
    const cloudOnly = form.querySelectorAll("[data-cloud-only]");
    const cloudNote = form.querySelectorAll("[data-cloud-note]");
    const smbOnly = form.querySelectorAll("[data-smb-only]");
    const sync = () => {
      const isCloud = destination.value === "Cloud";
      // "Local only" = everything a file-based job has (sections, retention) - an SMB job has them too.
      localOnly.forEach((element) => { element.hidden = isCloud; });
      cloudOnly.forEach((element) => { element.hidden = !isCloud; });
      cloudNote.forEach((element) => { element.hidden = !isCloud; });
      smbOnly.forEach((element) => { element.hidden = destination.value !== "Smb"; });
    };

    destination.addEventListener("change", sync);
    sync();
  });
}

/* System > Updates: the update screen. Matmon is restarting under it, so it asks /healthz (with XMLHttpRequest - the
   connection bar wraps fetch and would cover the screen with "No connection") until the server answers again, then
   reloads the page with the outcome. Back on the OLD version after having been away = rolled back; still the old
   version and never away after a while = the hand-over did not happen. */
// "A newer build is available" above every page for admins. Shown unless "Later" was pressed for THIS version (kept in the
// browser), so the next version is announced again; rendered hidden, so a dismissed one never flashes.
function initializeUpdateNotice() {
  const notice = document.querySelector("[data-update-notice]");
  if (!notice) {
    return;
  }
  const key = "matmon.updateNoticeLater";
  const version = notice.getAttribute("data-update-notice") || "";
  let dismissed = null;
  try { dismissed = localStorage.getItem(key); } catch { /* private mode: always shown */ }
  notice.hidden = dismissed === version;
  notice.querySelector("[data-update-notice-later]")?.addEventListener("click", () => {
    try { localStorage.setItem(key, version); } catch { /* nothing to keep */ }
    notice.hidden = true;
  });
}

function initializeUpdateScreen() {
  // Install confirmed: cover the page at once - the request downloads and checks the package before it answers.
  // MatUi's confirmation stops the first submit and sends the form again (requestSubmit) once it is answered, so the
  // screen waits for a submit that nobody stopped.
  const install = document.querySelector("form[data-update-install]");
  const prepare = document.querySelector("[data-update-prepare]");
  if (install && prepare) {
    install.addEventListener("submit", (event) => {
      window.setTimeout(() => {
        if (!event.defaultPrevented) {
          prepare.hidden = false;
        }
      }, 0);
    });
  }

  const screen = document.querySelector("[data-update-screen]");
  if (!screen) {
    return;
  }

  const from = screen.dataset.updateFrom || "";
  const to = screen.dataset.updateTo || "";
  // A restart rather than an update: done once Matmon went away and answers again, whatever version.
  const restart = screen.dataset.updateMode === "restart";
  const state = screen.querySelector("[data-update-state]");
  const elapsed = screen.querySelector("[data-update-elapsed]");
  const started = Date.now();
  const giveUpAfter = 8 * 60 * 1000;
  let wentAway = false;
  let finishing = false;

  // Inside the Matmon.Cloud console this page is a frame: the cloud lays its own screen over the WHOLE console (and
  // shows it again to whoever comes back while the instance restarts), so tell it - and tell it when it is over.
  const tellConsole = (type) => {
    if (window.parent === window) {
      return;
    }
    try {
      window.parent.postMessage({ type, from, to }, location.origin);
    } catch {
      // not the cloud console (another origin) - nothing to tell
    }
  };
  tellConsole(restart ? "matmon:restart" : "matmon:update");

  const say = (text) => { if (state) { state.textContent = text; } };
  const finish = (delay) => {
    if (finishing) {
      return;
    }
    finishing = true;
    tellConsole("matmon:update-done");
    // A moment for the installation to write down how it ended (a Docker helper does it right after the new container is up).
    window.setTimeout(() => {
      const query = restart ? new URLSearchParams({ restarted: "true" }) : new URLSearchParams({ finished: from, to });
      location.replace(location.pathname + "?" + query.toString());
    }, delay);
  };

  const health = () => new Promise((resolve) => {
    const request = new XMLHttpRequest();
    request.open("GET", "/healthz?t=" + Date.now());
    request.timeout = 4000;
    request.onload = () => {
      try {
        resolve(request.status === 200 ? JSON.parse(request.responseText) : null);
      } catch {
        resolve(null);
      }
    };
    request.onerror = request.ontimeout = () => resolve(null);
    request.send();
  });

  // The page's own account of the install ({ installing, step, steps, download }), null when it cannot be asked
  // (restarting, signed out, an older build).
  const installState = () => new Promise((resolve) => {
    const request = new XMLHttpRequest();
    request.open("GET", location.pathname + "?handler=State&t=" + Date.now());
    request.timeout = 4000;
    request.onload = () => {
      try {
        const answer = request.status === 200 ? JSON.parse(request.responseText) : null;
        resolve(answer && typeof answer.installing === "boolean" ? answer : null);
      } catch {
        resolve(null);
      }
    };
    request.onerror = request.ontimeout = () => resolve(null);
    request.send();
  });

  // The steps and the download, drawn from that account - all text through textContent.
  const stepList = screen.querySelector("[data-update-steps]");
  const downloadBox = screen.querySelector("[data-update-download]");
  const meter = screen.querySelector("[data-update-meter]");
  const bytes = screen.querySelector("[data-update-bytes]");
  const note = screen.querySelector("[data-update-note]");
  let steps = [];
  let lastInstalling = Date.now();
  const megabytes = (value) => (value / 1048576).toFixed(value < 10 * 1048576 ? 1 : 0);
  const duration = (seconds) => seconds >= 90 ? "about " + Math.round(seconds / 60) + " min left" : "about " + Math.max(1, Math.round(seconds)) + " s left";
  const drawSteps = (current) => {
    if (!stepList || steps.length === 0) {
      return;
    }
    const index = steps.findIndex((step) => step.key === current);
    stepList.replaceChildren(...steps.map((step, position) => {
      const item = document.createElement("li");
      item.textContent = step.title;
      item.dataset.state = position < index ? "done" : position === index ? "current" : "pending";
      if (position === index) {
        item.setAttribute("aria-current", "step");
      }
      return item;
    }));
    stepList.hidden = false;
  };
  const showProgress = (answer) => {
    if (!answer || !Array.isArray(answer.steps) || answer.steps.length === 0) {
      return;
    }
    steps = answer.steps;
    drawSteps(answer.step);
    const current = steps.find((step) => step.key === answer.step);
    if (current) {
      say(current.title + " ...");
    }

    const download = answer.download;
    if (!downloadBox || !download) {
      if (downloadBox) {
        downloadBox.hidden = true;
      }
      return;
    }
    const percent = download.total > 0 ? Math.min(100, Math.floor(download.done * 100 / download.total)) : 0;
    if (meter) {
      meter.setAttribute("aria-valuenow", String(percent));
      meter.querySelector(".mat-meter__fill")?.style.setProperty("--mat-meter", percent + "%");
    }
    const parts = [megabytes(download.done) + " of " + megabytes(download.total) + " MB"];
    if (download.complete) {
      parts.push("complete");
    } else {
      if (download.bytesPerSecond > 0) {
        parts.push((download.bytesPerSecond / 1048576).toFixed(1) + " MB/s");
      }
      if (typeof download.secondsLeft === "number") {
        parts.push(duration(download.secondsLeft));
      }
    }
    if (bytes) {
      bytes.textContent = parts.join(" · ");
    }
    if (note) {
      note.textContent = download.note || "";
      note.hidden = !download.note;
    }
    downloadBox.hidden = false;
  };
  // Once the old build went away, everything before the restart is done.
  const showRestarting = () => {
    if (steps.some((step) => step.key === "restart")) {
      drawSteps("restart");
    }
    if (downloadBox) {
      downloadBox.hidden = true;
    }
  };

  window.setInterval(() => {
    const seconds = Math.floor((Date.now() - started) / 1000);
    if (elapsed) {
      elapsed.textContent = Math.floor(seconds / 60) + ":" + String(seconds % 60).padStart(2, "0");
    }
  }, 1000);

  const poll = async () => {
    if (finishing) {
      return;
    }

    const answer = await health();
    const version = answer && answer.version;
    if (restart) {
      if (!version) {
        wentAway = true;
        say("Starting again ...");
      } else if (wentAway || (answer.run && screen.dataset.updateRun && answer.run !== screen.dataset.updateRun)) {
        say("Matmon is back - loading ...");
        finish(1500);
        return;
      } else {
        say("Stopping ...");
      }
      if (Date.now() - started > giveUpAfter) {
        finish(0);
        return;
      }
      window.setTimeout(poll, 2000);
      return;
    }

    if (!version) {
      wentAway = true;
      showRestarting();
      say("Matmon is restarting ...");
    } else if (version !== from) {
      say("Matmon " + version + " is up - loading ...");
      finish(3000);
      return;
    } else if (wentAway) {
      say("Matmon " + version + " is back - loading the outcome ...");
      finish(4000);
      return;
    } else {
      const answer = await installState();
      if (answer && answer.installing) {
        lastInstalling = Date.now();
        if (answer.steps && answer.steps.length > 0) {
          showProgress(answer);
        } else {
          say("Downloading and preparing " + to + " ...");
        }
      } else if (answer && answer.installing === false) {
        // Still the old build and nothing being installed any more: the install ended without a restart (the download
        // broke off, the package was refused) - show how it ended instead of waiting out the eight minutes.
        finish(0);
        return;
      } else {
        say(Date.now() - started < 15000 ? "Installing the update ..." : "Downloading and preparing " + to + " ...");
      }
    }

    // A slow download is not a hung update: the eight minutes count from the last time the install said it was busy.
    if (Date.now() - Math.max(started, lastInstalling) > giveUpAfter) {
      finish(0);
      return;
    }

    // While the download runs, a little more often - the figures should move.
    window.setTimeout(poll, downloadBox && !downloadBox.hidden ? 1500 : 3000);
  };

  window.setTimeout(poll, restart ? 2000 : 300);
}

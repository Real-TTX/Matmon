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
    const sync = () => {
      const isCloud = destination.value === "Cloud";
      localOnly.forEach((element) => { element.hidden = isCloud; });
      cloudOnly.forEach((element) => { element.hidden = !isCloud; });
      cloudNote.forEach((element) => { element.hidden = !isCloud; });
    };

    destination.addEventListener("change", sync);
    sync();
  });
}

/* System > Updates: the update screen. Matmon is restarting under it, so it asks /healthz (with XMLHttpRequest - the
   connection bar wraps fetch and would cover the screen with "No connection") until the server answers again, then
   reloads the page with the outcome. Back on the OLD version after having been away = rolled back; still the old
   version and never away after a while = the hand-over did not happen. */
function initializeUpdateScreen() {
  const screen = document.querySelector("[data-update-screen]");
  if (!screen) {
    return;
  }

  const from = screen.dataset.updateFrom || "";
  const to = screen.dataset.updateTo || "";
  const state = screen.querySelector("[data-update-state]");
  const elapsed = screen.querySelector("[data-update-elapsed]");
  const started = Date.now();
  const giveUpAfter = 8 * 60 * 1000;
  let wentAway = false;
  let finishing = false;

  const say = (text) => { if (state) { state.textContent = text; } };
  const finish = (delay) => {
    if (finishing) {
      return;
    }
    finishing = true;
    // A moment for the installation to write down how it ended (a Docker helper does it right after the new container is up).
    window.setTimeout(() => {
      const query = new URLSearchParams({ finished: from, to });
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
    if (!version) {
      wentAway = true;
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
      say(Date.now() - started < 15000 ? "Installing the update ..." : "Downloading and preparing " + to + " ...");
    }

    if (Date.now() - started > giveUpAfter) {
      finish(0);
      return;
    }

    window.setTimeout(poll, 3000);
  };

  window.setTimeout(poll, 2000);
}

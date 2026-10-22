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

// The offline page (offline.html, served by sw.js): keeps asking the server and carries on the moment it answers.
// A page that could not be LOADED is loaded again then; a form that could not be SENT is not sent again by itself - that
// is the person's decision (it may have arrived after all), so the page says the connection is back and offers Back.
(function () {
  "use strict";
  var body = document.body;
  var post = body.getAttribute("data-offline-reason") === "post";
  var title = document.querySelector("[data-offline-title]");
  var text = document.querySelector("[data-offline-text]");
  var status = document.querySelector("[data-offline-status]");
  var retry = document.querySelector("[data-offline-retry]");
  var back = document.querySelector("[data-offline-back]");
  var delay = 3;
  var timer = 0;

  if (post) {
    title.textContent = "Not sent";
    text.textContent = "The connection dropped while sending. Probably nothing arrived - go back and send it again once the connection is back.";
  }

  if (history.length <= 1) { back.hidden = true; }
  back.addEventListener("click", function () { history.back(); });
  retry.addEventListener("click", function () { check(true); });

  function schedule() {
    clearTimeout(timer);
    status.textContent = "Trying again in " + delay + " s…";
    timer = setTimeout(function () { check(false); }, delay * 1000);
    delay = Math.min(delay * 2, 30);
  }

  function check(byHand) {
    clearTimeout(timer);
    status.textContent = "Checking…";
    fetch("/healthz", { cache: "no-store" }).then(function (response) {
      if (!response.ok) { throw new Error("not ready"); }
      if (post) {
        status.textContent = "The connection is back.";
        retry.hidden = true;
        back.className = "mat-btn mat-btn--primary";
        return;
      }
      location.reload();
    }).catch(function () {
      if (byHand) { delay = 3; }
      schedule();
    });
  }

  window.addEventListener("online", function () { check(true); });
  schedule();
})();

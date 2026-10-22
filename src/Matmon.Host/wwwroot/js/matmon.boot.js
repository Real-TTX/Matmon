/* Matmon boot - runs before matui.boot.js. Before MatUi the theme was kept under "matmon-theme"; MatUi keeps it
   under "mat-theme". Carry an explicit choice over once so nobody's light or dark choice is lost with the move. */
(function () {
    try {
        var old = localStorage.getItem("matmon-theme");
        if (localStorage.getItem("mat-theme") === null && (old === "light" || old === "dark")) {
            localStorage.setItem("mat-theme", old);
        }
    } catch (e) { /* storage blocked: the system theme applies */ }
})();
